using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;


namespace AI_Workshop02
{
    public class AgentMover : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        private NavigationService _navigationService;
        [SerializeField] 
        private BoardManager _boardManager;

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
        [SerializeField] 
        private bool _visualizeSearch = true;
        
        private List<int> _pathIndices;
        private int _pathCursor;

        private int _startIndex = -1;
        private int _goalIndex = -1;

        private void Awake()
        {
            if (_boardManager == null) _boardManager = FindFirstObjectByType<BoardManager>();
            if (_navigationService == null) _navigationService = FindFirstObjectByType<NavigationService>();
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
            if (_boardManager == null || _navigationService == null) return;

            if (_navigationService.IsPathComputing)
                _navigationService.CancelPath();

            _pathIndices = null;
            _pathCursor = 0;

            _boardManager.GenerateNewGameBoard();
            StartNewRandomPath();
        }

        private void StartNewRandomPath()
        {
            if (_boardManager == null || _navigationService == null) return;
            if (_navigationService.IsPathComputing) return;

            _pathIndices = null;
            _pathCursor = 0;

            int minManhattan = ComputeMinManhattan();

            const int maxAttempts = 64;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (!TryPickRandomWalkableCell(out _startIndex))
                    break;

                if (_boardManager.TryPickRandomReachableGoal(_startIndex, minManhattan, _navigationService.AllowDiagonals, out _goalIndex))
                    goto FoundPair;
            }

            Debug.LogWarning("AgentMover: Could not find a valid start+goal pair (try fewer obstacles or lower minManhattan).");
            return;

            FoundPair:
            transform.position = IndexToWorldCenter(_startIndex, transform.position.z);

            _navigationService.RequestPath(_startIndex, _goalIndex, OnPathFound, _visualizeSearch);
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

            Vector3 first = IndexToWorldCenter(_pathIndices[0], transform.position.z);
            transform.position = first;
        }

        private void StepMovement()
        {
            if (_pathIndices == null || _pathIndices.Count == 0) return;
            if (_pathCursor >= _pathIndices.Count) return;

            Vector3 goalPos = IndexToWorldCenter(_pathIndices[_pathCursor], transform.position.z);

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

            int cellCount = _boardManager.CellCount;
            if (cellCount <= 0) return false;

            int w = _boardManager.Width;
            int h = _boardManager.Height;

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

                int candidate = _boardManager.CoordToIndex(x, y);
                if (_boardManager.GetWalkable(candidate))
                {
                    index = candidate;
                    return true;
                }
            }

            // fallback, anywhere
            for (int t = 0; t < tries; t++)
            {
                int candidate = UnityEngine.Random.Range(0, _boardManager.CellCount);
                if (_boardManager.GetWalkable(candidate))
                {
                    index = candidate;
                    return true;
                }
            }


            return false;
        }

        private int ComputeMinManhattan()
        {
            int w = _boardManager.Width;
            int h = _boardManager.Height;

            int scaled = Mathf.RoundToInt((w + h) * _minManhattanFactor);
            return Mathf.Clamp(scaled, _minManhattanClampMin, _minManhattanClampMax);
        }

        private Vector3 IndexToWorldCenter(int index, float z)
        {
            _boardManager.IndexToXY(index, out int x, out int y);
            return new Vector3(x + 0.5f, y + 0.5f, z);
        }

    }

}
