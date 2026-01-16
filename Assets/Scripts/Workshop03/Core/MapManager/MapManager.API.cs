using System;
using UnityEngine;


namespace AI_Workshop03
{
    // MapManager.API.cs               -   Purpose: public API for cell data access + paint API
    public partial class MapManager
    {

        // Grid Data Fields
        private int _minTerrainCost = 10;   // minimum terrain cost on the map (used for pathfinding heuristic)



        #region Cell Getters

        // check if cell is walkable, used for core loops, eg. pathfinding 
        public bool GetWalkable(int index)
        {
            if (_data == null) throw new InvalidOperationException("Map not generated yet.");

            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));
            return !_data.IsBlocked[index];
        }

        // check terrain cost, used for core loops, eg. pathfinding 
        public int GetTerrainCost(int index)
        {
            if (_data == null) throw new InvalidOperationException("Map not generated yet.");

            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));
            return _data.TerrainCosts[index];
        }


        #endregion



        #region Cell Setters

        // need to look into if this one is stillusefull or should be changed...
        public void SetWalkableStatus(int index, bool isWalkable = true)
        {
            if (_data == null) throw new InvalidOperationException("Map not generated yet.");


            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));

            _data.IsBlocked[index] = !isWalkable;         // blocked is the inverse of walkable, so need to register the opposit here to follow th name logic

            _data.LastPaintLayerIds[index] = 0;
            _data.TerrainTypeIds[index] = (byte)TerrainID.Land;

            if (isWalkable)
            {
                _data.TerrainCosts[index] = _baseTerrainCost;
                _data.BaseCellColors[index] = _walkableColor;
            }
            else
            {
                _data.TerrainCosts[index] = 0;
                _data.BaseCellColors[index] = _obstacleColor;
            }

            // Update visuals after truth changes
            _renderer2D?.MarkCellTruthChanged(index);
        }

        public void SetTerrainCost(int index, int terrainCost)
        {
            if (_data == null) throw new InvalidOperationException("Map not generated yet.");


            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));

            _data.TerrainCosts[index] = _data.IsBlocked[index] ? 0 : terrainCost;

            // Later if this affects visuals somhow: _renderer2D?.ResetColorsToBase();
        }

        public void SetBlockedAndCost(int index, bool blocked, int terrainCost)
        {
            if (_data == null) throw new InvalidOperationException("Map not generated yet.");


            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));

            _data.IsBlocked[index] = blocked;
            _data.TerrainCosts[index] = blocked ? 0 : terrainCost;
        }

        public void SetBaseColor(int index, Color32 color)
        {
            if (_data == null) throw new InvalidOperationException("Map not generated yet.");


            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));
            _data.BaseCellColors[index] = color;

            _renderer2D?.MarkCellTruthChanged(index);
        }


        public void SetCellData(
            int index,
            bool blocked,
            byte terrainKey,
            int terrainCost,
            Color32 baseColor,
            byte paintLayerId = 0
        )
        {
            if (_data == null) throw new InvalidOperationException("Map not generated yet.");


            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));

            _data.IsBlocked[index] = blocked;
            _data.TerrainTypeIds[index] = terrainKey;
            _data.TerrainCosts[index] = blocked ? 0 : terrainCost;
            _data.BaseCellColors[index] = baseColor;
            _data.LastPaintLayerIds[index] = paintLayerId;

            _renderer2D?.MarkCellTruthChanged(index); // change visuals to match new terrain 
        }


        #endregion


    }

}