using UnityEngine;


namespace AI_Workshop03
{
    // MapManager.WorldObjects.cs        -   Purpose (visual 3D): managing 3D world objects (obstacles, etc)
    public partial class MapManager
    {

        [Header("3D Obstacle Visuals")]
        [SerializeField] private GameObject _obstacleCubePrefab;
        [SerializeField] private Transform _obstacleRoot;
        private GameObject[] _obstacleInstances;



        private void RebuildObstacleCubes()
        {
            if (_obstacleCubePrefab == null) return;

            if (_obstacleInstances == null || _obstacleInstances.Length != _cellCount)
                _obstacleInstances = new GameObject[_cellCount];

            for (int i = 0; i < _cellCount; i++)
            {
                if (_blocked[i])
                {
                    if (_obstacleInstances[i] == null)
                    {
                        Vector3 pos = IndexToWorldCenterXZ(i, 0.5f);
                        _obstacleInstances[i] = Instantiate(_obstacleCubePrefab, pos, Quaternion.identity, _obstacleRoot);


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
