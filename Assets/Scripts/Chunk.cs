// === FILE: Chunk.cs ===
// Responsible for voxel data and mesh generation (greedy-ish: face culling)
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class Chunk : MonoBehaviour
{
    MeshFilter mf;
    MeshCollider mc;

    int sizeX, sizeY, sizeZ;
    float blockSize;
    int chunkX, chunkZ;
    TerrainGenerator terrainRef;

    byte[,,] voxels; // 1 = filled, 0 = empty
    public int CurrentLOD { get; private set; } = 1;

    List<Vector3> verts = new List<Vector3>();
    List<int> tris = new List<int>();
    List<Vector2> uvs = new List<Vector2>();
    List<Vector3> normals = new List<Vector3>();


    // We will define six faces with offsets and rotation
    static readonly Vector3[] faceOffsets = new Vector3[6]
    {
        new Vector3(0,0,-1), // back
        new Vector3(0,0,1),  // front
        new Vector3(-1,0,0), // left
        new Vector3(1,0,0),  // right
        new Vector3(0,-1,0), // bottom
        new Vector3(0,1,0)   // top
    };

    static readonly Vector3[][] faceVertexLocal = new Vector3[6][]
    {
        // back (-z)
        new Vector3[]{ new Vector3(1,0,0), new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(1,1,0) },
        // front (+z)
        new Vector3[]{ new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1) },
        // left (-x)
        new Vector3[]{ new Vector3(0,0,0), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(0,1,0) },
        // right (+x)
        new Vector3[]{ new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(1,1,1) },
        // bottom (-y)
        new Vector3[]{ new Vector3(0,0,1), new Vector3(0, 0, 0), new Vector3(1,0,0), new Vector3(1, 0, 1) },
        // top (+y)
        new Vector3[]{ new Vector3(0,1,0), new Vector3(0, 1, 1), new Vector3(1,1,1),  new Vector3(1, 1, 0) }
    };

    static readonly Vector3[] faceNormal = new Vector3[6]
    {
        Vector3.back, Vector3.forward, Vector3.left, Vector3.right, Vector3.down, Vector3.up
    };

    private void Awake()
    {
        mf = GetComponent<MeshFilter>();
        mc = GetComponent<MeshCollider>();
    }

    public void Initialize(int chunkSizeX, int chunkSizeY, float bSize, int cx, int cz, TerrainGenerator vt)
    {
        sizeX = chunkSizeX;
        sizeZ = chunkSizeX;
        sizeY = chunkSizeY;
        blockSize = bSize;
        chunkX = cx;
        chunkZ = cz;
        terrainRef = vt;

        voxels = new byte[sizeX, sizeY, sizeZ];
    }


    public void Build(int lod = 1)
    {
        CurrentLOD = lod;
        GenerateVoxelsSync(lod);
        BuildMesh(lod);
    }


    
    void GenerateVoxelsSync(int lod)
    {
        float scale = terrainRef.noiseScale;
        int octaves = terrainRef.octaves;
        float pers = terrainRef.persistence;
        float lac = terrainRef.lacunarity;
        float hm = terrainRef.heightMultiplier;
        int seed = terrainRef.seed;
        int additionalHeight = terrainRef.additionalHeigh;

        int step = lod;

        for (int x = 0; x < sizeX; x+= step)
        {
            for (int z = 0; z < sizeZ; z+= step)
            {
                // compute world X/Z
                float worldX = (chunkX * sizeX + x);
                float worldZ = (chunkZ * sizeZ + z);

                // fractal noise
                float amplitude = 1f;
                float frequency = 1f;
                float noiseHeight = 0f;

                for (int o = 0; o < octaves; o++)
                {
                    float sampleX = (worldX + seed * 1000f) * (scale * frequency);
                    float sampleZ = (worldZ + seed * 1000f) * (scale * frequency);

                    float per = Mathf.PerlinNoise(sampleX, sampleZ) * 2f - 1f; // -1..1
                    noiseHeight += per * amplitude;

                    amplitude *= pers;
                    frequency *= lac;
                }

                float height = (noiseHeight + 1f) * 0.5f * hm+additionalHeight; // normalize to 0..1 then * hm
                int maxY = Mathf.Clamp(Mathf.FloorToInt(height), 0, sizeY - 1);

                for (int y = 0; y <= maxY; y++)
                {
                    voxels[x, y, z] = 1;
                }
            }
        }
    }

    bool IsVoxelSolid(int x, int y, int z)
    {
        if (x < 0 || x >= sizeX || y < 0 || y >= sizeY || z < 0 || z >= sizeZ) return false;
        return voxels[x, y, z] == 1;
    }

    void BuildMesh(int lod)
    {
        verts.Clear(); tris.Clear(); uvs.Clear(); normals.Clear();

        int step = lod;

        for (int x = 0; x < sizeX; x += step)
        {
            for (int y = 0; y < sizeY; y += step)
            {
                for (int z = 0; z < sizeZ; z += step)
                {
                    if (voxels[x, y, z] == 0) continue;

                    Vector3 basePos = new Vector3(x, y, z) * blockSize;

                    // for every face, if neighbor is empty -> add face
                    for (int f = 0; f < 6; f++)
                    {
                        int nx = x + (int)faceOffsets[f].x*step;
                        int ny = y + (int)faceOffsets[f].y*step;
                        int nz = z + (int)faceOffsets[f].z * step;

                        if (!IsVoxelSolid(nx, ny, nz))
                        {
                            AddFace(basePos, f, step);
                        }
                    }
                }
            }
        }

        Mesh mesh = mf.sharedMesh;
        if(mesh == null)
        {
            mesh = new Mesh();
            mesh.name = $"Chunk_{chunkX}_{chunkZ}";
        } else
        {
            mesh.Clear();
        }

        mesh.indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.SetUVs(0, uvs);
        mesh.SetNormals(normals);
        mesh.Optimize();
        mesh.OptimizeIndexBuffers();
        mesh.OptimizeReorderVertexBuffer();

        mf.sharedMesh = mesh;

        // Optional: add collider
        if (mc == null) mc = gameObject.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;
    }

    void AddFace(Vector3 basePos, int faceIndex, int lod)
    {
        int vertIndex = verts.Count;
        float size = blockSize * lod;

        // add 4 verts for the face
        for (int i = 0; i < 4; i++)
        {
            Vector3 lv = faceVertexLocal[faceIndex][i] * size + basePos;
            verts.Add(lv);
            normals.Add(faceNormal[faceIndex]);
            // simple UVs: each face uses full square
            uvs.Add(new Vector2(i == 0 || i == 3 ? 0f : 1f, i == 0 || i == 1 ? 0f : 1f));
        }

        // two triangles (winding order depends on face orientation) - ensure consistent winding
        tris.Add(vertIndex + 0);
        tris.Add(vertIndex + 1);
        tris.Add(vertIndex + 2);

        tris.Add(vertIndex + 0);
        tris.Add(vertIndex + 2);
        tris.Add(vertIndex + 3);
    }



    public void Clear()
    {
        if (mf != null && mf.sharedMesh != null)
        {
            if (Application.isPlaying)
            {
                Destroy(mf.sharedMesh);
            }
            else
            {
                DestroyImmediate(mf.sharedMesh);
            }
        }

        if (mc) mc.sharedMesh = null;
    }



    // --------------------------
    // === NEW: Async-safe code
    // --------------------------

    private bool isBuilding = false;

    // Public async build that runs CPU-heavy work OFF the main thread
    public void BuildAsync(int lod, CancellationToken ct)
    {
        CurrentLOD = lod;
        if (isBuilding)
        {
            // If you want to allow multiple calls, you could queue or cancel - for now we just continue and let cancellation act.
        }

        isBuilding = true;

        // Capture the required settings into local variables (so background thread doesn't read Unity objects multiple times)
        float noiseScale = terrainRef.noiseScale;
        int octaves = terrainRef.octaves;
        float persistence = terrainRef.persistence;
        float lacunarity = terrainRef.lacunarity;
        float heightMultiplier = terrainRef.heightMultiplier;
        int seed = terrainRef.seed;
        int additionalHeight = terrainRef.additionalHeigh;

        int sizeXlocal = sizeX;
        int sizeYlocal = sizeY;
        int sizeZlocal = sizeZ;
        float bsize = blockSize;
        int cx = chunkX;
        int cz = chunkZ;

        // Run non-Unity CPU work on thread pool. IMPORTANT: do not call UnityEngine API here!
        Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return null;

            // Generate MeshData in a thread-safe way.
            var md = GenerateMeshDataThreadSafe(lod, ct,
                sizeXlocal, sizeYlocal, sizeZlocal, bsize,
                cx, cz,
                noiseScale, octaves, persistence, lacunarity, heightMultiplier, seed, additionalHeight);

            return md;
        }, ct).ContinueWith(t =>
        {
            // Continuation runs on thread pool — do not touch GameObjects here.
            if (t.IsCanceled || t.Result == null)
            {
                isBuilding = false;
                return;
            }

            // Enqueue the result to be applied on the main thread via TerrainGenerator
            terrainRef.EnqueueMeshResult(this, t.Result);
            isBuilding = false;
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    // Full thread-safe mesh generation that mirrors your BuildMesh logic
    MeshData GenerateMeshDataThreadSafe(int lod, CancellationToken ct,
        int sizeXlocal, int sizeYlocal, int sizeZlocal, float bsize,
        int cx, int cz,
        float noiseScale, int octaves, float persistence, float lacunarity, float heightMultiplier, int seed, int additionalHeight)
    {
        int step = lod;

        // local voxel buffer (fills same pattern as sync generator)
        byte[,,] localVoxels = new byte[sizeXlocal, sizeYlocal, sizeZlocal];

        // Generate voxels (using pure C# Perlin implementation)
        for (int x = 0; x < sizeXlocal; x += step)
        {
            if (ct.IsCancellationRequested) return null;
            for (int z = 0; z < sizeZlocal; z += step)
            {
                if (ct.IsCancellationRequested) return null;

                float worldX = (cx * sizeXlocal + x);
                float worldZ = (cz * sizeZlocal + z);

                // fractal noise
                double amplitude = 1.0;
                double frequency = 1.0;
                double noiseHeight = 0.0;

                for (int o = 0; o < octaves; o++)
                {
                    double sampleX = (worldX + seed * 1000f) * (noiseScale * frequency);
                    double sampleZ = (worldZ + seed * 1000f) * (noiseScale * frequency);

                    // PerlinNoise2D returns -1..1
                    double per = PerlinNoise2D.Noise(sampleX, sampleZ);
                    noiseHeight += per * amplitude;

                    amplitude *= persistence;
                    frequency *= lacunarity;
                }

                double height = (noiseHeight + 1.0) * 0.5 * (double)heightMultiplier + additionalHeight;
                int maxY = (int)Math.Floor(height);
                if (maxY < 0) maxY = 0;
                if (maxY > sizeYlocal - 1) maxY = sizeYlocal - 1;

                for (int y = 0; y <= maxY; y += step)
                {
                    localVoxels[x, y, z] = 1;
                }
            }
        }

        // Now build mesh lists (MeshData)
        MeshData md = new MeshData();

        // Local copies of face geometry for thread-safety
        // integer offsets
        int[,] fOffsets = new int[6, 3]
        {
            {0,0,-1}, // back
            {0,0,1},  // front
            {-1,0,0}, // left
            {1,0,0},  // right
            {0,-1,0}, // bottom
            {0,1,0}   // top
        };

        Vector3[][] fvLocal = new Vector3[6][]
        {
            new Vector3[]{ new Vector3(1,0,0), new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(1,1,0) }, // back
            new Vector3[]{ new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1) }, // front
            new Vector3[]{ new Vector3(0,0,0), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(0,1,0) }, // left
            new Vector3[]{ new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(1,1,1) }, // right
            new Vector3[]{ new Vector3(0,0,1), new Vector3(0, 0, 0), new Vector3(1,0,0), new Vector3(1, 0, 1) }, // bottom
            new Vector3[]{ new Vector3(0,1,0), new Vector3(0, 1, 1), new Vector3(1,1,1),  new Vector3(1, 1, 0) }  // top
        };

        Vector3[] fNormalLocal = new Vector3[6]
        {
            Vector3.back, Vector3.forward, Vector3.left, Vector3.right, Vector3.down, Vector3.up
        };

        // Build faces
        for (int x = 0; x < sizeXlocal; x += step)
        {
            if (ct.IsCancellationRequested) return null;
            for (int y = 0; y < sizeYlocal; y += step)
            {
                if (ct.IsCancellationRequested) return null;
                for (int z = 0; z < sizeZlocal; z += step)
                {
                    if (ct.IsCancellationRequested) return null;

                    if (localVoxels[x, y, z] == 0) continue;

                    Vector3 basePos = new Vector3(x, y, z) * bsize;
                    float faceSize = bsize * step;

                    for (int f = 0; f < 6; f++)
                    {
                        int nx = x + fOffsets[f, 0] * step;
                        int ny = y + fOffsets[f, 1] * step;
                        int nz = z + fOffsets[f, 2] * step;

                        bool neighborSolid = false;
                        if (nx >= 0 && nx < sizeXlocal && ny >= 0 && ny < sizeYlocal && nz >= 0 && nz < sizeZlocal)
                        {
                            neighborSolid = localVoxels[nx, ny, nz] == 1;
                        }
                        else
                        {
                            neighborSolid = false;
                        }

                        if (!neighborSolid)
                        {
                            int vertIndex = md.vertices.Count;

                            // add 4 verts for the face
                            for (int i = 0; i < 4; i++)
                            {
                                Vector3 lv = fvLocal[f][i] * faceSize + basePos;
                                md.vertices.Add(lv);
                                md.normals.Add(fNormalLocal[f]);
                                md.uvs.Add(new Vector2(i == 0 || i == 3 ? 0f : 1f, i == 0 || i == 1 ? 0f : 1f));
                            }

                            md.triangles.Add(vertIndex + 0);
                            md.triangles.Add(vertIndex + 1);
                            md.triangles.Add(vertIndex + 2);

                            md.triangles.Add(vertIndex + 0);
                            md.triangles.Add(vertIndex + 2);
                            md.triangles.Add(vertIndex + 3);
                        }
                    }
                }
            }
        }

        return md;
    }

    // --------------------------
    // === End thread-safe code
    // --------------------------
}


/// <summary>
/// Simple C# Perlin noise 2D implementation (returns -1..1).
/// Based on classic improved/permutation implementation. Thread-safe.
/// </summary>
public static class PerlinNoise2D
{
    static readonly int[] permutation = {
        151,160,137,91,90,15,
        131,13,201,95,96,53,194,233,7,225,140,36,103,30,
        69,142,8,99,37,240,21,10,23,
        190, 6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,
        35,11,32,57,177,33,88,237,149,56,87,174,20,125,136,171,
        168, 68,175,74,165,71,134,139,48,27,166,77,146,158,231,83,
        111,229,122,60,211,133,230,220,105,92,41,55,46,245,40,244,
        102,143,54, 65,25,63,161,1,216,80,73,209,76,132,187,208,
        89,18,169,200,196,135,130,116,188,159,86,164,100,109,198,173,
        186, 3,64,52,217,226,250,124,123,5,202,38,147,118,126,255,
        82,85,212,207,206,59,227,47,16,58,17,182,189,28,42,223,
        183,170,213,119,248,152,2,44,154,163,70,221,153,101,155,167,
        43,172,9,129,22,39,253,19,98,108,110,79,113,224,232,178,
        185,112,104,218,246,97,228,251,34,242,193,238,210,144,12,191,
        179,162,241,81,51,145,235,249,14,239,107,49,192,214,31,181,
        199,106,157,184, 84,204,176,115,121,50,45,127, 4,150,254,138,
        236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,
        61,156,180
    };

    static readonly int[] p; // permutation doubled
    static PerlinNoise2D()
    {
        p = new int[512];
        for (int i = 0; i < 512; i++)
            p[i] = permutation[i % 256];
    }

    static double Fade(double t)
    {
        // 6t^5 - 15t^4 + 10t^3
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    static double Lerp(double a, double b, double t)
    {
        return a + t * (b - a);
    }

    static double Grad(int hash, double x, double y)
    {
        int h = hash & 7;      // Convert low 3 bits of hash code
        double u = h < 4 ? x : y;
        double v = h < 4 ? y : x;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    /// <summary>
    /// Returns value in [-1,1]
    /// </summary>
    public static double Noise(double xin, double yin)
    {
        int X = (int)Math.Floor(xin) & 255;
        int Y = (int)Math.Floor(yin) & 255;

        double x = xin - Math.Floor(xin);
        double y = yin - Math.Floor(yin);

        double u = Fade(x);
        double v = Fade(y);

        int aa = p[p[X] + Y];
        int ab = p[p[X] + Y + 1];
        int ba = p[p[X + 1] + Y];
        int bb = p[p[X + 1] + Y + 1];

        double res = Lerp(
            Lerp(Grad(aa, x, y), Grad(ba, x - 1, y), u),
            Lerp(Grad(ab, x, y - 1), Grad(bb, x - 1, y - 1), u),
            v);

        // res is approx in [-1,1], return directly
        return res;
    }
}
