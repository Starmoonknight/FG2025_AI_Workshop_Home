using UnityEngine;
using UnityEngine.InputSystem;


namespace AI_Workshop03
{

    public sealed class MapInteractionManager : MonoBehaviour
    {
        [SerializeField] private MapManager _mapManager;
        [SerializeField] private Renderer _groundRenderer; 
        [SerializeField] private Transform _goalMarker;
        [SerializeField] private InputAction _click;
        [SerializeField] private Camera _cam;

        private MapData _data;

        private void Awake()
        {
            if (_mapManager == null) _mapManager = FindFirstObjectByType<MapManager>();
            if (_mapManager != null) _data = _mapManager.Data;
        }

        private void OnEnable()
        {
            _click = new InputAction(name: "Click", type: InputActionType.Button, binding: "<Mouse>/leftButton");
            _click.performed += OnClick;
            _click.Enable();
        }

        private void OnDisable()
        {
            if (_click != null)
            {
                _click.performed -= OnClick;
                _click.Disable();
            }
        }

        private void OnClick(InputAction.CallbackContext _)
        {
            if (_mapManager == null || _goalMarker == null) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            Ray ray = _cam.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (!Physics.Raycast(ray, out RaycastHit hit, 500f)) return;

            Collider groundCol = _groundRenderer.GetComponent<Collider>();
            if (hit.collider != groundCol) return;

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