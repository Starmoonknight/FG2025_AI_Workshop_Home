using System;


namespace AI_Workshop03
{


    // === Usage rules ===
    // 1) The stamp (StampArray/StampId) is a SHARED CACHE. Any Build... or Invalidate changes it for everyone.
    // 2) If needing a one-off answer, prefer self-contained methods (TryValidateReachablePair / TryPickRandomReachableGoal).
    // 3) If/When StampArray is exposed to other systems (debug overlay), remember to build ONE final full stamp after the map is finalized.
    // 4) When running partial checks (HasAtLeastReachable) and then continue changing the map, call InvalidateStamp() to prevent reuse.    (Currently done in the map generation)

    // Truth-stamp contract:
    // Only MapManager (or the owner of this MapReachability instance) should call BuildTruthStamp() at a well-defined time (e.g., end of generation).
    // After that, do not call Build/Invalidate until the next generation, if you want overlays/services to rely on the stamp / same instance of MapReachability.


    public class MapReachability
    {

        private int[] _bfsQueue;
        private int[] _reachStamp;
        private int _reachStampId;


        /// <summary>
        /// StampArray holds per-cell stamp markers. A cell is considered reachable in the current stamp if:
        /// StampArray[cell] == StampId.
        /// Treat this as a shared cache: any Build/Invalidate changes what "current" means.
        /// </summary>
        public int[] StampArray => _reachStamp;   // for debug overlays

        /// <summary>
        /// Current stamp token/version. Only meaningful when a full "truth stamp" has been built and not invalidated.
        /// </summary>
        public int StampId => _reachStampId;


        // Neighbor offsets for 8-directional movement, dirX stands for change in x, dirY for change in y
        private static readonly (int dirX, int dirY)[] Neighbors8 =
        {
            (-1,  0), ( 1,  0), ( 0, -1), ( 0,  1),     // Left, Right, Down, Up
            (-1, -1), (-1,  1), ( 1, -1), ( 1,  1),     // Bottom-Left, Bottom-Right, Top-Left, Top-Right
        };



        /// <summary>
        /// Ensures internal BFS buffers match the current map cell count.
        /// Allocates (or resizes) _bfsQueue and _reachStamp to exactly cellCount.
        /// Cost: may allocate GC memory when size changes.
        /// </summary>
        private void EnsureReachBuffers(int cellCount)
        {

            if (_bfsQueue == null || _bfsQueue.Length != cellCount)
                _bfsQueue = new int[cellCount];

            if (_reachStamp == null || _reachStamp.Length != cellCount)
                _reachStamp = new int[cellCount];
        }


        /// <summary>
        /// Invalidates the current stamp in O(1) by advancing StampId (monotonic invalidation). 
        /// After this, all stamp membership checks will return false until a new build is performed.
        /// Use after partial BFS checks or after mutating IsBlocked following a stamp build.
        /// </summary>
        public void InvalidateStamp()
        {
            // No buffers yet -> no usable stamp anyway
            if (_reachStamp == null)
            {
                _reachStampId = 0;
                return;
            }

            //on wrap, clear to avoid old values matching again when reuse IDs,, and reset to 0 so next Build => 1
            //
            // If we ever wrap/reuse IDs, we MUST clear to avoid old values matching again.
            if (_reachStampId == int.MaxValue)
            {
                Array.Clear(_reachStamp, 0, _reachStamp.Length);
                _reachStampId = 0; // next Build => 1
                return;
            }

            _reachStampId++; // monotonic invalidation
        }


        // NOTE: This has no functional difference, but used for semantic clarity for myself to separate the idea of "building a reusable truth stamp for later queries"
        // vs "running a one-off reachability check with a temporary stamp" in places like: after final map generation.
        /// <summary>
        /// Builds the final "truth stamp" intended for reuse (debug overlays/services) after map finalization.
        /// </summary>
        public void BuildTruthStamp(MapData data, int startIndex, bool allowDiagonals)
        {
            BuildReachableFrom(data, startIndex, allowDiagonals);
        }

        /// <summary>
        /// Builds a FULL reachability stamp from startIndex (no early-out).
        /// Writes the current StampId into each reachable cell in StampArray.
        /// Stand-alone call, but note: the stamp is a shared cache; any later build/invalidate overwrites validity.
        /// </summary>
        public int BuildReachableFrom(MapData data, int startIndex, bool allowDiagonals)
            => BuildReachableFromInternal_StopAt(data, startIndex, allowDiagonals, stopAtCount: int.MaxValue);

        /// <summary>
        /// Checks whether at least stopAt cells are reachable from startIndex.
        /// May early-out once the threshold is met, leaving a PARTIAL stamp in StampArray for the current StampId.
        /// Usage: treat stamp as INTERNAL/temporary unless you immediately consume it in the same method call.
        /// If callers might read StampArray/StampId later, call InvalidateStamp() after this check.
        /// </summary>
        public bool HasAtLeastReachable(MapData data, int startIndex, bool allowDiagonals, int stopAt, out int reached)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            reached = 0;

            if (stopAt <= 0) return true; // requirement disabled

            int cellCount = data.CellCount;
            if ((uint)startIndex >= (uint)cellCount) return false;
            if (data.IsBlocked[startIndex]) return false;

            // clamp requirement to possible range
            if (stopAt > cellCount) stopAt = cellCount;
            if (stopAt <= 1)
            {
                reached = 1;
                return true; // start cell counts as 1 (since already checked it's walkable, but if that changes in the future this can be a breaking point! Plan: make a separate version for obstacles reachability in future) 
            }

            reached = BuildReachableFromInternal_StopAt(data, startIndex, allowDiagonals, stopAtCount: stopAt);
            return reached >= stopAt;
        }

        /// <summary>
        /// Internal BFS that stamps reachable cells, optionally early-out at stopAtCount.
        /// Returns the number of reached cells (may be < full reachable if it early-outs).
        /// Not intended for external consumption; prefer BuildReachableFrom or HasAtLeastReachable.
        /// </summary>
        private int BuildReachableFromInternal_StopAt(MapData data, int startIndex, bool allowDiagonals, int stopAtCount)
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


        /// <summary>
        /// Pure stamp membership test against the CURRENT StampId.
        /// Returns true if index is marked reachable in the most recent stamp build and buffers match this MapData.
        /// Not stand-alone reliable unless you control stamp lifetime (i.e., no intervening Build/Invalidate).
        /// Good for debug overlays AFTER you have built a known "truth stamp".
        /// </summary>
        public bool IsIndexReachableFromCurrentStamp(MapData data, int index)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            return data.IsValidCellIndex(index)
                && _reachStamp != null
                && _reachStampId != 0
                && (uint)index < (uint)_reachStamp.Length
                && _reachStamp.Length == data.CellCount
                && _reachStamp[index] == _reachStampId;
        }

        /// <summary>
        /// Stamp membership test + additionally requires current data.IsBlocked[index] == false.
        /// Returns true if index is marked reachable in the most recent stamp build and buffers match this MapData.
        /// Not stand-alone reliable unless you control stamp lifetime (i.e., no intervening Build/Invalidate).
        /// Good for debug overlays AFTER you have built a known "truth stamp".
        /// </summary>
        public bool IsWalkableIndexReachableFromCurrentStamp(MapData data, int index)
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


        // check if point A and point B can reach eachother, by walkable tiles on the map
        /// <summary>
        /// Self-contained reachability check: builds a FULL stamp from startIndex, then checks goalIndex.
        /// Safe stand-alone call (builds+consumes stamp in the same call).
        /// Side-effect: overwrites the shared stamp cache (StampId/StampArray).
        /// </summary>
        public bool TryValidateReachablePair(MapData data, int startIndex, int goalIndex, bool allowDiagonals)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (!data.IsValidCellIndex(startIndex) || !data.IsValidCellIndex(goalIndex)) return false;
            if (data.IsBlocked[startIndex] || data.IsBlocked[goalIndex]) return false;

            BuildReachableFrom(data, startIndex, allowDiagonals);
            return _reachStamp[goalIndex] == _reachStampId;
        }


        // If I want the goal to be far-ish away, can also pick minManhattan as something like (_width + _height) / 4. 
        // This ensures the goal is at least a quarter of the board’s perimeter away from the start.
        /// <summary>
        /// Self-contained goal picker: builds a FULL stamp from startIndex, then reservoir-samples a reachable goal
        /// with Manhattan distance >= minManhattan.
        /// Safe stand-alone call (builds+consumes stamp in the same call).
        /// Side-effect: overwrites the shared stamp cache (StampId/StampArray).
        /// </summary>
        public bool TryPickRandomReachableGoal(MapData data, Random goalRng, int startIndex, int minManhattan, bool allowDiagonals, out int goalIndex)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (goalRng == null) throw new ArgumentNullException(nameof(goalRng));

            goalIndex = -1;

            int reachableCount = BuildReachableFrom(data, startIndex, allowDiagonals);
            if (reachableCount <= 1) return false;

            if (!data.IsValidCellIndex(startIndex)) return false;
            if (data.IsBlocked[startIndex]) return false;
            if (minManhattan < 0) minManhattan = 0;

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
