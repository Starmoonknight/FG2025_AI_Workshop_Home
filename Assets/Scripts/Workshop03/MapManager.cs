using System;
using System.Collections.Generic;
using UnityEngine;


namespace AI_Workshop03
{

    // Version2 of BoardManager    -> MapManager
    public class MapManager : MonoBehaviour
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

        private Color32[] _texturePixels;

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

        [Header("Debug: Seed HUD")]
        [SerializeField] private bool _showSeedHud = true;
        [SerializeField] private TMPro.TextMeshProUGUI _seedHudLabel;
        [SerializeField] private string _seedHudPrefix = "Seed: ";

        [Header("Debug: Generation")]
        [SerializeField] private bool _dumpFocusWeights = true; 
        [SerializeField] private bool _dumpFocusWeightsVerbose = false;

        /*
        [Header("Debug: Generation Attempts")]
        [SerializeField] private bool _logGenAttempts = true;
        [SerializeField] private bool _logGenAttemptFailures = true;
        [SerializeField] private bool _logPerTerrainSummary = true;
        [SerializeField] private bool _logObstacleBudgetHits = true;
        */

        [Header("Debug: A* Costs Overlay")]
        [SerializeField] private bool _showDebugCosts = true;
        [SerializeField] private TMPro.TextMeshPro _costLabelPrefab;
        [SerializeField] private Transform _costLabelRoot;
        [SerializeField] private float _costLabelOffsetY = 0.05f;

        private TMPro.TextMeshPro[] _costLabels;
        private readonly List<int> _costLabelsTouched = new();

        // Accessability Data
        private int[] _bfsQueue;
        private int[] _reachStamp;
        private int _reachStampId;


        // NOTE: In the task it explicitly mentions a Node[,] array.
        // But I represent nodes as per-cell data in 1D arrays indices in arrays rather than Node[,]
        // and then use the cells coordinates as its index value to match between all arrays 
        // I had a feeling it would be faster to be able to just access the parts of data that I needed at any time

        // Grid Data
        private int _cellCount;
        private bool[] _protected;  //look into storing as bitArray       // if I in the future want the rng ExpandRandom methods to ignore certain tiles, (start/goal, maybe a border ring) that must never be selected:   if (_protected != null && _protected[i]) continue;
        private bool[] _blocked;
        private byte[] _terrainKind;
        private int[] _terrainCost;
        private int _minTerrainCost = 10;

        // Grid Visualization
        private Color32[] _baseCellColors;
        private Color32[] _cellColors;
        private Texture2D _gridTexture;
        private bool _textureDirty;
        private byte[] _lastPaintLayerId;

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


        #region Public API


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

            int baseSeed  = (_seed != 0) ? _seed : Environment.TickCount;   // if seed is 0 use random seed
            int genSeed   = baseSeed;                               // main generation randomness seed
            int orderSeed = baseSeed ^ unchecked((int)0x73856093);  // terrain paint ordering randomness (rarity shuffle), salted seed
            int goalSeed  = baseSeed ^ unchecked((int)0x9E3779B9);  // goal picking randomness, salted seed

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

        public Vector3 IndexToWorldCenterXZ(int index, float yOffset = 0f)
        {
            IndexToXY(index, out int x, out int z);
            return new Vector3(x + 0.5f, yOffset, z + 0.5f);
        }


        #endregion



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



        #region Cell Data Setter

        // need to look into if this one is stillusefull or should be changed...
        public void SetWalkableStatus(int index, bool isWalkable = true)
        {
            if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));

            _blocked[index] = !isWalkable;              // blocked is the inverse of walkable, so need to register the opposit here to follow th name logic

            if (isWalkable)
            {
                _lastPaintLayerId[index] = 0;
                _terrainKind[index] = (byte)TerrainID.Land;
                _terrainCost[index] = 10;
                _baseCellColors[index] = _walkableColor;
            }
            else
            {
                _lastPaintLayerId[index] = 0;
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

        public void PaintMultipleCells(ReadOnlySpan<int> indices, Color32 color, bool shadeLikeGrid = true, bool skipIfObstacle = true)
        {
            for (int i = 0; i < indices.Length; i++)
                PaintCell(indices[i], color, shadeLikeGrid, skipIfObstacle);
        }

        public void PaintCellTint(int index, Color32 overlayColor, float strength01 = 0.35f, bool shadeLikeGrid = true, bool skipIfObstacle = true)
        {
            if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
            if (skipIfObstacle && _blocked[index]) return; 

            strength01 = Mathf.Clamp01(strength01); 

            Color32 basecolor = _cellColors[index];
            Color32 overlay = overlayColor;

            if(shadeLikeGrid)
            {
                IndexToXY(index, out int x, out int y);
                bool odd = ((x + y) & 1) == 1; 
                overlay = ApplyGridShading(overlayColor, odd);
            }

            _cellColors[index] = LerpColor32(basecolor, overlay, strength01); 
            _textureDirty = true;      
        }

        public void PaintMultipleCellTints(ReadOnlySpan<int> indices, Color32 overlayColor, float strength01 = 0.35f, bool shadeLikeGrid = true, bool skipIfObstacle = true)
        {
            for (int i = 0; i < indices.Length; i++)
                PaintCellTint(indices[i], overlayColor, strength01, shadeLikeGrid, skipIfObstacle);
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

            label.transform.position = IndexToWorldCenterXZ(index, _costLabelOffsetY);

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



        #region Internal Helpers
        
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
            if (_gridTexture == null) return;

            // fast path if visuals don't need to be fliped 
            if (!_flipTextureX && !_flipTextureY)
            {
                _gridTexture.SetPixels32(_cellColors);
                _gridTexture.Apply(false);
                return;
            }

            // when switching from XY Quad to a XZ Plane visuals flipped. The Plane’s UV orientation may not match the grid row/column order, fix is below.
            // Use DebugCornerColorTest to see what _flipTextureX/Y needs to be on.

            if (_texturePixels == null || _texturePixels.Length != _cellCount)
                _texturePixels = new Color32[_cellCount];

            for (int y = 0; y < _height; y++)
            {
                int srcRowBase = y * _width;

                int dstRowY = _flipTextureY ? (_height -1 -y) : y;
                int dstRowBase = dstRowY * _width;

                if (!_flipTextureX)
                {
                    // if only Y needed to be flipped 
                    Array.Copy(_cellColors, srcRowBase, _texturePixels, dstRowBase, _width);
                }
                else
                {
                    for (int x = 0; x < _width; x++)
                    {
                        int srcIndex = srcRowBase + x;

                        int dstX =  _width -1 -x;
                        int dstIndex = dstRowBase + dstX;
                        
                        _texturePixels[dstIndex] = _cellColors[srcIndex];
                    }
                }
            }

            _gridTexture.SetPixels32(_texturePixels);
            _gridTexture.Apply(false); 
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

        private static Color32 LerpColor32(Color32 a, Color32 b, float t)
        {
            t = Mathf.Clamp01(t);
            int ti = Mathf.RoundToInt(t * 255f);
            int inv = 255 - ti;

            byte r = (byte)((a.r * inv + b.r * ti + 127) / 255);
            byte g = (byte)((a.g * inv + b.g * ti + 127) / 255);
            byte bl = (byte)((a.b * inv + b.b * ti + 127) / 255);

            // should keep fully opaque for an opaque material
            return new Color32(r, g, bl, 255);
        }



        private void ValidateGridSize()
        {
            if (_width <= 0) throw new ArgumentOutOfRangeException(nameof(_width));
            if (_height <= 0) throw new ArgumentOutOfRangeException(nameof(_height));

            long count = (long)_width * _height;
            if (count > int.MaxValue)
                throw new OverflowException("Grid too large for int indexing.");
        }

        private void UpdateSeedHud()
        {
            if (!_showSeedHud) return;
            if (_seedHudLabel == null) return;

            string mode = (_seed == 0) ? " (random)" : "";
            _seedHudLabel.text = $"{_seedHudPrefix}{_lastGeneratedSeed}{mode}";
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





#if UNITY_EDITOR
        private void OnValidate() => ValidateGridSize();

        [ContextMenu("Debug/Corner Color Test")]
        private void DebugCornerColorTest()
        {
            if (_cellCount <= 0) return;

            // Paint corners WITHOUT checker shading and WITHOUT skipping obstacles
            PaintCell(CoordToIndex(0, 0), new Color32(255, 0, 0, 255), shadeLikeGrid: false, skipIfObstacle: false);                            // (0,0) red
            PaintCell(CoordToIndex(_width - 1, 0), new Color32(0, 255, 0, 255), shadeLikeGrid: false, skipIfObstacle: false);                   // (w-1,0) green
            PaintCell(CoordToIndex(0, _height - 1), new Color32(0, 0, 255, 255), shadeLikeGrid: false, skipIfObstacle: false);                  // (0,h-1) blue
            PaintCell(CoordToIndex(_width - 1, _height - 1), new Color32(255, 255, 255, 255), shadeLikeGrid: false, skipIfObstacle: false);     // (w-1,h-1) white

            _textureDirty = true;
            FlushTexture();
        }


        [ContextMenu("Seed/Copy LastGeneratedSeed -> Seed")]
        private void CopyLastSeedToSeed()
        {
            _seed = _lastGeneratedSeed;
            Debug.Log($"[MapManager] Copied last seed {_lastGeneratedSeed} into _seed.");
        }


        [ContextMenu("Seed/Copy LastGeneratedSeed -> Clipboard")]
        private void CopyLastSeedToClipboard()
        {
            UnityEditor.EditorGUIUtility.systemCopyBuffer = _lastGeneratedSeed.ToString();
            Debug.Log($"[MapManager] Copied seed {_lastGeneratedSeed} to clipboard.");
        }

#endif

    }

}
