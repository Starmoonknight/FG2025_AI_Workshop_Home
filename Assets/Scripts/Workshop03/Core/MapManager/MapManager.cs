using System;
using System.Collections;
using UnityEngine;


namespace AI_Workshop03
{

    // Version2 of BoardManager    -> MapManager
    // MapManager.cs         -   Purpose: the "header / face file", top-level API and properties
    public partial class MapManager : MonoBehaviour
    {
        [Serializable]
        public struct GenerationStage
        {
            [Min(1)] public int attempts;
            [Range(0f, 1f)] public float minUnblockedPercent;
            [Range(0f, 1f)] public float minReachablePercent;
        }


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

        private float m_cellTileSize = 1f;
        private int m_minTerrainCost = 10;   // minimum terrain cost on the map (used for pathfinding heuristic)

        private MapData m_data;
        private MapReachability m_reachability;

        private readonly MapGenerator m_generator = new MapGenerator();
        private int m_mapBuildId = 0;

        private Collider m_boardColliderCached;



        private static readonly WaitForEndOfFrame m_waitForEndOfFrame = new WaitForEndOfFrame();


        #endregion



        #region Public Properties and Events

        public int MinTerrainCost => m_minTerrainCost;
        public int LastGeneratedSeed => _lastGeneratedSeed;
        public MapData Data => m_data;
        public TerrainTypeData[] TerrainRules => _terrainData; 
        public Renderer BoardRenderer => _boardRenderer;
        public Collider BoardCollider => m_boardColliderCached; 


        // Fired immediately after MapData is generated and assigned (data is valid/stable).
        public event Action<MapData> OnMapRebuiltDataReady;

        // Fired one frame later (after renderers/world objects had a chance to sync).
        public event Action<MapData> OnMapRebuiltVisualsReady;

        // NOTE: MapManager should have two events, one for map setup and one for telling other script's map is completely finnished 
        //       It should send the first event and then wait for a returnr ping from all neccessary other scripts before sending OnMapRebuilt
        //       Like an event called MapDataGenerated ->
        //       the the Render2D and WorldObjects do their things ->
        //       Ping back to MapManager that keeps track of how many pings it is waiting for and then calls the  OnMapRebuilt Event
        //
        //       Probably having a bool set in the end of Generate map where it is currently calling OnMapRebuilt
        //       it should switch to callin another event (like: MapDataGenerated) and setting a bool called "MapBeingBuilt = true" and in Update wait for the pings,
        //       then as soon as pings == expectedPings, turn bool false and call  OnMapRebuilt Event
        //
        //       FIXED: But needs another ovewrlook before I delete this note 


        #endregion



        private const float UNITY_PLANE_SIZE = 10f; // Plane is 10x10 units at scale 1


        private void Awake()
        {
            if (_renderer2D == null)
                _renderer2D = FindFirstObjectByType<MapRenderer2D>();

            if (_worldObjects == null)
                _worldObjects = FindFirstObjectByType<MapWorldObjects>();

            if (_boardRenderer != null)
                m_boardColliderCached = _boardRenderer.GetComponent<Collider>();

            m_reachability = new MapReachability();

            GenerateNewGameBoard();

            // Used to fix texture being mirrored and not lining up with coord system map truth 
            //DebugCornerColorTest(); 
        }





        public void GenerateNewGameBoard()
        {
            if (_boardRenderer == null) { Debug.LogError("Board Renderer missing, impossible to make map!"); return; }

            ValidateGridSize();

            // Initialize or Resize: Ensure MapData instance exists and matches size
            if (m_data == null)
                m_data = new MapData(_width, _height);
            else
                m_data.Resize(_width, _height);


            // Generate seeds
            int baseSeed = (_seed != 0) ? _seed : Environment.TickCount;    // if seed is 0 use random seed
            int genSeed = baseSeed;                                         // main generation randomness seed
            int orderSeed = baseSeed ^ unchecked((int)0x73856093);          // terrain paint ordering randomness (rarity shuffle), salted seed

            // Store last used seed for reference
            _lastGeneratedSeed = baseSeed;
            LogGenerationSeed(baseSeed, genSeed, orderSeed);
            UpdateSeedHud();

            // Reset truth arrays to base state for a fresh generation   (walkable land, cost 10    /or whatever _baseTerrainCost is)
            m_data.InitializeToBase(
                width: _width,
                height: _height,
                baseTerrainKind: (byte)TerrainID.Land,
                baseTerrainCost: _baseTerrainCost,
                baseTerrainColor: _walkableColor
            );



            // TODO (architecture): Generator debug flags are currently driven by MapManager for convenience.
            // If MapGenerator needs to become reusable outside Unity/editor tooling later (non-Unity, tests, tools)
            // Move these debug toggles into a dedicated config/settings object (e.g. MapGenDebugSettings), passing a
            // small debug settings object/config into Generate() instead.


            // Setup the Debug
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_generator.Debug_DumpFocusWeights = _dumpFocusWeights;
            m_generator.Debug_DumpFocusWeightsVerbose = _dumpFocusWeightsVerbose;
#else
            m_generator.Debug_DumpFocusWeights = false;
            m_generator.Debug_DumpFocusWeightsVerbose = false;
#endif


            // Call the BoardGenerator to generate the map by running terrain generation into the existing MapData arrays (single source of truth)
            m_generator.Generate(
                m_data,
                genSeed,                            // Generation seed (0 = random). Controls placement randomness.
                orderSeed,                          // Terrain order seed (rarity/layer shuffle). Controls order of terrain rules.
                _terrainData,                       // Terrain rule list (ScriptableObject data). Each entry may paint/overwrite cells.
                _maxGenerateAttempts,               // Safety cap: max full generation retries before giving up (avoid infinite loops).
                _minUnblockedPercent,               // Required % of cells that must remain walkable (limits obstacle terrain budget). 
                _minReachablePercent,               // Required % of walkable cells that must be connected (flood-fill from start).
                _allowDiagonalTraversal,            // Should diagonal tiles be included when determening map reachability potential 
                m_reachability                       // Connectivity evaluator: returns reachable walkable cell count from a start index.
            );


            RecomputeMinTerrainCost();  // after terrain costs are set compute minimum traversal cost possible on this map

            // Board placement in world space
            float worldW = m_data.Width * m_cellTileSize;
            float worldH = m_data.Height * m_cellTileSize;

            // Scale plane to match grid world size, X = width, Z = height  (Unity Plane is 10x10 at scale 1)
            _boardRenderer.transform.localScale = new Vector3(worldW / UNITY_PLANE_SIZE, 1f, worldH / UNITY_PLANE_SIZE);

            // Center = world coords can run 0,0
            _boardRenderer.transform.position = new Vector3(worldW * 0.5f, 0f, worldH * 0.5f);   // Center the plane, works in XZ plane
            _boardRenderer.transform.rotation = Quaternion.identity;

            // Increment how many maps have been built
            m_mapBuildId++;

            // Grid origin is bottom left corner in world space
            Vector3 gridOrigin = _boardRenderer.transform.position - new Vector3(worldW * 0.5f, 0f, worldH * 0.5f);

            m_data.SetMapMeta(
                buildId: m_mapBuildId,
                mapGenSeed: baseSeed,
                gridOriginWorld: gridOrigin,
                cellTileSize: m_cellTileSize,
                allowDiagonals: _allowDiagonalTraversal
                );


            // Notify listeners that map has been rebuilt
            HandleMapRebuiltInternal(); 

        }

        // NEW
        private void HandleMapRebuiltInternal()
        {
            FitCameraOrthoTopDown();

            // 1) Data is valid right now
            OnMapRebuiltDataReady?.Invoke(m_data);                        // Systems that only need data (pathfinding caches, reachability) subscribe to OnMapRebuiltDataReady

            // 2) Visuals will be valid after subscribers run + a frame passes. Only run the coroutine if someone is actually listening
            if (OnMapRebuiltVisualsReady != null)
                StartCoroutine(InvokeVisualsReadyEndOfFrame());     // Systems that might compete with initial setup should subscribe to OnMapRebuiltVisualsReady
        }

        // NEW
        private IEnumerator InvokeVisualsReadyEndOfFrame()
        {
            yield return m_waitForEndOfFrame;
            OnMapRebuiltVisualsReady?.Invoke(m_data);
        }


        // Recomputes the minimum terrain cost on the map (non-blocked cells only)
        private void RecomputeMinTerrainCost()
        {
            int minCost = int.MaxValue;

            int n = m_data.CellCount;
            var blocked = m_data.IsBlocked;
            var cost = m_data.TerrainCosts;

            for (int i = 0; i < n; i++)
            {
                if (blocked[i]) continue;
                int c = cost[i];
                if (c < minCost) minCost = c;
            }

            if (minCost == int.MaxValue) minCost = _baseTerrainCost; // default if no walkable cells
            if (minCost < 1) minCost = 1;  // avoid zero cost

            m_minTerrainCost = minCost;
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

            Vector3 center = m_data.GridCenter;

            // Place camera above the board (a top down XZ plane view)
            _mainCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _mainCamera.transform.position = center + Vector3.up * 20f;

            // World footprint sizes
            float worldW = m_data.MaxWorld.x - m_data.MinWorld.x;  // X size in world units
            float worldH = m_data.MaxWorld.z - m_data.MinWorld.z;  // Z size in world units

            float halfW = worldW * 0.5f;
            float halfH = worldH * 0.5f;

            float aspect = _mainCamera.aspect; // width / height
            float sizeToFitHeight = halfH;
            float sizeToFitWidth = halfW / aspect;

            _mainCamera.orthographicSize = Mathf.Max(sizeToFitHeight, sizeToFitWidth) + _cameraPadding;
        }




    }

}
