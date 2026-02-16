using System;
using System.Collections.Generic;
using UnityEngine;



#if UNITY_EDITOR
using UnityEditor;
#endif


namespace AI_Workshop03
{

    // Version2 of BoardGenerator     -> MapDataGenerator
    // MapGenerator.cs         -   Purpose: Core behaviour and "Face" file 
    public sealed partial class MapGenerator                       // NOTE: the class was a public sealed class before I changed it to became partial, can thosde two keywords coexist?
    {


        private enum FailGate : byte
        {
            None,
            NoWalkableSeed,
            UnblockedTooLow,
            ReachabilityTooLow
        }

        private struct AttemptStats
        {
            public int attempt;
            public FailGate gate;

            public int blocked, maxBlocked;
            public int walkable, minWalkable;

            public int minReachableCells;
            public int reached;     // -1 if not measured yet

            public int touched;

            public float msObstacles;
            public float msBfs;
            public float msFinalize;
        }


        private sealed class GenScratch
        {
            public int[] heat;      // temp int storage
            public int[] heatStamp;
            public int heatStampId;
            
            public int[] poolPos;   // temp int storage
            public int[] queue;     // buffer
            public int stampId;

            public int[] stamp;     // stamp based marker
            public int[] used;      // stamp based marker. Only compare to the current id. Never check != 0

            public List<int> touched = new(4096);   // buffer for stamp-based undo
            public int[] touchedStamp;
            public int touchedStampId;  

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

        // Board array references      DO NOT WRITE DIRECTLY!!! — use PaintTerrainCell/PaintObstacleCell/SetBlocked
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



#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const int TelemetryRing = 32;
        private readonly AttemptStats[] _telemetry = new AttemptStats[TelemetryRing];
        private int _telemetryWrite;
#endif


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


        private int WalkableCount => _cellCount - _blockedCount;

        private bool HasAnyWalkable => _blockedCount < _cellCount;


        #endregion

        // NOTE: Should add summary content of what the generator does



        #region Map Generation Pipeline

        // NOTE: Future plan 
        /*  
         *  Next step revision: Stop full-grid resets per attempt
         *  
         *  ResetToBase() writes 5 arrays across _cellCount every attempt:
         *      _blocked (bool)
         *      _terrainTypeIds (byte)
         *      _terrainCost (int)
         *      _baseColors (Color32)
         *      _lastPaintLayerId (byte)
         *  On a 1000×1000 map, _cellCount = 1,000,000. That’s 5,000,000 writes per attempt, plus loop overhead. 
         *  At _maxGenerateAttempts = 50 it can hit 250 million writes in worst-case.
         *  
         *  Replace full reset-per-attempt with a diff/undo strategy (track written indices and reset only those), 
         *  or generate obstacles into scratch and “commit” only on success. Bigger code refactory rewrite, but it’s probably neccesary for 1000×1000 + many attempts.
         *  
         *  ------------------------------------------------------------------------------------------------------------------------------------
         *  
         *  
         *  
         *  // NOTE: Future plan - Part 2
         *  
         *  Currently there is one single BuildReachableFrom() done at generation time, it is worthless and there for my sanity sake.
         *  Make it an actuall usefull function, by ´stamping all reachable zones and add to map data so pathfinders can use that truth.
         *  Can also make one obstacle version so there are loggs of all walkable connected spaces AND all blocked connected spaces, 
         *  so all data is available for debug and gameplay use.
         *  
         *  "If you want the stamp to represent “all pockets”, the next step is a connected-components pass (flood fill from every unvisited walkable cell,
         *  assign component IDs + sizes). But for your stated goal (sanity + debug), your current “reachable from start” stamp is totally fine."
         *  
         *  
         *      Part 3? - look into doing this for all instances of connected terrain zones by type to create an 
         *                atlas that can be used for gameplay and debug purposes. 
         *                Maybe good info to keep in mapdata for gameplay purposes, and can be used for debug visualization to see how 
         *                the generator is carving up the map into different terrain zones.
         *                    
         *                      Need to consider cost / benefit off all added complexity and data size though, 
         *                      maybe only do this for obstacles or only for walkable zones, or only keep track of the biggest X zones to limit data size.
         *      
         *  
         *  // TODO: 
         *           Edge-band consistency: “Edge” must mean band everywhere (pool + pickers), not 1-cell border.
         *           // NOTE: Fix semantic mismatch later; don’t mix into retry/telemetry work.
         *           
         *           
         *           Semantic risk: 
         *                          Now using the same numeric thickness for both:
         *                              - Edge band thickness 
         *                              - Interior margin thickness
         *                          
         *                          Works because weights only expose one margin concept anyway. But it creates the constraint,
         *                          if later you want EdgeBand=6 but InteriorMargin=12, current API cannot express that without splitting the value.
         *                              If I get around to needing them as seperate values, can consider to do:
         *                              - Adding weights like EdgeBandPercent/EdgeMinBand later and then make ComputeEdgeBandCells() truly edge-specific.
         *  
         *  
         *  
         *  // TODO: 
         *           Commit-on-success: Commit-on-success means: generate attempts into scratch arrays, and only copy into MapData on success.
         *           
         *           Pros
         *           - Clean correctness model: MapData only ever contains “accepted” state.
         *           - No per-attempt reset in MapData.
         *           - Easy to save “best attempt snapshot” (you already have the scratch state).
         *           
         *           Cons
         *           - More refactor: all your generator code must write into “active buffers” (scratch), not directly into MapData arrays.
         *           - Extra memory: duplicates of the big arrays.
         *              - Rough ballpark for 1,000,000 cells:
         *                  - bool[] ~ 1 MB (implementation dependent)
         *                  - int[] ~ 4 MB
         *                  - Color32[] ~ 4 MB
         *                  - byte[] ~ 1 MB each
         *                  Total per set roughly ~11–12 MB + overhead; duplicating is usually acceptable on desktop, but still real.           
         *  
         *           Commit-on-success is the cleanest long-term, but it’s a bigger rewrite and you’ll likely do it only once you’re confident your 
         *           generation rules are stable and you want “best attempt replay/snapshots” as a first-class thing. 
         *          
         *           Hybrid option (often best):
         *            - Do touched-undo now.     - DONE
         *            - Later, when you want heavy telemetry + replay + storing “closest attempt map”, consider commit-on-success.
         *  
         */



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

            EnsureGenBuffers();

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

            // find a way to skip ResetToBase() on attempt 0 inside TryGenerateAttempts, how big cost win would that bring? 
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
                orderSeed,
                terrainData
            );


            // --- Fallback if to many attempts, keep last version and ensure walkable visuals are consistent ---
            if (!success)
                RunFallbackBranch(seed, terrainData, walkablesList, terrainDataId);


            if (Debug_DumpFocusWeights)
            {
                // debug + sanity check that things work like I expect them to, not meant to be used by anything 
                if (mapReach != null && EnsureWalkableStartIndex(ref startIndex))
                {
                    mapReach.BuildReachableFrom(data, startIndex, allowDiagonals);  // do one full BFS build on accepted map to mark reachable area for runtime use (doing multiple BFS checks during attempts can be costly on larger maps)
                }
            }
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
            // order should be stable for whole build
            _rngOrder = new System.Random(orderSeed);

            // default, will be overwritten per-attempt anyway 
            _rng = new System.Random(seed);
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
            int orderSeed,  
            TerrainTypeData[] terrainData
        )
        {
            //EnsureGenBuffers();       // calling once in Generate() before TryGenerateAttempts loop should be enough

            int attempts = Math.Max(1, maxGenerateAttempts);

            int minWalkableCells = ComputeMinWalkableCells(minUnblocked);
            float minReachable01 = Mathf.Clamp01(minReachablePercent);

            int successAttempt = -1;

            for (int attempt = 0; attempt < attempts; attempt++)        // attempt placement loop, if it fails to meat the any prerequisite, generate a new map.
            {
                AttemptStats stats = default; stats.attempt = attempt; stats.maxBlocked = _maxBlockedBudget; stats.minWalkable = minWalkableCells;


                // WARNING: touched undo works if this  ResetToBase() / UndoTouchedToBase() logic is correct,
                // be careful when refactoring and consider implications on the undo logic when making changes here.
                // Current precondition:
                //      Every attempt starts from base state, and
                //      Every cell modification during an attempt calls Touch(idx) before/when writing, and
                //      Base map is truly unblocked, because I hard-reset _blockedCount = 0 as the map generator can only handle walkable as base tiles.

                // Invariant: each attempt begins from the base map state.
                // Attempt 0: full reset. Attempts 1+: undo only what prior attempt touched
                if (attempt == 0) ResetToBase();                        // first attempt, start from a clean slate by resetting the whole map to base
                else UndoTouchedToBase();                               // undo previous failed attempt writes instead of doing a full ResetToBase() call


                // DEV NOTE: Current map is only designed to take in a base map with no blocked cells, if there are blocked cells in the base map,
                //           the generator will count those against the blocked budget and may fail to generate a valid map.
                //           Can add a pre-processing step to clear blocked cells from the base map if needed,
                //           but for now just assert that the base map is unblocked for sanity sake.
                AssertAttemptStartsFromUnblockedBase();    
                //
                //      If later support base has some blocked: Capture int _baseBlockedCount once after BindMapData / after initial ResetToBase(), and set _blockedCount = _baseBlockedCount on undo.
                //
                //      Future plan: can consider supporting blocked cells in the base map, but it will add complexity to the
                //      generator logic and may require a different approach to obstacle placement and blocked budget management.
                //      Will require larger refactory to support, so for now just assert that the base map is unblocked and
                //      consider this a potential future improvement if there is time and need for it.

                BeginAttempt();                                         // set stamp id + clear touched list

                // Per-attempt placement RNG: Should be stable with "seed reproduces map", but different across attempts to give the generator a chance to try different placements.    (placement randomness inside attempt N is deterministic and reproducible)
                _rng = new System.Random(unchecked(seed ^ ((int)0x9E3779B9 * (attempt + 1))));
                _attemptsThisBuild++;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // Cheap integrity spot-check, could run every attempt, but limited it for now.  
                if ((attempt & 7) == 0)                                 // runs every 8th attempt
                {
                    int recomputedBlocked = 0;
                    int nonBasePaint = 0;

                    for (int i = 0; i < _cellCount; i++)
                    {
                        if (_blocked[i]) recomputedBlocked++;
                        if (_lastPaintLayerId[i] != 0) nonBasePaint++;
                    }

                    if (recomputedBlocked != _blockedCount)
                        Debug.LogError($"BlockedCount mismatch after reset/undo: {recomputedBlocked} vs _blockedCount: {_blockedCount}");

                    if (nonBasePaint != 0)
                        Debug.LogError($"Undo/reset integrity: found {nonBasePaint} cells with LastPaintLayerId != 0 after reset/undo. Likely missing Touch() on some write.");
                }
#endif

                ApplyObstacleTerrains(obstaclesList, terrainDataId);    // obstacle placement before other terrain types
                stats.blocked = _blockedCount; stats.touched = _scratch.touched.Count;

                if (!EnsureWalkableStartIndex(ref startIndex))          // BuildReachableFrom() will need a valkable startIndex to validate the board 
                {
                    stats.gate = FailGate.NoWalkableSeed; RecordAttempt(stats); continue;
                }

                if (!MeetsUnblockedPercent(minWalkableCells, out int walkableCount))    // If map does not meet walkable space requirement, fail and try again
                {
                    stats.gate = FailGate.UnblockedTooLow; RecordAttempt(stats); continue;
                }

                int minReachableCells = Mathf.Clamp(
                    Mathf.CeilToInt(minReachable01 * walkableCount),
                    0, walkableCount
                );
                stats.minReachableCells = minReachableCells;

                if (!MeetsReachability(data, startIndex, walkableCount, minReachable01, allowDiagonals, mapReach, out stats.reached))  // use startIndex to validate the board’s navigability,
                {
                    stats.gate = FailGate.ReachabilityTooLow; RecordAttempt(stats); continue;
                }


                bool needsReachability = Mathf.Clamp01(minReachablePercent) > 0f;
                if (mapReach == null)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (needsReachability)
                        Debug.LogWarning("MapGenerator: mapReach is null but minReachablePercent > 0. Reachability check will be skipped and maps may be disconnected.");
#endif
                }


                // --- Successful Generation Branch ---                         // The generated map fullfils base requirements, continue to finnishing touches 
                FinalizeWalkableTerrains(walkablesList, terrainDataId);         // reset walkable tiles to base visuals/cost/id so terrain can build from clean base
                stats.gate = FailGate.None; RecordAttempt(stats);

                if (mapReach != null && EnsureWalkableStartIndex(ref startIndex))
                {
                    mapReach.BuildReachableFrom(data, startIndex, allowDiagonals); // full BFS
                }

                // --- Generate Debug Data  ---
                DumpDebugIfEnabled(seed, terrainData, fallbackBuilds: _usedFallbackThisBuild ? 1 : 0);
                successAttempt = attempt;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log($"[MapGen] baseSeed={seed} orderSeed={orderSeed} successAttempt={successAttempt} attemptsUsed={_attemptsThisBuild}");
#endif
                return true;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[MapGen] baseSeed={seed} orderSeed={orderSeed} FAILED attemptsUsed={_attemptsThisBuild}");
#endif

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
            if (_cellCount <= 0) return false;
            if (!HasAnyWalkable) return false;

            if (!_blocked[startIndex])
                return true;

            return TryPickRandomUnBlocked(out startIndex, 256, requireBase: false);     // pick ANY unblocked cell as BFS seed instead of failing the attempt
        }


        // Precompute once per Generate() call (or once per TryGenerateAttempts call)
        private int ComputeMinWalkableCells(float minUnblocked)
        {
            // Required walkable cells derived from percent
            int minWalkableCells = Mathf.CeilToInt(Mathf.Clamp01(minUnblocked) * _cellCount);
            return Mathf.Clamp(minWalkableCells, 0, _cellCount);
        }


        private bool MeetsUnblockedPercent(int minWalkableCells, out int walkableCount)   // NOTE: current design stops map from starting as fully blocked
        {
            walkableCount = WalkableCount; 
            if (walkableCount <= 0) return false;

            return walkableCount >= minWalkableCells;
        }


        private bool MeetsReachability(
            MapData data,
            int startIndex,
            int walkableCount,
            float minReachablePercentClamped,
            bool allowDiagonals,
            MapReachability mapReach,
            out int reached
        )
        {
            reached = -1; 

            // If reachability requirement is 0, accept without BFS
            if (minReachablePercentClamped <= 0f) return true;

            if (mapReach == null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogWarning("MapGenerator: skipping reachability check because mapReach is null.");
#endif
                return true; // keep generation running; strictness should be unchanged since I think it previously allowed null
            }

            // Avoid division: reachableCount / (float)walkableCount >= p  <=>  reachableCount >= ceil(p * walkableCount)      
            // Previously:  float reachablePercent = reachableCount / (float)walkableCount;
            int minReachableCells = Mathf.Clamp(
                Mathf.CeilToInt(minReachablePercentClamped * walkableCount),
                0, walkableCount
            );

            // if only need 0 or 1 reachable cell, and startIndex is ensured walkable, then this is trivially satisfied
            if (minReachableCells <= 1) return true;

            // do only partial BFS build to check if reachable cells meet the minReachableCells requirement, avoid full BFS build for performance if requirement is not met
            return mapReach.HasAtLeastReachable(data, startIndex, allowDiagonals, minReachableCells, out reached);
        }


        private void FinalizeWalkableTerrains(
            List<TerrainTypeData> walkablesList,
            Dictionary<TerrainTypeData, byte> terrainDataId
        )
        {
            // IMPORTANT: In your current pipeline, at this point walkable cells are already base, MapManager calls InitializeToBase() when generating new map.  
            //            (only painteded obstacles so far). ResetWalkableToBaseOnly() is redundant here but needs to be re-asessed if pipeline changes.
            //ResetWalkableToBaseOnly();

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

            switch (terrain.Mode)       // what "paint brush" is used to generate this tiles structure
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


#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_scratch.cells.Count == 0)
            {
                // Telemetry-only: terrain produced nothing this attempt.
                // Later refine this to "couldn't find seed after X tries".
                // RecordStarved(terrain, terrainLayerId);
            }
#endif

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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (!IsValidCell(index)) continue;    // This should be safe to remove? All callers indices produced internally should already be valid, because they are generated from valid coords and neighbors.
#endif
                if (!CanUseCell(terrain, index)) continue;

                PaintTerrainCell(index, terrain, terrainLayerId);
            }
        }

        private void ApplyObstacles(TerrainTypeData terrain, byte terrainLayerId, List<int> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (_blockedCount >= _maxBlockedBudget)
                    break;                          // reached max blocked budget, early out stop

                int index = cells[i];
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (!IsValidCell(index)) continue;  // This should be safe to remove? All callers indices produced internally should already be valid, because they are generated from valid coords and neighbors.
#endif
                if (!CanUseCell(terrain, index)) continue;
                if (!terrain.AllowOverwriteObstacle && _blocked[index]) continue;   // already blocked, no need to re-apply same values  <-- NOT FULLY TRUE ANYMORE: this defeats AllowOverwriteObstacle for obstacles

                PaintObstacleCell(index, terrain, terrainLayerId);
            }
        }

        private void SetBlocked(int index, bool blocked)
        {
            Touch(index);
            SetBlocked_NoTouch(index, blocked);
        }

        private void SetBlocked_NoTouch(int index, bool blocked)
        {
            bool wasBlocked = _blocked[index];
            if (wasBlocked == blocked) return;

            _blocked[index] = blocked;
            _blockedCount += blocked ? 1 : -1;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_blockedCount < 0) Debug.LogError("_blockedCount went negative. Missing base/reset invariant?");
#endif
            if (_blockedCount < 0) _blockedCount = 0; // safety clamp;
        }

        private void PaintTerrainCell(int index, TerrainTypeData terrain, byte terrainLayerId)
        {
            Touch(index);

            if (terrain.AllowOverwriteObstacle && _blocked[index])
                SetBlocked_NoTouch(index, false);

            // NOTE:
            //      TerrainID is the category enum (Land/Subterranean/Liquid/Air), not the unique terrain-rule identity.
            //      This saets a groupng of terrain types, not the specific terrain type, so different terrain types that share the same TerrainID will be
            //      grouped together for gameplay purposes (e.g. all Land terrains will share the same TerrainID even if they have different costs/colors/rules).
            //
            //      The terrainLayerId is computed and used only for _lastPaintLayerId, local for this generation of the map.
            //      Identifying which terrain type is responsible for painting this cell, so it can be used for generation rules that
            //      depend on "what was painted on this cell" (e.g. only paint this terrain on cells painted by that terrain,
            //      or avoid painting on cells painted by that terrain). 
            _terrainTypeIds[index] = (byte)terrain.TerrainID;
            _terrainCost[index] = terrain.Cost;
            _baseColors[index] = terrain.Color;
            _lastPaintLayerId[index] = terrainLayerId;
        }

        private void PaintObstacleCell(int index, TerrainTypeData terrain, byte terrainLayerId)
        {
            Touch(index);

            // Avoid double-touch: do NOT call SetBlocked() here since it also calls Touch(), and we already called Touch() at the start of this method 
            if (!_blocked[index])
                SetBlocked_NoTouch(index, true);

            // NOTE:
            //      TerrainID is the category enum (Land/Subterranean/Liquid/Air), not the unique terrain-rule identity.
            //      This saets a groupng of terrain types, not the specific terrain type, so different terrain types that share the same TerrainID will be
            //      grouped together for gameplay purposes (e.g. all Land terrains will share the same TerrainID even if they have different costs/colors/rules).
            //
            //      The terrainLayerId is computed and used only for _lastPaintLayerId, local for this generation of the map.
            //      Identifying which terrain type is responsible for painting this cell, so it can be used for generation rules that
            //      depend on "what was painted on this cell" (e.g. only paint this terrain on cells painted by that terrain,
            //      or avoid painting on cells painted by that terrain).
            //
            //      _terrainTypeIds stores category, _lastPaintLayerId stores rule id
            // 
            _terrainTypeIds[index] = (byte)terrain.TerrainID;
            _terrainCost[index] = 0;
            _baseColors[index] = terrain.Color;
            _lastPaintLayerId[index] = terrainLayerId;
        }



        #endregion



        #region Map-state clearing rules

        private void ResetToBase()
        {
            //EnsureGenBuffers();       // calling once in Generate() before TryGenerateAttempts loop should be enough
            AssertBuffersReady(); 

            for (int i = 0; i < _cellCount; i++)
            {
                _blocked[i] = false;
                _terrainTypeIds[i] = _baseTerrainType;
                _terrainCost[i] = _baseWalkableCost;
                _baseColors[i] = _baseWalkableColor;
                _lastPaintLayerId[i] = 0;
            }

            _blockedCount = 0;  // WARNING: correct ONLY if attempt always starts from base + all block writes go through went through Touch-path: SetBlocked/Touch
                                //          Safety provided by AssertBaseIsUnblocked(), so fine for now, but need to be careful if changing the logic of how
                                //          attempts are reset or how blocked writes are done, this could become a source of bugs if not properly maintained.
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

        private void BeginAttempt()
        {
            _scratch.touched.Clear();
            _scratch.touchedStampId = NextTouchedId();      // guarantees non-zero
        }

        private void Touch(int idx)
        {
            if (_scratch.touchedStamp[idx] == _scratch.touchedStampId) return;
            _scratch.touchedStamp[idx] = _scratch.touchedStampId;
            _scratch.touched.Add(idx);
        }

        private void UndoTouchedToBase()
        {
            var touched = _scratch.touched;
            for (int i = 0; i < touched.Count; i++)
            {
                int idx = touched[i];
                _blocked[idx] = false;
                _terrainTypeIds[idx] = _baseTerrainType;
                _terrainCost[idx] = _baseWalkableCost;
                _baseColors[idx] = _baseWalkableColor;
                _lastPaintLayerId[idx] = 0;
            }
            touched.Clear();
            _blockedCount = 0;  // WARNING: correct ONLY if attempt always starts from base + all block writes go through went through Touch-path: SetBlocked/Touch
                                //          Safety provided by AssertBaseIsUnblocked(), so fine for now, but need to be careful if changing the logic of how
                                //          attempts are reset or how blocked writes are done, this could become a source of bugs if not properly maintained.
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

        private int ComputeFocusThicknessCells(in TerrainTypeData.AreaFocusWeights weights)
        {
            float percent = Mathf.Clamp(weights.InteriorMarginPercent, 0f, 0.49f);
            int minDim = Mathf.Min(_width, _height);

            // ensures _rng.Next(low, high) has low < high.
            int maxMargin = Mathf.Max(0, (minDim - 1) / 2);

            int marginByPercent = Mathf.RoundToInt(minDim * percent);
            int margin = Mathf.Max(Mathf.Max(0, weights.InteriorMinMargin), marginByPercent);

            return Mathf.Clamp(margin, 0, maxMargin);
        }

        // These two became the same internal code, but has different caller reasons so keeping as wrappers until I can fix making them different again if needed.
        private int ComputeInteriorMarginCells(in TerrainTypeData.AreaFocusWeights w) => ComputeFocusThicknessCells(in w);
        private int ComputeEdgeBandCells(in TerrainTypeData.AreaFocusWeights w) => ComputeFocusThicknessCells(in w);


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

            if (_scratch.heatStamp == null || _scratch.heatStamp.Length != _cellCount)
                _scratch.heatStamp = new int[_cellCount];

            if (_scratch.touchedStamp == null || _scratch.touchedStamp.Length != _cellCount)
                _scratch.touchedStamp = new int[_cellCount];
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

        private int NextHeatId()
        {
            int next = _scratch.heatStampId + 1;
            if (next <= 0 || next == int.MaxValue)
            {
                Array.Clear(_scratch.heatStamp, 0, _scratch.heatStamp.Length);

                _scratch.heatStampId = 1;
                return 1;
            }

            // restart at 1 (0 means unmarked)
            _scratch.heatStampId = next;
            return next;
        }

        private int NextTouchedId()
        {
            int next = _scratch.touchedStampId + 1;
            if (next <= 0 || next == int.MaxValue)
            {
                Array.Clear(_scratch.touchedStamp, 0, _scratch.touchedStamp.Length);
                _scratch.touchedStampId = 1;
                return 1;
            }

            _scratch.touchedStampId = next;
            return next;
        }



        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void AssertBuffersReady()
        {
            if (_scratch.queue == null || _scratch.queue.Length != _cellCount)
                throw new InvalidOperationException("Gen buffers not prepared. Call EnsureGenBuffers after BindMapData.");
        }


        #endregion



        private static void EnsureListCapacity(List<int> list, int needed)
        {
            if (needed > list.Capacity) list.Capacity = needed;
        }



        #region Debug telemetry

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void AssertAttemptStartsFromUnblockedBase()
        {
            // If you ever decide base can be blocked later, this is where you’ll notice.
            for (int i = 0; i < _cellCount; i++)
                if (_blocked[i]) { Debug.LogError("MapGenerator assumes base map is fully unblocked."); break; }
        }


        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void RecordAttempt(in AttemptStats s)
        {
            _telemetry[_telemetryWrite++ & (TelemetryRing - 1)] = s;
        }


        #endregion



    }


}
