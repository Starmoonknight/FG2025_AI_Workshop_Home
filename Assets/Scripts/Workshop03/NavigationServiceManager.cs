using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace AI_Workshop03
{
    // Version2 of NavigationService
    public class NavigationServiceManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        private MapManager _mapManager;

        [Header("A* Settings")]
        [SerializeField]
        private bool _allowDiagonals = true;

        [Tooltip("Delay (seconds) between each current node' selection, for visualization.")]
        [SerializeField, Min(0f)]
        private float _searchDelay = 0.4f;
        [SerializeField, Min(0f)]
        private float _pathDelay = 0.1f;


        [Header("Visualization Colors")]
        [SerializeField, Range(0,1)]
        private float _spreadOverlayStrength = 0.35f;
        [SerializeField, Range(0,1)]
        private float _pathOverlayStrength = 0.5f;
        [SerializeField]
        private Color32 _triedColor = new(185, 0, 255, 255);        // closed,          purple
        [SerializeField]
        private Color32 _frontierColor = new(180, 170, 255, 255);   // open (optional), light purple-blue
        [SerializeField]
        private Color32 _pathColor = new(6, 225, 25, 255);          // final path,      green
        [SerializeField]
        private Color32 _startColor = new(0, 255, 255, 255);        // start cell,      light blue 
        [SerializeField]
        private Color32 _goalColor = new(255, 255, 0, 255);         // goal cell,       yellow 

        // Internal data
        private int _totalCells;    // total number of cells in the grid
        private int _searchId;      // to differentiate between searches
        private MinHeap _open;      // open set (priority queue)

        private int[] _fCost;       // total cost (g + h)
        private int[] _gCost;       // movement cost from start to current node
        private int[] _hCost;       // heuristic cost
        private int[] _parent;      // parent index for path reconstruction
        private ushort[] _seenId;   // to track which nodes have been initialized for the current search
        private byte[] _state;      // 0 = unvisited, 1 = in open set, 2 = closed


        private Coroutine _computeCo; 
        private Coroutine _replayCo;


        public bool IsPathComputing { get; private set; }
        public bool IsVisualizing { get; private set; }
        public List<int> CurrentPath { get; private set; }
        public int CurrentWaypointIndex { get; private set; }
        public int RemainingCost { get; private set; }

        public bool AllowDiagonals => _allowDiagonals;
        public int CurrentStartIndex { get; private set; } = -1;
        public int CurrentGoalIndex { get; private set; } = -1;
        public int TotalPathCost { get; private set; } = 0;



        // Neighbor offsets for 8-directional movement with associated step costs, dx stands for change in x, dy for change in y
        private static readonly (int dx, int dy, int stepCost)[] Neighbors8 =
        {
            (-1,  0, 10),  //Left
            ( 1,  0, 10),  //Right
            ( 0, -1, 10),  //Down
            ( 0,  1, 10),  //Up
            (-1, -1, 14),  //Bottom-Left
            ( 1, -1, 14),  //Bottom-Right
            (-1,  1, 14),  //Top-Left
            ( 1,  1, 14)   //Top-Right
        };


        private void Awake()
        {
            if (_mapManager == null) _mapManager = FindFirstObjectByType<MapManager>();
            EnsureCapacity();
        }

        private void EnsureCapacity()
        {
            _totalCells = _mapManager.CellCount;

            if (_fCost == null  || _fCost.Length  != _totalCells) _fCost  = new int[_totalCells];
            if (_gCost == null  || _gCost.Length  != _totalCells) _gCost  = new int[_totalCells];
            if (_hCost == null  || _hCost.Length  != _totalCells) _hCost  = new int[_totalCells];
            if (_parent == null || _parent.Length != _totalCells) _parent = new int[_totalCells];
            if (_seenId == null || _seenId.Length != _totalCells) _seenId = new ushort[_totalCells];
            if (_state == null  || _state.Length  != _totalCells) _state  = new byte[_totalCells];

            if (_open == null || _open.Capacity < _totalCells) _open = new MinHeap(_totalCells);
        }


        #region Public API

        public void RequestTravelPath(
            int startIndex, int goalIndex, Action<List<int>> onPathFound, 
            bool visualizeAll = false, bool showFinalPath = false, bool showStartAndGaol = false)
        {
            StopComputerReplay();
            _computeCo = StartCoroutine(AStarRoutine(startIndex, goalIndex, onPathFound, visualizeAll, showFinalPath, showStartAndGaol));
        }

        public void StartVisualPath(int startIndex, int goalIndex)
        {
            RequestTravelPath(startIndex, goalIndex, onPathFound: null, visualizeAll: true);
        }

        public void StartVisualPathFromCenter(int minManhattan = 20)
        {
            int startIndex = _mapManager.CoordToIndex(_mapManager.Width / 2, _mapManager.Height / 2);
            if (_mapManager.TryPickRandomReachableGoal(startIndex, minManhattan, _allowDiagonals, out int goalIndex))
            {
                StartVisualPath(startIndex, goalIndex);
            }
        }

        public void CancelPath(bool clearVisuals = true)
        {
            StopComputerReplay();

            IsPathComputing = false;
            CurrentPath = null;

            CurrentPath = null;
            CurrentWaypointIndex = 0;
            RemainingCost = 0;
            TotalPathCost = 0;
            CurrentStartIndex = -1;
            CurrentGoalIndex = -1;

            _open?.Clear();

            if (clearVisuals && _mapManager != null)
            {
                _mapManager.ResetColorsToBase();
                _mapManager.ClearDebugCostsTouched();
            }
        }

        public void SetProgressWaypoint(int waypointIndex)
        {
            if (CurrentPath == null || CurrentPath.Count == 0)
            {
                CurrentWaypointIndex = 0;
                RemainingCost = 0;
                return;
            }

            CurrentWaypointIndex = Mathf.Clamp(waypointIndex, 0, CurrentPath.Count - 1);

            int cell = CurrentPath[CurrentWaypointIndex];
            RemainingCost = TotalPathCost - _gCost[cell];
        }


        #endregion



        #region A* Implementation

        private IEnumerator AStarRoutine(
            int startIndex, int goalIndex, Action<List<int>> onPathFound, 
            bool visualizeAll, bool visualizeFinalPath, bool showStartAndGaol)
        {
            EnsureCapacity();

            IsPathComputing = true;
            CurrentPath = null;
            CurrentStartIndex = startIndex;
            CurrentGoalIndex = goalIndex;
            CurrentWaypointIndex = 0;
            RemainingCost = 0;
            TotalPathCost = 0;

            if (!_mapManager.IsValidCell(startIndex) || !_mapManager.IsValidCell(goalIndex))
            {
                IsPathComputing = false;
                onPathFound?.Invoke(null);
                yield break;
            }
            if (!_mapManager.GetWalkable(startIndex) || !_mapManager.GetWalkable(goalIndex))
            {
                IsPathComputing = false;
                onPathFound?.Invoke(null);
                yield break;
            }

            List<PaintStep> paintSteps = null;


            bool visualizeAny = (visualizeAll || visualizeFinalPath || showStartAndGaol);
            bool showPath = visualizeAll || visualizeFinalPath;
            bool showStartGoalMarkers = visualizeAll || showStartAndGaol;

            if (visualizeAny)
            {
                paintSteps = new();
                _mapManager.ResetColorsToBase();
                _mapManager.ClearDebugCostsTouched();

                if (visualizeAll)
                    _mapManager.BuildVisualReachableFrom(startIndex, _allowDiagonals);

                if (showStartGoalMarkers)
                {
                    _mapManager.PaintCell(startIndex, _startColor);
                    _mapManager.PaintCell(goalIndex, _goalColor);
                }
            } 

            NextSearchId();
            _open.Clear();

            InitiateNode(startIndex);
            InitiateNode(goalIndex);

            _parent[startIndex] = -1;
            _gCost[startIndex] = 0;
            _hCost[startIndex] = Heuristic(startIndex, goalIndex);
            _fCost[startIndex] = _gCost[startIndex] + _hCost[startIndex];

            _state[startIndex] = 1; // in open set
            _open.Push(startIndex, _fCost[startIndex]);

            bool found = false;

            while (_open.Count > 0)
            {
                int currentIndex = _open.PopMin();

                if (_state[currentIndex] == 2)  // already closed
                    continue;

                _state[currentIndex] = 2;       // closed
                if (visualizeAll)
                {
                    // adding closed node (current) to the visuals
                    if (currentIndex != startIndex && currentIndex != goalIndex)
                        paintSteps.Add(new PaintStep(currentIndex, _triedColor, _spreadOverlayStrength, StepPhase.Search, 
                            true, _gCost[currentIndex], _hCost[currentIndex], _fCost[currentIndex]));
                }

                if (currentIndex == goalIndex)
                {
                    found = true;
                    break;
                }

                _mapManager.IndexToXY(currentIndex, out int currentX, out int currentY);

                foreach (var (dx, dy, stepCost) in Neighbors8)
                {
                    if (!_allowDiagonals && dx != 0 && dy != 0)
                        continue;

                    int newX = currentX + dx;
                    int newY = currentY + dy;
                    if (!_mapManager.TryCoordToIndex(newX, newY, out int newIndex)) continue;
                    if (!_mapManager.GetWalkable(newIndex)) continue;

                    // Disallow diagonal ONLY when BOTH orthogonal side cells are blocked.
                    if (dx != 0 && dy != 0)
                    {
                        bool sideAOpen = _mapManager.TryCoordToIndex(currentX + dx, currentY, out int sideA) && _mapManager.GetWalkable(sideA);
                        bool sideBOpen = _mapManager.TryCoordToIndex(currentX, currentY + dy, out int sideB) && _mapManager.GetWalkable(sideB);

                        if (!sideAOpen && !sideBOpen)
                            continue;
                    }

                    InitiateNode(newIndex);

                    int terrain = _mapManager.GetTerrainCost(newIndex);
                    // interpret terrainCost as a multiplier where 10 = normal cost
                    int moveCost = (stepCost * terrain) / 10;

                    int tentativeG = _gCost[currentIndex] + moveCost;

                    if (tentativeG < _gCost[newIndex])
                    {
                        _parent[newIndex] = currentIndex;
                        _gCost[newIndex] = tentativeG;
                        _hCost[newIndex] = Heuristic(newIndex, goalIndex);
                        _fCost[newIndex] = tentativeG + _hCost[newIndex];

                        if (_state[newIndex] != 1)
                        {
                            _state[newIndex] = 1;
                            _open.Push(newIndex, _fCost[newIndex]);

                            if (visualizeAll)
                            {
                                // adding frontier node to the visuals (neighbor pushed into open)
                                if (newIndex != startIndex && newIndex != goalIndex)
                                    paintSteps.Add(new PaintStep(newIndex, _frontierColor, _spreadOverlayStrength, StepPhase.Marker, 
                                        true, _gCost[newIndex], _hCost[newIndex], _fCost[newIndex]));
                            }
                        }
                        else
                        {
                            _open.DecreaseKeyIfBetter(newIndex, _fCost[newIndex]);

                            if (visualizeAll)
                            {
                                paintSteps.Add(new PaintStep(newIndex, default, 0f, StepPhase.Marker,
                                    writeCost: true, _gCost[newIndex], _hCost[newIndex], _fCost[newIndex]));
                            }
                        }
                    }
                }
            }

            if (!found)
            {
                IsPathComputing = false;
                onPathFound?.Invoke(null);
                yield break;
            }

            var path = ReconstructPath(goalIndex);
            CurrentPath = path;

            IsPathComputing = false;
            _computeCo = null;
            CurrentWaypointIndex = 0;
            TotalPathCost = _gCost[goalIndex];
            RemainingCost = TotalPathCost;
            onPathFound?.Invoke(path);

            // extra safety, ensure list exists if going to add steps
            if ((showPath || visualizeAny) && paintSteps == null)
                paintSteps = new List<PaintStep>();

            if (showPath)
            {
                for (int i = 0; i < path.Count; i++)
                {
                    int idx = path[i]; 
                    paintSteps.Add(new PaintStep(idx, _pathColor, _pathOverlayStrength, StepPhase.Path, 
                        true, _gCost[idx], _hCost[idx], _fCost[idx])); 
                }
            }
            if (visualizeAny)
            {
                if (_replayCo != null) 
                { 
                    StopCoroutine(_replayCo);
                    _replayCo = null;
                }
                _replayCo = StartCoroutine(ReplayPaintStepsCoroutine(paintSteps, startIndex, goalIndex, showStartGoalMarkers)); 
            }

        }



        private void NextSearchId()
        {
            _searchId++;
            if (_searchId >= ushort.MaxValue)
            {
                Array.Clear(_seenId, 0, _seenId.Length); // reset seen IDs to avoid overflow if necessary
                _searchId = 1;
            }
        }

        private void InitiateNode(int index)
        {
            ushort currentSearchId = (ushort)_searchId;
            if (_seenId[index] == currentSearchId)
                return;

            _seenId[index] = currentSearchId;
            _gCost[index] = int.MaxValue / 4;
            _fCost[index] = int.MaxValue / 4;
            _parent[index] = -1;
            _state[index] = 0;
        }

        private List<int> ReconstructPath(int goalIndex)
        {
            List<int> path = new List<int>(128);
            int currentIndex = goalIndex;
            while (currentIndex != -1)
            {
                path.Add(currentIndex);
                currentIndex = _parent[currentIndex];
            }
            path.Reverse();
            return path;
        }

        private int Heuristic(int fromIndex, int toIndex)
        {
            _mapManager.IndexToXY(fromIndex, out int fromX, out int fromY);
            _mapManager.IndexToXY(toIndex, out int toX, out int toY);

            int distanceX = Math.Abs(fromX - toX);
            int distanceY = Math.Abs(fromY - toY);

            int minTerrain = _mapManager.MinTerrainCost;
            int minStraightMove = (10 * minTerrain / 10);
            int minDiagMove = (14 * minTerrain / 10);

            // safety clamp for to low terrain costs and negative values 
            if (minStraightMove < 1) minStraightMove = 1;
            if (minDiagMove < 1) minDiagMove = 1;

            if (_allowDiagonals)
            {
                int minDistance = Math.Min(distanceX, distanceY);
                int maxDistance = Math.Max(distanceX, distanceY);

                // Diagonal distance
                return minDiagMove * minDistance + minStraightMove * (maxDistance - minDistance); // 14 for diagonal steps, 10 for straight steps     // 10 * (distanceX + distanceY) + (14 - 2 * 10) * minDistance;
            }
            else
            {
                // Manhattan distance
                return minStraightMove * (distanceX + distanceY);
            }
        }

        #endregion



        #region Internal Helpers

        private void StopComputerReplay()
        {
            if (_computeCo != null) { StopCoroutine(_computeCo); _computeCo = null; }
            if (_replayCo != null) { StopCoroutine(_replayCo); _replayCo = null; }

            IsPathComputing = false;
            IsVisualizing = false;
        }

        #endregion



        #region Minimal Heap Implementation (priority queue)

        private sealed class MinHeap
        {
            private int[] _items;       // node indices
            private int[] _priority;    // priorities (fCost)
            private int[] _heapPos;     // nodeIndex -> heap position, -1 if not in heap
            private int _count;

            public int Count => _count;
            public int Capacity => _items.Length;

            public MinHeap(int capacity)
            {
                _items = new int[capacity];
                _priority = new int[capacity];
                _heapPos = new int[capacity];
                Array.Fill(_heapPos, -1);
                _count = 0;
            }

            public void Clear()
            {
                for (int i = 0; i < _count; i++)
                    _heapPos[_items[i]] = -1;
                _count = 0;

            }

            public void Push(int nodeIndex, int priority)
            {
                int pos = _heapPos[nodeIndex];
                if (pos != -1)
                {
                    DecreaseKeyIfBetter(nodeIndex, priority);
                    return;
                }

                int i = _count++;
                _items[i] = nodeIndex;
                _priority[i] = priority;
                _heapPos[nodeIndex] = i;
                SiftUp(i);
            }

            public int PopMin()
            {
                int min = _items[0];
                _heapPos[min] = -1;

                _count--;
                if (_count > 0)
                {
                    _items[0] = _items[_count];
                    _priority[0] = _priority[_count];
                    _heapPos[_items[0]] = 0;
                    SiftDown(0);
                }

                return min;
            }

            public void DecreaseKeyIfBetter(int nodeIndex, int newPriority)
            {
                int pos = _heapPos[nodeIndex];
                if (pos == -1 || _priority[pos] <= newPriority)
                    return;

                _priority[pos] = newPriority;
                SiftUp(pos);
            }

            private void SiftUp(int index)
            {
                while (index > 0)
                {
                    int parent = (index - 1) >> 1;
                    if (_priority[index] >= _priority[parent]) break;
                    Swap(index, parent);
                    index = parent;
                }
            }

            private void SiftDown(int index)
            {
                while (true)
                {
                    int leftChild = (index << 1) + 1;
                    int rightChild = leftChild + 1;
                    int smallest = index;

                    if (leftChild < _count && _priority[leftChild] < _priority[smallest])
                        smallest = leftChild;

                    if (rightChild < _count && _priority[rightChild] < _priority[smallest])
                        smallest = rightChild;

                    if (smallest == index) break;

                    Swap(index, smallest);
                    index = smallest;
                }
            }

            private void Swap(int a, int b)
            {
                (_items[a], _items[b]) = (_items[b], _items[a]);
                (_priority[a], _priority[b]) = (_priority[b], _priority[a]);

                _heapPos[_items[a]] = a;
                _heapPos[_items[b]] = b;
            }
        }

        #endregion



        #region Visuals

        private IEnumerator ReplayPaintStepsCoroutine(List<PaintStep> steps, int startIndex, int goalIndex, bool showStartGoal)
        {
            IsVisualizing = true; 

            for (int i = 0; i < steps.Count; i++) 
            {
                var step = steps[i];

                if (step.TintStrength > 0f)
                    _mapManager.PaintCellTint(step.Index, step.Color, step.TintStrength);
                if (step.WriteCosts)
                    _mapManager.SetDebugCosts(step.Index, step.GCost, step.HCost, step.FCost);

                float delay = (step.Phase == StepPhase.Search) ? _searchDelay
                    : (step.Phase == StepPhase.Path) ? _pathDelay
                    : 0f;

                if (delay > 0f) yield return new WaitForSeconds(delay);
            }

            if (showStartGoal)
            {
                _mapManager.PaintCell(startIndex, _startColor);
                _mapManager.PaintCell(goalIndex, _goalColor);
            }

            IsVisualizing = false;
            _replayCo = null;
        }


        private enum StepPhase : byte { Search, Path, Marker }

        private readonly struct PaintStep
        {
            public readonly int Index; 
            public readonly Color32 Color; 
            public readonly float TintStrength; 
            public readonly StepPhase Phase;
            public readonly bool WriteCosts;
            public readonly int GCost, HCost, FCost;

            public PaintStep(int index, Color32 color, float tintStrength, StepPhase phase, bool writeCost, int g, int h, int f)
            {
                Index = index;
                Color = color;
                TintStrength = tintStrength;
                Phase = phase; 
                WriteCosts = writeCost;
                GCost = g;
                HCost = h;
                FCost = f;
            }
        }


        #endregion



    }

}
