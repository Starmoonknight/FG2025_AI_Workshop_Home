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
            if (m_data == null) throw new InvalidOperationException("Map not generated yet.");
            if (!m_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));

            bool traversalChanged = false;

            // Hard coherence rule:
            if (edit.Blocked.HasValue)
            {
                bool newBlocked = edit.Blocked.Value;

                if (m_data.IsBlocked[index] != newBlocked)
                    traversalChanged = true;

                m_data.IsBlocked[index] = newBlocked;

                // if unblocked and cost invalid, repair to base
                if (newBlocked && m_data.TerrainCosts[index] != 0)
                {
                    m_data.TerrainCosts[index] = 0;
                    traversalChanged = true;
                }
            }

            if (edit.TerrainKey.HasValue)
                m_data.TerrainTypeIds[index] = edit.TerrainKey.Value;

            if (edit.TerrainCost.HasValue)
            {
                traversalChanged = true;
                m_data.TerrainCosts[index] = m_data.IsBlocked[index] ? 0 : Mathf.Max(1, edit.TerrainCost.Value);
            }
            else
            {
                // if unblocked and cost ended up non-positive, repair default
                if (!m_data.IsBlocked[index] && m_data.TerrainCosts[index] <= 0)
                {
                    m_data.TerrainCosts[index] = Mathf.Max(1, _baseTerrainCost);
                    traversalChanged = true;
                }
            }

            if (edit.BaseColor.HasValue)
                m_data.BaseCellColors[index] = edit.BaseColor.Value;

            if (edit.PaintLayerId.HasValue)
                m_data.LastPaintLayerIds[index] = edit.PaintLayerId.Value;

            if (traversalChanged)
            {
                OnTraversalTruthMutated();
            }

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
            if (m_data == null) throw new InvalidOperationException("Map not generated yet.");
            if (!m_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));

            int safeCost = blocked ? 0 : Mathf.Max(1, terrainCost);

            bool traversalChanged =
                (m_data.IsBlocked[index] != blocked) ||
                (m_data.TerrainCosts[index] != safeCost);

            m_data.IsBlocked[index] = blocked;
            m_data.TerrainTypeIds[index] = terrainKey;
            m_data.TerrainCosts[index] = safeCost;
            m_data.BaseCellColors[index] = baseColor;
            m_data.LastPaintLayerIds[index] = paintLayerId;

            if (traversalChanged)
                OnTraversalTruthMutated();

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
            if (m_data == null) throw new InvalidOperationException("Map not generated yet.");

            if (!m_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));
            return !m_data.IsBlocked[index];
        }

        // check terrain cost, used for core loops, eg. pathfinding 
        public int GetTerrainCost(int index)
        {
            if (m_data == null) throw new InvalidOperationException("Map not generated yet.");

            if (!m_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));
            return m_data.TerrainCosts[index];
        }


        #endregion


        #region Internal hot path getters (used in core loops, eg. map generation)

        internal bool GetWalkableUnchecked(int index) => !m_data.IsBlocked[index];

        internal int GetTerrainCostUnchecked(int index) => m_data.TerrainCosts[index];

        internal void SetCellDataUnchecked(
            int index,
            bool blocked,
            byte terrainKey,
            int terrainCost,
            Color32 baseColor,
            byte paintLayerId = 0)
        {
            m_data.IsBlocked[index] = blocked;
            m_data.TerrainTypeIds[index] = terrainKey;
            m_data.TerrainCosts[index] = blocked ? 0 : terrainCost;
            m_data.BaseCellColors[index] = baseColor;
            m_data.LastPaintLayerIds[index] = paintLayerId;
        }

        #endregion



    }

}