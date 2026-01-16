using System.Collections.Generic;
using UnityEngine;


namespace AI_Workshop03
{

    // Version2 of AgentMover
    public class SteeringAgent : MonoBehaviour
    {
        [SerializeField] private float _agentPlaneOffsetY = 0.5f;   // Y if XZ, Z if XY

        [Header("References")]
        [SerializeField] private NavigationServiceManager _navigationService;
        [SerializeField] private MapManager _mapManager;

        [Header("Movement")]
        [SerializeField] private float _speed = 5f;
        [SerializeField, Min(0.001f)] private float _waypointRadius = 0.05f;

        [Header("Random start/goal")]
        [SerializeField, Range(0f, 1f)] private float _minManhattanFactor = 0.30f;
        [SerializeField, Min(0)] private int _minManhattanClampMin = 2;
        [SerializeField, Min(0)] private int _minManhattanClampMax = 200;   // safety

        [Header("Visualization")] private bool _visualizeAll = true;    // show pathfinding + path + start/goal tiles
        [SerializeField] private bool _visualizeFinalPath = true;       // show path
        [SerializeField] private bool _showStartAndGaol = true;         // show path start/goal tiles

        private List<int> _pathIndices;
        private int _pathCursor;

        private int _startIndex = -1;
        private int _goalIndex = -1;
        private const int MaxPickAttempts = 64;

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
            StepMovement();
        }




        #region API

        // NEW
        public void RequestPath(int startIndex, int goalIndex)
        {
            if (!_mapManager.Data.IsValidCellIndex(startIndex) || _mapManager.Data.IsBlocked[startIndex])
            {
                Debug.LogWarning($"AgentMover: RequestPath startIndex {startIndex} is not a valid cell on the map).");
                return;
            }
            if (!_mapManager.Data.IsValidCellIndex(goalIndex) || _mapManager.Data.IsBlocked[goalIndex])
            {
                Debug.LogWarning($"AgentMover: RequestPath goalIndex {goalIndex} is not a valid cell on the map).");
                return;
            }
            if (!EnsureCanRequestPath()) return;

            ResetPathState();

            if (!_mapManager.TryValidateReachablePair(startIndex, goalIndex, _navigationService.AllowDiagonals)) return;

            transform.position = WorldFromIndex(startIndex);
            StartPath(startIndex, goalIndex);
        }

        // NEW
        public void StartNewRandomPath()
        {
            // Random start + random reachable goal
            if (!EnsureCanRequestPath()) return;

            ResetPathState();

            int minManhattan = ComputeMinManhattan();
            int startIndex;
            int goalIndex;

            for (int attempt = 0; attempt < MaxPickAttempts; attempt++)
            {
                if (!TryPickPoint(out startIndex)) break;
                if (!TryPickRandomReachableOther(startIndex, minManhattan, out goalIndex)) continue;

                transform.position = WorldFromIndex(startIndex);
                StartPath(startIndex, goalIndex);
                return;
            }

            Debug.LogWarning("AgentMover: Could not find a valid start+goal pair (try fewer obstacles or lower minManhattan).");
        }

        // NEW
        public void RequestPathFrom(int startIndex)
        {
            // Fixed start + random reachable goal
            if (!_mapManager.Data.IsValidCellIndex(startIndex) || _mapManager.Data.IsBlocked[startIndex])
            {
                Debug.LogWarning($"AgentMover: RequestPathFrom startIndex {startIndex} is not a valid cell on the map).");
                return;
            }

            if (!EnsureCanRequestPath()) return;
            ResetPathState();

            int minManhattan = ComputeMinManhattan();

            for (int attempt = 0; attempt < MaxPickAttempts; attempt++)
            {
                if (!TryPickRandomReachableOther(startIndex, minManhattan, out int goalIndex))
                    continue;

                transform.position = WorldFromIndex(startIndex);
                StartPath(startIndex, goalIndex);
                return;
            }

            Debug.LogWarning("AgentMover: Could not find a valid start+goal pair (try fewer obstacles or lower minManhattan).");
        }

        // NEW
        // Uses TryPickRandomReachableOther(goal -> start), truly random pick
        // Not really faster in big-O than _SpawnBiased, but might need fewer attempts, 
        public void RequestPathTo_Fast(int goalIndex, bool startFromCurrentPos = false)
        {
            // Random start + fixed goal
            if (!_mapManager.Data.IsValidCellIndex(goalIndex) || _mapManager.Data.IsBlocked[goalIndex])
            {
                Debug.LogWarning($"AgentMover: RequestPathTo goalIndex {goalIndex} is not a valid cell on the map).");
                return;
            }

            if (!EnsureCanRequestPath()) return;
            ResetPathState();

            if (startFromCurrentPos && CanUseAgentPos(goalIndex))
                return;

            if (TryPickRandomReachableOther(goalIndex, ComputeMinManhattan(), out int startIndex))
            {
                transform.position = WorldFromIndex(startIndex);
                StartPath(startIndex, goalIndex);
            }

            Debug.LogWarning("AgentMover: RequestPathTo_Fast could not find a reachable start for that goal.");
        }

        // NEW
        //Uses TryPickPoint + TryValidateReachablePair, biased to pick a point along that edge (top/bottom/left/right)
        public void RequestPathTo_SpawnBiased(int goalIndex, bool startFromCurrentPos = false)
        {
            // Random start + fixed goal
            if (!_mapManager.Data.IsValidCellIndex(goalIndex) || _mapManager.Data.IsBlocked[goalIndex])
            {
                Debug.LogWarning($"AgentMover: RequestPathTo goalIndex {goalIndex} is not a valid cell on the map).");
                return;
            }

            if (!EnsureCanRequestPath()) return;
            ResetPathState();

            if (startFromCurrentPos && CanUseAgentPos(goalIndex))
                return;

            for (int attempt = 0; attempt < MaxPickAttempts; attempt++)
            {
                if (!TryPickPoint(out int start)) break;
                if (!_mapManager.TryValidateReachablePair(start, goalIndex, _navigationService.AllowDiagonals))
                    continue;

                transform.position = WorldFromIndex(start);
                StartPath(start, goalIndex);
                return;
            }

            Debug.LogWarning("AgentMover: RequestPathTo_SpawnBiased could not find a reachable start for that goal.");
        }


        #endregion


        /*  Old method, saving in case all goes to shit
         
        public void StartNewRandomPath()
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

        */



        #region Internal API-Helpers

        // NEW
        private void StartPath(int startIndex, int goalIndex)
        {
            _startIndex = startIndex;
            _goalIndex = goalIndex;

            _navigationService.RequestTravelPath(
                _startIndex, _goalIndex,
                OnPathFound,
                _visualizeAll, _visualizeFinalPath, _showStartAndGaol
            );
        }

        // NEW
        private bool EnsureCanRequestPath(bool allowInteruptPath = false)
        {
            if (_mapManager == null || _navigationService == null) return false;

            if (_navigationService.IsPathComputing)
            {
                if (!allowInteruptPath) return false;
                _navigationService.CancelPath(clearVisuals: false);
            }

            return true;
        }

        // NEW
        private void ResetPathState()
        {
            _pathIndices = null;
            _pathCursor = 0;
        }

        // NEW - Pick either a random index or checks an index provied is valid 
        private bool TryPickPoint(out int index, int? haveIndex = null)
        {
            index = -1;

            if (haveIndex.HasValue)
            {
                int idx = haveIndex.Value;
                if (!_mapManager.Data.IsValidCellIndex(idx)) return false;
                if (_mapManager.Data.IsBlocked[idx]) return false; // important!
                index = idx;
                return true;
            }

            return TryPickRandomWalkableCell(out index);
        }

        // NEW 
        // If provided haveOtherIndex, it validates reachability pair
        // Else it picks a random reachable target using MapManager goal picker
        private bool TryPickRandomReachableOther(int anchorIndex, int minManhattan, out int otherIndex, int? haveOtherIndex = null)
        {
            otherIndex = -1;

            // if a haveOtherIndex candidate has been provided validated it
            if (haveOtherIndex.HasValue)
            {
                int idx = haveOtherIndex.Value;

                if (!_mapManager.TryValidateReachablePair(anchorIndex, idx, _navigationService.AllowDiagonals)) return false;

                otherIndex = idx;
                return true;
            }

            // pick a random reachable otherIndex
            return _mapManager.TryPickRandomReachableGoal(
                anchorIndex,
                minManhattan,
                _navigationService.AllowDiagonals,
                out otherIndex
            );
        }

        private bool CanUseAgentPos(int goalIndex)
        {
            if (_mapManager.Data.TryWorldToIndexXZ(transform.position, out int currentStartIdx)
                    && !_mapManager.Data.IsBlocked[currentStartIdx]
                    && _mapManager.TryValidateReachablePair(currentStartIdx, goalIndex, _navigationService.AllowDiagonals))
            {
                transform.position = WorldFromIndex(currentStartIdx);
                StartPath(currentStartIdx, goalIndex);
                return true;
            }

            return false;
        }


        #endregion



        #region Pathfinding

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

        // weighted to intentionally picks from a band/ring area around the board more often.
        // Good for testing but should not be used in main purpose without thinking over it one more time
        private bool TryPickRandomWalkableCell(out int index, int ringThickness = 3)
        {
            index = -1;

            int cellCount = _mapManager.Data.CellCount;
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

                int candidate = _mapManager.Data.CoordToIndex(x, y);
                if (_mapManager.GetWalkable(candidate))
                {
                    index = candidate;
                    return true;
                }
            }

            // fallback, anywhere
            for (int t = 0; t < tries; t++)
            {
                int candidate = UnityEngine.Random.Range(0, _mapManager.Data.CellCount);
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
            return _mapManager.Data.IndexToWorldCenterXZ(index, _agentPlaneOffsetY);
        }


        #endregion




    }

}
