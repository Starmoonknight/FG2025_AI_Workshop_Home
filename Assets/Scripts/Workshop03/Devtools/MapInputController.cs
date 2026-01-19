using UnityEngine;
using UnityEngine.InputSystem;


namespace AI_Workshop03
{

    public sealed class MapInputController : MonoBehaviour
    {
        [SerializeField] private Camera _cam;
        [SerializeField] private MapManager _mapManager;
        [SerializeField] private SteeringAgent _agent;

        [SerializeField] private Transform _goalMarker;
        [SerializeField] private InputAction _click;

        private MapData _data;
        private Collider _groundCollider;

        private Renderer GroundRenderer => _mapManager != null ? _mapManager.BoardRenderer : null;


        private void Awake()
        {
            if (_mapManager == null) _mapManager = FindFirstObjectByType<MapManager>();
            
            if (GroundRenderer != null) _groundCollider = GroundRenderer.GetComponent<Collider>();
        }

        private void OnEnable()
        {
            _click = new InputAction(name: "Click", type: InputActionType.Button, binding: "<Mouse>/leftButton");
            _click.performed += OnClick;
            _click.Enable();

            if (GroundRenderer != null) _groundCollider = GroundRenderer.GetComponent<Collider>();

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
            if (GroundRenderer != null) _groundCollider = GroundRenderer.GetComponent<Collider>();
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

        private void OnClick(InputAction.CallbackContext ctx)
        {
            if (_mapManager == null || _goalMarker == null) return;      
            if (_data == null) return;

            if (!TryGetMouseGroundHit(out var hit)) return;
            if (!TryHitToBoardIndex(hit, out int goalIndex)) return;
            if (_data.IsBlocked[goalIndex]) return;

            _goalMarker.position = _data.IndexToWorldCenterXZ(goalIndex, yOffset: 0f) + Vector3.up * 0.1f;
            Debug.Log($"Clicked goalIndex={goalIndex}");

            if (_agent != null)
                _agent.RequestPathTo_SpawnBiased(goalIndex, startFromCurrentPos: true);
        }





        private bool TryHitToBoardIndex(in RaycastHit hit, out int index)
        {
            index = -1;

            if (_data == null) return false;
            if (hit.collider != _groundCollider) return false;

            // world-space conversion using MapData origin + cell size
            return _data.TryWorldToIndexXZ(hit.point, out index);
        }

        private bool TryGetMouseGroundHit(out RaycastHit hit)
        {
            hit = default;

            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return false;

            Ray ray = _cam.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (!Physics.Raycast(ray, out hit, 500f)) return false;

            return hit.collider == _groundCollider;
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