using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;

/// <summary>
/// binds inspector sliders to TerrainManager settings
/// </summary>

public class UIController : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private TerrainManager _terrain;

    [Header("Noise Sliders")]
    [SerializeField] private Slider _heightSlider; 
    [SerializeField] private TMP_Text _heightText;
    [SerializeField] private Slider _scaleSlider; 
    [SerializeField] private TMP_Text _scaleText;
    [SerializeField] private Slider _unitSlider; 
    [SerializeField] private TMP_Text _unitText;
    [SerializeField] private Slider _resSlider; 
    [SerializeField] private TMP_Text _resText;

    [Header("View Sliders")]
    [SerializeField] private Slider _frontSlider; 
    [SerializeField] private TMP_Text _frontText;
    [SerializeField] private Slider _sideSlider; 
    [SerializeField] private TMP_Text _sideText;
    [SerializeField] private Slider _backSlider; 
    [SerializeField] private TMP_Text _backText;
    [SerializeField] private Slider _minCpsSlider; 
    [SerializeField] private TMP_Text _minCpsText;
    [SerializeField] private Slider _maxCpsSlider; 
    [SerializeField] private TMP_Text _maxCpsText;
    
    private void Start()
    {
        _heightSlider.value = _terrain.noiseSettings.heightMultiplier;
        _scaleSlider.value = _terrain.noiseSettings.baseScale;
        _unitSlider.value = _terrain.unitSize;
        _resSlider.value = Mathf.RoundToInt(Mathf.Log(_terrain.chunkResolution, 2));
        _frontSlider.value = _terrain.viewFront;
        _sideSlider.value = _terrain.viewSide;
        _backSlider.value = _terrain.viewBack;
        if (_minCpsSlider) _minCpsSlider.value = _terrain.minChunksPerSec;
        if (_maxCpsSlider) _maxCpsSlider.value = _terrain.maxChunksPerSec;

        _heightSlider.onValueChanged.AddListener(SetHeight);
        _scaleSlider.onValueChanged.AddListener(SetScale);
        _unitSlider.onValueChanged.AddListener(SetUnitSize);
        _resSlider.onValueChanged.AddListener(SetResolution);
        _frontSlider.onValueChanged.AddListener(SetFront);
        _sideSlider.onValueChanged.AddListener(SetSide);
        _backSlider.onValueChanged.AddListener(SetBack);
        if (_minCpsSlider) _minCpsSlider.onValueChanged.AddListener(SetMinCps);
        if (_maxCpsSlider) _maxCpsSlider.onValueChanged.AddListener(SetMaxCps);

        RefreshAllText();
    }

    public void SetHeight(float v) { _terrain.noiseSettings.heightMultiplier = v; _heightText.text = $"Height: {v:F1}"; }
    public void SetScale(float v) { _terrain.noiseSettings.baseScale = v; _scaleText.text = $"Scale: {v:F5}"; }
    public void SetUnitSize(float v) { _terrain.unitSize = Mathf.RoundToInt(v); _unitText.text = $"Unit Size: {_terrain.unitSize}"; }
    public void SetFront(float v) { int val = Mathf.RoundToInt(v); _terrain.viewFront = val; _frontText.text = $"Front: {val}"; }
    public void SetSide(float v) { int val = Mathf.RoundToInt(v); _terrain.viewSide = val; _sideText.text = $"Side: {val}"; }
    public void SetBack(float v) { int val = Mathf.RoundToInt(v); _terrain.viewBack = val; _backText.text = $"Back: {val}"; }
    public void SetMinCps(float v) { _terrain.minChunksPerSec = v; if (_minCpsText) _minCpsText.text = $"Min CPS: {v:F1}"; }
    public void SetMaxCps(float v){ _terrain.maxChunksPerSec = v; if (_maxCpsText) _maxCpsText.text = $"Max CPS: {v:F1}"; }

    public void SetResolution(float v)
    {
        int res = 1 << Mathf.RoundToInt(v);
        _terrain.chunkResolution = res;
        _resText.text = $"Resolution: {res}";
    }

    public void ReloadScene() => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    public void RegenerateTerrain() => _terrain.RegenerateAllChunks();
    
    private void RefreshAllText()
    {
        SetHeight(_heightSlider.value); SetScale(_scaleSlider.value);
        SetUnitSize(_unitSlider.value); SetResolution(_resSlider.value);
        SetFront(_frontSlider.value); SetSide(_sideSlider.value); SetBack(_backSlider.value);
        if (_minCpsSlider) SetMinCps(_minCpsSlider.value);
        if (_maxCpsSlider) SetMaxCps(_maxCpsSlider.value);
    }
}