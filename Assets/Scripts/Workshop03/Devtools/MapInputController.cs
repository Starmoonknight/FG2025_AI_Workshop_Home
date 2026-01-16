using AI_Workshop02;
using UnityEngine;
using UnityEngine.InputSystem;


namespace AI_Workshop03
{

    public sealed class MapInputController : MonoBehaviour
    {
        [SerializeField] private MapManager _mapManager;
        [SerializeField] private Renderer _groundRenderer; 
        [SerializeField] private SteeringAgent _agent;

        [SerializeField] private Transform _goalMarker;
        [SerializeField] private InputAction _click;
        [SerializeField] private Camera _cam;

        private MapData _data;
        private Collider _groundCollider;
        

        private void Awake()
        {
            if (_mapManager == null) _mapManager = FindFirstObjectByType<MapManager>();
            if (_groundRenderer != null) _groundCollider = _groundRenderer.GetComponent<Collider>();
        }

        private void OnEnable()
        {
            _click = new InputAction(name: "Click", type: InputActionType.Button, binding: "<Mouse>/leftButton");
            _click.performed += OnClick;
            _click.Enable();

            if (_groundRenderer != null) _groundCollider = _groundRenderer.GetComponent<Collider>();

            if (_mapManager == null) return;

            _mapManager.OnMapRebuilt += HandleMapRebuilt;

            if (_mapManager.Data != null)
                HandleMapRebuilt(_mapManager.Data);
        }

        private void OnDisable()
        {
            if (_click != null)
            {
                _click.performed -= OnClick;
                _click.Disable();
            }

            if (_mapManager != null)
                _mapManager.OnMapRebuilt -= HandleMapRebuilt;
        }

        private void Update()
        {

            if (Keyboard.current == null) return;
            if (Keyboard.current.rKey.wasPressedThisFrame)
            {
                RefreshBoard();
            }

            if (_agent == null) return;
            if (Keyboard.current.pKey.wasPressedThisFrame)
            {
                StartNewRandomPath();
            }
        }

        private void HandleMapRebuilt(MapData data)
        {
            _data = data;
            if (_groundRenderer != null) _groundCollider = _groundRenderer.GetComponent<Collider>();
        }



        private void RefreshBoard()
        {
            if (_mapManager == null) return;

            _mapManager.GenerateNewGameBoard();
            if (_agent != null) _agent.StartNewRandomPath();

        }

        private void StartNewRandomPath()
        {
            if (_mapManager == null) return;

            if (_agent != null) _agent.StartNewRandomPath();
        }

        private void OnClick(InputAction.CallbackContext _)
        {
            if (_mapManager == null || _goalMarker == null) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            
            if (_data == null) return;


            Ray ray = _cam.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (!Physics.Raycast(ray, out RaycastHit hit, 500f)) return;

            if (hit.collider != _groundCollider) return;

            Vector2 uv = hit.textureCoord;
            int x = Mathf.Clamp(Mathf.FloorToInt(uv.x * _mapManager.Width), 0, _mapManager.Width - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt(uv.y * _mapManager.Height), 0, _mapManager.Height - 1);

            if (!_data.TryCoordToIndex(x, z, out int idx)) return;
            if (!_mapManager.GetWalkable(idx)) return;

            _goalMarker.position = _data.IndexToWorldCenterXZ(idx, yOffset: 0f) + Vector3.up * 0.1f;
        }

    }

}



/*


// Move out to other script and put aside for now.
// Might use later if implementing click tile abilities

private void OnClickPerformed(InputAction.CallbackContext context)
{
    if (_mainCamera == null) _mainCamera = Camera.main;
    if (_mainCamera == null) return;

    Ray ray = _mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
    if (!Physics.Raycast(ray, out RaycastHit hit, 500f))
        return;

    Collider quadCollider = _quadRenderer.GetComponent<Collider>();
    if (hit.collider != quadCollider) return;

    Vector2 uv = hit.textureCoord;

    int cellX = Mathf.FloorToInt(uv.x * _width);
    int cellY = Mathf.FloorToInt(uv.y * _height);

    cellX = Mathf.Clamp(cellX, 0, _width - 1);
    cellY = Mathf.Clamp(cellY, 0, _height - 1);

    if (TryCoordToIndex(cellX, cellY, out int cellIndex))
    {
        bool newWalkable = _blocked[cellIndex]; // blocked -> make walkable, unblocked -> make blocked
        SetWalkable(cellIndex, newWalkable);
    }

}

*/