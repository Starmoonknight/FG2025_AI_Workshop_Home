using UnityEngine;
using UnityEngine.InputSystem;
using AI_Workshop01;
using AI_Workshop03.AI;


namespace AI_Workshop03
{

    public sealed class MapInputController : MonoBehaviour
    {
        private enum FollowMode { None, Player, SwarmLeader }


        [SerializeField] private Camera _cam;
        [SerializeField] private MapManager _mapManager;

        [Header("Player Avatar")]
        [SerializeField] private PlayerMovement _player;

        [Header("Goal Placement")]
        [SerializeField] private Transform _goalMarker;
        [SerializeField] private InputAction _click;

        [Header("First Gen Agent ")]
        [SerializeField] private SteeringAgent _agent;

        [Header("Camera Follow")]
        [SerializeField] private FollowMode _followMode = FollowMode.None;
        [SerializeField] private float _followSmooth = 8f;
        [SerializeField] private bool _keepOffsetOnSwitch = true;
        [SerializeField] private Vector3 _fixedOffset = new Vector3(0f, 8f, -8f);

        [Header("Camera Zoom Input")]
        [SerializeField] private float _cellsPerScrollNotch = 3f; // tune (2–6 feels good)
        [SerializeField] private float _scrollSensitivity = 2f; // 1 = normal, try 3–10 if it feels slow

        [Header("Camera Clamp")]
        [SerializeField] private float _edgePaddingCells = 0.5f; // 0.0 = tight, 0.5 = show full edge cells, 1.0 = extra breathing room

        [Header("Camera Zoom (Ortho)")]
        [SerializeField] private float _cellSizeWorld = 1f;     // 1 if 1 cell = 1 Unity unit
        [SerializeField] private float _followViewCells = 15f;  // target cells visible vertically
        [SerializeField] private float _overviewPadding = 1.5f;
        [SerializeField] private float _overviewHeight = 20f;   // just for raycasts; ortho size controls view
        [SerializeField] private float _zoomSmooth = 10f;
        [SerializeField] private float _minViewCells = 6f;
        [SerializeField] private float _maxViewCells = 40f;


        private float _targetOrthoSize;
        private Transform _followTarget;
        private Vector3 _followOffset;
        private bool _snapToTargetOnSwitch = true;

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

            _mapManager.OnMapRebuiltDataReady += HandleMapRebuilt;

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
                _mapManager.OnMapRebuiltDataReady -= HandleMapRebuilt;
        }

        private void Update()
        {
            if (Keyboard.current == null) return;
            if (Keyboard.current.rKey.wasPressedThisFrame)
            {
                RefreshBoard();
            }

            if (_agent != null && Keyboard.current.pKey.wasPressedThisFrame)
                StartNewRandomPath();

            if (_player != null && Keyboard.current.lKey.wasPressedThisFrame)
                ToggleFollowMode();

            UpdateCameraFollow();
            UpdateZoomInput(); 
        }

        private void HandleMapRebuilt(MapData data)
        {
            _data = data;
            if (GroundRenderer != null) _groundCollider = GroundRenderer.GetComponent<Collider>();

            ClearCameraFollow();
            ApplyOverviewCamera();
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






        // --- Camere components should really be moved out into a real camere controllers ---
        //          also look into using cinemaMachine?
        //          and to move out the board fitting from MapManager into the dedicated camera script 


        private Transform FindSwarmLeaderTransform()
        {
            // Requires SwarmingAgent to register in All in OnEnable/OnDisable!
            for (int i = 0; i < SwarmingAgent.All.Count; i++)
            {
                var a = SwarmingAgent.All[i];
                if (a != null && a.IsLeader)
                    return a.transform;
            }
            return null;
        }




        private void ToggleFollowMode()
        {
            // Cycle: Player -> SwarmLeader -> Player ...
            _followMode = _followMode == FollowMode.Player ? FollowMode.SwarmLeader : FollowMode.Player;

            Transform next = null;

            _keepOffsetOnSwitch = false; 
            _fixedOffset = new Vector3(0f, _overviewHeight, 0f);


            if (_followMode == FollowMode.Player)
            {
                next = _player != null ? _player.transform : null;
            }
            else // SwarmLeader
            {
                next = FindSwarmLeaderTransform();
                if (next == null)
                {
                    Debug.LogWarning("No swarm leader found yet. Falling back to Player.");
                    _followMode = FollowMode.Player;
                    next = _player != null ? _player.transform : null;
                }
            }

            SetFollowTarget(next);
            SetFollowZoomForCells(_followViewCells);
        }

        private void SetFollowTarget(Transform target)
        {
            _followTarget = target;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null || _followTarget == null) return;

            _followOffset = _keepOffsetOnSwitch
                ? _cam.transform.position - _followTarget.position
                : _fixedOffset;

            if (_snapToTargetOnSwitch)
            {
                // Snap position immediately so follow feels correct instantly
                Vector3 snapped = _followTarget.position + _followOffset;
                snapped = ClampCameraToMap(snapped);
                _cam.transform.position = snapped;
            }
        }

        private void SetFollowZoomForCells(float cellsVisibleVert)
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null || _data == null) return;

            float clampedCells = Mathf.Clamp(cellsVisibleVert, _minViewCells, _maxViewCells);

            // Visible height in world = cells * CellTileSize
            // OrthoSize is half the visible height.
            _targetOrthoSize = (clampedCells * _data.CellTileSize) * 0.5f;
        }

        private void UpdateCameraFollow()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null || _followTarget == null) return;

            if (_cam.orthographic)
            {
                float t = 1f - Mathf.Exp(-_zoomSmooth * Time.deltaTime);
                _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, _targetOrthoSize, t);
            }

            Vector3 desiredPos = _followTarget.position + _followOffset;
            desiredPos = ClampCameraToMap(desiredPos);

            // Smooth follow (critically damped-ish)
            _cam.transform.position = Vector3.Lerp(
                _cam.transform.position,
                desiredPos,
                1f - Mathf.Exp(-_followSmooth * Time.deltaTime)
            );
        }

        private void UpdateZoomInput()
        {
            if (_followTarget == null) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null || !_cam.orthographic) return;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;

            float scrollY = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scrollY) < 0.01f) return;

            
            // Scroll direction: 
            //float notches = scrollY / 120f; // approx “clicks” on most mice

            // Treat scroll as "notches" but allow sensitivity scaling, 120 is common for wheel mice, but many devices differ.
            float notches = (scrollY / 120f) * _scrollSensitivity;

            // Alternative: treat scroll as a continuous value rather than discrete notches, should feel consistent across wheel vs trackpad for smoother zoom on high-res scroll devices. Tune sensitivity to adjust feel.
            // float notches = (scrollY / Mathf.Max(1f, 120f)) * _scrollSensitivity; 


            _followViewCells = Mathf.Clamp(
                _followViewCells - notches * _cellsPerScrollNotch,
                _minViewCells,
                _maxViewCells
            );

            SetFollowZoomForCells(_followViewCells);
        }


        private void ApplyOverviewCamera()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null || _data == null) return;

            _cam.orthographic = true;
            _cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            // Center above the grid
            //Vector3 center = _data.GridCenter;
            //_cam.transform.position = center + Vector3.up * _overviewHeight;

            float halfCell = (_data != null ? _data.CellTileSize : 1f) * 0.5f;

            float minXEdge = _data.MinWorld.x - halfCell;
            float maxXEdge = _data.MaxWorld.x + halfCell;
            float minZEdge = _data.MinWorld.z - halfCell;
            float maxZEdge = _data.MaxWorld.z + halfCell;

            // Fit the whole board
            float worldW = maxXEdge - minXEdge;
            float worldH = maxZEdge - minZEdge;
            //float worldW = _data.MaxWorld.x - _data.MinWorld.x;
            //float worldH = _data.MaxWorld.z - _data.MinWorld.z;

            float halfW = worldW * 0.5f;
            float halfH = worldH * 0.5f;

            float aspect = _cam.aspect;
            float sizeToFitHeight = halfH;
            float sizeToFitWidth = halfW / aspect;

            _cam.orthographicSize = Mathf.Max(sizeToFitHeight, sizeToFitWidth) + _overviewPadding;
            
            // also sync follow target size baseline to current cam size
            _targetOrthoSize = _cam.orthographicSize;

            // also center using edges, not GridCenter if/now when GridCenter is center-of-cells
            Vector3 center = new Vector3((minXEdge + maxXEdge) * 0.5f, 0f, (minZEdge + maxZEdge) * 0.5f);
            _cam.transform.position = center + Vector3.up * _overviewHeight;
        }

        private void ClearCameraFollow()
        {
            _followTarget = null;
            _followMode = FollowMode.None;
        }


        /*
        private Vector3 ClampCameraToMap(Vector3 desiredPos)
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null || _data == null || !_cam.orthographic) return desiredPos;

            float halfH = Mathf.Max(_cam.orthographicSize, _targetOrthoSize);
            float halfW = halfH * _cam.aspect;

            float minX = _data.MinWorld.x + halfW;
            float maxX = _data.MaxWorld.x - halfW;
            float minZ = _data.MinWorld.z + halfH;
            float maxZ = _data.MaxWorld.z - halfH;

            desiredPos.x = Mathf.Clamp(desiredPos.x, minX, maxX);
            desiredPos.z = Mathf.Clamp(desiredPos.z, minZ, maxZ);

            return desiredPos;
        }
        */

        private Vector3 ClampCameraToMap(Vector3 desiredPos)
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null || _data == null || !_cam.orthographic) return desiredPos;

            //float halfH = _cam.orthographicSize;
            //float halfW = halfH * _cam.aspect;

            //float padWorld = _edgePaddingCells * _data.CellTileSize;
            float halfCell = (_data != null ? _data.CellTileSize : 1f) * 0.5f;

            float minXEdge = _data.MinWorld.x - halfCell;
            float maxXEdge = _data.MaxWorld.x + halfCell;
            float minZEdge = _data.MinWorld.z - halfCell;
            float maxZEdge = _data.MaxWorld.z + halfCell;

            float halfH = Mathf.Max(_cam.orthographicSize, _targetOrthoSize);
            float halfW = halfH * _cam.aspect;

            float minX = minXEdge + halfW;
            float maxX = maxXEdge - halfW;
            float minZ = minZEdge + halfH;
            float maxZ = maxZEdge - halfH;

            // If the view is bigger than the map on an axis, clamp to center on that axis.
            if (minXEdge > maxXEdge) desiredPos.x = (_data.MinWorld.x + _data.MaxWorld.x) * 0.5f;
            else desiredPos.x = Mathf.Clamp(desiredPos.x, minXEdge, maxXEdge);

            if (minZEdge > maxZEdge) desiredPos.z = (_data.MinWorld.z + _data.MaxWorld.z) * 0.5f;
            else desiredPos.z = Mathf.Clamp(desiredPos.z, minZEdge, maxZEdge);

            return desiredPos;
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