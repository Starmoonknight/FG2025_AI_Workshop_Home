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
            => BuildReachableFromInternal_StopAt(data, startIndex, allowDiagonals, stopAtCount: int.MaxValue);

        /// <summary>
        /// Returns true if reachable cells from start are at least minReachableCells.
        /// NOTE: This may leave a PARTIAL stamp if it early-outs.
        /// </summary>
        public bool HasAtLeastReachable(MapData data, int startIndex, bool allowDiagonals, int minReachableCells)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (minReachableCells <= 0) return true; // requirement disabled
            int cellCount = data.CellCount;
            if ((uint)startIndex >= (uint)cellCount) return false;
            if (data.IsBlocked[startIndex]) return false;

            // clamp requirement to possible range
            if (minReachableCells > cellCount) minReachableCells = cellCount;
            if (minReachableCells <= 1) return true; // start cell counts as 1 (since already checked it's walkable, but if that changes in the future this can be a breaking point! Plan: make a separate version for obstacles reachability in future) 

            int reached = BuildReachableFromInternal_StopAt(data, startIndex, allowDiagonals, stopAtCount: minReachableCells);
            return reached >= minReachableCells;
        }

        public int BuildReachableFromInternal_StopAt(MapData data, int startIndex, bool allowDiagonals, int stopAtCount)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            int cellCount = data.CellCount;
            EnsureReachBuffers(cellCount);

            if ((uint)startIndex >= (uint)cellCount) return 0;

            var blocked = data.IsBlocked;
            if (blocked[startIndex]) return 0;

            if (stopAtCount <= 1) stopAtCount = 1;

            // Prevent stamp id overflow, rare but possible
            if (_reachStampId == int.MaxValue)
            {
                Array.Clear(_reachStamp, 0, _reachStamp.Length);
                _reachStampId = 0; // so next ++ becomes 1
            }

            int stamp = ++_reachStampId;
            int[] stampArr = _reachStamp;
            int[] queue = _bfsQueue;

            int width = data.Width;
            int height = data.Height;

            int head = 0;   // index of the next item to dequeue
            int tail = 0;   // index where the next item will be enqueued

            queue[tail++] = startIndex;
            stampArr[startIndex] = stamp;

            int reachableCount = 1;
            if (reachableCount >= stopAtCount) return reachableCount;

            while (head < tail)
            {
                int currentIndex = queue[head++];

                int y = currentIndex / width;
                int x = currentIndex - (y * width);

                // Cardinal neighbors
                if (x > 0)
                {
                    int ni = currentIndex - 1;
                    if (!blocked[ni] && stampArr[ni] != stamp)
                    {
                        stampArr[ni] = stamp;
                        queue[tail++] = ni;
                        if (++reachableCount >= stopAtCount) return reachableCount;
                    }
                }

                if (x + 1 < width)
                {
                    int ni = currentIndex + 1;
                    if (!blocked[ni] && stampArr[ni] != stamp)
                    {
                        stampArr[ni] = stamp;
                        queue[tail++] = ni;
                        if (++reachableCount >= stopAtCount) return reachableCount;
                    }
                }

                if (y > 0)
                {
                    int ni = currentIndex - width;
                    if (!blocked[ni] && stampArr[ni] != stamp)
                    {
                        stampArr[ni] = stamp;
                        queue[tail++] = ni;
                        if (++reachableCount >= stopAtCount) return reachableCount;
                    }
                }

                if (y + 1 < height)
                {
                    int ni = currentIndex + width;
                    if (!blocked[ni] && stampArr[ni] != stamp)
                    {
                        stampArr[ni] = stamp;
                        queue[tail++] = ni;
                        if (++reachableCount >= stopAtCount) return reachableCount;
                    }
                }


                // Diagonal movement allowed only if at least one side is open
                if (!allowDiagonals) continue;

                // Corner rule: diagonal allowed only if at least one side is open
                bool leftOpen  = x > 0 && !blocked[currentIndex - 1];
                bool rightOpen = x + 1 < width && !blocked[currentIndex + 1];
                bool downOpen  = y > 0 && !blocked[currentIndex - width];
                bool upOpen    = y + 1 < height && !blocked[currentIndex + width];

                // Down-left
                if (x > 0 && y > 0 && (leftOpen || downOpen))
                {
                    int ni = currentIndex - width - 1;
                    if (!blocked[ni] && stampArr[ni] != stamp)
                    {
                        stampArr[ni] = stamp;
                        queue[tail++] = ni;
                        if (++reachableCount >= stopAtCount) return reachableCount;
                    }
                }

                // Down-right
                if (x + 1 < width && y > 0 && (rightOpen || downOpen))
                {
                    int ni = currentIndex - width + 1;
                    if (!blocked[ni] && stampArr[ni] != stamp)
                    {
                        stampArr[ni] = stamp;
                        queue[tail++] = ni;
                        if (++reachableCount >= stopAtCount) return reachableCount;
                    }
                }

                // Up-left
                if (x > 0 && y + 1 < height && (leftOpen || upOpen))
                {
                    int ni = currentIndex + width - 1;
                    if (!blocked[ni] && stampArr[ni] != stamp)
                    {
                        stampArr[ni] = stamp;
                        queue[tail++] = ni;
                        if (++reachableCount >= stopAtCount) return reachableCount;
                    }
                }

                // Up-right
                if (x + 1 < width && y + 1 < height && (rightOpen || upOpen))
                {
                    int ni = currentIndex + width + 1;
                    if (!blocked[ni] && stampArr[ni] != stamp)
                    {
                        stampArr[ni] = stamp;
                        queue[tail++] = ni;
                        if (++reachableCount >= stopAtCount) return reachableCount;
                    }
                }
            }

            return reachableCount;
        }


        // NOTE: Not an Atomic method, should only be exposed in a method that calles an Atomic method before this one?
        // pure stamp check
        public bool IsIndexReachableFromLastBuild(MapData data, int index)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            return data.IsValidCellIndex(index)
                && _reachStamp != null
                && _reachStampId != 0
                && (uint)index < (uint)_reachStamp.Length
                && _reachStamp.Length == data.CellCount
                && _reachStamp[index] == _reachStampId;
        }

        // NOTE: Not an Atomic method, should only be exposed in a method that calles an Atomic method before this one?
        // stamp check with strickter belt + suspenders safety guard
        public bool IsWalkableIndexReachableFromLastBuild(MapData data, int index)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            return data.IsValidCellIndex(index)
                && !data.IsBlocked[index]
                && _reachStamp != null
                && _reachStampId != 0
                && (uint)index < (uint)_reachStamp.Length
                && _reachStamp.Length == data.CellCount
                && _reachStamp[index] == _reachStampId;
        }

        
        // NOTE: Atomic method? safe to use and expose without considering considering stamg gen
        // check if point A and point B can reach eachother, by walkable tiles on the map
        public bool TryValidateReachablePair(MapData data, int startIndex, int goalIndex, bool allowDiagonals)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

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
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (goalRng == null) throw new ArgumentNullException(nameof(goalRng));

            goalIndex = -1;

            int reachableCount = BuildReachableFrom(data, startIndex, allowDiagonals);
            if (reachableCount <= 1) return false;

            int width = data.Width;
            int height = data.Height;
            int startY = startIndex / width;
            int startX = startIndex - (startY * width);

            bool[] blocked = data.IsBlocked;
            int stamp = _reachStampId;
            int[] reach = _reachStamp;

            int candidateCount = 0;

            for (int y = 0, idx = 0; y < height; y++)
            {
                // where distY is the vertical distance from the start cell, which is constant for each row.
                // This allows computing the Manhattan distance more efficiently by calculating distY once per row instead of for every cell.
                // |x-startX|+|y-startY|=|x-startX|+dy
                int distY = Math.Abs(y - startY);

                for (int x = 0; x < width; x++, idx++)
                {
                    if (idx == startIndex) continue;        // skip starting cell
                    if (blocked[idx]) continue;             // skip unwalkable cells
                    if (reach[idx] != stamp) continue;      // if not reachable in current step

                    int manhattan = Math.Abs(x - startX) + distY;
                    if (manhattan < minManhattan) continue;

                    candidateCount++;

                    // Reservoir sampling: each candidate has a 1/candidateCount chance to be selected
                    if (goalRng.Next(candidateCount) == 0)
                        goalIndex = idx;
                }
            }

            return goalIndex != -1;
        }





    }
}
