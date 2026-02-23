using UnityEngine;
using UnityEngine.InputSystem;
using AI_Workshop01;
using AI_Workshop03.AI;


namespace AI_Workshop03
{

    public sealed class MapInputController : MonoBehaviour
    {
        private enum FollowMode { None, Player, SwarmLeader }


        [SerializeField] private Camera m_cam;
        [SerializeField] private MapManager m_mapManager;

        [Header("Player Avatar")]
        [SerializeField] private PlayerMovement m_player;

        [Header("Goal Placement")]
        [SerializeField] private Transform m_goalMarker;
        [SerializeField] private InputAction m_click;

        [Header("First Gen Agent ")]
        [SerializeField] private SteeringAgent _agent;

        [Header("Camera Follow")]
        [SerializeField] private FollowMode m_followMode = FollowMode.None;
        [SerializeField] private float m_followSmooth = 8f;
        [SerializeField] private bool m_keepOffsetOnSwitch = true;
        [SerializeField] private Vector3 m_fixedOffset = new Vector3(0f, 8f, -8f);

        [Header("Camera Zoom Input")]
        [SerializeField] private float m_cellsPerScrollNotch = 3f; // tune (2–6 suggested, but felt a 20 was needed on 100x100 map)
        [SerializeField] private float m_scrollSensitivity = 2f; // 1 = normal, try 3–10 if it feels slow

        [Header("Camera Clamp")]
        [SerializeField] private float m_edgePaddingCells = 0.5f; // 0.0 = tight, 0.5 = show full edge cells, 1.0 = extra breathing room

        [Header("Camera Zoom (Ortho)")]
        [SerializeField] private float m_cellSizeWorld = 1f;     // 1 if 1 cell = 1 Unity unit
        [SerializeField] private float m_followViewCells = 15f;  // target cells visible vertically
        [SerializeField] private float m_overviewPadding = 1.5f;
        [SerializeField] private float m_overviewHeight = 20f;   // just for raycasts; ortho size controls view
        [SerializeField] private float m_zoomSmooth = 10f;
        [SerializeField] private float m_minViewCells = 6f;
        [SerializeField] private float m_maxViewCells = 40f;


        private float m_targetOrthoSize;
        private Transform m_followTarget;
        private Vector3 m_followOffset;
        private bool m_snapToTargetOnSwitch = true;

        private MapData m_data;
        private Collider m_groundCollider;

        private Renderer GroundRenderer => m_mapManager != null ? m_mapManager.BoardRenderer : null;

        


        private void Awake()
        {
            if (m_mapManager == null) m_mapManager = FindFirstObjectByType<MapManager>();
            
            if (GroundRenderer != null) m_groundCollider = GroundRenderer.GetComponent<Collider>();
        }

        private void OnEnable()
        {
            m_click = new InputAction(name: "Click", type: InputActionType.Button, binding: "<Mouse>/leftButton");
            m_click.performed += OnClick;
            m_click.Enable();

            if (GroundRenderer != null) m_groundCollider = GroundRenderer.GetComponent<Collider>();

            if (m_mapManager == null) return;

            m_mapManager.OnMapRebuiltDataReady += HandleMapRebuilt;

            if (m_mapManager.Data != null)
                HandleMapRebuilt(m_mapManager.Data);
        }

        private void OnDisable()
        {
            if (m_click != null)
            {
                m_click.performed -= OnClick;
                m_click.Disable();
            }

            if (m_mapManager != null)
                m_mapManager.OnMapRebuiltDataReady -= HandleMapRebuilt;
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

            if (m_player != null && Keyboard.current.lKey.wasPressedThisFrame)
                ToggleFollowMode();

            UpdateCameraFollow();
            UpdateZoomInput(); 
        }

        private void HandleMapRebuilt(MapData data)
        {
            m_data = data;
            if (GroundRenderer != null) m_groundCollider = GroundRenderer.GetComponent<Collider>();

            ClearCameraFollow();
            ApplyOverviewCamera();
        }



        private void RefreshBoard()
        {
            if (m_mapManager == null) return;

            m_mapManager.GenerateNewGameBoard();
            if (_agent != null) _agent.StartNewRandomPath();

        }

        private void StartNewRandomPath()
        {
            if (m_mapManager == null) return;

            if (_agent != null) _agent.StartNewRandomPath();
        }

        private void OnClick(InputAction.CallbackContext ctx)
        {
            if (m_mapManager == null || m_goalMarker == null) return;      
            if (m_data == null) return;

            if (!TryGetMouseGroundHit(out var hit)) return;
            if (!TryHitToBoardIndex(hit, out int goalIndex)) return;
            if (m_data.IsBlocked[goalIndex]) return;

            m_goalMarker.position = m_data.IndexToWorldCenterXZ(goalIndex, yOffset: 0f) + Vector3.up * 0.1f;
            Debug.Log($"Clicked goalIndex={goalIndex}");

            if (_agent != null)
                _agent.RequestPathTo_SpawnBiased(goalIndex, startFromCurrentPos: true);
        }



        private bool TryHitToBoardIndex(in RaycastHit hit, out int index)
        {
            index = -1;

            if (m_data == null) return false;
            if (hit.collider != m_groundCollider) return false;

            // world-space conversion using MapData origin + cell size
            return m_data.TryWorldToIndexXZ(hit.point, out index);
        }

        private bool TryGetMouseGroundHit(out RaycastHit hit)
        {
            hit = default;

            if (m_cam == null) m_cam = Camera.main;
            if (m_cam == null) return false;

            Ray ray = m_cam.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (!Physics.Raycast(ray, out hit, 500f)) return false;

            return hit.collider == m_groundCollider;
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
            m_followMode = m_followMode == FollowMode.Player ? FollowMode.SwarmLeader : FollowMode.Player;

            Transform next = null;

            m_keepOffsetOnSwitch = false; 
            m_fixedOffset = new Vector3(0f, m_overviewHeight, 0f);


            if (m_followMode == FollowMode.Player)
            {
                next = m_player != null ? m_player.transform : null;
            }
            else // SwarmLeader
            {
                next = FindSwarmLeaderTransform();
                if (next == null)
                {
                    Debug.LogWarning("No swarm leader found yet. Falling back to Player.");
                    m_followMode = FollowMode.Player;
                    next = m_player != null ? m_player.transform : null;
                }
            }

            SetFollowTarget(next);
            SetFollowZoomForCells(m_followViewCells);
        }

        private void SetFollowTarget(Transform target)
        {
            m_followTarget = target;
            if (m_cam == null) m_cam = Camera.main;
            if (m_cam == null || m_followTarget == null) return;

            m_followOffset = m_keepOffsetOnSwitch
                ? m_cam.transform.position - m_followTarget.position
                : m_fixedOffset;

            if (m_snapToTargetOnSwitch)
            {
                // Snap position immediately so follow feels correct instantly
                Vector3 snapped = m_followTarget.position + m_followOffset;
                snapped = ClampCameraToMap(snapped);
                m_cam.transform.position = snapped;
            }
        }

        private void SetFollowZoomForCells(float cellsVisibleVert)
        {
            if (m_cam == null) m_cam = Camera.main;
            if (m_cam == null || m_data == null) return;

            float clampedCells = Mathf.Clamp(cellsVisibleVert, m_minViewCells, m_maxViewCells);

            // Visible height in world = cells * CellTileSize
            // OrthoSize is half the visible height.
            m_targetOrthoSize = (clampedCells * m_data.CellTileSize) * 0.5f;
        }

        private void UpdateCameraFollow()
        {
            if (m_cam == null) m_cam = Camera.main;
            if (m_cam == null || m_followTarget == null) return;

            if (m_cam.orthographic)
            {
                float t = 1f - Mathf.Exp(-m_zoomSmooth * Time.deltaTime);
                m_cam.orthographicSize = Mathf.Lerp(m_cam.orthographicSize, m_targetOrthoSize, t);
            }

            Vector3 desiredPos = m_followTarget.position + m_followOffset;
            desiredPos = ClampCameraToMap(desiredPos);

            // Smooth follow (critically damped-ish)
            m_cam.transform.position = Vector3.Lerp(
                m_cam.transform.position,
                desiredPos,
                1f - Mathf.Exp(-m_followSmooth * Time.deltaTime)
            );
        }

        private void UpdateZoomInput()
        {
            if (m_followTarget == null) return;
            if (m_cam == null) m_cam = Camera.main;
            if (m_cam == null || !m_cam.orthographic) return;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;

            float scrollY = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scrollY) < 0.01f) return;

            
            // Scroll direction: 
            //float notches = scrollY / 120f; // approx “clicks” on most mice

            // Treat scroll as "notches" but allow sensitivity scaling, 120 is common for wheel mice, but many devices differ.
            float notches = (scrollY / 120f) * m_scrollSensitivity;

            // Alternative: treat scroll as a continuous value rather than discrete notches, should feel consistent across wheel vs trackpad for smoother zoom on high-res scroll devices. Tune sensitivity to adjust feel.
            // float notches = (scrollY / Mathf.Max(1f, 120f)) * _scrollSensitivity; 


            m_followViewCells = Mathf.Clamp(
                m_followViewCells - notches * m_cellsPerScrollNotch,
                m_minViewCells,
                m_maxViewCells
            );

            SetFollowZoomForCells(m_followViewCells);
        }


        private void ApplyOverviewCamera()
        {
            if (m_cam == null) m_cam = Camera.main;
            if (m_cam == null || m_data == null) return;

            m_cam.orthographic = true;
            m_cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            // Center above the grid
            //Vector3 center = _data.GridCenter;
            //_cam.transform.position = center + Vector3.up * _overviewHeight;

            float halfCell = (m_data != null ? m_data.CellTileSize : 1f) * 0.5f;

            float minXEdge = m_data.MinWorld.x - halfCell;
            float maxXEdge = m_data.MaxWorld.x + halfCell;
            float minZEdge = m_data.MinWorld.z - halfCell;
            float maxZEdge = m_data.MaxWorld.z + halfCell;

            // Fit the whole board
            float worldW = maxXEdge - minXEdge;
            float worldH = maxZEdge - minZEdge;
            //float worldW = _data.MaxWorld.x - _data.MinWorld.x;
            //float worldH = _data.MaxWorld.z - _data.MinWorld.z;

            float halfW = worldW * 0.5f;
            float halfH = worldH * 0.5f;

            float aspect = m_cam.aspect;
            float sizeToFitHeight = halfH;
            float sizeToFitWidth = halfW / aspect;

            m_cam.orthographicSize = Mathf.Max(sizeToFitHeight, sizeToFitWidth) + m_overviewPadding;
            
            // also sync follow target size baseline to current cam size
            m_targetOrthoSize = m_cam.orthographicSize;

            // also center using edges, not GridCenter if/now when GridCenter is center-of-cells
            Vector3 center = new Vector3((minXEdge + maxXEdge) * 0.5f, 0f, (minZEdge + maxZEdge) * 0.5f);
            m_cam.transform.position = center + Vector3.up * m_overviewHeight;
        }

        private void ClearCameraFollow()
        {
            m_followTarget = null;
            m_followMode = FollowMode.None;
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
            if (m_cam == null) m_cam = Camera.main;
            if (m_cam == null || m_data == null || !m_cam.orthographic) return desiredPos;

            //float halfH = _cam.orthographicSize;
            //float halfW = halfH * _cam.aspect;

            //float padWorld = _edgePaddingCells * _data.CellTileSize;
            float halfCell = (m_data != null ? m_data.CellTileSize : 1f) * 0.5f;

            float minXEdge = m_data.MinWorld.x - halfCell;
            float maxXEdge = m_data.MaxWorld.x + halfCell;
            float minZEdge = m_data.MinWorld.z - halfCell;
            float maxZEdge = m_data.MaxWorld.z + halfCell;

            float halfH = Mathf.Max(m_cam.orthographicSize, m_targetOrthoSize);
            float halfW = halfH * m_cam.aspect;

            float minX = minXEdge + halfW;
            float maxX = maxXEdge - halfW;
            float minZ = minZEdge + halfH;
            float maxZ = maxZEdge - halfH;

            // If the view is bigger than the map on an axis, clamp to center on that axis.
            if (minXEdge > maxXEdge) desiredPos.x = (m_data.MinWorld.x + m_data.MaxWorld.x) * 0.5f;
            else desiredPos.x = Mathf.Clamp(desiredPos.x, minXEdge, maxXEdge);

            if (minZEdge > maxZEdge) desiredPos.z = (m_data.MinWorld.z + m_data.MaxWorld.z) * 0.5f;
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