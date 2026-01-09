using AI_Workshop02;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;



// NOTE:
// This was a functional script that would have worked perfectly with minor terrain generation tweeks but
// would always require new additions to be hard coded into the script.
// It was growing leangthy and less precise in its purpose So I wanted to divide up the new version more clearly.  

public class LEGACY_BoardManager : MonoBehaviour
{

    [Header("Game Camera Settings")]
    [SerializeField]
    private Camera _mainCamera;
    [SerializeField]
    private float _cameraPadding = 1f;

    [Header("Board Settings")]
    [SerializeField]
    private Renderer _quadRenderer;
    [SerializeField, Min(1)]
    private int _width = 10;
    [SerializeField, Min(1)]
    private int _height = 10;

    [Header("Map Generation Settings")]
    [SerializeField]
    private int _seed;
    [SerializeField, Range(0f, 1f)]
    private float _obstaclePercent = 0.2f;
    [SerializeField, Range(0f, 1f)]
    private float _roadPercent = 0.08f;
    [SerializeField, Range(0f, 1f)]
    private float _mudPercent = 0.12f;
    [SerializeField, Range(0f, 1f)]
    private float _minReachablePercent = 0.75f;
    [SerializeField]
    private int _maxGenerateAttempts = 50;

    private System.Random _genRng;
    private System.Random _goalRng;

    [Header("Generation Rules")]
    [SerializeField] private AI_Workshop02.TerrainData[] _rules;

    private byte[] _terrainId;

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
    private int[] _bfsQueue;
    private int[] _reachStamp;
    private int _reachStampId;

    // Generation scratch 
    private int[] _genQueue;
    private int[] _genStamp;
    private int _genStampId;
    private readonly List<int> _genCells = new(4096);


    // Grid Data
    private int _cellCount;
    private bool[] _protected;         // if I in the future want the rng ExpandRandom methods to ignore certain tiles, (start/goal, maybe a border ring) that must never be selected:   if (_protected != null && _protected[i]) continue;
    private bool[] _walkable;
    private byte[] _terrainCost;
    private byte _minTerrainCost = 10;
    private byte _roadCost = 7;
    private byte _mudCost = 18;

    // Grid Visualization
    private Color32[] _baseCellColors;
    private Color32[] _cellColors;
    private Texture2D _gridTexture;
    private bool _textureDirty;

    [Header("Colors")]
    [SerializeField]
    private Color32 _walkableColor = new(255, 255, 255, 255);       // White
    [SerializeField]
    private Color32 _walkableRoadColor = new(217, 221, 130, 255);   // Dirty Light Yellow
    [SerializeField]
    private Color32 _walkableSwampColor = new(66, 120, 70, 255);    // Dark Green
    [SerializeField]
    private Color32 _obstacleColor = new(0, 0, 0, 255);             // Black
    [SerializeField]
    private Color32 _unReachableColor = new(255, 150, 150, 255);    // Light Red


    public InputAction ClickAction;

    public int Width => _width;
    public int Height => _height;
    public int CellCount => _cellCount;
    public byte MinTerrainCost => _minTerrainCost;


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
        return _walkable[index];
    }

    // check terrain cost, used for core loops, eg. pathfinding 
    public byte GetTerrainCost(int index)
    {
        if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
        return _terrainCost[index];
    }


    public int BuildReachableFrom(int startIndex)
    {
        EnsureReachBuffers();
        if (!IsValidCell(startIndex) || !_walkable[startIndex])
            return 0;

        // Prevent stamp id overflow, rare but possible
        if (_reachStampId == int.MaxValue)
        {
            Array.Clear(_reachStamp, 0, _reachStamp.Length);
            _reachStampId = 0; // so next ++ becomes 1
        }

        _reachStampId++;

        int head = 0;
        int tail = 0;

        _bfsQueue[tail++] = startIndex;
        _reachStamp[startIndex] = _reachStampId;

        int reachableCount = 1;

        while (head < tail)
        {
            int currentIndex = _bfsQueue[head++];
            IndexToXY(currentIndex, out int coordX, out int coordY);

            foreach (var (dirX, dirY, _) in Neighbors8)
            {
                if (dirX != 0 && dirY != 0)  // need to change to match A* 
                {
                    // Diagonal movement allowed only if at least one side is open
                    bool sideAOpen = TryCoordToIndex(coordX + dirX, coordY, out int sideIndexA) && _walkable[sideIndexA];
                    bool sideBOpen = TryCoordToIndex(coordX, coordY + dirY, out int sideIndexB) && _walkable[sideIndexB];

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
            if (!_walkable[ni]) return;
            if (_reachStamp[ni] == _reachStampId) return;

            _reachStamp[ni] = _reachStampId;
            _bfsQueue[tail++] = ni;
            reachableCount++;
        }

    }


    public void BuildVisualReachableFrom(int startIndex)
    {
        int reachableCount = BuildReachableFrom(startIndex);

        RebuildCellColorsFromBase();

        for (int i = 0; i < _cellCount; i++)
        {
            if (!_walkable[i]) continue;

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


    // If I want the goal to be far-ish away, can also pick minManhattan as something like (_width + _height) / 4. 
    // This ensures the goal is at least a quarter of the board’s perimeter away from the start.
    public bool TryPickRandomReachableGoal(int startIndex, int minManhattan, out int goalIndex)
    {
        goalIndex = -1;

        int reachableCount = BuildReachableFrom(startIndex);
        if (reachableCount <= 1) return false;

        IndexToXY(startIndex, out int startX, out int startY);

        int candidateCount = 0;

        for (int i = 0; i < _cellCount; i++)
        {
            if (!_walkable[i]) continue;                    // skip unwalkable cells
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

    public void SetWalkable(int index, bool walkable)
    {
        if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));

        _walkable[index] = walkable;
        _baseCellColors[index] = walkable
            ? _walkableColor
            : _obstacleColor;

        if (walkable)
            _terrainCost[index] = 10;

        IndexToXY(index, out int coordX, out int coordY);
        bool odd = ((coordX + coordY) & 1) == 1;
        _cellColors[index] = ApplyGridShading(_baseCellColors[index], odd);

        _textureDirty = true;
    }

    public void SetTerrainCost(int index, byte terrainCost)
    {
        if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
        _terrainCost[index] = terrainCost;
    }

    public void SetCellData(int index, bool walkable, byte terrainCost)
    {
        if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
        _walkable[index] = walkable;
        _terrainCost[index] = terrainCost;
    }

    public void PaintCell(int index, Color32 color, bool shadeLikeGrid = true, bool skipIfObstacle = true)
    {
        if (!IsValidCell(index)) throw new ArgumentOutOfRangeException(nameof(index));
        if (skipIfObstacle && !_walkable[index]) return;

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
        label.text = $"<size=60%>{g / 10f:0.0} {h / 10f:0.0}</size>\n<size=120%><b>{f / 10f:0.0}</b></size>";
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

        _walkable = new bool[_cellCount];
        _terrainCost = new byte[_cellCount];
        _baseCellColors = new Color32[_cellCount];
        _cellColors = new Color32[_cellCount];
        _terrainId = new byte[_cellCount];

        int seed = (_seed != 0) ? _seed : Environment.TickCount;
        _genRng = new System.Random(seed);
        _goalRng = new System.Random(seed ^ unchecked((int)0x9E3779B9));

        // Initialize all cells as walkable with default terrain cost
        for (int i = 0; i < _cellCount; i++)
        {
            _walkable[i] = true;
            _terrainCost[i] = 10;
            _baseCellColors[i] = _walkableColor;
        }

        GenerateSeededObstaclesUntilAcceptable();   // modifies _walkable by introducing obstacles and updates _baseCellColors to match
        GenerateTerrainCosts();
        RecomputeMinTerrainCost();                  // after terrain costs are set recompute minimum

        _gridTexture = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
        _gridTexture.filterMode = FilterMode.Point;
        _gridTexture.wrapMode = TextureWrapMode.Clamp;

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
        byte minCost = byte.MaxValue;

        for (int i = 0; i < _cellCount; i++)
        {
            if (!_walkable[i]) continue;

            if (_terrainCost[i] < minCost)
                minCost = _terrainCost[i];
        }

        if (minCost == byte.MaxValue) minCost = 10; // default if no walkable cells
        if (minCost < 1) minCost = 1;  // avoid zero cost

        _minTerrainCost = minCost;
    }

    private void GenerateTerrainCosts()
    {
        for (int i = 0; i < _cellCount; i++)
        {
            if (!_walkable[i])
                continue;

            _terrainCost[i] = 10;
            _baseCellColors[i] = _walkableColor;
        }

        // swamp tiles should bloom as blobs on the map
        int desiredMudCells = Mathf.RoundToInt(_mudPercent * _cellCount);
        int avgBlobSize = 350;                 // tune: bigger => fewer bigger blobs
        int blobCount = Mathf.Max(1, desiredMudCells / Mathf.Max(1, avgBlobSize));

        for (int b = 0; b < blobCount; b++)
        {
            if (!TryPickRandomWalkable(out int seed, 256)) break;

            int size = Mathf.Max(30, avgBlobSize + _genRng.Next(-120, 120));
            ExpandRandomBlob(_genRng, seed, size, growChance: 0.55f, smoothPasses: 1, outCells: _genCells);
            ApplyTerrain(_genCells, _mudCost, _walkableSwampColor);
        }

        // roads should spread like tendrils from edges
        int desiredRoadCells = Mathf.RoundToInt(_roadPercent * _cellCount);
        int roadCount = Mathf.Clamp(desiredRoadCells / 250, 1, 12);

        for (int r = 0; r < roadCount; r++)
        {
            if (!TryPickRandomEdgeWalkable(out int start, 256)) break;
            if (!TryPickRandomEdgeWalkable(out int goal, 256)) break;

            int maxSteps = _width + _height;   // good starting point
            ExpandRandomLichtenberg(_genRng, start, goal, maxSteps, maxWalkers: 6, towardTargetBias: 0.78f, branchChance: 0.06f, outCells: _genCells);

            WidenOnce(_genCells);              // comment out if you want thin roads
            ApplyTerrain(_genCells, _roadCost, _walkableRoadColor);
        }
    }

    private void ApplyTerrain(List<int> cells, byte terrainCost, Color32 color)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            int idx = cells[i];

            //if (idx == protectedIndex) continue;
            if (!_walkable[idx]) continue;

            _terrainCost[idx] = terrainCost;
            _baseCellColors[idx] = color;
        }
    }

    private int ApplyObstacles(List<int> cells, int protectedIndex = -1)
    {
        int applied = 0;

        for (int i = 0; i < cells.Count; i++)
        {
            int idx = cells[i];

            //if (idx == protectedIndex) continue;
            if (!_walkable[idx]) continue;          // avoids double counting if multi-pass

            _walkable[idx] = false;
            _terrainCost[idx] = 0;
            _baseCellColors[idx] = _obstacleColor;
            applied++;
        }

        return applied;
    }

    private void GenerateSeededObstaclesUntilAcceptable()
    {
        EnsureReachBuffers();

        int startIndex = CoordToIndex(_width / 2, _height / 2);

        for (int attempt = 0; attempt < _maxGenerateAttempts; attempt++)
        {
            int walkableCount = GenerateSeededObstaclesWithAttempt(attempt, startIndex);

            if (!_walkable[startIndex])     // force starting cell to be walkable
            {
                _walkable[startIndex] = true;
                _baseCellColors[startIndex] = _walkableColor;
                walkableCount++;            // if it was an obstacle before fix the count
            }

            int reachableCount = BuildReachableFrom(startIndex);

            float reachablePercent = walkableCount == 0
                ? 0f
                : (reachableCount / (float)walkableCount);

            if (reachablePercent >= _minReachablePercent)
            {
                return;
            }
        }

        Debug.LogWarning("Failed to generate acceptable obstacle layout within max attempts.");
    }

    private int GenerateSeededObstaclesWithAttempt(int attempt, int startIndex)
    {
        var attemptRng = (_seed != 0)
            ? new System.Random(_seed + attempt)
            : _genRng;

        for (int i = 0; i < _cellCount; i++)
        {
            _walkable[i] = true;
            _terrainCost[i] = 10;
            _baseCellColors[i] = _walkableColor;
        }

        // choose expansion method for electing obstacle cells
        ExpandRandomStatic(attemptRng, _obstaclePercent, _genCells, requireWalkable: false);

        int obstaclesApplied = ApplyObstacles(_genCells, protectedIndex: startIndex);

        int walkableCount = _cellCount - obstaclesApplied;
        return walkableCount;
    }

    private bool TryPickRandomWalkable(out int index, int tries)
    {
        index = -1;
        for (int t = 0; t < tries; t++)
        {
            int i = _genRng.Next(0, _cellCount);
            if (_walkable[i]) { index = i; return true; }
        }
        return false;
    }

    private bool TryPickRandomEdgeWalkable(out int index, int tries)
    {
        index = -1;
        for (int t = 0; t < tries; t++)
        {
            int side = _genRng.Next(0, 4);
            int coordX;
            int coordY;

            switch (side)
            {
                case 0: coordX = 0; coordY = _genRng.Next(0, _height); break;             // left
                case 1: coordX = _width - 1; coordY = _genRng.Next(0, _height); break;    // right
                case 2: coordX = _genRng.Next(0, _width); coordY = 0; break;              // bottom
                default: coordX = _genRng.Next(0, _width); coordY = _height - 1; break;   // top
            }

            int i = CoordToIndex(coordX, coordY);
            if (_walkable[i]) { index = i; return true; }
        }
        return false;
    }


    private void ExpandRandomStatic(System.Random rng, float chance, List<int> outCells, bool requireWalkable = true)
    {
        outCells.Clear();
        chance = Mathf.Clamp01(chance);

        for (int i = 0; i < _cellCount; i++)
        {
            if (requireWalkable && !_walkable[i]) continue;
            if (rng.NextDouble() <= chance)
                outCells.Add(i);
        }
    }

    private void ExpandRandomBlob(
        System.Random rng,
        int seedIndex,
        int maxCells,
        float growChance,
        int smoothPasses,
        List<int> outCells,
        bool requireWalkable = true)
    {

        outCells.Clear();
        if (!IsValidCell(seedIndex)) return;
        if (requireWalkable && !_walkable[seedIndex]) return;

        EnsureGenBuffers();
        int stampId = NextGenStampId();

        int head = 0;
        int tail = 0;

        _genStamp[seedIndex] = stampId;
        _genQueue[tail++] = seedIndex;
        outCells.Add(seedIndex);

        growChance = Mathf.Clamp01(growChance);
        maxCells = Mathf.Max(1, maxCells);

        // first pass: BFS-like expansion
        while (head < tail && outCells.Count < maxCells)
        {
            int currentIndex = _genQueue[head++];
            IndexToXY(currentIndex, out int coordX, out int coordY);

            for (int neighbor = 0; neighbor < Neighbors4.Length; neighbor++)
            {
                var (dirX, dirY) = Neighbors4[neighbor];
                if (!TryCoordToIndex(coordX + dirX, coordY + dirY, out int next)) continue;
                if (_genStamp[next] == stampId) continue;
                if (requireWalkable && !_walkable[next]) continue;
                if (rng.NextDouble() > growChance) continue;

                _genStamp[next] = stampId;
                _genQueue[tail++] = next;
                outCells.Add(next);

                if (outCells.Count >= maxCells) break;
            }
        }

        // smoothing passes to fill in small gaps
        for (int pass = 0; pass < smoothPasses; pass++)
        {
            int before = outCells.Count;
            for (int i = 0; i < before; i++) _genQueue[i] = outCells[i]; // copy to queue as a temp list to avoid alocation

            for (int i = 0; i < before && outCells.Count < maxCells; i++)
            {
                int currentIndex = _genQueue[i];
                IndexToXY(currentIndex, out int coordX, out int coordY);

                for (int neighbor = 0; neighbor < Neighbors4.Length && outCells.Count < maxCells; neighbor++)
                {
                    var (dirX, dirY) = Neighbors4[neighbor];
                    if (!TryCoordToIndex(coordX + dirX, coordY + dirY, out int next)) continue;
                    if (_genStamp[next] == stampId) continue;
                    if (requireWalkable && !_walkable[next]) continue;

                    _genStamp[next] = stampId;
                    outCells.Add(next);
                }
            }
        }
    }


    private void ExpandRandomLichtenberg(
        System.Random rng,
        int startIndex,
        int targetIndex,
        int maxSteps,
        int maxWalkers,
        float towardTargetBias,
        float branchChance,
        List<int> outCells,
        bool requireWalkable = true)
    {
        outCells.Clear();
        if (!IsValidCell(startIndex) || (requireWalkable && !_walkable[startIndex])) return;
        if (!IsValidCell(targetIndex) || (requireWalkable && !_walkable[targetIndex])) return;

        EnsureGenBuffers();
        int stampId = NextGenStampId();

        towardTargetBias = Mathf.Clamp01(towardTargetBias);
        branchChance = Mathf.Clamp01(branchChance);
        maxSteps = Mathf.Max(1, maxSteps);
        maxWalkers = Mathf.Clamp(maxWalkers, 1, 64);

        int walkerCount = 1;
        _genQueue[0] = startIndex;

        _genStamp[startIndex] = stampId;
        outCells.Add(startIndex);

        IndexToXY(targetIndex, out int targetX, out int targetY);

        for (int step = 0; step < maxSteps; step++)
        {
            // round-robin walkers
            int walkerThisStep = step % walkerCount;
            int currentIndex = _genQueue[walkerThisStep];

            if (currentIndex == targetIndex) break; // reached target

            IndexToXY(currentIndex, out int coordX, out int coordY);

            int stepX = Math.Sign(targetX - coordX);
            int stepY = Math.Sign(targetY - coordY);

            // Code memo to remember new words;
            // Span is a stack only ref struct
            // Stackalloc allocates a block of memory on the stack, not the heap. It’s extremely fast and automatically freed when the method scope ends (no GC, no pooling).                
            Span<(int dirX, int dirY)> candidates = stackalloc (int, int)[8];   // candidate directions
            int cCount = 0;

            bool hasX = stepX != 0;
            bool hasY = stepY != 0;

            if (hasX) candidates[cCount++] = (stepX, 0);
            if (hasY) candidates[cCount++] = (0, stepY);

            // perpendiculars
            if (hasX) { candidates[cCount++] = (stepX, 1); candidates[cCount++] = (stepX, -1); }
            if (hasY) { candidates[cCount++] = (1, stepY); candidates[cCount++] = (-1, stepY); }

            // opposites (fallback)
            if (hasX) candidates[cCount++] = (-stepX, 0);
            if (hasY) candidates[cCount++] = (0, -stepY);

            int nextIndex = -1;

            // bias towards early candidates
            for (int c = 0; c < cCount; c++)
            {
                var (dirX, dirY) = candidates[c];

                if (!TryCoordToIndex(coordX + dirX, coordY + dirY, out int cand)) continue;
                if (requireWalkable && !_walkable[cand]) continue;

                bool prefer = (c < 2);      // prefere the two most toward target
                double roll = rng.NextDouble();

                if (prefer)
                {
                    if (roll <= towardTargetBias) { nextIndex = cand; break; }
                }
                else
                {
                    // allow deviations sometimes
                    if (roll > towardTargetBias) { nextIndex = cand; break; }
                }

                // allow mild tie-breaker by grabbing un-used cell
                if (nextIndex < 0 && _genStamp[cand] != stampId)
                    nextIndex = cand;

            }

            if (nextIndex < 0) break;

            _genQueue[walkerThisStep] = nextIndex;

            if (_genStamp[nextIndex] != stampId)
            {
                _genStamp[nextIndex] = stampId;
                outCells.Add(nextIndex);
            }

            // branch into new walker from current position sometimes
            if (walkerCount < maxWalkers && rng.NextDouble() < branchChance)
            {
                _genQueue[walkerCount++] = nextIndex;
            }
        }
    }


    private void WidenOnce(List<int> cells)
    {
        EnsureGenBuffers();
        int stampId = NextGenStampId();

        // mark existing
        for (int i = 0; i < cells.Count; i++)
            _genStamp[cells[i]] = stampId;

        int originalCount = cells.Count;

        for (int i = 0; i < originalCount; i++)
        {
            int index = cells[i];
            IndexToXY(index, out int x, out int y);

            for (int neighbor = 0; neighbor < Neighbors4.Length; neighbor++)
            {
                var (dirX, dirY) = Neighbors4[neighbor];
                if (!TryCoordToIndex(x + dirX, y + dirY, out int neighbourIndex)) continue;
                if (_genStamp[neighbourIndex] == stampId) continue;
                if (!_walkable[neighbourIndex]) continue;

                _genStamp[neighbourIndex] = stampId;
                cells.Add(neighbourIndex);
            }
        }
    }



    private int NextGenStampId()
    {
        _genStampId++;
        if (_genStampId == int.MaxValue)
        {
            Array.Clear(_genStamp, 0, _genStamp.Length);
            _genStampId = 1;
        }
        return _genStampId;
    }

    private void EnsureGenBuffers()
    {
        if (_genQueue == null || _genQueue.Length != _cellCount)
            _genQueue = new int[_cellCount];

        if (_genStamp == null || _genStamp.Length != _cellCount)
            _genStamp = new int[_cellCount];
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
            bool newWalkable = !_walkable[cellIndex];
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
