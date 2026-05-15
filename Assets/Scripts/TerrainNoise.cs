using UnityEngine;

/// <summary>
/// i don't want to comment most of this script because i hate it
/// </summary>

public static class TerrainNoise
{
    public static float Sample(float wx, float wz, TerrainNoiseSettings s) => BiomeManager.SampleHeight(wx, wz, s);
}

[System.Serializable]
public class TerrainNoiseSettings
{
    public float heightMultiplier = 260f;
    public float baseScale = 0.0007f;
    public int octaves = 6;
    public float persistence = 0.50f;
    public float lacunarity = 2.1f;
    public Vector2 worldOffset = Vector2.zero;
    public int seed = 0;

    [Header("biome")]
    public float biomeScale = 0.00025f;
    [Range(1f, 4f)] public float biomeBlendSharpness = 1f;

    [Header("mountain anchors")]
    public float mountainGridSize = 6000f;
    [Range(0.2f, 0.8f)] public float mountainPullRadius = 0.55f;
    [Range(0f, 1f)] public float mountainAnchorStrength = 0.85f;

    [Header("flat zone")]
    public float flatZoneScale = 0.0008f;
    [Range(0f, 1f)] public float flatZoneFrequency = 0.38f;
}

/// <summary>
/// per-biome noise parameters. useBillow = abs(fbm) — always >= 0, creates ridge shapes
/// </summary>

public struct BiomeNoiseParams
{
    public float heightScale;
    public float frequency;
    public float persistence;
    public float lacunarity;
    public int octaves;
    public bool useBillow;
}

/// <summary>
/// stateless thread-safe biome sampling and vertex colour helpers, chunks use this when building
/// </summary>

public static class BiomeManager
{
    static readonly BiomeNoiseParams FlatPlains = new() { heightScale = 0.02f, frequency = 0.5f, persistence = 0.28f, lacunarity = 1.8f, octaves = 2, useBillow = false };
    static readonly BiomeNoiseParams Plains = new() { heightScale = 0.12f, frequency = 0.6f, persistence = 0.32f, lacunarity = 1.9f, octaves = 3, useBillow = false };
    static readonly BiomeNoiseParams Hills = new() { heightScale = 0.50f, frequency = 0.9f, persistence = 0.28f, lacunarity = 2.0f, octaves = 4, useBillow = false };
    static readonly BiomeNoiseParams Mountains = new() { heightScale = 5.00f, frequency = 1.4f, persistence = 0.52f, lacunarity = 2.05f, octaves = 7, useBillow = true };

    const float COLOR_MAX_SCALE = 3.6f; // billow peak ~0.70 * 5.0 = 3.5, slight headroom for snow

    #region Sampling [public]

    // single call per vertex in the mesh build loop avoids redundant biome noise evaluations
    public static (float height, float biomeVal, float flatStrength) SampleAll(float wx, float wz, TerrainNoiseSettings s)
    {
        float biomeVal = SampleBiomeValue(wx, wz, s);
        float flatStrength = SampleFlatStrength(wx, wz, s); // always sampled, no threshold
        float height = BlendHeight(wx, wz, s, biomeVal, flatStrength);
        return (height, biomeVal, flatStrength);
    }

    public static float SampleHeight(float wx, float wz, TerrainNoiseSettings s)
    {
        float biomeVal = SampleBiomeValue(wx, wz, s);
        float flatStrength = SampleFlatStrength(wx, wz, s);
        return BlendHeight(wx, wz, s, biomeVal, flatStrength);
    }

    public static float GetBiomeValue(float wx, float wz, TerrainNoiseSettings s) => SampleBiomeValue(wx, wz, s);
    public static float GetFlatZoneStrength(float wx, float wz, TerrainNoiseSettings s) => SampleFlatStrength(wx, wz, s);
    public static bool IsBuildableZone(float wx, float wz, TerrainNoiseSettings s) => SampleBiomeValue(wx, wz, s) < 0.30f && SampleFlatStrength(wx, wz, s) > 0.6f;
    
    public static Vector3 ComputeNormal(float wx, float wz, TerrainNoiseSettings s, float epsilon) // debugs
    {
        float l = SampleHeight(wx - epsilon, wz, s);
        float r = SampleHeight(wx + epsilon, wz, s);
        float d = SampleHeight(wx, wz - epsilon, s);
        float u = SampleHeight(wx, wz + epsilon, s);
        return new Vector3(l - r, 2f * epsilon, d - u).normalized;
    }

    #endregion Sampling [public]

    #region Sampling [internal]

    static float SampleBiomeValue(float wx, float wz, TerrainNoiseSettings s) // perlin with some clamping
    {
        float ox = s.seed * 1291.37f + s.worldOffset.x;
        float oz = s.seed * 2371.63f + s.worldOffset.y;
        float a = Mathf.PerlinNoise((wx + ox) * s.biomeScale, (wz + oz) * s.biomeScale);
        float b = Mathf.PerlinNoise((wx + ox) * s.biomeScale * 2.7f, (wz + oz) * s.biomeScale * 2.7f);
        float noise = Mathf.Pow(Mathf.Clamp01(a * 0.75f + b * 0.25f), s.biomeBlendSharpness);
        float anchor = MountainAnchorBias(wx, wz, s);
        return Mathf.Clamp01(noise + anchor * s.mountainAnchorStrength);
    }

    static float MountainAnchorBias(float wx, float wz, TerrainNoiseSettings s) // mountains go up more
    {
        float gs = s.mountainGridSize;
        int cx = Mathf.FloorToInt(wx / gs);
        int cz = Mathf.FloorToInt(wz / gs);
        float best = 0f;
        float r = gs * s.mountainPullRadius;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int gx = cx + dx; int gz = cz + dz;
            float hx = Frac(Mathf.Sin(gx * 127.1f + gz * 311.7f + s.seed) * 43758.5f);
            float hz = Frac(Mathf.Sin(gx * 269.5f + gz * 183.3f + s.seed) * 43758.5f);
            float mcx = (gx + hx) * gs; float mcz = (gz + hz) * gs;
            float dist = Mathf.Sqrt((wx - mcx) * (wx - mcx) + (wz - mcz) * (wz - mcz));
            float t = Mathf.Clamp01(1f - dist / r);
            best = Mathf.Max(best, t * t * (3f - 2f * t));
        }
        return best;
    }

    static float SampleFlatStrength(float wx, float wz, TerrainNoiseSettings s)
    {
        float ox = s.seed * 4421.11f + s.worldOffset.x;
        float oz = s.seed * 3317.83f + s.worldOffset.y;
        float n = Mathf.PerlinNoise((wx + ox) * s.flatZoneScale, (wz + oz) * s.flatZoneScale);
        float t = (n - (1f - s.flatZoneFrequency)) * 3f; // gentler multiplier
        float clamped = Mathf.Clamp01(t);
        return clamped * clamped * (3f - 2f * clamped); // smoothstep
    }

    static float BlendHeight(float wx, float wz, TerrainNoiseSettings s, float biomeVal, float flatStrength)
    {
        float hFlat = SampleBiome(wx, wz, s, FlatPlains);
        float hPlains = SampleBiome(wx, wz, s, Plains);
        float hHills = SampleBiome(wx, wz, s, Hills);
        float hMounts = SampleBiome(wx, wz, s, Mountains);

        float blended;
        if (biomeVal < 0.15f) blended = Mathf.Lerp(hFlat, hPlains, Mathf.SmoothStep(0f, 1f, biomeVal / 0.15f));
        else if (biomeVal < 0.45f) blended = Mathf.Lerp(hPlains, hHills, Mathf.SmoothStep(0f, 1f, (biomeVal - 0.15f) / 0.30f));
        else blended = Mathf.Lerp(hHills, hMounts, Mathf.SmoothStep(0f, 1f, (biomeVal - 0.45f) / 0.55f));

        if (biomeVal > 0.45f)
        {
            float t = Mathf.SmoothStep(0f, 1f, (biomeVal - 0.45f) / 0.55f);
            blended = Mathf.Max(blended, s.heightMultiplier * 0.06f * t);
        }

        float flatInfluence = flatStrength * (1f - Mathf.SmoothStep(0.3f, 0.6f, biomeVal));
        float flatTarget = Mathf.Max(hFlat * 0.3f, s.heightMultiplier * 0.02f);
        float result = Mathf.Lerp(blended, flatTarget, flatInfluence);
        return Mathf.Max(result, s.heightMultiplier * 0.01f);
    }

    static float SampleBiome(float wx, float wz, TerrainNoiseSettings s, in BiomeNoiseParams p)
    {
        float ox = s.seed * 9431.73f + s.worldOffset.x;
        float oz = s.seed * 7213.31f + s.worldOffset.y;
        float freq = s.baseScale * p.frequency;
        float amp = 1f, sum = 0f, max = 0f;
        for (int i = 0; i < p.octaves; i++)
        {
            float raw = Mathf.PerlinNoise((wx + ox) * freq, (wz + oz) * freq) * 2f - 1f;
            float n = p.useBillow ? Mathf.Abs(raw) : raw;
            sum += n * amp; max += amp;
            amp *= p.persistence; freq *= p.lacunarity;
        }
        float result = (sum / max) * s.heightMultiplier * p.heightScale;
        // non-billow (valley) noise should not go deeply negative to avoid cliffs
        return p.useBillow ? result : Mathf.Max(0f, result);
    }

    static float Frac(float x) => x - Mathf.Floor(x);

    #endregion Sampling [internal]

    #region Colours

    public static Color GetBlendedColor(float height, float maxH, float biomeVal, float flatStrength)
    {
        float t = Mathf.SmoothStep(0f, 1.2f, height / (maxH * COLOR_MAX_SCALE));
        t = Mathf.Clamp01(t);

        Color GetFlat(float ht) => Color.Lerp(new Color(0.45f, 0.42f, 0.28f), new Color(0.50f, 0.68f, 0.30f), ht / 0.6f);
        
        // big multiple colour gradients
        Color GetPlains(float ht) => ht < 0.08f ? Color.Lerp(new Color(0.28f, 0.24f, 0.14f), new Color(0.40f, 0.36f, 0.20f), ht / 0.08f)
           : ht < 0.20f ? Color.Lerp(new Color(0.40f, 0.36f, 0.20f), new Color(0.32f, 0.60f, 0.20f), (ht - 0.08f) / 0.12f)
           : Color.Lerp(new Color(0.32f, 0.60f, 0.20f), new Color(0.50f, 0.64f, 0.36f), (ht - 0.20f) / 0.80f);
        
        Color GetHills(float ht) =>
            ht < 0.14f ? Color.Lerp(new Color(0.28f, 0.38f, 0.14f), new Color(0.20f, 0.54f, 0.16f), ht / 0.14f)
           : ht < 0.30f ? Color.Lerp(new Color(0.20f, 0.54f, 0.16f), new Color(0.48f, 0.50f, 0.30f), (ht - 0.14f) / 0.16f)
           : Color.Lerp(new Color(0.48f, 0.50f, 0.30f), new Color(0.62f, 0.62f, 0.58f), (ht - 0.30f) / 0.70f);
        
        Color GetMountains(float ht) =>
            ht < 0.10f ? Color.Lerp(new Color(0.36f, 0.30f, 0.20f), new Color(0.46f, 0.44f, 0.36f), ht / 0.10f)
           : ht < 0.35f ? Color.Lerp(new Color(0.46f, 0.44f, 0.36f), new Color(0.58f, 0.58f, 0.56f), (ht - 0.10f) / 0.25f)
           : ht < 0.65f ? Color.Lerp(new Color(0.58f, 0.58f, 0.56f), new Color(0.82f, 0.84f, 0.88f), (ht - 0.35f) / 0.30f)
           : Color.Lerp(new Color(0.82f, 0.84f, 0.88f), Color.white, (ht - 0.65f) / 0.35f);

        Color biomeColor;
        if (biomeVal < 0.15f) biomeColor = Color.Lerp(GetFlat(t), GetPlains(t), Mathf.SmoothStep(0f, 1f, biomeVal / 0.15f));
        else if (biomeVal < 0.45f) biomeColor = Color.Lerp(GetPlains(t), GetHills(t), Mathf.SmoothStep(0f, 1f, (biomeVal - 0.15f) / 0.30f));
        else biomeColor = Color.Lerp(GetHills(t), GetMountains(t), Mathf.SmoothStep(0f, 1f, (biomeVal - 0.45f) / 0.55f));

        // final blend with flat zone; using smoothstep to avoid harsh transitions
        float flatBlend = Mathf.SmoothStep(0f, 0.6f, flatStrength);
        return Color.Lerp(biomeColor, Color.Lerp(GetFlat(t), new Color(0.72f, 0.70f, 0.42f), 0.4f), flatBlend);
    }

    #endregion Colours
}