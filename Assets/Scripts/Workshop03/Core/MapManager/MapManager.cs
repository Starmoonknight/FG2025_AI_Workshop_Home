using System;
using UnityEngine;


namespace AI_Workshop03
{

    // Version2 of BoardManager    -> MapManager
    // MapManager.cs         -   Purpose: the "header / face file", top-level API and properties
    public partial class MapManager : MonoBehaviour
    {
        #region Inspector Fields and Private Properties

        [Header("Game Camera Settings")]
        [SerializeField] private Camera _mainCamera;
        [SerializeField] private float _cameraPadding = 1f;

        [Header("Board Settings")]
        [SerializeField] private Renderer _boardRenderer;
        [SerializeField, Min(1)] private int _width = 10;
        [SerializeField, Min(1)] private int _height = 10;
        [SerializeField, Min(1)] private int _baseTerrainCost = 10;
        [SerializeField] private bool _allowDiagonalTraversal = true;

        // NOTE:
        /*
         * 
         *   Make a MapGenConfig / MapDefaults class for default settings? 
         *       
         *    [Serializable]
         *    public class MapDefaults
         *    {
         *        public int BaseTerrainCost = 10;
         *        public Color32 WalkableColor = new Color32(60, 140, 60, 255);
         *        public byte BaseTerrainKey = (byte)TerrainID.Land;
         *    }
         *    
         *    Then have MapManager use _defaults.BaseTerrainCost everywhere
         *    Scalability architecturewhen more defaults appear?
        */

        [Header("Map Generation Settings")]
        [SerializeField] private int _seed = 0;
        [SerializeField] private int _lastGeneratedSeed = 0;
        [SerializeField, Range(0f, 1f)] private float _minUnblockedPercent = 0.5f;
        [SerializeField, Range(0f, 1f)] private float _minReachablePercent = 0.75f;
        [SerializeField] private int _maxGenerateAttempts = 50;

        [Header("Generation Data")]
        [SerializeField] private TerrainTypeData[] _terrainData;

        [SerializeField] private MapRenderer2D _renderer2D;
        [SerializeField] private MapWorldObjects _worldObjects;

        [Header("Colors")]
        [SerializeField] private Color32 _walkableColor = new(255, 255, 255, 255);       // White
        [SerializeField] private Color32 _obstacleColor = new(0, 0, 0, 255);             // Black

        private float _cellTileSize = 1f;
        private int _minTerrainCost = 10;   // minimum terrain cost on the map (used for pathfinding heuristic)

        private MapData _data;
        private MapReachability _reachability;

        private readonly MapDataGenerator _generator = new MapDataGenerator();
        private int _mapBuildId = 0;


        #endregion
        


        #region Public Properties and Events

        public int MinTerrainCost => _minTerrainCost;
        public int LastGeneratedSeed => _lastGeneratedSeed;
        public MapData Data => _data;
        public TerrainTypeData[] TerrainRules => _terrainData; 
        public Renderer BoardRenderer => _boardRenderer;
        public Collider BoardCollider => _boardRenderer != null ? _boardRenderer.GetComponent<Collider>() : null;


        public event Action<MapData> OnMapRebuilt;


        #endregion



        private const float UNITY_PLANE_SIZE = 10f; // Plane is 10x10 units at scale 1


        private void Awake()
        {
            if (_renderer2D == null)
                _renderer2D = FindFirstObjectByType<MapRenderer2D>();

            if (_worldObjects == null)
                _worldObjects = FindFirstObjectByType<MapWorldObjects>();

            _reachability = new MapReachability();

            GenerateNewGameBoard();

            //DebugCornerColorTest(); 
        }





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

            // Store last used seed for reference
            _lastGeneratedSeed = baseSeed;
            Debug.Log($"[MapManager] Generated map with seed={baseSeed} (genSeed={genSeed}) (orderSeed={orderSeed})");
            UpdateSeedHud();

            // Reset truth arrays to base state for a fresh generation   (walkable land, cost 10    /or whatever _baseTerrainCost is)
            _data.InitializeToBase(
                width: _width,
                height: _height,
                baseTerrainKind: (byte)TerrainID.Land,
                baseTerrainCost: _baseTerrainCost,
                baseTerrainColor: _walkableColor
            );



            // TODO (architecture): Generator debug flags are currently driven by MapManager for convenience.
            // If MapGenerator needs to become reusable (non-Unity, tests, tools), move these debug toggles into a
            // dedicated config/settings object (e.g. MapGenDebugSettings) passed into the generator instead.

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
                genSeed,                            // Generation seed (0 = random). Controls placement randomness.
                orderSeed,                          // Terrain order seed (rarity/layer shuffle). Controls order of terrain rules.
                _terrainData,                       // Terrain rule list (ScriptableObject data). Each entry may paint/overwrite cells.
                _maxGenerateAttempts,               // Safety cap: max full generation retries before giving up (avoid infinite loops).
                _minUnblockedPercent,               // Required % of cells that must remain walkable (limits obstacle terrain budget). 
                _minReachablePercent,               // Required % of walkable cells that must be connected (flood-fill from start).
                _allowDiagonalTraversal,            // Should diagonal tiles be included when determening map reachability potential 
                _reachability                       // Connectivity evaluator: returns reachable walkable cell count from a start index.
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
                cellTileSize: _cellTileSize,
                allowDiagonals: _allowDiagonalTraversal
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
