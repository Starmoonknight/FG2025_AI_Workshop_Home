using System;


namespace AI_Workshop03
{


    public class MapReachability
    {

        private int[] _bfsQueue;
        private int[] _reachStamp;
        private int _reachStampId;

        public int[] StampArray => _reachStamp;   // for debug overlays
        public int StampId => _reachStampId;


        // Neighbor offsets for 8-directional movement, dirX stands for change in x, dirY for change in y
        private static readonly (int dirX, int dirY)[] Neighbors8 =
        {
            (-1,  0), ( 1,  0), ( 0, -1), ( 0,  1),     // Left, Right, Down, Up
            (-1, -1), (-1,  1), ( 1, -1), ( 1,  1),     // Bottom-Left, Bottom-Right, Top-Left, Top-Right
        };



        private void EnsureReachBuffers(int cellCount)
        {

            if (_bfsQueue == null || _bfsQueue.Length != cellCount)
                _bfsQueue = new int[cellCount];

            if (_reachStamp == null || _reachStamp.Length != cellCount)
                _reachStamp = new int[cellCount];
        }



        // NOTE: Not an Atomic method, should only be exposed in a method that calles an Atomic method before this one?
        public int BuildReachableFrom(MapData data, int startIndex, bool allowDiagonals)
        {
            EnsureReachBuffers(data.CellCount);

            if (!data.IsValidCellIndex(startIndex) || data.IsBlocked[startIndex])
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
                data.IndexToXY(currentIndex, out int coordX, out int coordY);

                foreach (var (dirX, dirY) in Neighbors8)
                {
                    // need to match the A* diagonal toggle, otherwise might have an unreachable map but say it is reachable
                    if (!allowDiagonals && dirX != 0 && dirY != 0)
                        continue;

                    if (dirX != 0 && dirY != 0)  // need to change to match A*  // think I changed it but double check later
                    {
                        // Diagonal movement allowed only if at least one side is open
                        bool sideAOpen = data.TryCoordToIndex(coordX + dirX, coordY, out int sideIndexA) && !data.IsBlocked[sideIndexA];
                        bool sideBOpen = data.TryCoordToIndex(coordX, coordY + dirY, out int sideIndexB) && !data.IsBlocked[sideIndexB];

                        if (!sideAOpen && !sideBOpen)
                            continue;
                    }

                    TryEnqueue(coordX + dirX, coordY + dirY);
                }
            }

            return reachableCount;

            void TryEnqueue(int newX, int newY)
            {
                if (!data.TryCoordToIndex(newX, newY, out int ni)) return;
                if (data.IsBlocked[ni]) return;
                if (_reachStamp[ni] == _reachStampId) return;

                _reachStamp[ni] = _reachStampId;
                _bfsQueue[tail++] = ni;
                reachableCount++;
            }

        }

        // NOTE: Not an Atomic method, should only be exposed in a method that calles an Atomic method before this one?
        // pure stamp check
        public bool IsIndexReachableFromLastBuild(MapData data, int index)
        {
            return data.IsValidCellIndex(index)
                && _reachStamp != null
                && _reachStamp[index] == _reachStampId;
        }

        // NOTE: Not an Atomic method, should only be exposed in a method that calles an Atomic method before this one?
        // stamp check with strickter belt + suspenders safety guard
        public bool IsWalkableIndexReachableFromLastBuild(MapData data, int index)
        {
            return data.IsValidCellIndex(index)
                && !data.IsBlocked[index]
                && _reachStamp != null
                && _reachStamp[index] == _reachStampId;
        }

        
        // NOTE: Atomic method? safe to use and expose without considering considering stamg gen
        // check if point A and point B can reach eachother, by walkable tiles on the map
        public bool TryValidateReachablePair(MapData data, int startIndex, int goalIndex, bool allowDiagonals)
        {
            if (!data.IsValidCellIndex(startIndex) || !data.IsValidCellIndex(goalIndex)) return false;
            if (data.IsBlocked[startIndex] || data.IsBlocked[goalIndex]) return false;

            BuildReachableFrom(data, startIndex, allowDiagonals);
            return _reachStamp[goalIndex] == _reachStampId;
        }

        // NOTE: Atomic method? safe to use and expose without considering considering stamg gen
        // If I want the goal to be far-ish away, can also pick minManhattan as something like (_width + _height) / 4. 
        // This ensures the goal is at least a quarter of the board’s perimeter away from the start.
        public bool TryPickRandomReachableGoal(MapData data, Random goalRng, int startIndex, int minManhattan, bool allowDiagonals, out int goalIndex)
        {
            goalIndex = -1;

            int reachableCount = BuildReachableFrom(data, startIndex, allowDiagonals);
            if (reachableCount <= 1) return false;

            data.IndexToXY(startIndex, out int startX, out int startY);

            int candidateCount = 0;

            for (int i = 0; i < data.CellCount; i++)
            {
                if (data.IsBlocked[i]) continue;               // skip unwalkable cells
                if (_reachStamp[i] != _reachStampId) continue;  // if not reachable in current step
                if (i == startIndex) continue;                  // skip starting cell

                data.IndexToXY(i, out int cellX, out int cellY);
                int manhattan = Math.Abs(cellX - startX) + Math.Abs(cellY - startY);
                if (manhattan < minManhattan) continue;

                candidateCount++;

                // Reservoir sampling: each candidate has a 1/candidateCount chance to be selected
                if (goalRng.Next(candidateCount) == 0)
                    goalIndex = i;
            }

            return goalIndex != -1;
        }





    }
}
