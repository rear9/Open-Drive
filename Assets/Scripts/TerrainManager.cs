using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// owns full chunk lifecycle; streaming, LOD, colliders
/// LOD changes use in-place rebuilds so there's no visible pop
/// </summary>

public class TerrainManager : MonoBehaviour
{
    [Header("Scene Refs")]
    [SerializeField] private Transform _player;

    [Header("Shape")]
    public int chunkResolution = 64;
    public float unitSize = 2f;

    [Header("LOD (dist away in chunks)")]
    public int lod0Distance = 5;
    public int lod1Distance = 9;

    [Header("View Dist (chunks)")]
    public int viewFront = 14;
    public int viewSide = 10;
    public int viewBack = 6;

    [Header("Coll")]
    public int colliderRadius = 2;

    [Header("Rate")]
    public float minChunksPerSec = 1f;
    public float maxChunksPerSec = 16f;

    [Header("Noise")]
    public TerrainNoiseSettings noiseSettings = new();

    
    private readonly Dictionary<Vector2Int, TerrainChunk> _active = new();
    private readonly Dictionary<Vector2Int, int> _lodMap = new();
    private readonly HashSet<Vector2Int> _withCollider = new();
    private readonly HashSet<Vector2Int> _pending = new();
    private readonly List<Vector2Int> _toRemove = new();
    private readonly List<Vector2Int> _missing = new();

    private Vector2Int _playerChunk;
    private Vector2 _travelDir = Vector2.up;
    private Vector3 _prevPlayerPos;
    private float _spawnBudget = 0f;

    public int ActiveChunkCount => _active.Count;
    public int QueuedChunkCount => _pending.Count;
    public float ChunkWorldSize => chunkResolution * unitSize;
    public Transform player => _player;
    public Material terrainMat;
    public float GetHeightAt(float wx, float wz) => TerrainNoise.Sample(wx, wz, noiseSettings);

    #region Init

    private void Start()
    {
        if (_player == null) { var sp = FindFirstObjectByType<SimPlayer>(); if (sp != null) _player = sp.transform; }
        if (_player == null) { Debug.LogError("[TerrainManager] no player found"); enabled = false; return; }
        _prevPlayerPos = _player.position;
        _playerChunk = WorldToChunk(_player.position);
        ScheduleRefresh();
    }

    private void Update()
    {
        TerrainChunk.ResetFrameApplyCounter();

        Vector3 delta = _player.position - _prevPlayerPos;
        float xzSqr = delta.x * delta.x + delta.z * delta.z;
        if (xzSqr > 0.04f) _travelDir = new Vector2(delta.x, delta.z).normalized; // used to compute forward chunks
        _prevPlayerPos = _player.position;

        Vector2Int current = WorldToChunk(_player.position); // updating playerchunk
        if (current != _playerChunk)
        {
            _playerChunk = current;
            ScheduleRefresh();
        }

        float cps = GetDynamicChunksPerSec();
        _spawnBudget += cps * Time.deltaTime; // (60cps = avg 1 chunk per frame)
        int toSpawn = Mathf.FloorToInt(_spawnBudget);
        _spawnBudget -= toSpawn;
        TerrainChunk.maxAppliesPerFrame = Mathf.Max(2, toSpawn);

        while (toSpawn > 0 && _pending.Count > 0) // gets the highest priority chunk and spawns/loads it
        {
            Vector2Int best = GetHighestPriorityPending();
            _pending.Remove(best);
            if (!_active.ContainsKey(best)) { SpawnChunk(best); toSpawn--; }
        }

        SyncLodAndColliders();
    }
    
    #endregion Init

    #region Chunk Sched

    private void ScheduleRefresh()
    {
        var required = ComputeRequiredChunks(); // gets all the chunks

        _toRemove.Clear();
        foreach (var coord in _active.Keys)
        {
            if (!required.Contains(coord)) { _toRemove.Add(coord); continue; }
            if (_lodMap.TryGetValue(coord, out int committed) && ShouldChangeLod(coord, committed)) // if the lod should change, rebuild the chunk
            {
                int desired = RawGetLod(coord);
                if (_active[coord].PendingLod != desired) _active[coord].Rebuild(desired, chunkResolution, unitSize, noiseSettings);
            }
        }
        foreach (var coord in _toRemove)
        {
            _active[coord].RemoveCollider();
            Destroy(_active[coord].gameObject);
            _active.Remove(coord); _lodMap.Remove(coord); _withCollider.Remove(coord);
        }

        _missing.Clear();
        foreach (var coord in required)
            if (!_active.ContainsKey(coord) && !_pending.Contains(coord)) _missing.Add(coord);

        _missing.Sort((a, b) =>
        {
            int la = RawGetLod(a); int lb = RawGetLod(b);
            if (la != lb) return la.CompareTo(lb);
            return ChebyshevDist(a, _playerChunk).CompareTo(ChebyshevDist(b, _playerChunk));
        });

        foreach (var coord in _missing) _pending.Add(coord);
    }

    // upgrade is always immediate; downgrade requires being 1 chunk past threshold
    private bool ShouldChangeLod(Vector2Int coord, int committed)
    {
        int desired = RawGetLod(coord);
        if (desired == committed) return false;
        if (desired < committed) return true;
        int threshold = desired == 1 ? lod0Distance : lod1Distance;
        return ChebyshevDist(coord, _playerChunk) > threshold + 1;
    }

    private Vector2Int GetHighestPriorityPending() // find unrendered chunk with highest rendering priority (usually closest)
    {
        Vector2Int best = default;
        int bestLod = int.MaxValue, bestDist = int.MaxValue;
        foreach (var coord in _pending)
        {
            int lod = RawGetLod(coord); int dist = ChebyshevDist(coord, _playerChunk);
            if (lod < bestLod || (lod == bestLod && dist < bestDist)) { bestLod = lod; bestDist = dist; best = coord; }
        }
        return best;
    }

    #endregion Chunk Sched

    #region Colliders

    // single pass per frame; detects completed in-place lod swaps, updates lodMap and colliders
    private void SyncLodAndColliders()
    {
        foreach (var kvp in _active)
        {
            var chunk = kvp.Value;
            if (_lodMap.TryGetValue(kvp.Key, out int recorded) && recorded != chunk.LodLevel)
            {
                _lodMap[kvp.Key] = chunk.LodLevel;
                if (_withCollider.Contains(kvp.Key)) chunk.RefreshCollider();
            }
            if (!chunk.IsReady) continue; // run for unrendered chunks
            // if the chunk needs a collider and doesn't have one, add it (else remove it)
            if (NeedsCollider(kvp.Key) && !_withCollider.Contains(kvp.Key)) { chunk.AddCollider(); _withCollider.Add(kvp.Key); }
            else if (!NeedsCollider(kvp.Key) && _withCollider.Contains(kvp.Key)) { chunk.RemoveCollider(); _withCollider.Remove(kvp.Key); }
        }
    }

    private bool NeedsCollider(Vector2Int coord) => _lodMap.TryGetValue(coord, out int lod) && lod == 0 && ChebyshevDist(coord, _playerChunk) <= colliderRadius;

    #endregion Colliders

    #region Util

    private float GetDynamicChunksPerSec() // uses player position to judge how fast to load the world (closer to edge = faster)
    {
        float cw = ChunkWorldSize;
        Vector3 orig = new Vector3(_playerChunk.x * cw, 0f, _playerChunk.y * cw);
        float fx = Mathf.Abs((_player.position.x - orig.x) / cw - 0.5f) * 2f;
        float fz = Mathf.Abs((_player.position.z - orig.z) / cw - 0.5f) * 2f;
        float edge = Mathf.Clamp01(Mathf.Max(fx, fz));
        return Mathf.Lerp(minChunksPerSec, maxChunksPerSec, edge * edge);
    }

    private HashSet<Vector2Int> ComputeRequiredChunks()
    {
        var result = new HashSet<Vector2Int>();
        Vector2 fwd = _travelDir.sqrMagnitude > 0.01f ? _travelDir.normalized : Vector2.up; // determines forward direction
        Vector2 right = new Vector2(fwd.y, -fwd.x); // makes right vector
        int maxR = Mathf.Max(viewFront, viewSide, viewBack); // finds highest value so it can define search bounds (square grid to loop through)
        for (int dz = -maxR; dz <= maxR; dz++) // said loop
        for (int dx = -maxR; dx <= maxR; dx++)
        {
            var offset = new Vector2(dx, dz); // vector from player chunk to looped chunk
            float fDot = Vector2.Dot(offset, fwd); // project into forward space
            float sDot = Mathf.Abs(Vector2.Dot(offset, right)); // right space
            bool inside = fDot >= 0f ? fDot <= viewFront && sDot <= viewSide : -fDot <= viewBack && sDot <= viewSide; // checks if chunk is inside the viewcone of the player first (directional loading)
            if (inside) result.Add(_playerChunk + new Vector2Int(dx, dz)); // adds valid chunks
        }
        return result; // return a bunch of chunk coords
    }

    private int RawGetLod(Vector2Int coord)
    {
        int d = ChebyshevDist(coord, _playerChunk); // lod setting for each chunk based on which chunk the player is on
        if (d <= lod0Distance) return 0;
        if (d <= lod1Distance) return 1;
        return 2;
    }

    private void SpawnChunk(Vector2Int coord) // spawn func
    {
        int lod = RawGetLod(coord);
        var go = new GameObject();
        go.transform.SetParent(transform, worldPositionStays: false);
        var chunk = go.AddComponent<TerrainChunk>();
        chunk.Initialize(coord, chunkResolution, unitSize, terrainMat, noiseSettings, lod);
        _active[coord] = chunk;
        _lodMap[coord] = lod;
    }

    private Vector2Int WorldToChunk(Vector3 pos) // used to find which chunk the player is on
    {
        float w = ChunkWorldSize;
        return new Vector2Int(Mathf.FloorToInt(pos.x / w), Mathf.FloorToInt(pos.z / w));
    }

    private static int ChebyshevDist(Vector2Int a, Vector2Int b) => // https://en.wikipedia.org/wiki/Chebyshev_distance
        Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));

    public void RegenerateAllChunks() // for regen button
    {
        foreach (var chunk in _active.Values) Destroy(chunk.gameObject);
        _active.Clear(); _lodMap.Clear(); _withCollider.Clear();
        _pending.Clear(); _spawnBudget = 0f;
        ScheduleRefresh();
    }

    public void SetTerrainMaterial(Material mat)
    {
        if (mat == null) return;
        terrainMat = mat;
        foreach (var chunk in _active.Values)
        {
            var mr = chunk.GetComponent<MeshRenderer>();
            if (mr) mr.sharedMaterial = mat;
        }
    }

    #endregion Util

    #region Gizmos

    private void OnDrawGizmos()
    {
        if (_player == null) return;
        float cw = ChunkWorldSize;
        Color[] fill = { new Color(0.2f,1f,0.3f,0.07f), new Color(1f,0.85f,0.1f,0.07f), new Color(1f,0.5f,0.1f,0.07f) };
        Color[] wire = { new Color(0.2f,1f,0.3f,0.35f), new Color(1f,0.85f,0.1f,0.35f), new Color(1f,0.5f,0.1f,0.35f) };
        foreach (var kvp in _active)
        {
            int lod = _lodMap.TryGetValue(kvp.Key, out int l) ? l : 0;
            var c = new Vector3((kvp.Key.x + 0.5f) * cw, 0f, (kvp.Key.y + 0.5f) * cw);
            Gizmos.color = fill[lod]; Gizmos.DrawCube(c, new Vector3(cw, 0.5f, cw));
            Gizmos.color = wire[lod]; Gizmos.DrawWireCube(c, new Vector3(cw, 1f, cw));
        }
        Gizmos.color = new Color(0.5f, 0.5f, 1f, 0.4f);
        foreach (var coord in _pending)
        {
            var c = new Vector3((coord.x + 0.5f) * cw, 2f, (coord.y + 0.5f) * cw);
            Gizmos.DrawWireCube(c, new Vector3(cw * 0.8f, 0.5f, cw * 0.8f));
        }
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(_player.position + Vector3.up * 2f, new Vector3(_travelDir.x, 0f, _travelDir.y) * cw * 1.5f);
    }
    
    #endregion Gizmos
}