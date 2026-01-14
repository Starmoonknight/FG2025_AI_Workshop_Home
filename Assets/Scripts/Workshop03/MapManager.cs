using System;
using UnityEngine;


namespace AI_Workshop03
{

    // Version2 of BoardManager    -> MapManager
    public partial class MapManager : MonoBehaviour
    {

        [Header("Game Camera Settings")]
        [SerializeField] private Camera _mainCamera;
        [SerializeField] private float _cameraPadding = 1f;

        [Header("Board Settings")]
        [SerializeField] private Renderer _boardRenderer;
        [SerializeField, Min(1)] private int _width = 10;
        [SerializeField, Min(1)] private int _height = 10;

        [Header("Texture Mapping")]
        [SerializeField] private bool _flipTextureX = true;
        [SerializeField] private bool _flipTextureY = true;


        [Header("Map Generation Settings")]
        [SerializeField] private int _seed = 0;
        [SerializeField]  private int _lastGeneratedSeed = 0;
        [SerializeField, Range(0f, 1f)] private float _minUnblockedPercent = 0.5f;
        [SerializeField, Range(0f, 1f)] private float _minReachablePercent = 0.75f;
        [SerializeField]  private int _maxGenerateAttempts = 50;

        private System.Random _goalRng;

        [Header("Generation Data")]
        [SerializeField] private TerrainTypeData[] _terrainData;

        private readonly MapDataGenerator _generator = new MapDataGenerator();

        [Header("3D Obstacle Visuals")]
        [SerializeField] private GameObject _obstacleCubePrefab;
        [SerializeField] private Transform _obstacleRoot;
        private GameObject[] _obstacleInstances;


        // Accessability Data
        private int[] _bfsQueue;
        private int[] _reachStamp;
        private int _reachStampId;



        [Header("Colors")]
        [SerializeField]
        private Color32 _walkableColor = new(255, 255, 255, 255);       // White
        [SerializeField]
        private Color32 _obstacleColor = new(0, 0, 0, 255);             // Black
        [SerializeField]
        private Color32 _unReachableColor = new(255, 150, 150, 255);    // Light Red


        public int Width => _width;
        public int Height => _height;
        public int CellCount => _cellCount;
        public int MinTerrainCost => _minTerrainCost;
        public int LastGeneratedSeed => _lastGeneratedSeed;



        private const float UNITY_PLANE_SIZE = 10f; // Plane is 10x10 units at scale 1

        private static readonly (int dirX, int dirY)[] Neighbors4 =
        {
            (-1, 0), (1, 0), (0, -1), (0, 1)
        };

        // Neighbor offsets for 8-directional movement with associated step costs, dx stands for change in x, dy for change in y
        private static readonly (int dirX, int dirY, int stepCost)[] Neighbors8 =
        {
            (-1,  0, 10),  //Left
            ( 1,  0, 10),  //Right
            ( 0, -1, 10),  //Down
            ( 0,  1, 10),  //Up
            (-1, -1, 14),  //Bottom-Left
            ( 1, -1, 14),  //Bottom-Right
            (-1,  1, 14),  //Top-Left
            ( 1,  1, 14)   //Top-Right
        };



        private void Awake()
        {
            GenerateNewGameBoard();

            //DebugCornerColorTest(); 
        }

        private void LateUpdate()
        {
            if (!_textureDirty) return;
            _textureDirty = false;
            RefreshTexture();
        }




        #region Cell Data Getters

        // this was a later addon to match diagonal bool option in the A*, keept the old signature below for other callers
        public int BuildReachableFrom(int startIndex) =>
            BuildReachableFrom(startIndex, allowDiagonals: true);

        public int BuildReachableFrom(int startIndex, bool allowDiagonals)
        {
            EnsureReachBuffers();
            if (!IsValidCell(startIndex) || _blocked[startIndex])
                return 0;

            // Prevent stamp id overflow, rare but possible
            if (_reachStampId == int.MaxValue)
            {
                Array.Clear(_reachStamp, 0, _reachStamp.Length);
                _reachStampId = 0; // so next ++ becomes 1
            }

            _reachStampId++;

            int head = 0;   // index of the next item to dequeue
            int tail = 0;   // index where the next item will be enqueued

            _bfsQueue[tail++] = startIndex;
            _reachStamp[startIndex] = _reachStampId;

            int reachableCount = 1;

            while (head < tail)
            {
                int currentIndex = _bfsQueue[head++];
                IndexToXY(currentIndex, out int coordX, out int coordY);

                foreach (var (dirX, dirY, _) in Neighbors8)
                {
                    // need to match the A* diagonal toggle, otherwise might have an unreachable map but say it is reachable
                    if (!allowDiagonals && dirX != 0 && dirY != 0)
                        continue;

                    if (dirX != 0 && dirY != 0)  // need to change to match A*  // think I changed it but double check later
                    {
                        // Diagonal movement allowed only if at least one side is open
                        bool sideAOpen = TryCoordToIndex(coordX + dirX, coordY, out int sideIndexA) && !_blocked[sideIndexA];
                        bool sideBOpen = TryCoordToIndex(coordX, coordY + dirY, out int sideIndexB) && !_blocked[sideIndexB];

                        if (!sideAOpen && !sideBOpen)
                            continue;
                    }

                    TryEnqueue(coordX + dirX, coordY + dirY);
                }
            }

            return reachableCount;

            void TryEnqueue(int newX, int newY)
            {
                if (!TryCoordToIndex(newX, newY, out int ni)) return;
                if (_blocked[ni]) return;
                if (_reachStamp[ni] == _reachStampId) return;

                _reachStamp[ni] = _reachStampId;
                _bfsQueue[tail++] = ni;
                reachableCount++;
            }

        }


        public void BuildVisualReachableFrom(int startIndex, bool allowDiagonals = true)
        {
            BuildReachableFrom(startIndex, allowDiagonals);
            RebuildCellColorsFromBase();

            for (int i = 0; i < _cellCount; i++)
            {
                if (_blocked[i]) continue;

                bool isReachable = (_reachStamp[i] == _reachStampId);
                if (!isReachable)
                {
                    IndexToXY(i, out int x, out int y);
                    bool odd = ((x + y) & 1) == 1;
                    _cellColors[i] = ApplyGridShading(_unReachableColor, odd);
                }
            }

            _textureDirty = true;
        }


        // this was a later addon to match diagonal bool option in the A*, keept the old signature below for other callers
        public bool TryPickRandomReachableGoal(int startIndex, int minManhattan, out int goalIndex) =>
            TryPickRandomReachableGoal(startIndex, minManhattan, allowDiagonals: true, out goalIndex);

        // If I want the goal to be far-ish away, can also pick minManhattan as something like (_width + _height) / 4. 
        // This ensures the goal is at least a quarter of the board’s perimeter away from the start.
        public bool TryPickRandomReachableGoal(int startIndex, int minManhattan, bool allowDiagonals, out int goalIndex)
        {
            goalIndex = -1;

            int reachableCount = BuildReachableFrom(startIndex, allowDiagonals);
            if (reachableCount <= 1) return false;

            IndexToXY(startIndex, out int startX, out int startY);

            int candidateCount = 0;

            for (int i = 0; i < _cellCount; i++)
            {
                if (_blocked[i]) continue;                      // skip unwalkable cells
                if (_reachStamp[i] != _reachStampId) continue;  // if not reachable in current step
                if (i == startIndex) continue;                  // skip starting cell

                IndexToXY(i, out int cellX, out int cellY);
                int manhattan = Math.Abs(cellX - startX) + Math.Abs(cellY - startY);
                if (manhattan < minManhattan) continue;

                candidateCount++;

                // Reservoir sampling: each candidate has a 1/candidateCount chance to be selected
                if (_goalRng.Next(candidateCount) == 0)
                    goalIndex = i;
            }

            return goalIndex != -1;
        }


        #endregion



        #region Internal Helpers
        
        private void EnsureReachBuffers()
        {
            if (_bfsQueue == null || _bfsQueue.Length != _cellCount)
                _bfsQueue = new int[_cellCount];

            if (_reachStamp == null || _reachStamp.Length != _cellCount)
                _reachStamp = new int[_cellCount];
        }


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



        #endregion




    }

}
