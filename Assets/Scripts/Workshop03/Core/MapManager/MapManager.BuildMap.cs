using UnityEngine;
using System;



namespace AI_Workshop03
{

    // MapManager.BuildMap.cs         -   Purpose: map creation + calling generator + map init / reseed
    public partial class MapManager
    {


        public void GenerateNewGameBoard()
        {
            ValidateGridSize();

            // Initialize or Resize: Ensure MapData instance exists and matches size
            if (_data == null)
                _data = new MapData(_width, _height);
            else
                _data.Resize(_width, _height);


            // Generate seeds
            int baseSeed = (_seed != 0) ? _seed : Environment.TickCount;    // if seed is 0 use random seed
            int genSeed = baseSeed;                                         // main generation randomness seed
            int orderSeed = baseSeed ^ unchecked((int)0x73856093);          // terrain paint ordering randomness (rarity shuffle), salted seed
            int goalSeed = baseSeed ^ unchecked((int)0x9E3779B9);           // goal picking randomness, salted seed

            // Store last used seed for reference
            _lastGeneratedSeed = baseSeed;
            Debug.Log($"[MapManager] Generated map with seed={baseSeed} (genSeed={genSeed}) (orderSeed={orderSeed})");
            UpdateSeedHud();

            // Initialize random generators to ensure seeded repeatability
            _goalRng = new System.Random(goalSeed);

            // Reset truth arrays to base state for a fresh generation   (walkable land, cost 10    /or whatever _baseTerrainCost is)
            _data.InitializeToBase(
                width: _width,
                height: _height,
                baseTerrainKind: (byte)TerrainID.Land,
                baseTerrainCost: _baseTerrainCost,
                baseTerrainColor: _walkableColor
            );


            // Setup the Debug
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _generator.Debug_DumpFocusWeights = _dumpFocusWeights;
            _generator.Debug_DumpFocusWeightsVerbose = _dumpFocusWeightsVerbose;
#else
            _generator.Debug_DumpFocusWeights = false;
            _generator.Debug_DumpFocusWeightsVerbose = false;
#endif


            // Call the BoardGenerator to generate the map by running terrain generation into the existing MapData arrays (single source of truth)
            _generator.Generate(
                _data,
                genSeed,                // Generation seed (0 = random). Controls placement randomness.
                orderSeed,              // Terrain order seed (rarity/layer shuffle). Controls order of terrain rules.
                _terrainData,           // Terrain rule list (ScriptableObject data). Each entry may paint/overwrite cells.
                _maxGenerateAttempts,   // Safety cap: max full generation retries before giving up (avoid infinite loops).
                _minUnblockedPercent,   // Required % of cells that must remain walkable (limits obstacle terrain budget). 
                _minReachablePercent,   // Required % of walkable cells that must be connected (flood-fill from start).
                BuildReachableFrom      // Connectivity evaluator: returns reachable walkable cell count from a start index.
            );


            RecomputeMinTerrainCost();  // after terrain costs are set compute minimum traversal cost possible on this map

            // Board placement in world space
            float worldW = _data.Width * _cellTileSize;
            float worldH = _data.Height * _cellTileSize;

            // Scale plane to match grid world size, X = width, Z = height  (Unity Plane is 10x10 at scale 1)
            _boardRenderer.transform.localScale = new Vector3(worldW / UNITY_PLANE_SIZE, 1f, worldH / UNITY_PLANE_SIZE);

            // Center = world coords can run 0,0
            _boardRenderer.transform.position = new Vector3(worldW * 0.5f, 0f, worldH * 0.5f);   // Center the plane, works in XZ plane
            _boardRenderer.transform.rotation = Quaternion.identity;

            // Increment how many maps have been built
            _mapBuildId++;

            // Grid origin is bottom left corner in world space
            Vector3 gridOrigin = _boardRenderer.transform.position - new Vector3(worldW * 0.5f, 0f, worldH * 0.5f);

            _data.SetMapMeta(
                buildId: _mapBuildId,
                mapGenSeed: baseSeed,
                gridOriginWorld: gridOrigin,
                cellTileSize: _cellTileSize
                );


            FitCameraOrthoTopDown();
            
            
            // Notify listeners that map has been rebuilt
            OnMapRebuilt?.Invoke(_data);

        }


        // Recomputes the minimum terrain cost on the map (non-blocked cells only)
        private void RecomputeMinTerrainCost()
        {
            int minCost = int.MaxValue;

            int n = _data.CellCount;
            var blocked = _data.IsBlocked;
            var cost = _data.TerrainCosts;

            for (int i = 0; i < n; i++)
            {
                if (blocked[i]) continue;
                int c = cost[i];
                if (c < minCost) minCost = c;
            }

            if (minCost == int.MaxValue) minCost = _baseTerrainCost; // default if no walkable cells
            if (minCost < 1) minCost = 1;  // avoid zero cost

            _minTerrainCost = minCost;
        }


        // Make sure map size is valid
        private void ValidateGridSize()
        {
            _width = Mathf.Max(1, _width);
            _height = Mathf.Max(1, _height);

            long count = (long)_width * _height;
            if (count > int.MaxValue)
                throw new OverflowException("Grid too large for int indexing.");
        }


        // Fit the main camera to show the whole board in orthographic top-down view
        private void FitCameraOrthoTopDown()
        {
            if (_mainCamera == null) _mainCamera = Camera.main;
            if (_mainCamera == null) return;

            _mainCamera.orthographic = true;

            Vector3 center = _data.GridCenter;

            // Place camera above the board (a top down XZ plane view)
            _mainCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _mainCamera.transform.position = center + Vector3.up * 20f;

            // World footprint sizes
            float worldW = _data.MaxWorld.x - _data.MinWorld.x;  // X size in world units
            float worldH = _data.MaxWorld.z - _data.MinWorld.z;  // Z size in world units

            float halfW = worldW * 0.5f;
            float halfH = worldH * 0.5f;

            float aspect = _mainCamera.aspect; // width / height
            float sizeToFitHeight = halfH;
            float sizeToFitWidth = halfW / aspect;

            _mainCamera.orthographicSize = Mathf.Max(sizeToFitHeight, sizeToFitWidth) + _cameraPadding;
        }


    }

}
