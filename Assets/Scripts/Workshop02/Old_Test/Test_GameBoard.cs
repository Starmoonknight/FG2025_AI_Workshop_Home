using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine;


namespace AI_Workshop02_Testing 
{
    public class Test_GameBoard : MonoBehaviour
    {


        [Header("Board Settings")]
        [SerializeField] 
        private int _width = 10;
        [SerializeField] 
        private int _height = 10;
        [SerializeField]
        private float _cellSize = 1f;

        [Header("Prefabs & Materials")]
        [SerializeField] 
        private GameObject _tilePrefab;
        [SerializeField] 
        private Material _walkableMaterial;
        [SerializeField] 
        private Material _wallMaterial;
        [SerializeField]
        private Material _occupiedMaterial;
        [SerializeField]
        private Material _pathMaterial;
        [SerializeField]
        private Material _falsePathMaterial;
        [SerializeField]
        private Material _targetMaterial;
        [SerializeField]
        private Material _startMaterial;

        private Cell[,] _cells;
        private Dictionary<GameObject, Cell> _tileToCell = new();

        public InputAction ClickAction; 

        public int Width => _width;
        public int Height => _height;
        public float CellSize => _cellSize; 


        [Header("Cell State Colors")]



        private bool _isOccupied;

        private enum CellState
        {
            Walkable, 
            Occupied,
            Wall,
            Path,
            FalsePath,
            Target,
            Start
        }




        public class Cell
        {
            private Vector2Int _vCordinates;
            private bool _walkable;


            #region Properties

            public Cell Connection { get; private set; }
            public GameObject Tile { get; private set; }
            public Vector2Int VCordinates => _vCordinates;
            public bool Walkable => _walkable;
            public float G { get; private set; }
            public float H { get; private set; }
            public float F => G + H;

            public void SetG(float g) => G = g;
            public void SetH(float h) => H = h;
            public void SetConnection(Cell parent) => Connection = parent;
            public void SetWalkable(bool walkable) => _walkable = walkable;

            public void ResetSearchData()
            {
                G = float.PositiveInfinity;
                H = 0f;
                Connection = null;
            }

            #endregion


            public Cell(Vector2Int vCoord, bool walkable, GameObject tile)
            {
                this._vCordinates = vCoord;
                this._walkable = walkable;
                this.Tile = tile;

                G = float.PositiveInfinity;
                H = 0;
                Connection = null;
            }

        }



        private void Awake()
        {
            GenerateGrid();
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

        private void GenerateGrid()
        {
            _cells = new Cell[_width, _height];

            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    Vector3 wprldPos = new Vector3(x * _cellSize, 0, y * _cellSize);

                    GameObject tileGO = Instantiate(_tilePrefab, wprldPos, Quaternion.identity, transform);
                    tileGO.name = $"Tile_{x}_{y}";
                    Cell cell = new Cell(new Vector2Int(x, y), true, tileGO);
                    _cells[x, y] = cell;
                    _tileToCell[tileGO] = cell;
                    SetTileMaterial(cell, _walkableMaterial);
                }
            }
        }


        private void OnClickPerformed(InputAction.CallbackContext context)
        {
            HandleMouseClick(); 
        }


        private void HandleMouseClick()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());

            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                GameObject clicked = hit.collider.gameObject;
                if (_tileToCell.TryGetValue(clicked, out Cell cell))
                {
                    bool newWalkable = !cell.Walkable;
                    SetWalkable(cell, newWalkable);
                }
            }
        }

        public Cell GetCell(Vector2Int vector2Int)
        {
            if (vector2Int.x < 0 || vector2Int.x >= _width || vector2Int.y < 0 || vector2Int.y >= _height)
                return null;

            return _cells[vector2Int.x, vector2Int.y];
        }


        public Cell GetNodeFromWorldPosition(Vector3 worldPos)
        {
            int x = Mathf.RoundToInt(worldPos.x / _cellSize);
            int y = Mathf.RoundToInt(worldPos.z / _cellSize);
            return GetCell(new Vector2Int(x, y));
        }


        public IEnumerable<Cell> GetNeighbours(Cell cell, bool allowDiagonals = false)
        {
            int x = cell.VCordinates.x;
            int y = cell.VCordinates.y;

            yield return GetCell(new Vector2Int(x + 1, y)); // Right
            yield return GetCell(new Vector2Int(x - 1, y)); // Left
            yield return GetCell(new Vector2Int(x, y + 1)); // Up
            yield return GetCell(new Vector2Int(x, y - 1)); // Down

            if (allowDiagonals)
            {
                yield return GetCell(new Vector2Int(x + 1, y + 1)); // Top-Right
                yield return GetCell(new Vector2Int(x - 1, y + 1)); // Top-Left
                yield return GetCell(new Vector2Int(x + 1, y - 1)); // Bottom-Right
                yield return GetCell(new Vector2Int(x - 1, y - 1)); // Bottom-Left
            }
        }


        public void SetWalkable(Cell cell, bool walkable)
        {
            if (cell == null) return;

            cell.SetWalkable(walkable);
            SetTileMaterial(cell, walkable ? _walkableMaterial : _wallMaterial);
        }

        private void SetTileMaterial(Cell cell, Material material)
        {
            Renderer renderer = cell.Tile.GetComponent<Renderer>();
            if (renderer != null && material != null)
            {
                renderer.material = material;
            }
        }



    }

}
