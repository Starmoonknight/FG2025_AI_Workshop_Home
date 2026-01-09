using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;


namespace AI_Workshop03
{

    // Version2 of AgentMover
    public class SteeringAgent : MonoBehaviour
    {
        [SerializeField] 
        private float _agentPlaneOffsetY = 0.5f; // Y if XZ, Z if XY

        [Header("References")]
        [SerializeField]
        private NavigationServiceManager _navigationService;
        [SerializeField]
        private MapManager _mapManager;

        [Header("Movement")]
        [SerializeField]
        private float _speed = 5f;
        [SerializeField, Min(0.001f)]
        private float _waypointRadius = 0.05f;

        [Header("Random start/goal")]
        [SerializeField, Range(0f, 1f)]
        private float _minManhattanFactor = 0.30f;
        [SerializeField, Min(0)]
        private int _minManhattanClampMin = 2;
        [SerializeField, Min(0)]
        private int _minManhattanClampMax = 200; // safety

        [Header("Visualization")]
        [SerializeField]
        private bool _visualizeAll = true;          // show pathfinding + path + start/goal tiles
        [SerializeField]
        private bool _visualizeFinalPath = true;    // show path
        [SerializeField]
        private bool _showStartAndGaol = true;      // show path start/goal tiles

        private List<int> _pathIndices;
        private int _pathCursor;

        private int _startIndex = -1;
        private int _goalIndex = -1;

        private void Awake()
        {
            if (_mapManager == null) _mapManager = FindFirstObjectByType<MapManager>();
            if (_navigationService == null) _navigationService = FindFirstObjectByType<NavigationServiceManager>();

            transform.rotation = Quaternion.identity;
            transform.localScale = new Vector3(Mathf.Abs(transform.localScale.x), Mathf.Abs(transform.localScale.y), Mathf.Abs(transform.localScale.z));

        }

        private void Start()
        {
            if (_startIndex >= 0)
                transform.position = WorldFromIndex(_startIndex);
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.pKey.wasPressedThisFrame)
            {
                StartNewRandomPath();
            }

            if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            {
                RefreshBoard();
            }

            StepMovement();
        }


        private void RefreshBoard()
        {
            if (_mapManager == null || _navigationService == null) return;

            if (_navigationService.IsPathComputing)
                _navigationService.CancelPath();

            _pathIndices = null;
            _pathCursor = 0;

            _mapManager.GenerateNewGameBoard();
            StartNewRandomPath();
        }

        private void StartNewRandomPath()
        {
            if (_mapManager == null || _navigationService == null) return;
            if (_navigationService.IsPathComputing) return;

            _pathIndices = null;
            _pathCursor = 0;

            int minManhattan = ComputeMinManhattan();

            const int maxAttempts = 64;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (!TryPickRandomWalkableCell(out _startIndex))
                    break;

                if (_mapManager.TryPickRandomReachableGoal(_startIndex, minManhattan, _navigationService.AllowDiagonals, out _goalIndex))
                    goto FoundPair;
            }

            Debug.LogWarning("AgentMover: Could not find a valid start+goal pair (try fewer obstacles or lower minManhattan).");
            return;


        FoundPair:

            transform.position = WorldFromIndex(_startIndex);
            _navigationService.RequestTravelPath(_startIndex, _goalIndex, OnPathFound, _visualizeAll, _visualizeFinalPath, _showStartAndGaol);
        }

        private void OnPathFound(List<int> path)
        {
            if (path == null || path.Count == 0)
            {
                Debug.LogWarning("AgentMover: Pathfinding failed to find a valid path.");
                _pathIndices = null;
                _pathCursor = 0;
                return;
            }

            _pathIndices = path;
            _pathCursor = 0;

            transform.position = WorldFromIndex(_pathIndices[0]);
        }

        private void StepMovement()
        {
            if (_pathIndices == null || _pathIndices.Count == 0) return;
            if (_pathCursor >= _pathIndices.Count) return;

            Vector3 goalPos = WorldFromIndex(_pathIndices[_pathCursor]);
            transform.position = Vector3.MoveTowards(transform.position, goalPos, _speed * Time.deltaTime);

            float distanceSqr = (transform.position - goalPos).sqrMagnitude;
            if (distanceSqr <= _waypointRadius * _waypointRadius)
            {
                _pathCursor++;
            }
        }

        private bool TryPickRandomWalkableCell(out int index, int ringThickness = 3)
        {
            index = -1;

            int cellCount = _mapManager.CellCount;
            if (cellCount <= 0) return false;

            int w = _mapManager.Width;
            int h = _mapManager.Height;

            int ringMax = Mathf.Max(1, Mathf.Min(w, h) / 2);
            ringThickness = Mathf.Clamp(ringThickness, 1, ringMax);

            const int tries = 128;

            for (int i = 0; i < tries; i++)
            {
                int x = UnityEngine.Random.Range(0, w);
                int y = UnityEngine.Random.Range(0, h);

                bool inRing =
                    x < ringThickness || x >= w - ringThickness ||
                    y < ringThickness || y >= h - ringThickness;

                if (!inRing) continue;

                int candidate = _mapManager.CoordToIndex(x, y);
                if (_mapManager.GetWalkable(candidate))
                {
                    index = candidate;
                    return true;
                }
            }

            // fallback, anywhere
            for (int t = 0; t < tries; t++)
            {
                int candidate = UnityEngine.Random.Range(0, _mapManager.CellCount);
                if (_mapManager.GetWalkable(candidate))
                {
                    index = candidate;
                    return true;
                }
            }

            return false;
        }

        private int ComputeMinManhattan()
        {
            int w = _mapManager.Width;
            int h = _mapManager.Height;

            int scaled = Mathf.RoundToInt((w + h) * _minManhattanFactor);
            return Mathf.Clamp(scaled, _minManhattanClampMin, _minManhattanClampMax);
        }

        private Vector3 WorldFromIndex(int index)
        {
            return _mapManager.IndexToWorldCenterXZ(index, _agentPlaneOffsetY);
        }

    }

}
