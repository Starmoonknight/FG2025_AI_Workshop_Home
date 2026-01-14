using System;
using UnityEngine;


namespace AI_Workshop03
{
    // MapManager.Data.cs               -   Partial class to hold map data related methods
    public partial class MapManager
    {



        #region Properties - Grid Data Fields

        /* SoA or AoS choice:
        * NOTE: In the task it explicitly mentions a Node[,] array.
        * But I represent nodes as per-cell data in 1D arrays indices in arrays rather than Node[,]
        * and then use the cells coordinates as its index value to match between all arrays 
        * I had a feeling it would be faster to be able to just access the parts of data that I needed at any time
        */



        // Grid Data Fields
        private int _cellCount;             // total number of cells in the grid (_width * _height)
        private bool[] _blocked;            // walkability of each cell
        private byte[] _terrainKind;        // TerrainDataType of each cell
        private int[] _terrainCost;         // movement cost of each cell, quick lookup for A* pathfinding
        private int _minTerrainCost = 10;   // minimum terrain cost on the map (used for pathfinding heuristic)
        private Color32[] _baseCellColors;  // base color of each cell (before any paint layers)
        private byte[] _lastPaintLayerId;   // what TerrainData layer last painted this cell, for quick lookup.

        //private ubite[] _TerrainKey; permaner lookup id that would survive over multiple map generations while lastPaintLayerId is per-generation
        //private bool[] _protected;  //look into storing as bitArray       // if I in the future want the rng ExpandRandom methods to ignore certain tiles, (start/goal, maybe a border ring) that must never be selected:   if (_protected != null && _protected[i]) continue;

        #endregion


        #region Coordinates conversion

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


        #region Cell Getters

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


        #endregion


        #region Cell Setters


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

            if (shadeLikeGrid)
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


        #endregion


    }

}