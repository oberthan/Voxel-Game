using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEngine;
using Random = UnityEngine.Random;

public class TerrainGenerator : MonoBehaviour
{
    public int renderDistance = 4;
    public int chunkSize = 16; // X and Z dimensions per chunk
    public int chunkHeight = 64; // Y dimension per chunk
    public float blockSize = 1f;

    public GameObject chunkPrefab;


    [Header("Noise")]
    public int seed = 0;
    public float noiseScale = 0.03f;
    public int octaves = 4;
    public float persistence = 0.5f;
    public float lacunarity = 2f;
    public float heightMultiplier = 20f;
    public int additionalHeigh = 20;

    [Header("LOD Settings")]
    public Transform player;
    public float lodDistance = 32f;


    public Material chunkMaterial;

    private Dictionary<Vector2Int, Chunk> chunks = new Dictionary<Vector2Int, Chunk>();
    private Vector2Int lastPlayerChunk;

    private Queue<GameObject> chunkPool = new Queue<GameObject>();
    // thread-safe queue of finished meshes to apply on main thread
    private ConcurrentQueue<(Chunk chunk, MeshData meshData)> meshQueue = new ConcurrentQueue<(Chunk, MeshData)>();
    // keep per-chunk cancellation tokens so we can cancel background tasks
    private Dictionary<Vector2Int, CancellationTokenSource> chunkCts = new Dictionary<Vector2Int, CancellationTokenSource>();

    void Start()
    {
        if (chunkMaterial == null)
        {
            Debug.LogWarning("No material assigned to VoxelTerrain. Assign a simple diffuse material.");
        }


        Random.InitState(seed);
        lastPlayerChunk = new Vector2Int(0, 0);
        GenerateInitialWorld();
    }

    void GenerateInitialWorld()
    {
        for (int cx = -4; cx < 4; cx++)
        {
            for (int cz = -4; cz < 4; cz++)
            {
                GameObject go = Instantiate(chunkPrefab, transform);
                go.name = $"Chunk_{cx}_{cz}";
                go.transform.position = new Vector3(cx * chunkSize * blockSize, 0, cz * chunkSize * blockSize);


                Chunk chunk = go.GetComponent<Chunk>();
                chunk.Initialize(chunkSize, chunkHeight, blockSize, cx, cz, this);


                chunk.Build();
                chunks.Add(new Vector2Int(cx, cz), chunk);
            }
        }

    }


    void Update()
    {

        var currentChunk = GetPlayerChunkCoord();
        if (currentChunk != lastPlayerChunk)
        {
            lastPlayerChunk = currentChunk;
            UpdateChunks();
        }

        int maxApplyPerFrame = 2;
        int applied = 0;
        while (applied < maxApplyPerFrame && meshQueue.TryDequeue(out var result))
        {
            ApplyMeshDataToChunk(result.chunk, result.meshData);
            applied++;
        }
    }

    // Called from Chunk (or Chunk task) thread-safe
    public void EnqueueMeshResult(Chunk chunk, MeshData md)
    {
        meshQueue.Enqueue((chunk, md));
    }

    void ApplyMeshDataToChunk(Chunk chunk, MeshData md)
    {
        if (chunk == null || chunk.gameObject == null)
        {
            Debug.LogError("Chunk does not exist");
            return; // chunk destroyed — ignore
        }

        var mf = chunk.GetComponent<MeshFilter>();
        if (mf == null)
        {
            Debug.LogError("Mesh filter on chunk does not exist");
            return; // object destroyed before mesh applied
        }
    


    // create the Unity Mesh and assign it (main thread only)
        var mr = chunk.GetComponent<MeshRenderer>();
        var mc = chunk.GetComponent<MeshCollider>();

        Mesh mesh = new Mesh();
        mesh.indexFormat = md.vertices.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(md.vertices);
        mesh.SetTriangles(md.triangles, 0);
        mesh.SetUVs(0, md.uvs);
        mesh.SetNormals(md.normals);

        mf.sharedMesh = mesh;

        if (mc == null) mc = chunk.gameObject.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;

        // mark chunk ready, etc.
    }


    async void UpdateChunks()
    {
        HashSet<Vector2Int> newChunkCoords = new HashSet<Vector2Int>();


        Vector2Int playerChunk = lastPlayerChunk;

        for (int cx = -renderDistance; cx < renderDistance; cx++)
        {
            for (int cz = -renderDistance; cz < renderDistance; cz++)
            {
                CreateChunk(cx, cz, newChunkCoords);
            }
        }
        /*
                List<Vector2Int> chunksToRemove = new List<Vector2Int>();
                foreach (var chunk in chunks)
                {
                    if (!newChunkCoords.Contains(chunk.Key))
                    {
                        Destroy(chunk.Value.gameObject);
                        chunksToRemove.Add(chunk.Key);
                    }
                }
                foreach (var coord in chunksToRemove)
                {
                    if (chunkCts.TryGetValue(coord, out var cts))
                    {
                        cts.Cancel();
                        cts.Dispose();
                        chunkCts.Remove(coord);
                    }
                    chunks.Remove(coord);
                }*/

        List<Vector2Int> chunksToDeactivate = new List<Vector2Int>();
        foreach (var kvp in chunks)
        {
            if (!newChunkCoords.Contains(kvp.Key))
            {
                Chunk chunk = kvp.Value;

                // cancel async build
                if (chunkCts.TryGetValue(kvp.Key, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                    chunkCts.Remove(kvp.Key);
                }

                // disable and pool it
                chunk.Clear();
                chunk.gameObject.SetActive(false);
                chunkPool.Enqueue(chunk.gameObject);

                chunksToDeactivate.Add(kvp.Key);
            }
        }

        foreach (var coord in chunksToDeactivate)
        {
            chunks.Remove(coord);
        }

    }



    void CreateChunk(int cx, int cz, HashSet<Vector2Int> newChunkCoords)
    {
       
        Vector2Int coord = new Vector2Int(lastPlayerChunk.x + cx, lastPlayerChunk.y + cz);
        newChunkCoords.Add(coord);

        int lod = Mathf.CeilToInt(Vector2.Distance(Vector2.zero, new Vector2(cx, cz )) / lodDistance);


        if (!chunks.ContainsKey(coord))
        {
            GameObject go;
            if (chunkPool.Count > 0)
            {
                go = chunkPool.Dequeue();
                go.SetActive(true);
            }
            else
            {
                go = Instantiate(chunkPrefab, transform);
            }


            go.name = $"Chunk_{coord.x}_{coord.y}";
            go.transform.position = new Vector3(coord.x * chunkSize * blockSize, 0, coord.y * chunkSize * blockSize);


            Chunk chunk = go.GetComponent<Chunk>();
            chunk.Initialize(chunkSize, chunkHeight, blockSize, coord.x, coord.y, this);

            // register cancellation token for this chunk
            var cts = new CancellationTokenSource();
            chunkCts[coord] = cts;

            chunk.BuildAsync(lod, cts.Token);
            chunks.Add(coord, chunk);
        }
        else
        {
            var chunk = chunks[coord];
            if (chunk.CurrentLOD != lod)
            {
                // cancel any running job for this chunk and start a new one
                Vector2Int key = coord;
                if (chunkCts.TryGetValue(key, out var oldCts))
                {
                    oldCts.Cancel();
                    oldCts.Dispose();
                }
                var cts = new CancellationTokenSource();
                chunkCts[key] = cts;
                chunk.BuildAsync(lod, cts.Token);
            }
        }
    }

    Vector2Int GetPlayerChunkCoord()
    {
        int cx = Mathf.FloorToInt(player.position.x / (chunkSize * blockSize));
        int cz = Mathf.FloorToInt(player.position.z / (chunkSize * blockSize));
        return new Vector2Int(cx, cz);
    }

}
