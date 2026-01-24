using System;
using System.Collections.Generic;
using UnityEngine;



#if UNITY_EDITOR
using UnityEditor;
#endif


namespace AI_Workshop03
{

    // Version2 of BoardGenerator     -> MapDataGenerator
    // MapDataGenerator.cs         -   Purpose: the "header / face file", top-level API and properties
    public sealed partial class MapDataGenerator                       // NOTE: the class was a public sealed class before I changed it to became partial, can thosde two keywords coexist?
    {

        private sealed class GenScratch
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


        #region Inspector Fields and Private Properties

        // Generator data fields
        private readonly GenScratch _scratch = new();
        private int _width;
        private int _height;
        private int _cellCount;
        private int _blockedCount;
        private int _maxBlockedBudget;

        // Board array references
        private bool[] _blocked;
        private int[] _terrainCost;
        private Color32[] _baseColors;
        private byte[] _terrainTypeIds;
        private byte[] _lastPaintLayerId;

        private byte _baseTerrainType;
        private int _baseWalkableCost;
        private Color32 _baseWalkableColor;

        // Rng instances
        private System.Random _rng;
        private System.Random _rngOrder;


        // Generation trackers 
        private int _attemptsThisBuild = 0;
        private bool _usedFallbackThisBuild;

        public int AttemptsLastBuild => _attemptsThisBuild;
        public bool UsedFallbackLastBuild => _usedFallbackThisBuild;

        // Debug Settings
        public bool Debug_DumpFocusWeights { get; set; } = false;
        public bool Debug_DumpFocusWeightsVerbose { get; set; } = false;


        private static readonly MapGenDebugReporter _debugReporter = new MapGenDebugReporter();


        #endregion



        #region Core Fields

        // Neighbor direction offsets
        private static readonly (int dirX, int dirY)[] Neighbors4 =
        {
            (-1, 0), ( 1, 0), (0, -1), (0,  1)
        };

        private static readonly (int dirX, int dirY)[] Neighbors8 =
        {
            (-1, 0), ( 1, 0), (0, -1), (0,  1),
            (-1,-1), (-1, 1), (1, -1), (1,  1)
        };

        // Helper: get opposite side index
        private static int OppositeSide(int side) => side ^ 1; // 0<->1, 2<->3


        #endregion

        // NOTE: Should add summary content of what the generator does



        #region Map Generation Pipeline

        public void Generate(
            MapData data,
            int seed,
            int orderSeed,
            TerrainTypeData[] terrainData,
            int maxGenerateAttempts,
            float minUnblockedPercent,
            float minReachablePercent,
            bool allowDiagonals,
            MapReachability mapReach   // callback into BoardManager
        )
        {

            BeginBuild();

            BindMapData(data);
            InitRng(seed, orderSeed);

            terrainData ??= Array.Empty<TerrainTypeData>();

            float minUnblocked = Mathf.Clamp01(minUnblockedPercent);
            ComputeBlockedBudget(minUnblocked);

            OrganizeTerrains(
                terrainData,
                out List<TerrainTypeData> obstaclesList,
                out List<TerrainTypeData> walkablesList,
                out Dictionary<TerrainTypeData, byte> terrainDataId
            );

            int startIndex = CoordToIndexUnchecked(_width / 2, _height / 2);

            bool success = TryGenerateAttempts(
                data,
                obstaclesList,
                walkablesList,
                terrainDataId,
                maxGenerateAttempts,
                minUnblocked,
                minReachablePercent,
                allowDiagonals,
                mapReach,
                ref startIndex,
                seed,
                terrainData
            );


            // --- Fallback if to many attempts, keep last version and ensure walkable visuals are consistent ---
            if (!success)
                RunFallbackBranch(seed, terrainData, walkablesList, terrainDataId);

        }



        private void BeginBuild()
        {
            _attemptsThisBuild = 0;
            _usedFallbackThisBuild = false;
        }

        // --- Get reference pointers to current game board ---
        private void BindMapData(MapData data)
        {
            _width = data.Width;
            _height = data.Height;
            _cellCount = data.CellCount;

            _baseWalkableCost = data.BaseTerrainCost;
            _baseWalkableColor = data.BaseTerrainColor;
            _baseTerrainType = data.BaseTerrainType;

            _blocked = data.IsBlocked ?? throw new ArgumentNullException(nameof(data.IsBlocked));
            _terrainTypeIds = data.TerrainTypeIds ?? throw new ArgumentNullException(nameof(data.TerrainTypeIds));
            _terrainCost = data.TerrainCosts ?? throw new ArgumentNullException(nameof(data.TerrainCosts));
            _baseColors = data.BaseCellColors ?? throw new ArgumentNullException(nameof(data.BaseCellColors));
            _lastPaintLayerId = data.LastPaintLayerIds ?? throw new ArgumentNullException(nameof(data.LastPaintLayerIds));

            if (_blocked.Length != _cellCount ||
                _terrainTypeIds.Length != _cellCount ||
                _terrainCost.Length != _cellCount ||
                _baseColors.Length != _cellCount ||
                _lastPaintLayerId.Length != _cellCount)
                throw new ArgumentException("Board arrays length mismatch.");
        }


        private void InitRng(int seed, int orderSeed)
        {
            _rng = new System.Random(seed);
            _rngOrder = new System.Random(orderSeed);
        }

        // --- Compute max blocked budget based on min unblocked percent ---
        private void ComputeBlockedBudget(float minUnblocked)
        {
            int minWalkableCells = Mathf.CeilToInt(minUnblocked * _cellCount);
            minWalkableCells = Mathf.Clamp(minWalkableCells, 0, _cellCount);

            _maxBlockedBudget = _cellCount - minWalkableCells;
        }

        // --- Organize all terrains in use  ---
        private void OrganizeTerrains(
            TerrainTypeData[] terrainData,
            out List<TerrainTypeData> obstaclesList,
            out List<TerrainTypeData> walkablesList,
            out Dictionary<TerrainTypeData, byte> terrainDataId
        )
        {
            obstaclesList = new List<TerrainTypeData>(terrainData.Length);
            walkablesList = new List<TerrainTypeData>(terrainData.Length);

            for (int i = 0; i < terrainData.Length; i++)
            {
                var terrain = terrainData[i];
                if (terrain == null) continue;

                if (terrain.IsObstacle) obstaclesList.Add(terrain);
                else walkablesList.Add(terrain);
            }

            TerrainOrderUtility.ShuffleWithinOrderBucketsByEarlyBias(obstaclesList, _rngOrder);
            TerrainOrderUtility.ShuffleWithinOrderBucketsByEarlyBias(walkablesList, _rngOrder);

            terrainDataId = AssignTerrainIds(obstaclesList, walkablesList, terrainData.Length);
        }

        // helper to OrganizeTerrains() 
        private static Dictionary<TerrainTypeData, byte> AssignTerrainIds(
            List<TerrainTypeData> obstaclesList,
            List<TerrainTypeData> walkablesList,
            int capacity
        )
        {
            var terrainDataId = new Dictionary<TerrainTypeData, byte>(capacity);        // terrainId is assigned by list order after sorting, 0 reserved for base

            byte nextId = 1;

            for (int i = 0; i < obstaclesList.Count && nextId != byte.MaxValue; i++)    // assign all obstacles IDs first
            {
                var terrainObst = obstaclesList[i];
                if (terrainObst == null) continue;

                if (!terrainDataId.ContainsKey(terrainObst))
                    terrainDataId[terrainObst] = nextId++;
            }

            for (int i = 0; i < walkablesList.Count && nextId != byte.MaxValue; i++)    // then assign all walkables IDs
            {
                var terrainWalk = walkablesList[i];
                if (terrainWalk == null) continue;

                if (!terrainDataId.ContainsKey(terrainWalk))
                    terrainDataId[terrainWalk] = nextId++;
            }

            return terrainDataId;
        }


        // --- Attempt to generate base game map ---
        private bool TryGenerateAttempts(
            MapData data,
            List<TerrainTypeData> obstaclesList,
            List<TerrainTypeData> walkablesList,
            Dictionary<TerrainTypeData, byte> terrainDataId,
            int maxGenerateAttempts,
            float minUnblocked,
            float minReachablePercent,
            bool allowDiagonals,
            MapReachability mapReach,
            ref int startIndex,
            int seed,
            TerrainTypeData[] terrainData
        )
        {
            int attempts = Math.Max(1, maxGenerateAttempts);

            for (int attempt = 0; attempt < attempts; attempt++)            // attempt placement loop, if it fails to meat the any prerequisite, generate a new map. 
            {
                ResetToBase();
                _attemptsThisBuild++;

                ApplyObstacleTerrains(obstaclesList, terrainDataId);        // obstacle placement before other terrain types

                if (!EnsureWalkableStartIndex(ref startIndex))              // BuildReachableFrom() will need a valkable startIndex to validate the board 
                    continue;

                if (!MeetsUnblockedPercent(minUnblocked, out int walkableCount))     // If map does not meet walkable space requirement, fail and try again
                    continue;

                if (!MeetsReachability(data, startIndex, walkableCount, minReachablePercent, allowDiagonals, mapReach))  // use startIndex to validate the board’s navigability,
                    continue;


                // --- Successful Generation Branch ---                     // The generated map fullfils base requirements, continue to finnishing touches 
                FinalizeWalkableTerrains(walkablesList, terrainDataId);     // reset walkable tiles to base visuals/cost/id so terrain can build from clean base

                // --- Generate Debug Data  ---
                DumpDebugIfEnabled(seed, terrainData, fallbackBuilds: _usedFallbackThisBuild ? 1 : 0);   
                return true;
            }

            return false;
        }

        private void ApplyObstacleTerrains(
            List<TerrainTypeData> obstaclesList,
            Dictionary<TerrainTypeData, byte> terrainDataId
        )
        {
            for (int i = 0; i < obstaclesList.Count; i++)
            {
                if (_blockedCount >= _maxBlockedBudget)
                    break;

                var terrain = obstaclesList[i];
                if (terrain == null || !terrain.IsObstacle) continue;

                if (!terrainDataId.TryGetValue(terrain, out byte id))
                    continue;

                ApplyTerrainData(terrain, id, isObstacle: true);
            }
        }


        // that BFS needs a walkable starting node to measure “how connected is this map” from center,
        private bool EnsureWalkableStartIndex(ref int startIndex)
        {
            if (!_blocked[startIndex])
                return true;

            return TryPickRandomUnBlocked(out startIndex, 256, requireBase: false);     // pick ANY unblocked cell as BFS seed instead of failing the attempt
        }                                                                               


        private bool MeetsUnblockedPercent(float minUnblocked, out int walkableCount)   // NOTE: current design stops map from starting as fully blocked
        {
            walkableCount = CountWalkable();
            if (walkableCount <= 0)
                return false;

            float unblockedPercent = walkableCount / (float)_cellCount;
            return unblockedPercent >= minUnblocked;
        }


        private bool MeetsReachability(
            MapData data,
            int startIndex,
            int walkableCount,
            float minReachablePercent,
            bool allowDiagonals,
            MapReachability mapReach
        )
        {
            int reachableCount = mapReach.BuildReachableFrom(data, startIndex, allowDiagonals);
            float reachablePercent = reachableCount / (float)walkableCount;
            return reachablePercent >= minReachablePercent;
        }


        private void FinalizeWalkableTerrains(
            List<TerrainTypeData> walkablesList,
            Dictionary<TerrainTypeData, byte> terrainDataId
        )
        {
            ResetWalkableToBaseOnly();

            for (int i = 0; i < walkablesList.Count; i++)
            {
                var terrain = walkablesList[i];
                if (terrain == null || terrain.IsObstacle) continue;

                if (!terrainDataId.TryGetValue(terrain, out byte id))
                    continue;

                ApplyTerrainData(terrain, id, isObstacle: false);
            }
        }


        private void DumpDebugIfEnabled(int seed, TerrainTypeData[] terrainData, int fallbackBuilds)
        {
            if (!Debug_DumpFocusWeights)
                return;

            _debugReporter.DumpFocusWeights(
                seed, _width, _height, terrainData,
                Debug_DumpFocusWeightsVerbose,
                areaWeights => ComputeInteriorMarginCells(in areaWeights),
                totalAttemptedBuilds: _attemptsThisBuild,
                totalFallbackBuilds: fallbackBuilds
            );
        }


        private void RunFallbackBranch(
            int seed,
            TerrainTypeData[] terrainData,
            List<TerrainTypeData> walkablesList,
            Dictionary<TerrainTypeData, byte> terrainDataId
        )
        {
            ResetWalkableToBaseOnly();
            _usedFallbackThisBuild = true;

            // --- Generate Debug Data  ---
            DumpDebugIfEnabled(seed, terrainData, fallbackBuilds: 1);

            // Place walkables on the last attempt’s obstacle map
            for (int i = 0; i < walkablesList.Count; i++)
            {
                var terrain = walkablesList[i];
                if (terrain == null || terrain.IsObstacle) continue;

                if (!terrainDataId.TryGetValue(terrain, out byte id))
                    continue;

                ApplyTerrainData(terrain, id, isObstacle: false);
            }
        }







        #endregion





        #region Terrain Dispatch - (switches between Static/Blob/Lichtenberg)

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


        #endregion



        #region Overwrite / Apply Cell Data

        private void ApplyTerrain(TerrainTypeData terrain, byte terrainLayerId, List<int> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                int index = cells[i];
                if (!IsValidCell(index)) continue;
                if (!CanUseCell(terrain, index)) continue;

                if (terrain.AllowOverwriteObstacle && _blocked[index])
                    SetBlocked(index, false);

                _terrainTypeIds[index] = (byte)terrain.TerrainID;
                _terrainCost[index] = terrain.Cost;
                _baseColors[index] = terrain.Color;
                _lastPaintLayerId[index] = terrainLayerId;
            }
        }

        private void ApplyObstacles(TerrainTypeData terrain, byte terrainLayerId, List<int> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (_blockedCount >= _maxBlockedBudget)
                    break; // reached max blocked budget, early out stop

                int index = cells[i];
                if (!IsValidCell(index)) continue;
                if (!CanUseCell(terrain, index)) continue;

                if (_blocked[index])
                    continue; // already blocked, no need to re-apply same values 

                SetBlocked(index, true);

                _terrainTypeIds[index] = (byte)terrain.TerrainID;
                _terrainCost[index] = 0;
                _lastPaintLayerId[index] = terrainLayerId;
                _baseColors[index] = terrain.Color;
            }
        }

        private void SetBlocked(int index, bool blocked)
        {
            bool wasBlocked = _blocked[index];
            if (wasBlocked == blocked) return;

            _blocked[index] = blocked;

            if (blocked) _blockedCount++;
            else _blockedCount = Math.Max(0, _blockedCount - 1);
        }


        #endregion



        #region Map-state clearing rules

        private void ResetToBase()
        {
            EnsureGenBuffers();

            for (int i = 0; i < _cellCount; i++)
            {
                _blocked[i] = false;
                _terrainTypeIds[i] = _baseTerrainType;
                _terrainCost[i] = _baseWalkableCost;
                _baseColors[i] = _baseWalkableColor;
                _lastPaintLayerId[i] = 0;
            }

            _blockedCount = 0;
        }

        private void ResetWalkableToBaseOnly()
        {
            for (int i = 0; i < _cellCount; i++)
            {
                if (_blocked[i]) continue;

                _terrainTypeIds[i] = _baseTerrainType;
                _terrainCost[i] = _baseWalkableCost;
                _baseColors[i] = _baseWalkableColor;
                _lastPaintLayerId[i] = 0;
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



        #region Helpers - Map Bounds and Cells 

        private bool IsValidCell(int index) =>
            GridMath.IsValidIndex(index, _cellCount);

        private int CoordToIndexUnchecked(int x, int y) => 
            GridMath.CoordToIndexUnchecked(x, y, _width);

        private bool TryCoordToIndex(int x, int y, out int index) =>
            GridMath.TryCoordToIndex(x, y, _width, _height, out index);

        private void IndexToXY(int index, out int x, out int y) => 
            GridMath.IndexToXY(index, _width, out x, out y);


        private Vector3 IndexToWorldCenterXZ(int index, float y = 0f)
        {
            IndexToXY(index, out int x, out int z);
            return new Vector3(x + 0.5f, y, z + 0.5f);
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

        private int ComputeEdgeBandCells(in TerrainTypeData.AreaFocusWeights weights)
        {
            int minDim = Mathf.Min(_width, _height);
            int maxBand = Mathf.Max(0, (minDim - 1) / 2);

            int band = ComputeInteriorMarginCells(in weights);
            return Mathf.Clamp(band, 0, maxBand);
        }


        private int HeuristicManhattan(int a, int b)
        {
            IndexToXY(a, out int ax, out int ay);
            IndexToXY(b, out int bx, out int by);
            return Math.Abs(ax - bx) + Math.Abs(ay - by);
        }


        #endregion



        #region Helpers - Stamp data 

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
            if (next <= 0 || next == int.MaxValue)
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


        #endregion



    }


}
