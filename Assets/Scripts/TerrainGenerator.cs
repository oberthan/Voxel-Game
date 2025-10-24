using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
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


    private SemaphoreSlim generationSemaphore;
    public int maxConcurrentGenerations = Math.Max(1, System.Environment.ProcessorCount - 1); // tune to CPU cores (e.g. Environment.ProcessorCount)
    private ConcurrentDictionary<Vector2Int, Task> runningTasks = new ConcurrentDictionary<Vector2Int, Task>();
    private int maxQueuedMeshes = 64; // tune

    private Queue<GameObject> chunkPool = new Queue<GameObject>();
    // thread-safe queue of finished meshes to apply on main thread
    private ConcurrentQueue<(Chunk chunk, MeshData meshData)> meshQueue = new ConcurrentQueue<(Chunk, MeshData)>();
    // keep per-chunk cancellation tokens so we can cancel background tasks
    private Dictionary<Vector2Int, CancellationTokenSource> chunkCts = new Dictionary<Vector2Int, CancellationTokenSource>();

    void Start()
    {
        generationSemaphore = new SemaphoreSlim(maxConcurrentGenerations);
        
        if (chunkMaterial == null)
        {
            Debug.LogWarning("No material assigned to VoxelTerrain. Assign a simple diffuse material.");
        }


        Random.InitState(seed);
        lastPlayerChunk = new Vector2Int(0, 0);
        GenerateInitialWorld();
        UpdateChunks();
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
            Debug.Log($"Player chunk: {currentChunk}");
            lastPlayerChunk = currentChunk;
            UpdateChunks();
        }

        int maxApplyPerFrame = 10;
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
        if (meshQueue.Count > maxQueuedMeshes)
        {
            // either drop this md, or dequeue oldest one to make room.
            // we'll drop the oldest:
            meshQueue.TryDequeue(out var _);
        }
        meshQueue.Enqueue((chunk, md));
    }

    void ApplyMeshDataToChunk(Chunk chunk, MeshData md)
    {
        if (chunk == null || chunk.gameObject == null) return;
        var mf = chunk.GetComponent<MeshFilter>();
        if (mf == null) return;

        // destroy existing mesh to free native memory
        if (mf.sharedMesh != null)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(mf.sharedMesh);
            else
                UnityEngine.Object.DestroyImmediate(mf.sharedMesh);
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = md.vertices.Count > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(md.vertices);
        mesh.SetTriangles(md.triangles, 0);
        mesh.SetUVs(0, md.uvs);
        mesh.SetNormals(md.normals);
        mesh.RecalculateBounds();

        mf.sharedMesh = mesh;

        if (chunk.CurrentLOD == 1)
        {
            var mc = chunk.GetComponent<MeshCollider>();
            if (mc == null) mc = chunk.gameObject.AddComponent<MeshCollider>();

            // destroy old collider mesh and assign new (avoid leaving collider with native mesh references)
            if (mc.sharedMesh != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(mc.sharedMesh);
                else
                    UnityEngine.Object.DestroyImmediate(mc.sharedMesh);
            }

            mc.sharedMesh = mesh;
        }
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
                    if (runningTasks.TryGetValue(kvp.Key, out var task))
                    {
                        try
                        {
                            task.Wait(50); // wait 100ms (don't block long on main thread)
                        }
                        catch { }
                    }
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

            StartChunkGeneration(chunk, coord, lod, cts.Token);
            //chunk.BuildAsync(lod, cts.Token);
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
                //chunk.BuildAsync(lod, cts.Token);
                StartChunkGeneration(chunk, coord, lod, cts.Token);
            }
        }
    }

    Vector2Int GetPlayerChunkCoord()
    {
        int cx = Mathf.FloorToInt(player.position.x / (chunkSize * blockSize));
        int cz = Mathf.FloorToInt(player.position.z / (chunkSize * blockSize));
        return new Vector2Int(cx, cz);
    }

    private void StartChunkGeneration(Chunk chunk, Vector2Int coord, int lod, CancellationToken token)
    {
        // enqueue a Task that respects the semaphore, generation count and registers itself
        var t = Task.Run(async () =>
        {
            try
            {
                await generationSemaphore.WaitAsync(token).ConfigureAwait(false);
                // If cancelled before we started, return
                if (token.IsCancellationRequested) return;
                // call the chunk's thread-safe generator directly (it enqueues the result itself)
                chunk.GenerateAndEnqueue(lod, token); // see next chunk code changes
            }
            catch (OperationCanceledException) { /* cancelled */ }
            finally
            {
                generationSemaphore.Release();
            }
        }, token);

        runningTasks[coord] = t;

        // when task finishes remove from dictionary
        t.ContinueWith(_ => {
            runningTasks.TryRemove(coord, out _);
        }, TaskScheduler.Default);
    }


}
