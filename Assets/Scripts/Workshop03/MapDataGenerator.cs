using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

using AreaFocusWeights = AI_Workshop03.TerrainTypeData.AreaFocusWeights;

#if UNITY_EDITOR
using UnityEditor;
#endif


namespace AI_Workshop03
{

    public sealed class GenScratch
    {
        public int[] heat;      // temp int storage
        public int[] poolPos;   // temp int storage
        public int[] queue;     // buffer
        public int stampId;

        public int[] stamp;     // stamp based marker
        public int[] used;      // stamp based marker
        public readonly List<int> cells = new(4096);
        public readonly List<int> temp = new(2048);     // buffer
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


        public bool Debug_DumpFocusWeights { get; set; } = false;
        public bool Debug_DumpFocusWeightsVerbose { get; set; } = false;


        private static readonly (int dirX, int dirY)[] Neighbors4 =
        {
            (-1, 0), ( 1, 0), (0, -1), (0,  1)
        };

        private static readonly (int dirX, int dirY)[] Neighbors8 =
        {
            (-1, 0), ( 1, 0), (0, -1), (0,  1),
            (-1,-1), (-1, 1), (1, -1), (1,  1)
        };

        private static int OppositeSide(int side) => side ^ 1; // 0<->1, 2<->3



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
            TerrainTypeData[] terrainData,
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

            terrainData ??= Array.Empty<TerrainTypeData>();


            // --- Generate Debug Data  ---
            DebugDumpFocusWeights(seed, terrainData);


            // --- Organize all terrains in use  ---
            Array.Sort(terrainData, (a, b) => (a?.Order ?? 0).CompareTo(b?.Order ?? 0));


            var terrainDataId = new Dictionary<TerrainTypeData, byte>(terrainData.Length);            // terrainId is assigned by list order after sorting, 0 reserved for base
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

        private void ApplyTerrainData(TerrainTypeData terrain, byte terrainLayerId, bool isObstacle)
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



        private void GenerateBlobs(TerrainTypeData terrain, List<int> outCells)
        {
            outCells.Clear();

            int desiredCells = Mathf.RoundToInt(terrain.CoveragePercent * _cellCount);
            if (desiredCells <= 0) return;

            int avgSize = Mathf.Max(1, terrain.Blob.AvgBlobSize);
            int blobCount = desiredCells / avgSize;
            blobCount = Mathf.Clamp(blobCount, terrain.Blob.MinBlobCount, terrain.Blob.MaxBlobCount);

            for (int b = 0; b < blobCount; b++)
            {
                if (!TryPickCell_ByFocusArea(terrain, terrain.Blob.PlacementArea, terrain.Blob.PlacementWeights, out int seed, 256))
                    break;

                int size = avgSize + _rng.Next(-terrain.Blob.BlobSizeJitter, terrain.Blob.BlobSizeJitter + 1);
                size = Mathf.Max(10, size);

                _scratch.temp.Clear();
                ExpandRandomBlob(terrain, seed, size, _scratch.temp);
                outCells.AddRange(_scratch.temp);
            }
        }


        private void GenerateLichtenberg(TerrainTypeData terrain, byte dataLayerId, List<int> outCells)
        {

            outCells.Clear();
            EnsureGenBuffers();
            Array.Clear(_scratch.heat, 0, _cellCount);

            int usedId = NextMarkId();

            int desiredCells = Mathf.RoundToInt(terrain.CoveragePercent * _cellCount);
            if (desiredCells <= 0) return;

            int perPath = Mathf.Max(1, terrain.Lichtenberg.CellsPerPath);
            int pathCount = desiredCells / perPath;
            pathCount = Mathf.Clamp(pathCount, terrain.Lichtenberg.MinPathCount, terrain.Lichtenberg.MaxPathCount);

            int maxSteps = Mathf.RoundToInt((_width + _height) * terrain.Lichtenberg.StepBudgetScale);


            for (int r = 0; r < pathCount; r++)
            {
                int start;
                int goal;

                if (terrain.Lichtenberg.UseEdgePairPresets)
                {
                    if (!TryPickCell_EdgePairPreset(terrain, terrain.Lichtenberg.EdgePairMode, out start, out goal, 256)) 
                        break;
                }
                else
                {
                    if (!TryPickCell_ByFocusArea(terrain, terrain.Lichtenberg.OriginArea, terrain.Lichtenberg.OriginWeights, out start, 256)) 
                        break;

                    if (!TryPickCell_ByFocusArea(terrain, terrain.Lichtenberg.GrowthAimArea, terrain.Lichtenberg.GrowthAimWeights, out goal, 256)) 
                        break;
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

        private void ApplyTerrain(TerrainTypeData terrain, byte terrainLayerId, List<int> cells)
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

        private void ApplyObstacles(TerrainTypeData terrain, byte terrainLayerId, List<int> cells)
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
            TerrainTypeData terrain,
            List<int> outCells)
        {
            outCells.Clear();

            float coverage = Mathf.Clamp01(terrain.CoveragePercent);
            if (coverage <= 0) return;

            EnsureGenBuffers();

            ExpansionAreaFocus placement = terrain.Static.PlacementArea;
            TerrainTypeData.AreaFocusWeights weights = terrain.Static.PlacementWeights;
            int margin = ComputeInteriorMarginCells(in weights);

            float clusterBias = Mathf.Clamp01(terrain.Static.ClusterBias);

            ExpansionAreaFocus poolFilter =
                (placement == ExpansionAreaFocus.Weighted) ? ExpansionAreaFocus.Anywhere : placement;

            _scratch.temp.Clear();
            int poolMark = NextMarkId();


            for (int i = 0; i < _cellCount; i++)
            {
                if (!CanUseCell(terrain, i)) continue;
                if (!MatchesFocus(i, poolFilter, margin)) continue;

                int pos = _scratch.temp.Count;
                _scratch.temp.Add(i);

                _scratch.used[i] = poolMark;
                _scratch.poolPos[i] = pos; 
            }

            int eligible = _scratch.temp.Count;
            if (eligible == 0) return;

            int targetTotal = Mathf.RoundToInt(coverage * _cellCount);
            int target = Mathf.Min(targetTotal, eligible); 
            if (target <= 0) return;


            // Selection picking eligible candidates

            if (clusterBias <= 0f)
            {

                if (placement == ExpansionAreaFocus.Weighted)
                {
                    // for weighted search of viable cells 
                    for (int k = 0; k < target; k++)
                    {
                        ExpansionAreaFocus pickFocus = RollWeightedFocus(in weights, _rng);

                        int focusTries = Mathf.Clamp(_scratch.temp.Count / 8, 8, 64);
                        if (!TryPickFromPool_ByFocus(pickFocus, margin, focusTries, out int pickedIdx))
                            break;

                        RemoveFromPool(pickedIdx, poolMark);
                        outCells.Add(pickedIdx);
                    }
                    return;
                }

                // non-weighted uniform pick by Partial Fisher-Yates: pick 'target' unique cells     // Note to self; look more into Fisher-Yates and Partial Fisher-Yates later
                for (int k = 0; k < target; k++)
                {
                    int swap = _rng.Next(k, eligible);
                    (_scratch.temp[k], _scratch.temp[swap]) = (_scratch.temp[swap], _scratch.temp[k]);
                    outCells.Add(_scratch.temp[k]);
                }
                return;
            }


            // should scale with map size
            int loose = Mathf.Max(4, Mathf.RoundToInt(Mathf.Min(_width, _height) * 0.02f));
            int tight = 2;
            // higer bias should make the static expansion cluster more into groups 
            int maxRadius = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(loose, tight, clusterBias)));

            int nearTries = Mathf.Max(8, Mathf.RoundToInt(Mathf.Lerp(8f, 32f, clusterBias)));
            for (int k = 0; k < target; k++)
            {
                if (_scratch.temp.Count == 0) break;

                ExpansionAreaFocus pickFocus =
                    (placement == ExpansionAreaFocus.Weighted)
                        ? RollWeightedFocus(in weights, _rng)
                        : placement;

                int pickedIdx = -1;

                // tries cluster-near pick first inside enforced focus region
                if (outCells.Count > 0 && _rng.NextDouble() < clusterBias)
                {
                    if (TryPickCell_NearExisting(outCells, poolMark, maxRadius, nearTries,
                            out int near,
                            requiredArea: pickFocus,
                            interiorMargin: margin))
                    {
                        pickedIdx = near;
                    }
                }

                // if clustering didn’t pick anything, pick from pool respecting focus
                if (pickedIdx < 0)
                {
                    // the tries = how hard it will try to find a cell that matches focus area from available in pool
                    int focusTries = Mathf.Clamp(_scratch.temp.Count / 8, 8, 64);

                    if (!TryPickFromPool_ByFocus(pickFocus, margin, focusTries, out pickedIdx))
                        break; // pool empty or something very wrong
                }

                RemoveFromPool(pickedIdx, poolMark);
                outCells.Add(pickedIdx);
            }

        }


        private void ExpandRandomBlob(
            TerrainTypeData terrain,
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

            float growChance = Mathf.Clamp01(terrain.Blob.GrowChance);
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
            TerrainTypeData terrain,
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
            float towardTargetBias = Mathf.Clamp01(terrain.Lichtenberg.GoalGrowthBias);
            float branchChance = Mathf.Clamp01(terrain.Lichtenberg.BranchSpawnChance);
            int maxWalkers = Mathf.Clamp(terrain.Lichtenberg.MaxActiveWalkers, 1, 64);
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
                        float repel = terrain.Lichtenberg.HeatRepelStrength * heat;

                        // penalize stepping on cells allready targeted 
                        if (terrain.Lichtenberg.RepelPenaltyFromExisting && usedByThisTerrainAlready)
                            repel += terrain.Lichtenberg.ExistingCellPenalty;

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
                AddHeat(nextIndex, terrain.Lichtenberg.HeatRepelRadius, terrain.Lichtenberg.HeatAdd, terrain.Lichtenberg.HeatFalloff);

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

        private void WidenOnce(TerrainTypeData terrain, List<int> cells)
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



        #region Cell Pickers

        private bool CanUseCell(TerrainTypeData terrain, int idx)
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

        private bool CanPickCell(TerrainTypeData terrain, int idx)
        {
            if (terrain.ForceUnblockedSeed && _blocked[idx]) return false;
            return CanUseCell(terrain, idx);
        }


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

        private bool TryPickCell_Anywhere(TerrainTypeData terrain, out int index, int tries)
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

        private bool TryPickCell_NearExisting( List<int> chosen, int poolId, int radius, int tries, out int result,
            ExpansionAreaFocus requiredArea = ExpansionAreaFocus.Anywhere, int interiorMargin = 0)
        {

            result = -1;

            if (requiredArea == ExpansionAreaFocus.Weighted)
            {
#if UNITY_EDITOR
                Debug.LogError("TryPickCell_NearExisting: focus must not be Weighted. Roll it first.");
#endif
                requiredArea = ExpansionAreaFocus.Anywhere;
            }


            for (int i = 0; i < tries; i++)
            {
                int anchor = chosen[_rng.Next(0, chosen.Count)];
                IndexToXY(anchor, out int ax, out int ay);

                var (dx, dy) = Neighbors8[_rng.Next(0, Neighbors8.Length)];

                // bias towards small distances, cluster
                int dist = 1 + (int)(System.Math.Pow(_rng.NextDouble(), 2.0) * radius);

                int x = ax + dx * dist;
                int y = ay + dy * dist;

                if ((uint)x >= (uint)_width || (uint)y >= (uint)_height) continue;


                int candIdx = CoordToIndex(x, y);
                if (_scratch.used[candIdx] != poolId) continue;

                // enforce focus region, if terrain uses that
                if (!MatchesFocus(candIdx, requiredArea, interiorMargin)) continue;

                result = candIdx;
                return true;
            }

            return false;
        }

        private bool TryPickCell_Interior(TerrainTypeData terrain, in TerrainTypeData.AreaFocusWeights weights, out int index, int tries)
        {
            index = -1;
            int margin = ComputeInteriorMarginCells(weights);

            if (_width - 2 * margin <= 0 || _height - 2 * margin <= 0)
                return false;

            for (int t = 0; t < tries; t++)
            {
                int x = _rng.Next(margin, _width - margin);
                int y = _rng.Next(margin, _height - margin);

                int candIdx = CoordToIndex(x, y);
                if (!CanPickCell(terrain, candIdx)) continue;
                index = candIdx;
                return true;
            }
            return false;
        }

        private bool TryPickCell_Edge(TerrainTypeData terrain, out int index, int tries)
        {
            index = -1;

            for (int t = 0; t < tries; t++)
            {
                int side = _rng.Next(0, 4);
                int x;
                int y;

                switch (side)
                {
                    case 0: x = 0; y = _rng.Next(0, _height); break;            // left
                    case 1: x = _width - 1; y = _rng.Next(0, _height); break;   // right
                    case 2: x = _rng.Next(0, _width); y = 0; break;             // bottom
                    default: x = _rng.Next(0, _width); y = _height - 1; break;  // top
                }

                int i = CoordToIndex(x, y);
                if (!CanPickCell(terrain, i)) continue;
                index = i;
                return true;
            }
            return false;
        }

        private bool TryPickCell_EdgeOnSide(TerrainTypeData terrain, int side, out int index, int tries)
        {
            index = -1;
            side = Mathf.Clamp(side, 0, 3);

            for (int t = 0; t < tries; t++)
            {
                int x, y;
                switch (side)
                {
                    case 0: x = 0; y = _rng.Next(0, _height); break;               // left
                    case 1: x = _width - 1; y = _rng.Next(0, _height); break;      // right
                    case 2: x = _rng.Next(0, _width); y = 0; break;                // bottom
                    default: x = _rng.Next(0, _width); y = _height - 1; break;     // top
                }

                int candIdx = CoordToIndex(x, y);
                if (!CanPickCell(terrain, candIdx)) continue;
                index = candIdx;
                return true;
            }

            return false;
        }


        #endregion


        #region Cell Pickers - Advanced 

        // maybe add in a version of this method where the startIdx or startSide can be choosen and not rng,
        // move code and make this a a wrapper that feeds in a rng start value to the other method 
        private bool TryPickCell_EdgePairPreset(TerrainTypeData terrain, LichtenbergEdgePairMode mode, out int startIdx, out int goalIdx, int tries)
        {
            startIdx = -1;
            goalIdx = -1;

            for (int t = 0; t < tries; t++)
            {
                int startSide = _rng.Next(0, 4);

                if (!TryPickCell_EdgeOnSide(terrain, startSide, out startIdx, 32))
                    continue;

                int goalSide = PickGoalSideByMode(startSide, mode);

                if (!TryPickCell_EdgeOnSide(terrain, goalSide, out goalIdx, 32))
                    continue;

                if (startIdx == goalIdx) continue; 

                return true;
            }

            return false;
        }
       
        private bool TryPickCell_ByFocusArea(
            TerrainTypeData terrain, 
            ExpansionAreaFocus focus, 
            in TerrainTypeData.AreaFocusWeights weights, 
            out int index, int tries)
        {
            index = -1;

            int fallbackTries = Mathf.Max(8, tries / 2);    // protects against: tries/2 can become 0 wherepick loops would never run

            switch (focus)
            {
                case ExpansionAreaFocus.Edge:
                    return TryPickCell_Edge(terrain, out index, tries);

                case ExpansionAreaFocus.Interior:
                    {
                        return TryPickCell_Interior(terrain, in weights, out index, tries);
                    }

                case ExpansionAreaFocus.Anywhere:
                    return TryPickCell_Anywhere(terrain, out index, tries);

                case ExpansionAreaFocus.Weighted:
                    {
                        // RollWeightedFocus can return Anywhere if weights are all zero/misconfigured as a sensible default.
                        ExpansionAreaFocus rolled = RollWeightedFocus(in weights, _rng);

                        switch (rolled)
                        {
                            case ExpansionAreaFocus.Edge:
                            {
                                // Rolled into EDGE range -> try EDGE first
                                if (TryPickCell_Edge(terrain, out index, tries)) return true;

                                // fallback choice, next best to originally wanted: Interior, then Anywhere
                                if (TryPickCell_Interior(terrain, in weights, out index, fallbackTries)) return true;
                                return TryPickCell_Anywhere(terrain, out index, fallbackTries);
                            }

                            case ExpansionAreaFocus.Interior:
                            {
                                // Rolled into INTERIOR range -> try INTERIOR first
                                if (TryPickCell_Interior(terrain, in weights, out index, tries)) return true;

                                // fallback choice, next best to originally wanted: Anywhere, then Edge
                                if (TryPickCell_Anywhere(terrain, out index, fallbackTries)) return true;
                                return TryPickCell_Edge(terrain, out index, fallbackTries);
                            }

                            case ExpansionAreaFocus.Anywhere:
                            default:
                            {
                                // Rolled into ANYWHERE range -> try ANYWHERE first
                                if (TryPickCell_Anywhere(terrain, out index, tries)) return true;

                                // fallback choice, next best to originally wanted: Interior, then Edge 
                                if (TryPickCell_Interior(terrain, in weights, out index, fallbackTries)) return true;
                                return TryPickCell_Edge(terrain, out index, fallbackTries);
                            }
                        }
                    }

                default:
                    return TryPickCell_Anywhere(terrain, out index, tries);
            }
        }



        private bool TryPickFromPool_ByFocus(
            ExpansionAreaFocus focusArea,   // expected: Edge/Interior/Anywhere   DO NOT call with ExpansionAreaFocus.Weighted
            int interiorMargin,
            int tries,
            out int idx)
        {

            idx = -1;


            if (focusArea == ExpansionAreaFocus.Weighted)
            {
#if UNITY_EDITOR
                Debug.LogError("TryPickFromPool_ByFocus: focus must not be Weighted. Roll it first.");
#endif            
                focusArea = ExpansionAreaFocus.Anywhere;
            }


            int count = _scratch.temp.Count;
            if (count == 0 ) return false;  

            // pick anywhere fast path
            if (focusArea == ExpansionAreaFocus.Anywhere)
            {
                idx = _scratch.temp[_rng.Next(0, count)];
                return true;
            }

            // rejection sample from pool to find cells matching focus area
            for( int i = 0; i < tries; i++ )
            {
                int candIdx = _scratch.temp[_rng.Next(0, count)];
                if (!MatchesFocus(candIdx, focusArea, interiorMargin)) continue;
                idx = candIdx;
                return true;    
            }

#if UNITY_EDITOR
            Debug.LogWarning("TryPickFromPool_ByFocus: had to resort to fallback pick, a silent quality drop");
#endif
            // fallback, give a randomly generated result back anyways 
            idx = _scratch.temp[_rng.Next(0,count)];
            return true;
        }


        #endregion


        #region Cell-Pick Helpers


        private bool IsEdgeCell(int idx)
        {
            IndexToXY(idx, out int x, out int y);
            return x == 0 || y == 0 || x == _width - 1 || y == _height - 1;
        }

        private bool IsInteriorCell(int idx, int interiorMargin)
        {
            int m = Mathf.Clamp(interiorMargin, 0, Mathf.Min(_width, _height) / 2);

            if (_width - 2 * m <= 0 || _height - 2 * m <= 0)
                return false;

            IndexToXY(idx, out int x, out int y);
            return x >= m && x < _width - m && y >= m && y < _height - m;
        }

        private bool MatchesFocus(int idx, ExpansionAreaFocus focus, int interiorMargin)
        {
            switch (focus)
            {
                case ExpansionAreaFocus.Edge: return IsEdgeCell(idx);
                case ExpansionAreaFocus.Interior: return IsInteriorCell(idx, interiorMargin);
                case ExpansionAreaFocus.Anywhere:
                default: return true;
            }
        }

        private int PickGoalSideByMode(int startSide, LichtenbergEdgePairMode mode)
        {
            int opp = OppositeSide(startSide);

            switch (mode)
            {
                case LichtenbergEdgePairMode.Any:
                    return _rng.Next(0, 4);

                case LichtenbergEdgePairMode.SameEdge:
                    return startSide;

                case LichtenbergEdgePairMode.AdjacentEdge:
                    {
                        // not same edge, not the opposite edge

                        if (startSide < 2)      // For left/right (0/1), adjacent are bottom/top (2/3).
                                return (_rng.NextDouble() < 0.5) ? 2 : 3;
                            else                // For bottom/top (2/3), adjacent are left/right (0/1).
                                return (_rng.NextDouble() < 0.5) ? 0 : 1;
                    }

                case LichtenbergEdgePairMode.OppositeEdgePair:
                    return opp;

                case LichtenbergEdgePairMode.NotOpposite:
                default:
                    {
                        // anything except opposite: {same, adjacent left, adjacent right} = 3 choices
                        int roll = _rng.Next(0, 3);
                        if (roll == 0) return startSide;

                        if (startSide < 2)
                            return (roll == 1) ? 2 : 3;
                        else
                            return (roll == 1) ? 0 : 1; 
                    }
            }
        }

        private static ExpansionAreaFocus RollWeightedFocus(in AreaFocusWeights weights, System.Random rng)
        {
            float edgeWeight = Mathf.Max(0f, weights.EdgeWeight);
            float interiorWeight = Mathf.Max(0f, weights.InteriorWeight);
            float anywhereWeight = Mathf.Max(0f, weights.AnywhereWeight);

            float total = edgeWeight + interiorWeight + anywhereWeight;
            if (total <= 0f) return ExpansionAreaFocus.Anywhere;

            double r = rng.NextDouble() * total;

            if (r < edgeWeight) return ExpansionAreaFocus.Edge;
            if (r < edgeWeight + interiorWeight) return ExpansionAreaFocus.Interior;
            return ExpansionAreaFocus.Anywhere;
        }


        private void RemoveFromPool(int idx, int poolMark)
        {
            if (_scratch.used[idx] != poolMark) return;

            int pos = _scratch.poolPos[idx];
            int lastPos = _scratch.temp.Count - 1;
            int lastIdx = _scratch.temp[lastPos];

            _scratch.temp[pos] = lastIdx;
            _scratch.poolPos[lastIdx] = pos;

            _scratch.temp.RemoveAt(lastPos);
            _scratch.used[idx] = 0; // not in pool
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

            if (_scratch.poolPos == null || _scratch.poolPos.Length != _cellCount)
                _scratch.poolPos = new int[_cellCount];
        }

        private int NextMarkId()
        {
            int next = _scratch.stampId + 1;
            if (next <= 0 ||next == int.MaxValue)
            {
                Array.Clear(_scratch.stamp, 0, _scratch.stamp.Length);
                Array.Clear(_scratch.used, 0, _scratch.used.Length);

                _scratch.stampId = 1;
                return 1;
            }

            // restart at 1 (0 means unmarked)
            _scratch.stampId = next;
            return next; 
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

        private int ComputeInteriorMarginCells()
        {
            // 5% of min dimension, at least 2 cells
            return Mathf.Max(2, Mathf.RoundToInt(Mathf.Min(_width, _height) * 0.05f));
        }

        private int ComputeInteriorMarginCells(in TerrainTypeData.AreaFocusWeights weights)
        {
            float percent = Mathf.Clamp(weights.InteriorMarginPercent, 0f, 0.49f);
            int minDim = Mathf.Min(_width, _height);

            // ensures _rng.Next(low, high) has low < high.
            int maxMargin = Mathf.Max(0, (minDim - 1) / 2);

            int marginByPercent = Mathf.RoundToInt(minDim * percent);
            int margin = Mathf.Max(Mathf.Max(0, weights.InteriorMinMargin), marginByPercent);

            return Mathf.Clamp(margin, 0, maxMargin);
        }


        #endregion



        #region Debug Tools

        private void DebugDumpFocusWeights(int seed, TerrainTypeData[] terrainData)
        {
            if (!Debug_DumpFocusWeights) return;

            var stringBuilder = new StringBuilder(2048);
            stringBuilder.AppendLine($"[MapGen Focus Weights] seed={seed} size={_width}x{_height} terrains={terrainData?.Length ?? 0}");

            if (terrainData == null || terrainData.Length == 0)
            {
                stringBuilder.AppendLine("  (no terrain data)");
                Debug.Log(stringBuilder.ToString());
                return;
            }

            for (int i = 0; i < terrainData.Length; i++)
            {
                var t = terrainData[i];
                if (t == null) continue;

                stringBuilder.AppendLine($"- {t.name}  Mode={t.Mode}  Coverage={t.CoveragePercent:0.###}  Obstacle={t.IsObstacle}");

                switch (t.Mode)
                {
                    case PlacementMode.Static:
                        AppendFocus(stringBuilder, "Static.Placement", t.Static.PlacementArea, in t.Static.PlacementWeights);
                        break;

                    case PlacementMode.Blob:
                        AppendFocus(stringBuilder, "Blob.Placement", t.Blob.PlacementArea, in t.Blob.PlacementWeights);
                        break;

                    case PlacementMode.Lichtenberg:
                        AppendFocus(stringBuilder, "Lichtenberg.Origin", t.Lichtenberg.OriginArea, in t.Lichtenberg.OriginWeights);
                        AppendFocus(stringBuilder, "Lichtenberg.GrowthAim", t.Lichtenberg.GrowthAimArea, in t.Lichtenberg.GrowthAimWeights);

                        if (Debug_DumpFocusWeightsVerbose)
                        {
                            stringBuilder.AppendLine($"    EdgePresets: Use={t.Lichtenberg.UseEdgePairPresets} Mode={t.Lichtenberg.EdgePairMode}");
                            stringBuilder.AppendLine($"    Paths: [{t.Lichtenberg.MinPathCount}..{t.Lichtenberg.MaxPathCount}] Cells/Path={t.Lichtenberg.CellsPerPath}");
                        }
                        break;
                }
            }

            Debug.Log(stringBuilder.ToString());
        }

        
        private void AppendFocus(
            StringBuilder stringBuilder,
            string label,
            ExpansionAreaFocus focus,
            in TerrainTypeData.AreaFocusWeights weights)
        {

            // Interior margin is only relevant for Interior or Weighted (since Weighted may choose Interior)
            int marginCells = ComputeInteriorMarginCells(in weights);

            int innerW = Mathf.Max(0, _width - 2 * marginCells);
            int innerH = Mathf.Max(0, _height - 2 * marginCells);
            int interiorCellCount = innerW * innerH;

            stringBuilder.AppendLine($"    {label}: focus={focus}");

            if (focus == ExpansionAreaFocus.Weighted)
            {
                float edgeWeights = Mathf.Max(0f, weights.EdgeWeight);
                float interiorWeights = Mathf.Max(0f, weights.InteriorWeight);
                float anywhereWeight = Mathf.Max(0f, weights.AnywhereWeight);
                float total = edgeWeights + interiorWeights + anywhereWeight;

                if (total <= 0f)
                {
                    stringBuilder.AppendLine("      weights: (all <= 0) -> effective: Anywhere=100%");
                }
                else
                {
                    float pE = edgeWeights / total;
                    float pI = interiorWeights / total;
                    float pA = anywhereWeight / total;

                    stringBuilder.AppendLine(
                        $"      raw: Edge={edgeWeights:0.###} Interior={interiorWeights:0.###} Anywhere={anywhereWeight:0.###}  (sum={total:0.###})");
                    stringBuilder.AppendLine(
                        $"      normalized: Edge={pE:P1} Interior={pI:P1} Anywhere={pA:P1}");
                }

                stringBuilder.AppendLine(
                    $"      interior margin: {marginCells} cells  -> interior rect {innerW}x{innerH} ({interiorCellCount} cells)");
            }
            else if (focus == ExpansionAreaFocus.Interior)
            {
                stringBuilder.AppendLine(
                    $"      interior margin: {marginCells} cells  -> interior rect {innerW}x{innerH} ({interiorCellCount} cells)");
            }
            else if (Debug_DumpFocusWeightsVerbose)
            {
                // Useful when tuning: still show what the weights *are*, even if not used in non-weighted modes.
                stringBuilder.AppendLine(
                    $"      (weights ignored unless focus=Weighted) raw Edge={weights.EdgeWeight:0.###} Interior={weights.InteriorWeight:0.###} Anywhere={weights.AnywhereWeight:0.###}");
            }
        }


        #endregion


    }


}
