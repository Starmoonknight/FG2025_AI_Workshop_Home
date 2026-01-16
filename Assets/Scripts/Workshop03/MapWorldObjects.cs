using UnityEngine;


namespace AI_Workshop03
{
    public class MapWorldObjects : MonoBehaviour
    {
        [SerializeField] private MapManager _mapManager;

        [Header("3D Obstacle Visuals")]
        [SerializeField] private GameObject _obstacleCubePrefab;
        [SerializeField] private Transform _obstacleRoot;
        private GameObject[] _obstacleInstances;

        private void Awake()
        {
            if (_mapManager == null)
                _mapManager = FindFirstObjectByType<MapManager>();
        }

        private void OnEnable()
        {
            if (_mapManager == null) return;

            _mapManager.OnMapRebuilt += HandleMapRebuilt;

            if (_mapManager.Data != null)
                HandleMapRebuilt(_mapManager.Data);
        }

        private void OnDisable()
        {
            if (_mapManager != null)
                _mapManager.OnMapRebuilt -= HandleMapRebuilt;
        }

        private void HandleMapRebuilt(MapData data)
        {
            RebuildObstacleCubes(data);
        }



        public void RebuildObstacleCubes(MapData data)
        {
            if (_obstacleCubePrefab == null) return;


            if (_obstacleInstances != null && _obstacleInstances.Length > data.CellCount)
            {
                for (int i = data.CellCount; i < _obstacleInstances.Length; i++)
                {
                    if (_obstacleInstances[i] != null)
                        Destroy(_obstacleInstances[i]);
                }
            }

            if (_obstacleInstances == null || _obstacleInstances.Length != data.CellCount)
                _obstacleInstances = new GameObject[data.CellCount];

            for (int i = 0; i < data.CellCount; i++)
            {
                if (data.IsBlocked[i])
                {
                    if (_obstacleInstances[i] == null)
                    {


                        //Vector3 pos = data.IndexToWorldCenterXZ(i, 0.5f);
                        //_obstacleInstances[i] = Instantiate(_obstacleCubePrefab, pos, Quaternion.identity, _obstacleRoot);
                        
                        Vector3 pos = data.IndexToWorldCenterXZ(i, 0.5f);
                        Transform parent = _obstacleRoot != null ? _obstacleRoot : null;
                        _obstacleInstances[i] = Instantiate(_obstacleCubePrefab, pos, Quaternion.identity, parent);






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

                    }
                }
                else
                {
                    if (_obstacleInstances[i] != null)
                    {
                        Destroy(_obstacleInstances[i]);     // need to do pooling instead of destruction
                        _obstacleInstances[i] = null;
                    }
                }
            }
        }


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
