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

        public bool Debug_DumpFocusWeights { get; set; } = false;
        public bool Debug_DumpFocusWeightsVerbose { get; set; } = false;


        private MapGenDebugReporter _debugReporter;
        private MapGenDebugReporter DebugReporter
            => _debugReporter ??= new MapGenDebugReporter();




        // NOTE: Should add summary content of what the generator does




        public void Generate(
            MapData data,
            int seed,
            int orderSeed,
            TerrainTypeData[] terrainData,
            int maxGenerateAttempts,
            float minUnblockedPercent,
            float minReachablePercent,
            Func<int, int> buildReachableFrom   // callback into BoardManager
        )
        {

            // --- Get reference pointers to current game board ---

            _width = data.Width;
            _height = data.Height;
            _cellCount = data.CellCount;

            _baseWalkableCost = data.BaseTerrainCost;
            _baseWalkableColor = data.BaseTerrainColor;
            _baseTerrainKind = data.BaseTerrainKind;

            _blocked = data.IsBlocked ?? throw new ArgumentNullException(nameof(data.IsBlocked));
            _terrainKindIds = data.TerrainKindIds ?? throw new ArgumentNullException(nameof(data.TerrainKindIds));
            _terrainCost = data.TerrainCosts ?? throw new ArgumentNullException(nameof(data.TerrainCosts));
            _baseColors = data.BaseCellColors ?? throw new ArgumentNullException(nameof(data.BaseCellColors));
            _lastPaintLayerId = data.LastPaintLayerIds ?? throw new ArgumentNullException(nameof(data.LastPaintLayerIds));

            if (_blocked.Length != _cellCount || _terrainKindIds.Length != _cellCount || _terrainCost.Length != _cellCount ||
                _baseColors.Length != _cellCount || _lastPaintLayerId.Length != _cellCount)
                throw new ArgumentException("Board arrays length mismatch.");


            _rng = new System.Random(seed);
            _rngOrder = new System.Random(orderSeed);


            terrainData ??= Array.Empty<TerrainTypeData>();


            // --- Compute max blocked budget based on min unblocked percent ---

            float minUnblocked = Mathf.Clamp01(minUnblockedPercent);

            int minWalkableCells = Mathf.CeilToInt(minUnblocked * _cellCount);
            minWalkableCells = Mathf.Clamp(minWalkableCells, 0, _cellCount);

            _maxBlockedBudget = _cellCount - minWalkableCells;


            // --- Generate Debug Data  ---

            if (Debug_DumpFocusWeights)
                DebugReporter.DumpFocusWeights(seed, _width, _height, terrainData, Debug_DumpFocusWeightsVerbose, areaWeights => ComputeInteriorMarginCells(in areaWeights));



            // --- Organize all terrains in use  ---

            var obstaclesList = new List<TerrainTypeData>(terrainData.Length);
            var walkablesList = new List<TerrainTypeData>(terrainData.Length);
            for (int i = 0; i < terrainData.Length; i++)
            {
                var terrain = terrainData[i];
                if (terrain == null) continue;

                if (terrain.IsObstacle) obstaclesList.Add(terrain);
                else walkablesList.Add(terrain);
            }

            TerrainOrderUtility.ShuffleWithinOrderBucketsByEarlyBias(obstaclesList, _rngOrder);
            TerrainOrderUtility.ShuffleWithinOrderBucketsByEarlyBias(walkablesList, _rngOrder);


            var terrainDataId = new Dictionary<TerrainTypeData, byte>(terrainData.Length);            // terrainId is assigned by list order after sorting, 0 reserved for base
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


            int startIndex = CoordToIndex(_width / 2, _height / 2);

            // --- Atempt to generate base game map ---
            for (int attempt = 0; attempt < Math.Max(1, maxGenerateAttempts); attempt++)    // attempt placement loop
            {
                ResetToBase();

                for (int i = 0; i < obstaclesList.Count; i++)                               // obstacle placement before other terrain types
                {
                    if (_blockedCount >= _maxBlockedBudget)
                        break;

                    var terrain = obstaclesList[i];
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
                if (unblockedPercent < minUnblocked) continue;                        // make sure map don't have to many obstacles placed

                int reachableCount = buildReachableFrom(startIndex);
                float reachablePercent = reachableCount / (float)walkableCount;

                if (reachablePercent >= minReachablePercent)
                {
                    ResetWalkableToBaseOnly();                                              // reset walkable tiles to base visuals/cost/id so terrain can build from clean base

                    for (int i = 0; i < walkablesList.Count; i++)
                    {
                        var terrain = walkablesList[i];
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
            for (int i = 0; i < walkablesList.Count; i++)
            {
                var terrain = walkablesList[i];
                if (terrain == null || terrain.IsObstacle) continue;

                if (!terrainDataId.TryGetValue(terrain, out byte id))
                    continue;

                ApplyTerrainData(terrain, id, isObstacle: false);
            }
        }



    }


}
