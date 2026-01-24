using System;
using UnityEngine;


namespace AI_Workshop03
{
    // MapManager.API.cs               -   Purpose: public API for cell data access + paint API
    public partial class MapManager
    {


        public readonly struct CellEdit
        {
            public readonly bool? Blocked;
            public readonly byte? TerrainKey;
            public readonly int? TerrainCost;
            public readonly Color32? BaseColor;
            public readonly byte? PaintLayerId;

            public CellEdit(bool? blocked = null, byte? terrainKey = null, int? terrainCost = null,
                            Color32? baseColor = null, byte? paintLayerId = null)
            {
                Blocked = blocked;
                TerrainKey = terrainKey;
                TerrainCost = terrainCost;
                BaseColor = baseColor;
                PaintLayerId = paintLayerId;
            }
        }


        #region Cell Setters

        // partial edit of data 
        public void ApplyCellEdit(int index, in CellEdit edit, bool updateVisuals = true)
        {
            if (_data == null) throw new InvalidOperationException("Map not generated yet.");
            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));

            if (edit.Blocked.HasValue)
                _data.IsBlocked[index] = edit.Blocked.Value;

            if (edit.TerrainKey.HasValue)
                _data.TerrainTypeIds[index] = edit.TerrainKey.Value;

            if (edit.TerrainCost.HasValue)
                _data.TerrainCosts[index] = _data.IsBlocked[index] ? 0 : edit.TerrainCost.Value;

            if (edit.BaseColor.HasValue)
                _data.BaseCellColors[index] = edit.BaseColor.Value;

            if (edit.PaintLayerId.HasValue)
                _data.LastPaintLayerIds[index] = edit.PaintLayerId.Value;

            if (updateVisuals)
                _renderer2D?.MarkCellTruthChanged(index);
        }


        // full truth write
        public void SetCellData(
            int index,
            bool blocked,
            byte terrainKey,
            int terrainCost,
            Color32 baseColor,
            byte paintLayerId = 0,
            bool updateVisuals = true
        )
        {
            if (_data == null) throw new InvalidOperationException("Map not generated yet.");
            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));

            _data.IsBlocked[index] = blocked;
            _data.TerrainTypeIds[index] = terrainKey;
            _data.TerrainCosts[index] = blocked ? 0 : terrainCost;
            _data.BaseCellColors[index] = baseColor;
            _data.LastPaintLayerIds[index] = paintLayerId;

            if (updateVisuals)
                _renderer2D?.MarkCellTruthChanged(index); // change visuals to match new terrain after truth changes
        }



        // need to look into if this one is stillusefull or should be changed...
        public void ResetToDefaultCellPreset(int index, bool isWalkable = true, bool updateVisuals = true)
        {
            bool blocked = !isWalkable;
            byte terrainKey = (byte)TerrainID.Land;
            int cost = blocked ? 0 : _baseTerrainCost;
            Color32 color = blocked ? _obstacleColor : _walkableColor;

            SetCellData(index, blocked, terrainKey, cost, color, paintLayerId: 0, updateVisuals: updateVisuals);
        }

        public void SetTerrainCost(int index, int terrainCost, bool updateVisuals = false)
        {
            // cost doesn’t need visuals now, but leave option for later if this affects visuals somhow
            ApplyCellEdit(index, new CellEdit(terrainCost: terrainCost), updateVisuals);
        }

        public void SetBlockedAndTerrainCost(int index, bool blocked, int terrainCost, bool updateVisuals = true)
        {
            ApplyCellEdit(index, new CellEdit(blocked: blocked, terrainCost: terrainCost), updateVisuals);
        }

        public void SetBaseColor(int index, Color32 color, bool updateVisuals = true)
        {
            ApplyCellEdit(index, new CellEdit(baseColor: color), updateVisuals);
        }


        // NOTE: remeber that now when setting multiple cells set updateVisuals = false untill the last one. 
        //       needs to be checked and maybe updated in all generation scripts later?
        /*
            for (int i = 0; i < _data.CellCount; i++)
            {
            ApplyCellEdit(i, new CellEdit(baseColor: new Color32(0,0,0,255)), updateVisuals: false);
            }

            // once
            _renderer2D?.MarkCellTruthChanged(index); // or ResetColorsToBase / Rebuild
        */


        #endregion




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




}

}