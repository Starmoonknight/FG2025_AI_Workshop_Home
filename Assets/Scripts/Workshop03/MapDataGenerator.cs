using System;
using System.Collections.Generic;
using UnityEngine;

using TerrainData = AI_Workshop02.TerrainData;
using PlacementMode = AI_Workshop02.PlacementMode;
using TerrainID = AI_Workshop02.TerrainID;


namespace AI_Workshop03
{

    public sealed class GenScratch
    {
        public int[] heat;
        public int[] queue;
        public int[] stamp;
        public int stampId;

        public int[] used;
        public readonly List<int> cells = new(4096);
        public readonly List<int> temp = new(2048);
    }


    // Version2 of BoardGenerator     -> MapDataGenerator
    public sealed class MapDataGenerator
    {

        private readonly GenScratch _scratch = new();

        private int _width;
        private int _height;
        private int _cellCount;

        private bool[] _blocked;
        private byte[] _terrainKind;
        private int[] _terrainCost;
        private Color32[] _baseColors;
        private byte[] _painterId;

        private Color32 _baseWalkableColor;
        private int _baseWalkableCost;

        private System.Random _rng;

        private static readonly (int dirX, int dirY)[] Neighbors4 =
        {
            (-1, 0), ( 1, 0), (0, -1), (0,  1)
        };

        public void Generate(
            int width,
            int height,
            bool[] blocked,
            byte[] terrainKind,
            int[] terrainCost,
            Color32[] baseColors,
            byte[] painterId,
            Color32 baseWalkableColor,
            int baseWalkableCost,
            int seed,
            TerrainData[] terrainData,
            int maxGenerateAttempts,
            float minUnblockedPercent,
            float minReachablePercent,
            Func<int, int> buildReachableFrom   // callback into BoardManager
        )
        {

            // --- Get reference pointers to current game board ---
            _width = width;
            _height = height;
            _cellCount = checked(width * height);

            _blocked = blocked ?? throw new ArgumentNullException(nameof(blocked));
            _terrainKind = terrainKind ?? throw new ArgumentNullException(nameof(terrainKind));
            _terrainCost = terrainCost ?? throw new ArgumentNullException(nameof(terrainCost));
            _baseColors = baseColors ?? throw new ArgumentNullException(nameof(baseColors));
            _painterId = painterId ?? throw new ArgumentNullException(nameof(painterId));

            if (_blocked.Length != _cellCount || _terrainKind.Length != _cellCount || _terrainCost.Length != _cellCount ||
                _baseColors.Length != _cellCount || _painterId.Length != _cellCount)
                throw new ArgumentException("Board arrays length mismatch.");

            _baseWalkableColor = baseWalkableColor;
            _baseWalkableCost = baseWalkableCost;

            _rng = new System.Random(seed);

            terrainData ??= Array.Empty<TerrainData>();


            // --- Organize all terrains in use  ---
            Array.Sort(terrainData, (a, b) => (a?.Order ?? 0).CompareTo(b?.Order ?? 0));


            var terrainDataId = new Dictionary<TerrainData, byte>(terrainData.Length);            // terrainId is assigned by list order after sorting, 0 reserved for base
            byte nextId = 1;
            for (int i = 0; i < terrainData.Length; i++)
            {
                if (terrainData[i] == null) continue;
                if (nextId == byte.MaxValue) break;                                         // safety cap, if too many terrain types are listed 
                terrainDataId[terrainData[i]] = nextId++;
            }

            int startIndex = CoordToIndex(_width / 2, _height / 2);

            // --- Atempt to generate base game map ---
            for (int attempt = 0; attempt < Math.Max(1, maxGenerateAttempts); attempt++)    // attempt placement loop
            {
                ResetToBase();

                for (int i = 0; i < terrainData.Length; i++)                               // obstacle placement before other terrain types
                {
                    var terrain = terrainData[i];
                    if (terrain == null || !terrain.IsObstacle) continue;

                    if (!terrainDataId.TryGetValue(terrain, out byte id))
                        continue;
                    ApplyTerrainData(terrain, id, isObstacle: true);
                }

                if (_blocked[startIndex])                                                   // BuildReachableFrom() will use startIndex to validate the board’s navigability,
                {
                    // pick ANY unblocked cell as BFS seed instead of failing the attempt   
                    if (!TryPickRandomUnBlocked(out startIndex, 256, requireBase: false))   // that BFS needs a walkable starting node to measure “how connected is this map” from center,
                        continue;                                                           // if it fails, generate a new map. 
                }



                // if it fails, generate a new map. 

                int walkableCount = CountWalkable();
                if (walkableCount <= 0) continue;

                float unblockedPercent = walkableCount / (float)_cellCount;
                if (unblockedPercent < minUnblockedPercent) continue;                        // make sure map don't have to many obstacles placed

                int reachableCount = buildReachableFrom(startIndex);
                float reachablePercent = reachableCount / (float)walkableCount;

                if (reachablePercent >= minReachablePercent)
                {
                    ResetWalkableToBaseOnly();                                              // reset walkable tiles to base visuals/cost/id so terrain can build from clean base

                    for (int i = 0; i < terrainData.Length; i++)
                    {
                        var terrain = terrainData[i];
                        if (terrain == null || terrain.IsObstacle) continue;

                        if (!terrainDataId.TryGetValue(terrain, out byte id))
                            continue;
                        ApplyTerrainData(terrain, id, isObstacle: false);
                    }

                    return;
                }
            }

            // --- Fallback if to many attempts, keep last version and ensure walkable visuals are consistent ---
            ResetWalkableToBaseOnly();
            for (int i = 0; i < terrainData.Length; i++)
            {
                var terrain = terrainData[i];
                if (terrain == null || terrain.IsObstacle) continue;

                if (!terrainDataId.TryGetValue(terrain, out byte id))
                    continue;
                ApplyTerrainData(terrain, id, isObstacle: false);
            }
        }




        #region Cell Data

        private void ApplyTerrainData(TerrainData terrain, byte terrainLayerId, bool isObstacle)
        {
            _scratch.cells.Clear();

            switch (terrain.Mode)                                                              // what "paint brush" is used to generate this tiles structure
            {
                case PlacementMode.Static:
                    ExpandRandomStatic(terrain, _scratch.cells);
                    break;

                case PlacementMode.Blob:
                    GenerateBlobs(terrain, _scratch.cells);
                    break;

                case PlacementMode.Lichtenberg:
                    GenerateLichtenberg(terrain, terrainLayerId, _scratch.cells);
                    break;
            }

            if (_scratch.cells.Count == 0) return;

            if (isObstacle)
                ApplyObstacles(terrain, terrainLayerId, _scratch.cells);
            else
                ApplyTerrain(terrain, terrainLayerId, _scratch.cells);
        }


        private void GenerateBlobs(TerrainData terrain, List<int> outCells)
        {
            outCells.Clear();

            int desiredCells = Mathf.RoundToInt(terrain.CoveragePercent * _cellCount);
            if (desiredCells <= 0) return;

            int avgSize = Mathf.Max(1, terrain.Blob.AvgSize);
            int blobCount = desiredCells / avgSize;
            blobCount = Mathf.Clamp(blobCount, terrain.Blob.MinSeparateBlobs, terrain.Blob.MaxSeparateBlobs);

            for (int b = 0; b < blobCount; b++)
            {
                if (!TryPickRandomValidCell(terrain, out int seed, 256))
                    break;

                int size = avgSize + _rng.Next(-terrain.Blob.SizeJitter, terrain.Blob.SizeJitter + 1);
                size = Mathf.Max(10, size);

                _scratch.temp.Clear();
                ExpandRandomBlob(terrain, seed, size, _scratch.temp);
                outCells.AddRange(_scratch.temp);
            }
        }


        private void GenerateLichtenberg(TerrainData terrain, byte dataLayerId, List<int> outCells)
        {

            outCells.Clear();
            EnsureGenBuffers();
            Array.Clear(_scratch.heat, 0, _cellCount);

            int usedId = NextMarkId();

            int desiredCells = Mathf.RoundToInt(terrain.CoveragePercent * _cellCount);
            if (desiredCells <= 0) return;

            int perPath = Mathf.Max(1, terrain.Lichtenberg.CellsPerPath);
            int pathCount = desiredCells / perPath;
            pathCount = Mathf.Clamp(pathCount, terrain.Lichtenberg.MinSeperatePaths, terrain.Lichtenberg.MaxSeparatePaths);

            int maxSteps = Mathf.RoundToInt((_width + _height) * terrain.Lichtenberg.StepsScale);


            for (int r = 0; r < pathCount; r++)
            {
                int start;
                int goal;

                if (terrain.Lichtenberg.RequireOppositeEdgePair)
                {
                    if (!TryPickOppositeEdgePair(terrain, out int startIdx, out int goalIdx, 256)) break;
                    start = startIdx;
                    goal = goalIdx;
                }
                else
                {
                    if (!TryPickRandomValidCell(terrain, out int startIdx, 256)) break;
                    if (!TryPickRandomValidCell(terrain, out int goalIdx, 256)) break;
                    start = startIdx;
                    goal = goalIdx;
                }


                _scratch.temp.Clear();
                ExpandRandomLichtenberg(terrain, dataLayerId, usedId, start, goal, maxSteps, _scratch.temp);

                // if widen passes are set above zero 
                for (int p = 0; p < terrain.Lichtenberg.WidenPasses; p++)
                    WidenOnce(terrain, _scratch.temp);

                // remember used tiles for heat maping 
                for (int i = 0; i < _scratch.temp.Count; i++)
                    _scratch.used[_scratch.temp[i]] = usedId;

                outCells.AddRange(_scratch.temp);
            }
        }

        #endregion


        #region Update and Overwrite Cell Data

        private void ApplyTerrain(TerrainData terrain, byte terrainLayerId, List<int> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                int index = cells[i];
                if (!IsValidCell(index)) continue;
                if (!CanUseCell(terrain, index)) continue;

                if (_blocked[index] && terrain.AllowOverwriteObstacle)
                    _blocked[index] = false;

                _terrainKind[index] = (byte)terrain.TerrainID;
                _terrainCost[index] = terrain.Cost;
                _baseColors[index] = terrain.Color;
                _painterId[index] = terrainLayerId;
            }
        }

        private void ApplyObstacles(TerrainData terrain, byte terrainLayerId, List<int> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                int index = cells[i];
                if (!IsValidCell(index)) continue;

                if (!CanUseCell(terrain, index)) continue;

                _blocked[index] = true;
                _terrainKind[index] = (byte)terrain.TerrainID;
                _terrainCost[index] = 0;
                _painterId[index] = terrainLayerId;
                _baseColors[index] = terrain.Color;
            }
        }

        #endregion


        #region Expansion Algorithms  - rng and modifier 

        private void ExpandRandomStatic(
            TerrainData terrain,
            List<int> outCells)
        {
            outCells.Clear();

            float coverage = Mathf.Clamp01(terrain.CoveragePercent);
            if (coverage <= 0) return;

            _scratch.temp.Clear();
            for (int i = 0; i < _cellCount; i++)
            {
                if (!CanUseCell(terrain, i)) continue;
                _scratch.temp.Add(i);
            }

            int eligible = _scratch.temp.Count;
            if (eligible == 0) return;

            int target = Mathf.RoundToInt(coverage * eligible);
            if (target <= 0) return;
            if (target > eligible) target = eligible;


            // Partial Fisher-Yates: pick 'target' unique cells     // Note to self; look more into Fisher-Yates and Partial Fisher-Yates later
            for (int k = 0; k < target; k++)
            {
                int swap = _rng.Next(k, eligible);
                (_scratch.temp[k], _scratch.temp[swap]) = (_scratch.temp[swap], _scratch.temp[k]);
                outCells.Add(_scratch.temp[k]);
            }

        }


        private void ExpandRandomBlob(
            TerrainData terrain,
            int seedIndex,
            int maxCells,
            List<int> outCells)
        {
            outCells.Clear();
            if (!IsValidCell(seedIndex)) return;
            if (!CanUseCell(terrain, seedIndex)) return;

            EnsureGenBuffers();
            int stampId = NextMarkId();

            int head = 0;
            int tail = 0;

            _scratch.stamp[seedIndex] = stampId;
            _scratch.queue[tail++] = seedIndex;
            outCells.Add(seedIndex);

            float growChance = Mathf.Clamp01(terrain.Blob.ExpansionChance);
            int smoothPasses = terrain.Blob.SmoothPasses;
            maxCells = Mathf.Max(1, maxCells);

            // BFS-like growth
            while (head < tail && outCells.Count < maxCells)
            {
                int current = _scratch.queue[head++];
                IndexToXY(current, out int x, out int y);

                for (int neighbor = 0; neighbor < Neighbors4.Length; neighbor++)
                {
                    var (dirX, dirY) = Neighbors4[neighbor];
                    if (!TryCoordToIndex(x + dirX, y + dirY, out int next)) continue;
                    if (_scratch.stamp[next] == stampId) continue;
                    if (!CanUseCell(terrain, next)) continue;
                    if (_rng.NextDouble() > growChance) continue;

                    _scratch.stamp[next] = stampId;
                    _scratch.queue[tail++] = next;
                    outCells.Add(next);

                    if (outCells.Count >= maxCells) break;
                }
            }

            // Smoothing passes to fill in small gaps
            for (int pass = 0; pass < smoothPasses; pass++)
            {
                int before = outCells.Count;
                for (int i = 0; i < before; i++) _scratch.queue[i] = outCells[i];

                for (int i = 0; i < before && outCells.Count < maxCells; i++)
                {
                    int current = _scratch.queue[i];
                    IndexToXY(current, out int x, out int y);

                    for (int neighbor = 0; neighbor < Neighbors4.Length && outCells.Count < maxCells; neighbor++)
                    {
                        var (dirX, dirY) = Neighbors4[neighbor];
                        if (!TryCoordToIndex(x + dirX, y + dirY, out int next)) continue;
                        if (_scratch.stamp[next] == stampId) continue;
                        if (!CanUseCell(terrain, next)) continue;

                        _scratch.stamp[next] = stampId;
                        outCells.Add(next);
                    }
                }
            }
        }


        private void ExpandRandomLichtenberg(
            TerrainData terrain,
            byte terrainPaintId,
            int usedId,
            int startIndex,
            int targetIndex,
            int maxSteps,
            List<int> outCells)
        {
            outCells.Clear();
            if (!IsValidCell(startIndex) || !IsValidCell(targetIndex)) return;
            if (!CanUseCell(terrain, startIndex)) return;
            if (!CanUseCell(terrain, targetIndex)) return;

            EnsureGenBuffers();
            int stampId = NextMarkId();


            bool preferUnused = terrain.Lichtenberg.PreferUnusedCells;
            bool allowReuse = terrain.Lichtenberg.AllowReuseIfStuck;
            float towardTargetBias = Mathf.Clamp01(terrain.Lichtenberg.GrowthTowardTargetBias);
            float branchChance = Mathf.Clamp01(terrain.Lichtenberg.BranchChance);
            int maxWalkers = Mathf.Clamp(terrain.Lichtenberg.MaxWalkers, 1, 64);
            maxSteps = Mathf.Max(1, maxSteps);

            int walkerCount = 1;
            _scratch.queue[0] = startIndex;

            _scratch.stamp[startIndex] = stampId;
            outCells.Add(startIndex);

            IndexToXY(targetIndex, out int targetX, out int targetY);

            for (int step = 0; step < maxSteps; step++)
            {
                int walkerThisStep = step % walkerCount;
                int current = _scratch.queue[walkerThisStep];

                if (current == targetIndex) break;

                IndexToXY(current, out int x, out int y);

                int stepX = Math.Sign(targetX - x);
                int stepY = Math.Sign(targetY - y);

                // Code memo to remember new words;
                // Span is a stack only ref struct
                // Stackalloc allocates a block of memory on the stack, not the heap. It’s extremely fast and automatically freed when the method scope ends (no GC, no pooling).    
                Span<(int dirX, int dirY)> candidates = stackalloc (int, int)[8];
                int cCount = 0;

                bool hasX = stepX != 0;
                bool hasY = stepY != 0;

                if (hasX) candidates[cCount++] = (stepX, 0);
                if (hasY) candidates[cCount++] = (0, stepY);

                if (hasX) { candidates[cCount++] = (stepX, 1); candidates[cCount++] = (stepX, -1); }
                if (hasY) { candidates[cCount++] = (1, stepY); candidates[cCount++] = (-1, stepY); }

                if (hasX) candidates[cCount++] = (-stepX, 0);
                if (hasY) candidates[cCount++] = (0, -stepY);


                int nextIndex = -1;

                // it tries two passes to avoid clumping paths into a ball of spaghetti: unused-first, then allow used if stuck.
                for (int pass = 0; pass < 2 && nextIndex < 0; pass++)
                {
                    bool allowUsedThisPass = (pass == 1);

                    if (allowUsedThisPass && (!allowReuse || !preferUnused))
                        break;

                    float bestScore = float.NegativeInfinity;
                    int bestCand = -1;

                    // for simple shuffle-ish by random start
                    int startC = _rng.Next(0, Math.Max(1, cCount));

                    for (int k = 0; k < cCount; k++)
                    {
                        int c = (startC + k) % cCount;
                        var (dirX, dirY) = candidates[c];

                        int cx = x + dirX;
                        int cy = y + dirY;

                        if (!TryCoordToIndex(cx, cy, out int cand)) continue;
                        if (!CanUseCell(terrain, cand)) continue;

                        bool alreadyInThisRoad = (_scratch.stamp[cand] == stampId);
                        if (alreadyInThisRoad)
                            continue;

                        bool usedByEarlierPaths = (_scratch.used[cand] == usedId);
                        bool usedByThisTerrainAlready = (_painterId[cand] == terrainPaintId);

                        bool used = usedByEarlierPaths || usedByThisTerrainAlready;
                        if (!allowUsedThisPass && used)
                            continue;

                        // heat scoring to biase choise
                        int heat = _scratch.heat[cand];
                        float repel = terrain.Lichtenberg.RepelStrength * heat;

                        // penalize stepping on cells allready targeted 
                        if (terrain.Lichtenberg.RepelFromExisting && usedByThisTerrainAlready)
                            repel += terrain.Lichtenberg.ExistingPenalty;

                        int candH = Math.Max(Math.Abs(targetX - cx), Math.Abs(targetY - cy));

                        // allow towardTargetBias to influence scoring
                        bool toward =
                            (dirX == stepX && dirY == 0) ||
                            (dirX == 0 && dirY == stepY);

                        float towardBonus = toward ? (towardTargetBias * 0.35f) : -(towardTargetBias * 0.15f);

                        float noise = (float)_rng.NextDouble() * 0.25f;

                        float score = (-candH) + towardBonus + noise - repel;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestCand = cand;
                        }
                    }

                    if (bestCand >= 0)
                        nextIndex = bestCand;
                }

                if (nextIndex < 0) break;

                _scratch.queue[walkerThisStep] = nextIndex;

                // update heat after a walker moves
                AddHeat(nextIndex, terrain.Lichtenberg.RepelRadius, terrain.Lichtenberg.HeatAdd, terrain.Lichtenberg.HeatFalloff);

                if (_scratch.stamp[nextIndex] != stampId)
                {
                    _scratch.stamp[nextIndex] = stampId;
                    outCells.Add(nextIndex);
                }

                if (walkerCount < maxWalkers && _rng.NextDouble() <= branchChance)
                {
                    _scratch.queue[walkerCount++] = nextIndex;
                }
            }
        }

        private void WidenOnce(TerrainData terrain, List<int> cells)
        {
            EnsureGenBuffers();
            int stampId = NextMarkId();

            // Mark existing
            for (int i = 0; i < cells.Count; i++)
            {
                int index = cells[i];
                if (IsValidCell(index))
                    _scratch.stamp[index] = stampId;
            }

            int originalCount = cells.Count;
            for (int i = 0; i < originalCount; i++)
            {
                int current = cells[i];
                IndexToXY(current, out int x, out int y);

                for (int neighbor = 0; neighbor < Neighbors4.Length; neighbor++)
                {
                    var (dirX, dirY) = Neighbors4[neighbor];
                    if (!TryCoordToIndex(x + dirX, y + dirY, out int next)) continue;
                    if (_scratch.stamp[next] == stampId) continue;
                    if (!CanUseCell(terrain, next)) continue;

                    _scratch.stamp[next] = stampId;
                    cells.Add(next);
                }
            }
        }

        #endregion


        #region Pickers

        private bool TryPickRandomUnBlocked(out int index, int tries, bool requireBase)
        {
            index = -1;

            for (int t = 0; t < tries; t++)
            {
                int i = _rng.Next(0, _cellCount);
                if (_blocked[i]) continue;
                if (requireBase && _painterId[i] != 0) continue;
                index = i;
                return true;
            }

            return false;

        }

        private bool TryPickRandomValidCell(TerrainData terrain, out int index, int tries)
        {
            index = -1;

            for (int t = 0; t < tries; t++)
            {
                int i = _rng.Next(0, _cellCount);
                if (!CanPickCell(terrain, i)) continue;
                index = i;
                return true;
            }

            return false;
        }

        private bool TryPickRandomInteriorValidCell(TerrainData terrain, int margin, out int index, int tries)
        {
            index = -1;
            margin = Mathf.Clamp(margin, 0, Mathf.Min(_width, _height) / 2);

            for (int t = 0; t < tries; t++)
            {
                int x = _rng.Next(margin, _width - margin);
                int y = _rng.Next(margin, _height - margin);
                int i = CoordToIndex(x, y);
                if (!CanPickCell(terrain, i)) continue;
                index = i;
                return true;
            }
            return false;
        }

        private bool TryPickRandomEdgeValidCell(TerrainData terrain, out int index, int tries)
        {
            index = -1;

            for (int t = 0; t < tries; t++)
            {
                int side = _rng.Next(0, 4);
                int x;
                int y;

                switch (side)
                {
                    case 0: x = 0; y = _rng.Next(0, _height); break;  // left
                    case 1: x = _width - 1; y = _rng.Next(0, _height); break;  // right
                    case 2: x = _rng.Next(0, _width); y = 0; break;  // bottom
                    default: x = _rng.Next(0, _width); y = _height - 1; break;  // top
                }

                int i = CoordToIndex(x, y);
                if (!CanPickCell(terrain, i)) continue;
                index = i;
                return true;
            }
            return false;
        }

        private bool TryPickOppositeEdgePair(TerrainData terrain, out int start, out int goal, int tries)
        {
            start = -1;
            goal = -1;

            for (int t = 0; t < tries; t++)
            {
                int side = _rng.Next(0, 4);

                int sideX;
                int sideY;
                switch (side)
                {
                    case 0: sideX = 0; sideY = _rng.Next(0, _height); break;          // left
                    case 1: sideX = _width - 1; sideY = _rng.Next(0, _height); break;          // right
                    case 2: sideX = _rng.Next(0, _width); sideY = 0; break;          // bottom
                    default: sideX = _rng.Next(0, _width); sideY = _height - 1; break;          // top
                }

                int sideCand = CoordToIndex(sideX, sideY);
                if (!CanPickCell(terrain, sideCand)) continue;


                int opositSide = side ^ 1;  // 0<->1, 2<->3

                int goalSideX;
                int goalSideY;
                switch (opositSide)
                {
                    case 0: goalSideX = 0; goalSideY = _rng.Next(0, _height); break;
                    case 1: goalSideX = _width - 1; goalSideY = _rng.Next(0, _height); break;
                    case 2: goalSideX = _rng.Next(0, _width); goalSideY = 0; break;
                    default: goalSideX = _rng.Next(0, _width); goalSideY = _height - 1; break;
                }

                int goalCand = CoordToIndex(goalSideX, goalSideY);
                if (!CanPickCell(terrain, goalCand)) continue;

                start = sideCand;
                goal = goalCand;
                return true;
            }

            return false;
        }

        private bool CanUseCell(TerrainData terrain, int idx)
        {
            // Hard-block overwrite policy
            if (_blocked[idx])
                return terrain.AllowOverwriteObstacle;  //  (obstacle overwrite)

            // Terrain overwrite gating (painterId = layer; 0 = base)        
            bool isBase = (_painterId[idx] == 0);

            if (terrain.OnlyAffectBase)                 // can this only effect base terrain tile?      (terrain overwrite)
                return isBase;

            if (!terrain.AllowOverwriteTerrain)         // if terrain is not a base tile, may it overwrite it?     (terrain overwrite)
                return isBase;

            return true;
        }

        private bool CanPickCell(TerrainData terrain, int idx)
        {
            if (terrain.ForceUnblockedSeed && _blocked[idx]) return false;
            return CanUseCell(terrain, idx);
        }

        #endregion


        #region Reset Helpers

        private void ResetToBase()
        {
            EnsureGenBuffers();

            for (int i = 0; i < _cellCount; i++)
            {
                _blocked[i] = false;
                _terrainKind[i] = (byte)TerrainID.Land;
                _terrainCost[i] = _baseWalkableCost;
                _baseColors[i] = _baseWalkableColor;
                _painterId[i] = 0;
            }
        }

        private void ResetWalkableToBaseOnly()
        {
            for (int i = 0; i < _cellCount; i++)
            {
                if (_blocked[i]) continue;

                _terrainKind[i] = (byte)TerrainID.Land;
                _terrainCost[i] = _baseWalkableCost;
                _baseColors[i] = _baseWalkableColor;
                _painterId[i] = 0;
            }
        }

        private int CountWalkable()
        {
            int count = 0;
            for (int i = 0; i < _cellCount; i++)
                if (!_blocked[i]) count++;
            return count;
        }

        #endregion


        #region Internal Helpers, Coordinates and Stamp data 

        private bool IsValidCell(int index) => (uint)index < (uint)_cellCount;

        private int CoordToIndex(int x, int y) => x + y * _width;

        private bool TryCoordToIndex(int x, int y, out int index)
        {
            if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
            {
                index = -1;
                return false;
            }
            index = x + y * _width;
            return true;
        }

        private void IndexToXY(int index, out int x, out int y)
        {
            x = index % _width;
            y = index / _width;
        }

        private Vector3 IndexToWorldCenterXZ(int index, float y = 0f)
        {
            IndexToXY(index, out int x, out int z);
            return new Vector3(x + 0.5f, y, z + 0.5f);
        }

        private int HeuristicManhattan(int a, int b)
        {
            IndexToXY(a, out int ax, out int ay);
            IndexToXY(b, out int bx, out int by);
            return Math.Abs(ax - bx) + Math.Abs(ay - by);
        }

        private void EnsureGenBuffers()
        {
            if (_scratch.queue == null || _scratch.queue.Length != _cellCount)
                _scratch.queue = new int[_cellCount];

            if (_scratch.stamp == null || _scratch.stamp.Length != _cellCount)
                _scratch.stamp = new int[_cellCount];

            if (_scratch.used == null || _scratch.used.Length != _cellCount)
                _scratch.used = new int[_cellCount];

            if (_scratch.heat == null || _scratch.heat.Length != _cellCount)
                _scratch.heat = new int[_cellCount];
        }

        private int NextMarkId()
        {
            _scratch.stampId++;
            if (_scratch.stampId == int.MaxValue)
            {
                Array.Clear(_scratch.stamp, 0, _scratch.stamp.Length);
                Array.Clear(_scratch.used, 0, _scratch.used.Length);
                _scratch.stampId = 1;
            }
            return _scratch.stampId;
        }

        private void AddHeat(int idx, int radius, int add, int falloff)
        {
            _scratch.heat[idx] += add;

            if (radius <= 0) return;

            IndexToXY(idx, out int x, out int y);
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (!TryCoordToIndex(x + dx, y + dy, out int n)) continue;

                    int manhattanDist = Mathf.Abs(dx) + Mathf.Abs(dy);
                    int v = Mathf.Max(0, add - falloff * manhattanDist);
                    _scratch.heat[n] += v;
                }
        }

        #endregion

    }


}
