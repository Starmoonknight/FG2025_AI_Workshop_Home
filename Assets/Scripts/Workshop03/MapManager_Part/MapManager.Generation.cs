using UnityEngine;
using System;



namespace AI_Workshop03
{

    // MapManager.Generation.cs         -   Partial class to hold map generation related methods
    public partial class MapManager
    {


        public void GenerateNewGameBoard()
        {
            ValidateGridSize();

            _cellCount = _width * _height;

            _blocked = new bool[_cellCount];
            _terrainKind = new byte[_cellCount];
            _terrainCost = new int[_cellCount];
            _baseCellColors = new Color32[_cellCount];
            _cellColors = new Color32[_cellCount];
            _lastPaintLayerId = new byte[_cellCount];

            int baseSeed = (_seed != 0) ? _seed : Environment.TickCount;   // if seed is 0 use random seed
            int genSeed = baseSeed;                               // main generation randomness seed
            int orderSeed = baseSeed ^ unchecked((int)0x73856093);  // terrain paint ordering randomness (rarity shuffle), salted seed
            int goalSeed = baseSeed ^ unchecked((int)0x9E3779B9);  // goal picking randomness, salted seed

            _lastGeneratedSeed = baseSeed;
            Debug.Log($"[MapManager] Generated map with seed={baseSeed} (genSeed={genSeed}) (orderSeed={orderSeed})");
            UpdateSeedHud();

            _goalRng = new System.Random(goalSeed);

            // Initialize all cells as walkable with default terrain cost
            for (int i = 0; i < _cellCount; i++)
            {
                _blocked[i] = false;
                _terrainCost[i] = 10;
                _baseCellColors[i] = _walkableColor;

                _lastPaintLayerId[i] = 0;
                _terrainKind[i] = (byte)TerrainID.Land;
            }


            // Setup the Debug
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _generator.Debug_DumpFocusWeights = _dumpFocusWeights;
            _generator.Debug_DumpFocusWeightsVerbose = _dumpFocusWeightsVerbose;
#else
            _generator.Debug_DumpFocusWeights = false;
            _generator.Debug_DumpFocusWeightsVerbose = false;
#endif


            // Call the BoardGenerator to set up the game 
            _generator.Generate(
                _width,
                _height,
                _blocked,               // if true blocks all movement over this tile
                _terrainKind,           // what kind of terrain type this tile is, id: 0 = basic/land   (Land/Liquid/etc)
                _terrainCost,           // cost modifier of moving over this tile
                _baseCellColors,        // base colors before any external modifiers
                _lastPaintLayerId,      // what TerrainData affect this, layer id: 0 = base 
                _walkableColor,         // base color before any terrain modifier
                10,                     // base cost before any terrain modifier
                genSeed,                // seed (0 means random)
                orderSeed,              // terrain paint ordering randomness (rarity shuffle)
                _terrainData,           // TerrainData[]
                _maxGenerateAttempts,   // limit for map generator
                _minUnblockedPercent,   // ratio of allowd un-blocked to blocked tiles 
                _minReachablePercent,   // how much walkable ground is required to be connected when BuildReachableFrom
                BuildReachableFrom      // Func<int,int> reachable count from start
            );



            RecomputeMinTerrainCost();  // after terrain costs are set compute minimum traversal cost possible on this map

            _gridTexture = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
            _gridTexture.filterMode = FilterMode.Point;
            _gridTexture.wrapMode = TextureWrapMode.Clamp;

            RebuildCellColorsFromBase();
            FlushTexture();

            // Scale plane to match grid world size (X = width, Z = height)
            _boardRenderer.transform.localScale = new Vector3(_width / UNITY_PLANE_SIZE, 1f, _height / UNITY_PLANE_SIZE);
            // Center = world coords can run 0,0
            _boardRenderer.transform.position = new Vector3(_width * 0.5f, 0f, _height * 0.5f);   // Center the quad, works in XZ plane
            _boardRenderer.transform.rotation = Quaternion.identity;


            var mat = _boardRenderer.material;

            // set mainTexture (should cover multiple shaders)
            mat.mainTexture = _gridTexture;

            // set whichever property the shader actually uses
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", _gridTexture);   // URP
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", _gridTexture);   // Built-in

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);




            // Temp placeholder location!
            RebuildObstacleCubes();




            FitCameraOrthoTopDown();
        }



        private void RecomputeMinTerrainCost()
        {
            int minCost = int.MaxValue;

            for (int i = 0; i < _cellCount; i++)
            {
                if (_blocked[i]) continue;

                if (_terrainCost[i] < minCost)
                    minCost = _terrainCost[i];
            }

            if (minCost == int.MaxValue) minCost = 10; // default if no walkable cells
            if (minCost < 1) minCost = 1;  // avoid zero cost

            _minTerrainCost = minCost;
        }



        private void ValidateGridSize()
        {
            if (_width <= 0) throw new ArgumentOutOfRangeException(nameof(_width));
            if (_height <= 0) throw new ArgumentOutOfRangeException(nameof(_height));

            long count = (long)_width * _height;
            if (count > int.MaxValue)
                throw new OverflowException("Grid too large for int indexing.");
        }



    }


}
