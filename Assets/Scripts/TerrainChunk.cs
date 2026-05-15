using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// one terrain tile - mesh data is built on background thread then uploaded on main thread.
/// supports in-place LOD rebuild (old mesh stays visible until the new one is ready)
/// </summary>

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class TerrainChunk : MonoBehaviour
{
    public Vector2Int Coord { get; private set; }
    public bool IsReady { get; private set; }
    public int LodLevel { get; private set; }
    public int PendingLod { get; private set; } = -1; // -1 = not rebuilding

    private MeshFilter _mf;
    private MeshRenderer _mr;

    private Vector3[] _verts, _newVerts;
    private Vector3[] _normals, _newNormals;
    private int[] _tris, _newTris;
    private Vector2[] _uvs, _newUvs;
    private Color[] _colors, _newColors;

    private volatile bool _dataReady = false;
    private volatile bool _rebuildReady = false;

    public static int appliesThisFrame = 0;
    public static int maxAppliesPerFrame = 2;
    public static void ResetFrameApplyCounter() => appliesThisFrame = 0;

    #region Init

    public void Initialize(Vector2Int coord, int chunkRes, float unitSize, Material material, TerrainNoiseSettings ns, int lodLevel)
    {
        Coord = coord; // vec2int used for integer grid system
        LodLevel = lodLevel;
        PendingLod = -1; // doesn't need rebuild on init

        _mf = GetComponent<MeshFilter>(); // visual stuff
        _mr = GetComponent<MeshRenderer>();
        _mr.sharedMaterial = material;
        _mr.shadowCastingMode = ShadowCastingMode.On;
        _mr.receiveShadows = true;

        transform.position = ChunkOriginWorld(coord, chunkRes, unitSize); // place chunk in grid at coordinate
        name = $"Chunk [{coord.x},{coord.y}] lod{lodLevel}";

        int lodFactor = 1 << lodLevel; // faster comp of Mathf.Pow(2, lodLevel)
        int cs = Mathf.Max(2, chunkRes / lodFactor); // compute chunk size after LOD & clamp
        float stepWorld = unitSize; // fixed world spacing, independent of LOD (fixes border artifacts)
        float ox = coord.x * chunkRes * unitSize; // gets world pos from grid coord
        float oz = coord.y * chunkRes * unitSize;

        Task.Run(() => // background threads are used to create all the vertices / tris / normals / colours / heights from noise etc
        {
            Build(out _verts, out _normals, out _tris, out _uvs, out _colors,
                  ox, oz, chunkRes, cs, lodFactor, stepWorld, ns);
            _dataReady = true;
        });
    }

    // keeps old mesh visible until the new lod mesh is ready, then swaps atomically
    public void Rebuild(int newLod, int chunkRes, float unitSize, TerrainNoiseSettings ns)
    {
        if (PendingLod == newLod) return;
        PendingLod = newLod;
        _rebuildReady = false;

        int lodFactor = 1 << newLod;
        int cs = Mathf.Max(2, chunkRes / lodFactor);
        float stepWorld = unitSize;
        float ox = Coord.x * chunkRes * unitSize;
        float oz = Coord.y * chunkRes * unitSize;

        Task.Run(() =>
        {
            Build(out _newVerts, out _newNormals, out _newTris, out _newUvs, out _newColors,
                  ox, oz, chunkRes, cs, lodFactor, stepWorld, ns);
            _rebuildReady = true;
        });
    }

    #endregion Init

    #region Build

    // border verts are now sampled exactly like interior vertices (no snapping)
    // normals are computed using world-space sampling to ensure consistency across chunk borders
    static void Build(out Vector3[] outV, out Vector3[] outN, out int[] outT, out Vector2[] outU, out Color[] outC,
                      float ox, float oz, int chunkRes, int cs, int lodFactor, float stepWorld, TerrainNoiseSettings ns)
    {
        int fullRes = chunkRes + 1; // mesh & array setup
        float[] heights = new float[fullRes * fullRes];
        
        for (int z = 0; z <= chunkRes; z++) // loop over every vertex in the chunk (x/z)
        {
            float wz = oz + z * stepWorld;
            for (int x = 0; x <= chunkRes; x++)
            {
                float wx = ox + x * stepWorld;
                var (h, _, _) = BiomeManager.SampleAll(wx, wz, ns);
                heights[z * fullRes + x] = h;
            }
        }

        int vc = cs + 1; // vertices per side after LOD downsampling
        outV = new Vector3[vc * vc];
        outN = new Vector3[vc * vc];
        outU = new Vector2[vc * vc];
        outC = new Color[vc * vc];
        outT = new int[cs * cs * 6];

        float invCs = 1f / cs; // for normalization
        float maxH = ns.heightMultiplier;

        for (int z = 0; z < vc; z++)
        {
            int iz = z * lodFactor;
            float wz = oz + iz * stepWorld;
            for (int x = 0; x < vc; x++)
            {
                int ix = x * lodFactor;
                float wx = ox + ix * stepWorld;
                int i = z * vc + x;

                float h = heights[iz * fullRes + ix];
                outV[i] = new Vector3(x * lodFactor * stepWorld, h, z * lodFactor * stepWorld);
                outU[i] = new Vector2(x * invCs, z * invCs); // map UVs

                float biomeVal = BiomeManager.GetBiomeValue(wx, wz, ns);
                float flatStrength = BiomeManager.GetFlatZoneStrength(wx, wz, ns);
                outC[i] = BiomeManager.GetBlendedColor(h, maxH, biomeVal, flatStrength);
            }
        }

        float eps = stepWorld * lodFactor * 0.5f; // half the vertex spacing
        for (int z = 0; z < vc; z++)
        {
            int iz = z * lodFactor;
            for (int x = 0; x < vc; x++)
            {
                int ix = x * lodFactor;
                float wx = ox + ix * stepWorld;
                float wz = oz + iz * stepWorld;
                int i = z * vc + x;

                float l = BiomeManager.SampleHeight(wx - eps, wz, ns);
                float r = BiomeManager.SampleHeight(wx + eps, wz, ns);
                float d = BiomeManager.SampleHeight(wx, wz - eps, ns);
                float u = BiomeManager.SampleHeight(wx, wz + eps, ns);

                Vector3 normal = new Vector3(l - r, 2f * eps, d - u).normalized;
                outN[i] = normal;
            }
        }

        int ti = 0;
        for (int z = 0; z < cs; z++) // loops over cells this time
        {
            for (int x = 0; x < cs; x++)
            {
                int a = z * vc + x; // gets all the 'quad' points  ( C --- D )
                int b = z * vc + x + 1; //                         ( |     | )
                int c = (z + 1) * vc + x; //                       ( A --- B )
                int d = (z + 1) * vc + x + 1;
                outT[ti++] = a; outT[ti++] = c; outT[ti++] = b; // splits the quad into two tris
                outT[ti++] = b; outT[ti++] = c; outT[ti++] = d;
            }
        }
    }

    #endregion Build

    #region Lifecycle

    private void Update()
    {
        if (appliesThisFrame >= maxAppliesPerFrame) return; // capping mesh renders so pc doesn't explode

        if (_dataReady && !IsReady) // if background thread is finished
        {
            appliesThisFrame++;
            ApplyMesh(_verts, _normals, _tris, _uvs, _colors);
            IsReady = true;
            return;
        }

        if (_rebuildReady && PendingLod >= 0)
        {
            appliesThisFrame++;
            _verts = _newVerts; _normals = _newNormals; _tris = _newTris; _uvs = _newUvs; _colors = _newColors; // replacing vars
            _newVerts = null; _newNormals = null; _newTris = null; _newUvs = null; _newColors = null;
            LodLevel = PendingLod; // change LOD
            PendingLod = -1;
            _rebuildReady = false;
            ApplyMesh(_verts, _normals, _tris, _uvs, _colors);
        }
    }

    private void OnDestroy()
    {
        if (_mf != null && _mf.sharedMesh != null) Destroy(_mf.sharedMesh);
    }

    #endregion Lifecycle

    #region Mesh / Collider

    void ApplyMesh(Vector3[] v, Vector3[] n, int[] t, Vector2[] u, Color[] c)
    {
        var mesh = _mf.sharedMesh != null ? _mf.sharedMesh : new Mesh { name = $"ChunkMesh [{Coord.x},{Coord.y}]" }; // use existing mesh or make new
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.Clear();
        mesh.SetVertices(v); mesh.SetNormals(n);
        mesh.SetTriangles(t, 0);
        mesh.SetUVs(0, u); mesh.SetColors(c);
        mesh.RecalculateBounds();
        _mf.sharedMesh = mesh;
    }

    public void AddCollider()
    {
        if (!IsReady || GetComponent<MeshCollider>() != null) return;
        gameObject.AddComponent<MeshCollider>().sharedMesh = _mf.sharedMesh;
    }

    public void RemoveCollider()
    {
        var mc = GetComponent<MeshCollider>();
        if (mc != null) Destroy(mc);
    }

    public void RefreshCollider()
    {
        var mc = GetComponent<MeshCollider>();
        if (mc != null) mc.sharedMesh = _mf.sharedMesh;
    }

    public static Vector3 ChunkOriginWorld(Vector2Int coord, int chunkRes, float unitSize) =>
        new Vector3(coord.x * chunkRes * unitSize, 0f, coord.y * chunkRes * unitSize);

    #endregion Mesh / Collider
}