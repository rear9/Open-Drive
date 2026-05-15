using UnityEngine;

/// <summary>
/// player controller with terrain-height grounding; C to cycle camera modes
/// </summary>

public class SimPlayer : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float _moveSpeed = 18f;
    [SerializeField] private float _sprintMultiplier = 3.5f;
    [SerializeField] private float _moveSmoothTime = 0.10f;
    [SerializeField] private float _gravity = -28f;
    [SerializeField] private float _mouseSensitivity = 2f;

    [Header("Camera")]
    [SerializeField] private Camera _playerCamera;
    [SerializeField] private float _firstPersonHeight = 1.8f;
    [SerializeField] private float _camHeight = 90f;
    [SerializeField] private float _camBackDist = 70f;
    [SerializeField] private float _camLookAhead = 30f;
    [SerializeField] private float _camSmoothTime = 0.2f;
    [SerializeField] private float _overheadHeight = 200f;
    [SerializeField] private float _overheadOrthoSize = 120f;

    [Header("HUD")]
    public bool showDebugHUD = true;

    private enum CamMode { FirstPerson, ThirdPerson, Overhead }
    private CamMode _currentMode = CamMode.FirstPerson;

    private Vector3 _smoothAccel;
    private Vector3 _currentVelocity;
    private Vector3 _camVelocity;
    private Vector3 _lastMoveDir = Vector3.forward;
    private Vector3 _smoothedMoveDir = Vector3.forward;
    private Vector3 _moveDirVel;
    private float _pitch = 0f;
    private float _verticalVelocity = 0f;
    // noise snap is used while mesh colliders are still building on background threads; flips when cc.isGrounded fires
    private bool _useNoiseSnap = true;

    private CharacterController _cc;
    private TerrainManager _terrain;

    private const float MIN_MOVE_SQR = 0.01f;

    #region Init

    private void Awake() // get / set things
    {
        _terrain = FindFirstObjectByType<TerrainManager>();

        _cc = GetComponent<CharacterController>();
        if (_cc == null) _cc = gameObject.AddComponent<CharacterController>();
        _cc.height = 2.0f; _cc.radius = 0.4f; _cc.center = new Vector3(0f, 1.0f, 0f);
        _cc.stepOffset = 1000f;
        _cc.slopeLimit = 90f;
        _cc.skinWidth = 0.08f;

        if (_playerCamera == null) _playerCamera = Camera.main;
    }

    private void Start() // put player on terrain and start cam
    {
        if (_terrain != null)
        {
            float h = _terrain.GetHeightAt(transform.position.x, transform.position.z);
            transform.position = new Vector3(transform.position.x, h, transform.position.z);
        }
        ApplyCameraMode();
        SnapCamera();
    }

    private void Update() // movement prio
    {
        MoveAndApplyGravity();
        HandleMouseLook();
        UpdateCamera();
        HandleCameraToggle();
    }
    
    #endregion Init

    #region Movement

    private void MoveAndApplyGravity() // base movement w/ sprint (charactercontroller)
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        bool sprint = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        float speed = _moveSpeed * (sprint ? _sprintMultiplier : 1f);

        Vector3 input = new Vector3(h, 0f, v);
        if (input.sqrMagnitude > 1f) input.Normalize();
        Vector3 fwd = _playerCamera.transform.forward;
        Vector3 right = _playerCamera.transform.right;
        fwd.y = 0f; fwd.Normalize();
        right.y = 0f; right.Normalize();

        Vector3 moveDir = fwd * input.z + right * input.x;
        if (moveDir.sqrMagnitude > MIN_MOVE_SQR) _lastMoveDir = moveDir.normalized;
        _smoothedMoveDir = Vector3.SmoothDamp(_smoothedMoveDir, _lastMoveDir, ref _moveDirVel, 0.15f);

        Vector3 targetXZ = moveDir.sqrMagnitude > MIN_MOVE_SQR ? moveDir.normalized * speed : Vector3.zero;
        _currentVelocity = Vector3.SmoothDamp(_currentVelocity, targetXZ, ref _smoothAccel, _moveSmoothTime);

        if (_useNoiseSnap)
        {
            if (_cc.isGrounded)
            {
                _useNoiseSnap = false;
                _verticalVelocity = -2f;
            }
            else if (_terrain != null)
            {
                float groundH = _terrain.GetHeightAt(transform.position.x, transform.position.z);
                transform.position = new Vector3(transform.position.x,
                    Mathf.Lerp(transform.position.y, groundH, Time.deltaTime * 14f), transform.position.z);
                _verticalVelocity = 0f;
            }
        }
        else
        {
            if (_cc.isGrounded && _verticalVelocity < 0f) _verticalVelocity = -2f; // physics
            else _verticalVelocity += _gravity * Time.deltaTime;
        }

        _cc.Move(new Vector3(_currentVelocity.x, _verticalVelocity, _currentVelocity.z) * Time.deltaTime); // move player

        if (transform.position.y < -300f && _terrain != null) // fallback if went thru map
        {
            float h2 = _terrain.GetHeightAt(transform.position.x, transform.position.z);
            transform.position = new Vector3(transform.position.x, h2, transform.position.z);
            _verticalVelocity = 0f; _useNoiseSnap = true;
        }
    }

    #endregion Movement

    #region Camera

    private void HandleMouseLook()
    {
        if (_currentMode != CamMode.FirstPerson) return;
        transform.Rotate(Vector3.up, Input.GetAxis("Mouse X") * _mouseSensitivity); // rotate player based on mouse x
        _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * _mouseSensitivity, -80f, 80f);
        _playerCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f); // rotate camera based on mouse y
    }

    private void HandleCameraToggle()
    {
        if (!Input.GetKeyDown(KeyCode.C)) return; // 3-type toggle through enum
        _currentMode = (CamMode)(((int)_currentMode + 1) % 3);
        ApplyCameraMode();
    }

    private void ApplyCameraMode()
    {
        if (_currentMode == CamMode.FirstPerson) // setting values for each mode
        {
            _playerCamera.transform.SetParent(transform);
            _playerCamera.transform.localPosition = new Vector3(0f, _firstPersonHeight, 0f);
            _playerCamera.transform.localRotation = Quaternion.identity;
            _playerCamera.orthographic = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _pitch = 0f;
        }
        else
        {
            _playerCamera.transform.SetParent(null);
            _playerCamera.orthographic = _currentMode == CamMode.Overhead;
            if (_currentMode == CamMode.Overhead) _playerCamera.orthographicSize = _overheadOrthoSize;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        SnapCamera();
    }

    private void UpdateCamera()
    {
        if (_currentMode == CamMode.FirstPerson) return;
        Vector3 lookDir = _smoothedMoveDir.magnitude > 0.1f ? _smoothedMoveDir : _lastMoveDir; // smooths camera
        
        Vector3 targetPos;
        if (_currentMode == CamMode.Overhead)
        {
            targetPos = transform.position + Vector3.up * _overheadHeight;
            _playerCamera.transform.position = Vector3.SmoothDamp(_playerCamera.transform.position, targetPos, ref _camVelocity, _camSmoothTime);
            _playerCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
        else // third person
        {
            targetPos = transform.position + Vector3.up * _camHeight + (-lookDir) * _camBackDist;
            _playerCamera.transform.position = Vector3.SmoothDamp(_playerCamera.transform.position, targetPos, ref _camVelocity, _camSmoothTime);
            _playerCamera.transform.LookAt(transform.position + lookDir * _camLookAhead + Vector3.up * 5f);
        }
    }

    private void SnapCamera()
    {
        if (_currentMode == CamMode.FirstPerson) return;
        _playerCamera.transform.position = transform.position + Vector3.up * _camHeight - _lastMoveDir * _camBackDist;
        _playerCamera.transform.LookAt(transform.position + Vector3.up * 5f);
        _camVelocity = Vector3.zero;
    }

    #endregion Camera

    #region Debug

    private void OnGUI()
    {
        if (!showDebugHUD) return;
        float spd = new Vector2(_currentVelocity.x, _currentVelocity.z).magnitude;
        var sb = new System.Text.StringBuilder(); // stringbuilder is more efficient here
        sb.AppendLine($"<b>Speed:</b> {spd:F1} m/s");
        sb.AppendLine($"<b>Pos:</b> {transform.position.x:F0}, {transform.position.y:F1}, {transform.position.z:F0}");
        sb.AppendLine($"<b>Mode:</b> {_currentMode}");
        sb.AppendLine($"<b>Ground:</b> {(_cc.isGrounded ? "collider" : _useNoiseSnap ? "noise snap" : "air")}");
        if (_terrain != null) sb.AppendLine($"<b>Chunks:</b> {_terrain.ActiveChunkCount} active {_terrain.QueuedChunkCount} queued");
        sb.AppendLine("\n[C] cycle camera / [shift] sprint");
        var style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, alignment = TextAnchor.UpperLeft };
        style.normal.textColor = Color.white;
        GUI.color = Color.black;
        GUI.Label(new Rect(12f, 12f, 340f, 240f), sb.ToString(), style);
        GUI.color = Color.white;
        GUI.Label(new Rect(10f, 10f, 340f, 240f), sb.ToString(), style);
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawSphere(transform.position + Vector3.up * 1.5f, 0.5f);
        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(transform.position + Vector3.up * 2f, _lastMoveDir * 8f);
        Gizmos.color = Color.green;
        Gizmos.DrawRay(transform.position + Vector3.up * 2.5f, _smoothedMoveDir * 8f);
    }

    #endregion Debug
}