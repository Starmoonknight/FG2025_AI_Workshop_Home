using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;


namespace AI_Workshop02
{
    public class BoardManager : MonoBehaviour
    {

        [Header("Game Camera Settings")]
        [SerializeField]
        private Camera   _mainCamera;
        [SerializeField]
        private float   _cameraPadding = 1f;

        [Header("Board Settings")]
        [SerializeField]
        private Renderer _quadRenderer;
        [SerializeField, Min(1)]
        private int      _width = 10;
        [SerializeField, Min(1)]
        private int      _height = 10;

        [Header("Map Generation Settings")]
        [SerializeField]
        private int     _seed;
        [SerializeField, Range(0f, 1f)]
        private float   _minUnblockedPercent = 0.5f;
        [SerializeField, Range(0f, 1f)] 
        private float   _minReachablePercent = 0.75f;
        [SerializeField] 
        private int     _maxGenerateAttempts = 50;

        private System.Random _goalRng;

        [Header("Generation Data")]
        [SerializeField] private TerrainData[] _terrainData;


        private readonly BoardGenerator _generator = new BoardGenerator();

        [Header("Debug A* Costs Overlay")]
        [SerializeField] 
        private bool _showDebugCosts = true;
        [SerializeField] 
        private TMPro.TextMeshPro _costLabelPrefab;
        [SerializeField] 
        private Transform _costLabelRoot;
        [SerializeField] 
        private float _costLabelZOffset = -0.05f;

        private TMPro.TextMeshPro[] _costLabels;
        private readonly List<int> _costLabelsTouched = new();

        // Accessability Data
        private int[]   _bfsQueue; 
        private int[]   _reachStamp;
        private int     _reachStampId;


        // NOTE: In the task it explicitly mentions a Node[,] array.
        // But I represent nodes as per-cell data in 1D arrays indices in arrays rather than Node[,]
        // and then use the cells coordinates as its index value to match between all arrays 
        // I had a feeling it would be faster to be able to just access the parts of data that I needed at any time

        // Grid Data
        private int     _cellCount;
        private bool[]  _protected;         // if I in the future want the rng ExpandRandom methods to ignore certain tiles, (start/goal, maybe a border ring) that must never be selected:   if (_protected != null && _protected[i]) continue;
        private bool[]  _blocked;
        private byte[]  _terrainKind;
        private int[]   _terrainCost;
        private int     _minTerrainCost = 10;

        // Grid Visualization
        private Color32[] _baseCellColors;
        private Color32[] _cellColors;
        private Texture2D _gridTexture;
        private bool      _textureDirty; 
        private byte[]    _painterId;

        [Header("Colors")]
        [SerializeField] 
        private Color32 _walkableColor = new(255, 255, 255, 255);       // White
        [SerializeField] 
        private Color32 _obstacleColor = new(0, 0, 0, 255);             // Black
        [SerializeField]
        private Color32 _unReachableColor = new(255, 150, 150, 255);    // Light Red


        public InputAction ClickAction;

        public int Width => _width;
        public int Height => _height;
        public int CellCount => _cellCount;
        public int MinTerrainCost => _minTerrainCost;




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
        }

        private void OnEnable()
        {
            ClickAction = new InputAction(name: "Click", type: InputActionType.Button, binding: "<Mouse>/leftButton");
            ClickAction.performed += OnClickPerformed;
            ClickAction.Enable();
        }

        private void OnDisable()
        {
            if (ClickAction != null)
            {
                ClickAction.performed -= OnClickPerformed;
                ClickAction.Disable();
            }
        }

        private void LateUpdate()
        {
            if (!_textureDirty) return;
            _textureDirty = false;
            RefreshTexture();
        }



        #region Cell Data Getters

        // Checks if cell coordinates or index are within bounds
        public bool IsValidCell(int x, int y) => (uint)x < (uint)_width && (uint)y < (uint)_height;
        public bool IsValidCell(int index) => (uint)index < (uint)_cellCount;


        // check if cell is walkable, used for core loops, eg. pathfinding 
        public bool GetWalkable(int index)
        {
            if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
            return !_blocked[index];
        }

        // check terrain cost, used for core loops, eg. pathfinding 
        public int GetTerrainCost(int index)
        {
            if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
            return _terrainCost[index];
        }


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
                        bool sideAOpen =  TryCoordToIndex(coordX + dirX, coordY, out int sideIndexA) && !_blocked[sideIndexA];
                        bool sideBOpen =  TryCoordToIndex(coordX, coordY + dirY, out int sideIndexB) && !_blocked[sideIndexB];

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
            TryPickRandomReachableGoal(startIndex,minManhattan, allowDiagonals: true, out goalIndex);

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


        #region Cell Data Setter

        public void SetWalkable(int index, bool isWalkable = true)
        {
            if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));

            _blocked[index] = !isWalkable;              // blocked is the inverse of walkable, so need to register the opposit here to follow th name logic

            if (isWalkable)
            {
                _painterId[index] = 0;
                _terrainKind[index] = (byte)TerrainID.Land;
                _terrainCost[index] = 10;
                _baseCellColors[index] = _walkableColor;
            }
            else
            {
                _painterId[index] = 0;
                _terrainKind[index] = (byte)TerrainID.Land;
                _terrainCost[index] = 0;
                _baseCellColors[index] = _obstacleColor; 
            }

            IndexToXY(index, out int coordX, out int coordY);
            bool odd = ((coordX + coordY) & 1) == 1;    // for checkerboard color helper
            _cellColors[index] = ApplyGridShading(_baseCellColors[index], odd);

            _textureDirty = true;   
        }

        public void SetTerrainCost(int index, int terrainCost)
        {
            if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
            _terrainCost[index] = terrainCost;
        }

        public void SetCellData(int index, bool blocked, int terrainCost)
        {
            if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
            _blocked[index] = blocked;
            _terrainCost[index] = terrainCost;
        }

        public void PaintCell(int index, Color32 color, bool shadeLikeGrid = true, bool skipIfObstacle = true)
        {
            if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
            if (skipIfObstacle && _blocked[index]) return;

            if (shadeLikeGrid)
            {
                IndexToXY(index, out int coordX, out int coordY);
                bool odd = ((coordX + coordY) & 1) == 1;
                _cellColors[index] = ApplyGridShading(color, odd);
            }
            else
            {
                _cellColors[index] = color;
            }

            _textureDirty = true;
        }

        public void PaintCells(ReadOnlySpan<int> indices, Color32 color, bool shadeLikeGrid = true, bool skipIfObstacle = true)
        {
            for (int i = 0; i < indices.Length; i++)
                PaintCell(indices[i], color, shadeLikeGrid, skipIfObstacle);
        }


        public void ResetColorsToBase()
        {
            RebuildCellColorsFromBase();
            _textureDirty = true;

        }

        public void FlushTexture()
        {
            _textureDirty = false;
            RefreshTexture();
        }

        public void SetDebugCosts(int index, int g, int h, int f)
        {
            if (!_showDebugCosts) return;
            if (!IsValidCell(index)) return;
            if (_costLabelPrefab == null || _costLabelRoot == null) return;

            if (_costLabels == null || _costLabels.Length != _cellCount)
                _costLabels = new TMPro.TextMeshPro[_cellCount];

            var label = _costLabels[index];
            if (label == null)
            {
                label = Instantiate(_costLabelPrefab, _costLabelRoot);
                label.alignment = TMPro.TextAlignmentOptions.Center;
                _costLabels[index] = label;
            }

            if (!label.gameObject.activeSelf)
            {
                label.gameObject.SetActive(true);
                _costLabelsTouched.Add(index);
            }

            IndexToXY(index, out int x, out int y);
            label.transform.position = new Vector3(x + 0.5f, y + 0.5f, _costLabelZOffset);

            // show approx tiles step cost by dividing by 10. Layout: g and h small, f big. Format to one decimal place
            label.text = $"<size=60%>G{g / 10f:0.0} H{h / 10f:0.0}</size>\n<size=100%><b>F{f / 10f:0.0}</b></size>";
        }

        public void ClearDebugCostsTouched()
        {
            if (_costLabelsTouched.Count == 0) return;

            for (int i = 0; i < _costLabelsTouched.Count; i++)
            {
                int index = _costLabelsTouched[i];
                var label = _costLabels?[index];
                if (label != null) label.gameObject.SetActive(false);
            }

            _costLabelsTouched.Clear();
        }


        #endregion


        #region Other Utilities

        public void GenerateNewGameBoard()
        {
            ValidateGridSize();

            _cellCount = _width * _height;

            _blocked = new bool[_cellCount];
            _terrainKind = new byte[_cellCount];
            _terrainCost = new int[_cellCount];
            _baseCellColors = new Color32[_cellCount];
            _cellColors = new Color32[_cellCount];
            _painterId = new byte[_cellCount];

            int baseSeed = (_seed != 0) ? _seed : Environment.TickCount;
            int genSeed = baseSeed;
            int goalSeed = baseSeed ^ unchecked((int)0x9E3779B9);       // salted seed

            _goalRng = new System.Random(goalSeed);

            // Initialize all cells as walkable with default terrain cost
            for (int i = 0; i < _cellCount; i++)
            {
                _blocked[i] = false;
                _terrainCost[i] = 10;
                _baseCellColors[i] = _walkableColor;

                _painterId[i] = 0;
                _terrainKind[i] = (byte)TerrainID.Land;
            }

            // Call the BoardGenerator to set up the game 
            _generator.Generate(
                _width,
                _height,
                _blocked,               // if true blocks all movement over this tile
                _terrainKind,           // what kind of terrain type this tile is, id: 0 = basic/land   (Land/Liquid/etc)
                _terrainCost,           // cost modifier of moving over this tile
                _baseCellColors,        // base colors before any external modifiers
                _painterId,             // what TerrainData affect this, layer id: 0 = base 
                _walkableColor,         // base color before any terrain modifier
                10,                     // base cost before any terrain modifier
                genSeed,                // seed (0 means random)
                _terrainData,           // TerrainData[]
                _maxGenerateAttempts,   // limit for map generator
                _minUnblockedPercent,   // ratio of allowd un-blocked to blocked tiles 
                _minReachablePercent,   // how much walkable ground is required to be connected when BuildReachableFrom
                BuildReachableFrom      // Func<int,int> reachable count from start
            );

            RecomputeMinTerrainCost();  // after terrain costs are set compute minimum traversal cost possible on this map

            _gridTexture            = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
            _gridTexture.filterMode = FilterMode.Point;
            _gridTexture.wrapMode   = TextureWrapMode.Clamp;

            RebuildCellColorsFromBase();
            FlushTexture();

            _quadRenderer.transform.localScale = new Vector3(_width, _height, 1f);
            _quadRenderer.transform.position = new Vector3(_width * 0.5f, _height * 0.5f, 0f);   // Center the quad, works in XY plane
            //_quadRenderer.transform.position = new Vector3(_width * 0.5f, 0f, _height * 0.5f);   // Center the quad, works in XZ plane

            var mat = _quadRenderer.material;

            // set mainTexture (should cover multiple shaders)
            mat.mainTexture = _gridTexture;

            // set whichever property the shader actually uses
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", _gridTexture);   // URP
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", _gridTexture);   // Built-in

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);

            FitCameraOrthoTopDown();
        }

        // Public version with exception on out of bounds
        public int CoordToIndex(int x, int y)
        {
            if (!TryCoordToIndex(x, y, out int index))
                throw new ArgumentOutOfRangeException();
            return index;
        }

        // Safe version with bounds checking, use when not sure coordinates are valid
        public bool TryCoordToIndex(int x, int y, out int index)
        {
            if ((uint)x >= (uint)_width || (uint)y >= (uint)_height) { index = -1; return false; }
            index = x + y * _width;
            return true;
        }

        public void IndexToXY(int index, out int x, out int y)
        {
            x = index % _width;
            y = index / _width;
        }

        #endregion



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



        private void EnsureReachBuffers()
        {
            if (_bfsQueue == null || _bfsQueue.Length != _cellCount)
                _bfsQueue = new int[_cellCount];

            if (_reachStamp == null || _reachStamp.Length != _cellCount)
                _reachStamp = new int[_cellCount];
        }


        private void RefreshTexture()
        {
            _gridTexture.SetPixels32(_cellColors);
            _gridTexture.Apply(false);
        }

        private void RebuildCellColorsFromBase()
        {
            for (int i = 0; i < _cellCount; i++)
            {
                IndexToXY(i, out int x, out int y);
                bool odd = ((x + y) & 1) == 1;
                _cellColors[i] = ApplyGridShading(_baseCellColors[i], odd);
            }

            _textureDirty = true;
        }

        private static Color32 ApplyGridShading(Color32 c, bool odd)
        {
            // Small change so it’s visible but not ugly
            const int delta = 12;

            int d = odd ? +delta : -delta;

            byte r = (byte)Mathf.Clamp(c.r + d, 0, 255);
            byte g = (byte)Mathf.Clamp(c.g + d, 0, 255);
            byte b = (byte)Mathf.Clamp(c.b + d, 0, 255);

            return new Color32(r, g, b, c.a);
        }



        private void OnClickPerformed(InputAction.CallbackContext context)
        {
            if (_mainCamera == null) _mainCamera = Camera.main;
            if (_mainCamera == null) return;

            Ray ray = _mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (!Physics.Raycast(ray, out RaycastHit hit, 500f))
                return;

            Collider quadCollider = _quadRenderer.GetComponent<Collider>();
            if (hit.collider != quadCollider) return;

            Vector2 uv = hit.textureCoord;

            int cellX = Mathf.FloorToInt(uv.x * _width);
            int cellY = Mathf.FloorToInt(uv.y * _height);

            cellX = Mathf.Clamp(cellX, 0, _width - 1);
            cellY = Mathf.Clamp(cellY, 0, _height - 1);

            if (TryCoordToIndex(cellX, cellY, out int cellIndex))
            {
                bool newWalkable = _blocked[cellIndex]; // blocked -> make walkable, unblocked -> make blocked
                SetWalkable(cellIndex, newWalkable);
            }

        }


        private void ValidateGridSize()
        {
            if (_width <= 0) throw new ArgumentOutOfRangeException(nameof(_width));
            if (_height <= 0) throw new ArgumentOutOfRangeException(nameof(_height));

            long count = (long)_width * _height;
            if (count > int.MaxValue) 
                throw new OverflowException("Grid too large for int indexing.");
        }


        private void FitCameraOrthoTopDown()
        {
            if (_mainCamera == null) _mainCamera = Camera.main;
            if (_mainCamera == null) return;

            _mainCamera.orthographic = true;

            // Center of the board in world space (Quad centered at its transform)
            Vector3 center = _quadRenderer.transform.position;

            // Place camera above the board (assuming quad is in XY plane facing camera OR rotated to XZ)
            _mainCamera.transform.position = center + new Vector3(0f, 0f, -10f); // if viewing in XY plane
            _mainCamera.transform.rotation = Quaternion.identity;

            // Fit whole board in view (orthographicSize is half of vertical size)
            float halfH = _height * 0.5f;
            float halfW = _width * 0.5f;

            float aspect = _mainCamera.aspect; // width / height
            float sizeToFitHeight = halfH;
            float sizeToFitWidth = halfW / aspect;

            _mainCamera.orthographicSize = Mathf.Max(sizeToFitHeight, sizeToFitWidth) + _cameraPadding;
        }



#if UNITY_EDITOR
        private void OnValidate() => ValidateGridSize();
#endif

    }

}
