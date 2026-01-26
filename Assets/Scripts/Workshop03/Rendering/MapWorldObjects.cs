using System.Collections.Generic;
using UnityEngine;


namespace AI_Workshop03
{

    // Cache renderers + colliders once per spawned GameObject, and reuse those cached arrays forever
    public sealed class WorldVisualInstance : MonoBehaviour
    {
        [HideInInspector] public Renderer[] Renderers;
        [HideInInspector] public Collider[] Colliders;

        private bool _cached;

        public void CacheIfNeeded()
        {
            if (_cached) return;

            // These allocate arrays ONCE, then you're done forever.
            Renderers = GetComponentsInChildren<Renderer>(true);
            Colliders = GetComponentsInChildren<Collider>(true);

            _cached = true;
        }
    }

    public class MapWorldObjects : MonoBehaviour
    {

        #region Fields

        [Header("Scene refs")]
        [SerializeField] private MapManager _mapManager;

        [Header("Fallback visuals")]
        [SerializeField] private GameObject _fallbackCubePrefab;
        [SerializeField] private Transform _worldRoot;

        [Header("Behaviour")]
        [SerializeField] private int _worldVisualsLayer = 0;    // default layer
        [SerializeField] private bool _obstaclesBlockPhysics = true;

        [Header("Prewarm")]
        [SerializeField] private bool _prewarmPools = true;
        [SerializeField] private int _prewarmPerPrefab = 64;
        [SerializeField] private int _prewarmFallbackCount = 256;
        [SerializeField] private int _prewarmPerFrame = 50;

        [Header("Tuning")]
        [SerializeField] private float _yOffset = 0f;
        [SerializeField] private bool _spawnForObstacles = true;
        [SerializeField] private bool _tintFallbackCube = true;
        private bool _spawnForWalkables = false;   // not [SerializeField] for now   
                                                   // NOTE DESIGN WARNING: need to find a sane way to spawn on bigger maps to not end up with million of objects.
                                                   // Need to find a way later to spawn “decor props” in a much sparser way.
                                                   // Or make them texture? Or active in visal space only?  

        // NOTE VARNING TEMP DESIGN AID: safety patching to many items built under one frame, need to look up better methods as well
        [Header("Safety Limits")]
        [SerializeField] private bool _useSafetyLimit = true;
        [SerializeField] private int _maxActiveWorldObjects = 20000;    // safe default for editor, a bandaid try to stop a cascading million item build 
        [SerializeField] private bool _abortRebuildIfOverCap = true;
        [SerializeField] private int _maxNewInstantiatesPerRebuild = 2000; // this should help to stop mass-instantiating in one frame 
        private int _newInstantiatesThisRebuild = 0;
        [SerializeField] private float _maxRebuildMs = 12f;     // Tracks elapsed real time. This should prevent Editor freezing,
                                                                // but will leave partially rebuilt visuals (acceptable for stress testing).
                                                                // Remember to fix fully!! 
        int totalCreated = 0;
        int maxTotalPrewarm = 5000;



        private MapData _data;

        // cell -> current instance
        private GameObject[] _instanceByCell;        // length = CellCount
        private GameObject[] _prefabKeyByCell;       // which prefab that instance belongs to (same length)

        // dictionary of terrain specific pools
        private Dictionary<GameObject, Stack<GameObject>> _poolByPrefab = new();

        // terrainId -> prefab lookup with O(1) access
        private GameObject[] _prefabByTerrainId = new GameObject[256];

        // reuse MPB to avoid GC + material instancing
        private MaterialPropertyBlock _mpb;         // decided to learn more about MPB so using it more in the project 


        private static readonly int ColorId_BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId_Color = Shader.PropertyToID("_Color");
        private readonly bool[] _tintPrefabByTerrainId = new bool[256];


        #endregion




        #region Lifecycle

        private void Awake()
        {
            if (_worldRoot == null) _worldRoot = transform;
            if (_mapManager == null) _mapManager = FindFirstObjectByType<MapManager>();

            _mpb = new MaterialPropertyBlock();

            CreateFallbackCubePrefab();
        }

        private void OnEnable()
        {
            if (_mapManager == null)
                _mapManager = FindFirstObjectByType<MapManager>();

            if (_mapManager != null)
            {
                _mapManager.OnMapRebuiltDataReady += HandleMapRebuilt;

                // If map already exists, sync immediately
                var current = _mapManager.Data;
                if (current != null)
                    HandleMapRebuilt(current);
            }
        }

        private void OnDisable()
        {
            if (_mapManager != null)
                _mapManager.OnMapRebuiltDataReady -= HandleMapRebuilt;
        }

        private void Start()
        {
            if (_prewarmPools)
                StartCoroutine(PrewarmPoolsCoroutine());
        }


        #endregion



        #region Map Hooks

        private void HandleMapRebuilt(MapData data)
        {
            _newInstantiatesThisRebuild = 0;
            DespawnAll();   // return all active objects to pool to not end up orphan anything when calling EnsureCapacity() if map size changes

            _data = data;
            if (_data == null) return;

            BuildTerrainPrefabLookup();
            EnsureCapacity();
            RebuildAll();
        }

        private void RebuildAll()
        {
            if (_data == null) return;

            // from before the bandaid-safety patch CellWantsAnyWorldObject existed 
            if (!_useSafetyLimit)
            {
                int n = _data.CellCount;
                for (int i = 0; i < n; i++)
                    UpdateCell(i);

                return; 
            }

            var start = Time.realtimeSinceStartup;
            float budget = _maxRebuildMs / 1000f;
            int cells = _data.CellCount;
            int spawned = 0;

            for (int i = 0; i < cells; i++)
            {
                // Predict whether this cell wants a spawn (cheap check)
                if (CellWantsAnyWorldObject(i))
                {
                    spawned++;

                    if (_abortRebuildIfOverCap && spawned > _maxActiveWorldObjects)
                    {
                        Debug.LogError(
                            $"[MapWorldObjects] ABORTED rebuild: would spawn >{_maxActiveWorldObjects} objects " +
                            $"(spawned so far={spawned}, mapCells={cells}). " +
                            $"Disable walkable spawning or lower obstacle density."
                        );

                        // Stop early -> should hopefully avoids crashing Unity
                        return;
                    }
                }

                UpdateCell(i);

                if (Time.realtimeSinceStartup - start > budget)
                {
                    Debug.LogWarning($"[MapWorldObjects] Rebuild hit time budget ({_maxRebuildMs}ms). Stopping early.");
                    return;
                }
            }
        }

        // maybe a RebuildIncremental start (later) 


        #endregion



        #region Public API

        /// <summary>
        /// Used for updating a single cell tile
        /// </summary>
        public void UpdateCell(int index)
        {

            if (_data == null) return;
            if (!_data.IsValidCellIndex(index)) return;

            bool isBlocked = _data.IsBlocked[index];
            byte terrainId = _data.TerrainTypeIds[index];
            Color32 cellColor = _data.BaseCellColors[index];

            // decide what prefab is needed here
            GameObject desiredPrefab = _prefabByTerrainId[terrainId];

            bool shouldSpawnByWalkability = (_spawnForWalkables && !isBlocked) || (_spawnForObstacles && isBlocked);

            // rules for spawning:
            // - spawn prefab if exists (walkable or obstacle, dosen't matter)
            // - if prefab is null and IsBlocked then spawn fallback cube
            // - if prefab is null and !IsBlocked do nothing
            bool wantsToSpawnSomething = shouldSpawnByWalkability &&
                (desiredPrefab != null ||                   // terrain has prefab
                (desiredPrefab == null && isBlocked));      // fallback cube for obstacles only

            if (!wantsToSpawnSomething)
            {
                DespawnCell(index);
                return; 
            }

            GameObject prefabKey;
            if (desiredPrefab != null)
            {
                prefabKey = desiredPrefab;
            }
            else
            {
                // fallback cube
                prefabKey = _fallbackCubePrefab;
                if (prefabKey == null)
                {
                    DespawnCell(index);
                    return;
                }
            }


            bool tintPrefab = _tintPrefabByTerrainId[terrainId];
            bool shouldTintFallback = (prefabKey == _fallbackCubePrefab) && _tintFallbackCube;  // tint fallback cube if enabled
            bool shouldTintPrefab = (prefabKey != _fallbackCubePrefab) && tintPrefab;   // tint prefab if terrain says so

            // if the correct prefab type exists on this cell already, just reposition/tint
            if (_instanceByCell[index] != null && _prefabKeyByCell[index] == prefabKey)
            {
                ApplyTransform(index, _instanceByCell[index], prefabKey);

                if (shouldTintFallback || shouldTintPrefab)
                    ApplyColor(_instanceByCell[index], cellColor);
                else
                    ClearTint(_instanceByCell[index]);

                return;
            }

            // if there is a prefab on this cell, but it is the wrong type -> return it to pool and spawn correct
            DespawnCell(index);


            // NOTE: can also be tweeked by keeping colliders but ignore them via layer collision matrix 
            //       Unity -> Project Settings -> Physics -> Layer Collision Matrix
            //       Can set to: WorldVisuals X Player, WorldVisuals X Agents, WorldVisuals X Ground, etc.
            //       useful to keep in mind for later if I want clicking on visuals for interactions, but still no physics blocking
            bool useCollider = isBlocked && _obstaclesBlockPhysics;
            GameObject instance = SpawnFromPool(prefabKey, useCollider);

            if (instance == null)
            {
                // make sure the cell doesn't keep old references
                DespawnCell(index);
                return;
            }

            _instanceByCell[index] = instance;
            _prefabKeyByCell[index] = prefabKey;

            ApplyTransform(index, instance, prefabKey);

            if (shouldTintFallback || shouldTintPrefab)
                ApplyColor(instance, cellColor);
            else
                ClearTint(instance);
        }
       
        public void DespawnAll()
        {
            if (_instanceByCell == null) return;

            for (int i = 0; i < _instanceByCell.Length; i++)
                DespawnCell(i);
        }

        public void DespawnCell(int index)
        {
            var inst = _instanceByCell[index];
            if (inst == null) return;

            var key = _prefabKeyByCell[index];
            _instanceByCell[index] = null;
            _prefabKeyByCell[index] = null;

            ReturnToPool(key, inst);
        }

        // maybe a PrewarmNow (later)

        #endregion



        #region Core Update Logic

        private bool CellWantsAnyWorldObject(int index)
        {
            if (_data == null) return false;
            if (!_data.IsValidCellIndex(index)) return false;

            bool isBlocked = _data.IsBlocked[index];
            byte terrainId = _data.TerrainTypeIds[index];

            GameObject desiredPrefab = _prefabByTerrainId[terrainId];

            bool shouldSpawnByWalkability =
                (_spawnForWalkables && !isBlocked) ||
                (_spawnForObstacles && isBlocked);

            if (!shouldSpawnByWalkability)
                return false;

            // spawn prefab if exists, otherwise fallback cube for obstacles
            return desiredPrefab != null || isBlocked;
        }

        // method used to stop allocating a new array every time painting a cell, was a problem before   (used to have:  instance.GetComponentsInChildren<Renderer>(true); in ApplyColor / ClearTint)
        private WorldVisualInstance GetVisual(GameObject instance)
        {
            if (!instance.TryGetComponent(out WorldVisualInstance vis))
                vis = instance.AddComponent<WorldVisualInstance>();

            vis.CacheIfNeeded();
            return vis;
        }


        #endregion



        #region Pooling

        private GameObject SpawnFromPool(GameObject prefabKey, bool useCollider)
        {
            // first check if there is a record of this prefab type 
            if (!_poolByPrefab.TryGetValue(prefabKey, out var stack))
            {
                // if ther was no matching prefab recorded, make a new stack in the dictionary for it. For pooling
                stack = new Stack<GameObject>(32);
                _poolByPrefab[prefabKey] = stack;
            }
            
            bool newlyCreated = false;
 
            GameObject instance;
            if (stack.Count > 0)
            {
                // activate and re-use a pooled prefab of correct type 
                instance = stack.Pop();
            }
            else
            {
                // NOTE: safety patching to many items built under one frame
                if (_useSafetyLimit && (_newInstantiatesThisRebuild >= _maxNewInstantiatesPerRebuild))
                {
                    Debug.LogError(
                        $"[MapWorldObjects] ABORTED: too many new instantiates in one rebuild " +
                        $"({_newInstantiatesThisRebuild}/{_maxNewInstantiatesPerRebuild})."
                    );
                    return null; // caller should handle
                }

                // or Instantiate a new one if the pool is empty
                instance = Instantiate(prefabKey, _worldRoot);
                _newInstantiatesThisRebuild++;
                newlyCreated = true; 
            }

            instance.SetActive(true);

            // finishing touches, setting layer and colliders 
            if (newlyCreated)
                ApplyLayerRecursively(instance, _worldVisualsLayer);

            var vis = GetVisual(instance);
            SetCollidersEnabled(vis, useCollider);

            return instance;
        }

        private void ReturnToPool(GameObject prefabKey, GameObject instance)
        {
            // the prefabKey is the identity of the type
            if (prefabKey == null || instance == null) return;

            instance.SetActive(false);
            instance.transform.SetParent(_worldRoot, worldPositionStays: false);

            // look up pool stack for this prefabKey and if pool doesn't exist, create it 
            if (!_poolByPrefab.TryGetValue(prefabKey, out var stack))
            {
                stack = new Stack<GameObject>(32);
                _poolByPrefab[prefabKey] = stack;
            }

            // store the object for later use
            stack.Push(instance);
        }

        // maybe a EnsurePoolStack (later)

        #endregion



        #region Rendering & Transform

        private void ApplyTransform(int index, GameObject instance, GameObject prefabKey)
        {
            if (_data == null) return;

            // if using a terrain prefab, place it on the correct cell tile on the map
            Vector3 pos = _data.IndexToWorldCenterXZ(index, yOffset: _yOffset);

            // otherwise, scale fallback cube to cell size and then place it
            if (prefabKey == _fallbackCubePrefab)
            {
                float scale = Mathf.Max(0.01f, _data.CellTileSize);
                instance.transform.localScale = new Vector3(scale, scale, scale);
                instance.transform.position = pos + Vector3.up * (scale * 0.5f);
            }
            else
            {
                instance.transform.position = pos;
            }
        }

        private void ApplyColor(GameObject instance, Color32 cellColor)
        {
            if (instance == null || _data == null) return;

            var vis = GetVisual(instance);
            var renderers = vis.Renderers;
            if (renderers == null || renderers.Length == 0) return;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                r.GetPropertyBlock(_mpb);

                // should work for both legacy and URP materials
                _mpb.SetColor(ColorId_BaseColor, cellColor);
                _mpb.SetColor(ColorId_Color, cellColor);

                r.SetPropertyBlock(_mpb);
            }
        }

        // NOTE FIX LATER: track a bool WasTinted on WorldVisualInstance and only clear when needed!
        private void ClearTint(GameObject instance)
        {
            if (instance == null || _data == null) return;

            var vis = GetVisual(instance);
            var renderers = vis.Renderers;
            if (renderers == null || renderers.Length == 0) return;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                r.GetPropertyBlock(_mpb);

                _mpb.SetColor(ColorId_BaseColor, Color.white);
                _mpb.SetColor(ColorId_Color, Color.white);

                r.SetPropertyBlock(_mpb);
            }
        }
        
        private static void ApplyLayerRecursively(GameObject go, int layer)
        {
            // if a prefab has childern this should loop through all of thme and set to correct layer 
            go.layer = layer;
            foreach (Transform child in go.transform)
                ApplyLayerRecursively(child.gameObject, layer);
        }

        private void SetCollidersEnabled(WorldVisualInstance vis, bool enabled)
        {
            var cols = vis.Colliders;
            if (cols == null) return;

            for (int i = 0; i < cols.Length; i++)
                cols[i].enabled = enabled;
        }


        #endregion



        #region Lookup / Setup

        private void BuildTerrainPrefabLookup()
        {
            // clear lookup before building a new one
            for (int i = 0; i < _prefabByTerrainId.Length; i++)
            {
                _prefabByTerrainId[i] = null;
                _tintPrefabByTerrainId[i] = false;
            }

            if (_mapManager == null) return;

            var rules = _mapManager.TerrainRules; 
            if (rules == null) return;

            for (int i = 0; i < rules.Length; i++)
            {
                var terrain = rules[i];
                if (terrain == null) continue;

                byte id = (byte)terrain.TerrainID;

                // NOTE: if multiple rules share same TerrainID, last one wins, usually you want each TerrainID unique for visuals.
                //       I have not yet built in a safety for this, I think. 
                var previous = _prefabByTerrainId[id];
                if (terrain.VisualPrefab != null)
                {
                    // warning when duplicate IDs exist:
                    if (previous != null && previous != terrain.VisualPrefab)
                        Debug.LogWarning($"Duplicate TerrainID {id} found. Last one wins: {terrain.name}");

                    _prefabByTerrainId[id] = terrain.VisualPrefab;
                }


                // tint flag is stored even if prefab is missing so fallback visuals can still inherit terrain color if desired
                _tintPrefabByTerrainId[id] = terrain.ColorToTintPrefab;
            }
        }

        private void EnsureCapacity()
        {
            // See if this is a better check? And if so use everywhere! 
            /*
            if (_data == null && _mapManager.Data != null)
                _data = _mapManager.Data;
            else if (_data == null)
            {
                Debug.LogWarning("MapWorldObjects could not find any map data!");
                return;
            }
            */

            if (_data == null) return; 

            int n = _data.CellCount;

            if (_instanceByCell == null || _instanceByCell.Length != n)
                _instanceByCell = new GameObject[n];

            if (_prefabKeyByCell == null || _prefabKeyByCell.Length != n)
                _prefabKeyByCell = new GameObject[n];
        }

        // create a normal 3D cube as a prefab fallback, 
        private void CreateFallbackCubePrefab()
        {
            if (_fallbackCubePrefab != null) return;

            var fallbackObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fallbackObject.name = "[FallbackObstacleCubePrefab_Runtime]";
            fallbackObject.SetActive(false);


            // Put the fallback cube under this object so it doesn't clutter the scene root
            fallbackObject.transform.SetParent(transform, worldPositionStays: false);

            _fallbackCubePrefab = fallbackObject;
        }


        #endregion



        #region Prewarm

        private System.Collections.IEnumerator PrewarmPoolsCoroutine()
        {
            if (_mapManager == null) yield break;

            // collect unique prefab keys that might be spawned
            var prefabKeys = new List<GameObject>(64);

            // the _fallbackCubePrefab will allways be included in prefabKeys
            if (_fallbackCubePrefab != null)
                prefabKeys.Add(_fallbackCubePrefab);

            // add all unique terrain prefabs  
            var rules = _mapManager.TerrainRules;
            if (rules != null)
            {
                for (int i = 0; i < rules.Length; i++)
                {
                    var t = rules[i];
                    if (t == null || t.VisualPrefab == null) continue;

                    if (!prefabKeys.Contains(t.VisualPrefab))
                        prefabKeys.Add(t.VisualPrefab);
                }
            }

            int spawnedThisFrame = 0;

            for (int k = 0; k < prefabKeys.Count; k++)
            {
                GameObject key = prefabKeys[k];

                int targetCount = (key == _fallbackCubePrefab)
                    ? _prewarmFallbackCount
                    : _prewarmPerPrefab;

                for (int i = 0; i < targetCount; i++)
                {

                    if (totalCreated >= maxTotalPrewarm)
                    {
                        Debug.LogWarning($"[MapWorldObjects] Prewarm stopped at cap {maxTotalPrewarm}.");
                        yield break;
                    }

                    var go = Instantiate(key, _worldRoot);
                    totalCreated++;

                    go.SetActive(false);

                    // make sure caching exists once
                    var vis = GetVisual(go);

                    // put into pool immediately
                    ReturnToPool(key, go);

                    spawnedThisFrame++;
                    if (spawnedThisFrame >= _prewarmPerFrame)
                    {
                        spawnedThisFrame = 0;
                        yield return null; // spread work over frames to try avoid throttling systems 
                    }
                }
            }

            Debug.Log($"[MapWorldObjects] Prewarm complete. Keys={prefabKeys.Count}, perPrefab={_prewarmPerPrefab}, fallback={_prewarmFallbackCount}");
        }

        // maybe a StartPrewarm (later)

        #endregion



        #region Debug / Safety
        // Safety counters reset
        // Abort helpers / debug logs
        #endregion









        /*    Need to place the prefabs in the array to follow same idx model as all other data
         *    
         *    
         *    ? use parts of this below ?
         * 
        var pos = _mapManager.IndexToWorldCenter(index, 0.5f); // cube half-height
        cube.transform.position = pos;
        cube.transform.localScale = new Vector3(1f, 1f, 1f);

        var r = cube.GetComponent<Renderer>();
        r.material.color = _baseCellColors[index]; // or terrainData color
        */



        /* Plans for future methods:
         * 
         * RebuildTerrainProps()
         * PlaceLootAtCell(int idx, LootDefinition loot)
         * ClearProps()   // pooling
         * SpawnPrefabOnCell(int idx, GameObject prefab, ...)
         * 
         * 
         * Future loot system should not need to know about _obstacleInstances[] or renderer state.
         *
         *   It should call MapManager in a clean way like:
         *      -  bool IsBlocked(int idx)
         *      - Vector3 GetCellWorldCenter(int idx)
         *      - bool TryGetRandomWalkableCell(out int idx)
         *      - byte GetTerrainKind(int idx)
         */









    }

}
