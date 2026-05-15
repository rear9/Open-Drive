using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// rolling-average FPS counter (reused from st4rdog 'https://gist.github.com/st4rdog/80057b406bfd00f44c8ec8796a071a13')
/// </summary>

public class FPSCounter : MonoBehaviour
{
    public enum DeltaTimeType { Smooth, Unscaled }

    [SerializeField] private Text _text;
    [SerializeField] private DeltaTimeType _deltaType = DeltaTimeType.Smooth;

    private Dictionary<int, string> _cachedStrings = new();
    private int[] _samples;
    private int _cacheLimit = 3000;
    private int _sampleCount = 30;
    private int _counter;
    private int _currentAverage;

    private void Awake()
    {
        for (int i = 0; i < _cacheLimit; i++) _cachedStrings[i] = i.ToString();
        _samples = new int[_sampleCount];
    }

    private void Update()
    {
        float dt = _deltaType switch
        {
            DeltaTimeType.Smooth => Time.smoothDeltaTime,
            DeltaTimeType.Unscaled => Time.unscaledDeltaTime,
            _ => Time.unscaledDeltaTime
        };
        _samples[_counter] = (int)Math.Round(1f / dt);

        float avg = 0f;
        foreach (var s in _samples) avg += s;
        _currentAverage = (int)Math.Round(avg / _sampleCount);
        _counter = (_counter + 1) % _sampleCount;

        _text.text = "FPS: " + _currentAverage switch
        {
            var x when x >= 0 && x < _cacheLimit => _cachedStrings[x],
            var x when x >= _cacheLimit => $"> {_cacheLimit}",
            var x when x < 0 => "< 0",
            _ => "?"
        };
    }
}