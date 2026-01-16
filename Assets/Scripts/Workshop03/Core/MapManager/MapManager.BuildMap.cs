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
            _data.ResetToBase(
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

            // Scale plane to match grid world size (X = width, Z = height)
            _boardRenderer.transform.localScale = new Vector3(_data.Width / UNITY_PLANE_SIZE, 1f, _data.Height / UNITY_PLANE_SIZE);

            // Center = world coords can run 0,0
            _boardRenderer.transform.position = new Vector3(_data.Width * 0.5f, 0f, _data.Height * 0.5f);   // Center the quad, works in XZ plane
            _boardRenderer.transform.rotation = Quaternion.identity;


            // THIS PART IS BROKERN, maybe fixed part of it
            // but need to fully implement later
            /*

            // Temp placeholder location!
            if (_worldObjects != null)
                _worldObjects.RebuildObstacleCubes(_data);

            */

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

            // Center of the board in world space (Quad centered at its transform)
            Vector3 center = _boardRenderer.transform.position;

            // Place camera above the board (a top down XZ plane view)
            _mainCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _mainCamera.transform.position = center + new Vector3(0f, 20f, 0f);

            // Fit whole board in view
            float halfH = _height * 0.5f;       // Z-size half
            float halfW = _width * 0.5f;        // X-size half

            float aspect = _mainCamera.aspect; // width / height
            float sizeToFitHeight = halfH;
            float sizeToFitWidth = halfW / aspect;

            _mainCamera.orthographicSize = Mathf.Max(sizeToFitHeight, sizeToFitWidth) + _cameraPadding;
        }


    }

}
