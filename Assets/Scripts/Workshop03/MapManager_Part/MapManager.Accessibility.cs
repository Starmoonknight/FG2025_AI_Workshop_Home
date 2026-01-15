using System;
using UnityEngine;


namespace AI_Workshop03
{
    // MapManager.Accessibility.cs               -   Purpose: reachability + BFS (accessibility)
    public partial class MapManager
    {

        // Accessability Data
        private int[] _bfsQueue;
        private int[] _reachStamp;
        private int _reachStampId;


        private void EnsureReachBuffers()
        {
            if (_bfsQueue == null || _bfsQueue.Length != _cellCount)
                _bfsQueue = new int[_cellCount];

            if (_reachStamp == null || _reachStamp.Length != _cellCount)
                _reachStamp = new int[_cellCount];
        }


        // this was a later addon to match diagonal bool option in the A*, keept the old signature below for other callers
        public int BuildReachableFrom(int startIndex) =>
            BuildReachableFrom(startIndex, allowDiagonals: true);

        public int BuildReachableFrom(int startIndex, bool allowDiagonals)
        {
            EnsureReachBuffers();
            if (!IsValidCell(startIndex) || _blocked[startIndex])
                return 0;

            // Prevent stamp id overflow, rare but possible
            if (_reachStampId == int.MaxValue)
            {
                Array.Clear(_reachStamp, 0, _reachStamp.Length);
                _reachStampId = 0; // so next ++ becomes 1
            }

            _reachStampId++;

            int head = 0;   // index of the next item to dequeue
            int tail = 0;   // index where the next item will be enqueued

            _bfsQueue[tail++] = startIndex;
            _reachStamp[startIndex] = _reachStampId;

            int reachableCount = 1;

            while (head < tail)
            {
                int currentIndex = _bfsQueue[head++];
                IndexToXY(currentIndex, out int coordX, out int coordY);

                foreach (var (dirX, dirY, _) in Neighbors8)
                {
                    // need to match the A* diagonal toggle, otherwise might have an unreachable map but say it is reachable
                    if (!allowDiagonals && dirX != 0 && dirY != 0)
                        continue;

                    if (dirX != 0 && dirY != 0)  // need to change to match A*  // think I changed it but double check later
                    {
                        // Diagonal movement allowed only if at least one side is open
                        bool sideAOpen = TryCoordToIndex(coordX + dirX, coordY, out int sideIndexA) && !_blocked[sideIndexA];
                        bool sideBOpen = TryCoordToIndex(coordX, coordY + dirY, out int sideIndexB) && !_blocked[sideIndexB];

                        if (!sideAOpen && !sideBOpen)
                            continue;
                    }

                    TryEnqueue(coordX + dirX, coordY + dirY);
                }
            }

            return reachableCount;

            void TryEnqueue(int newX, int newY)
            {
                if (!TryCoordToIndex(newX, newY, out int ni)) return;
                if (_blocked[ni]) return;
                if (_reachStamp[ni] == _reachStampId) return;

                _reachStamp[ni] = _reachStampId;
                _bfsQueue[tail++] = ni;
                reachableCount++;
            }

        }


        public void BuildVisualReachableFrom(int startIndex, bool allowDiagonals = true)
        {
            BuildReachableFrom(startIndex, allowDiagonals);
            RebuildCellColorsFromBase();

            for (int i = 0; i < _cellCount; i++)
            {
                if (_blocked[i]) continue;

                bool isReachable = (_reachStamp[i] == _reachStampId);
                if (!isReachable)
                {
                    IndexToXY(i, out int x, out int y);
                    bool odd = ((x + y) & 1) == 1;
                    _cellColors[i] = ApplyGridShading(_unReachableColor, odd);
                }
            }

            _textureDirty = true;
        }


        // this was a later addon to match diagonal bool option in the A*, keept the old signature below for other callers
        public bool TryPickRandomReachableGoal(int startIndex, int minManhattan, out int goalIndex) =>
            TryPickRandomReachableGoal(startIndex, minManhattan, allowDiagonals: true, out goalIndex);

        // If I want the goal to be far-ish away, can also pick minManhattan as something like (_width + _height) / 4. 
        // This ensures the goal is at least a quarter of the board’s perimeter away from the start.
        public bool TryPickRandomReachableGoal(int startIndex, int minManhattan, bool allowDiagonals, out int goalIndex)
        {
            goalIndex = -1;

            int reachableCount = BuildReachableFrom(startIndex, allowDiagonals);
            if (reachableCount <= 1) return false;

            IndexToXY(startIndex, out int startX, out int startY);

            int candidateCount = 0;

            for (int i = 0; i < _cellCount; i++)
            {
                if (_blocked[i]) continue;                      // skip unwalkable cells
                if (_reachStamp[i] != _reachStampId) continue;  // if not reachable in current step
                if (i == startIndex) continue;                  // skip starting cell

                IndexToXY(i, out int cellX, out int cellY);
                int manhattan = Math.Abs(cellX - startX) + Math.Abs(cellY - startY);
                if (manhattan < minManhattan) continue;

                candidateCount++;

                // Reservoir sampling: each candidate has a 1/candidateCount chance to be selected
                if (_goalRng.Next(candidateCount) == 0)
                    goalIndex = i;
            }

            return goalIndex != -1;
        }





    }


}
