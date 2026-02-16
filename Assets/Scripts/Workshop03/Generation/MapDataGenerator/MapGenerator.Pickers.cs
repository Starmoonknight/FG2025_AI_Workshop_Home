using System.Collections.Generic;
using UnityEngine;
using AreaFocusWeights = AI_Workshop03.TerrainTypeData.AreaFocusWeights;



namespace AI_Workshop03
{
    // MapGenerator.Pickers.cs      -   Purpose: cell selection toolbox: (choose cells + focus logic + pool logic)   
    public sealed partial class MapGenerator
    {

        #region Cell Pickers - Core Pick Rules

        private bool CanUseCell(TerrainTypeData terrain, int idx)
        {
            // Hard-block overwrite policy
            if (_blocked[idx])
                return terrain.AllowOverwriteObstacle;  //  (obstacle overwrite)

            // Terrain overwrite gating (painterId = layer; 0 = base)        
            bool isBase = (_lastPaintLayerId[idx] == 0);

            if (terrain.OnlyAffectBase)                 // can this only effect base terrain tile?      (terrain overwrite)
                return isBase;

            if (!terrain.AllowOverwriteTerrain)         // if terrain is not a base tile, may it overwrite it?     (terrain overwrite)
                return isBase;

            return true;
        }

        private bool CanPickCell(TerrainTypeData terrain, int idx)
        {
            if (terrain.ForceUnblockedSeed && _blocked[idx]) return false;
            return CanUseCell(terrain, idx);
        }


        #endregion



        #region Cell Pickers - Basic Pickers

        private bool TryPickRandomUnBlocked(out int index, int tries, bool requireBase)
        {
            index = -1;

            if (_cellCount <= 0) return false;
            if (!HasAnyWalkable) return false;

            for (int t = 0; t < tries; t++)
            {
                int i = _rng.Next(0, _cellCount);
                if (_blocked[i]) continue;
                if (requireBase && _lastPaintLayerId[i] != 0) continue;
                index = i;
                return true;
            }

            return false;

        }

        private bool TryPickCell_Anywhere(TerrainTypeData terrain, out int index, int tries)
        {
            index = -1;
            if (_cellCount <= 0) return false;

            if (terrain.ForceUnblockedSeed && !HasAnyWalkable)
                return false;

            for (int t = 0; t < tries; t++)
            {
                int i = _rng.Next(0, _cellCount);
                if (!CanPickCell(terrain, i)) continue;
                index = i;
                return true;
            }

            return false;
        }

        // chooses from absolut map edge-boarder, the outermost ring of cells, only 1 cell thick
        private bool TryPickCell_EdgeLine(TerrainTypeData terrain, out int index, int tries)
        {
            index = -1;

            for (int t = 0; t < tries; t++)
            {
                int side = _rng.Next(0, 4);
                if (TryPickCell_EdgeLine_Once(terrain, side, out index))
                    return true;
            }

            return false;
        }

        // chooses from absolut map edge-boarder, the outermost ring of cells, only 1 cell thick
        private bool TryPickCell_EdgeLineOnSide(TerrainTypeData terrain, int side, out int index, int tries)
        {
            index = -1;
            side = ((side % 4) + 4) % 4;

            for (int t = 0; t < tries; t++)
            {
                if (TryPickCell_EdgeLine_Once(terrain, side, out index))
                    return true;
            }

            return false;
        }

        // chooses from absolut map edge-boarder, the outermost ring of cells, only 1 cell thick
        private bool TryPickCell_EdgeLine_Once(TerrainTypeData terrain, int side, out int index)
        {
            index = -1;

            int x, y;
            switch (side)
            {
                case 0: x = 0; y = _rng.Next(0, _height); break;       // left
                case 1: x = _width - 1; y = _rng.Next(0, _height); break;       // right
                case 2: x = _rng.Next(0, _width); y = 0; break;                 // bottom
                default: x = _rng.Next(0, _width); y = _height - 1; break;                 // top
            }

            int candIdx = CoordToIndexUnchecked(x, y);
            if (!CanPickCell(terrain, candIdx)) return false;

            index = candIdx;
            return true;
        }


        #endregion



        #region Cell Pickers - Advanced Focus Pickers

        private bool TryPickCell_ByFocusArea(
            TerrainTypeData terrain,
            ExpansionAreaFocus focusArea,
            in TerrainTypeData.AreaFocusWeights weights,
            out int index, int tries)
        {
            index = -1;

            tries = Mathf.Max(1, tries);
            int fallbackTries = Mathf.Max(8, tries / 2);    // protects against: tries/2 can become 0 where pick loops would never run

            switch (focusArea)
            {
                case ExpansionAreaFocus.Edge:
                    return TryPickCell_EdgeBandArea(terrain, in weights, out index, tries);

                case ExpansionAreaFocus.Interior:
                    {
                        return TryPickCell_Interior(terrain, in weights, out index, tries);
                    }

                case ExpansionAreaFocus.Anywhere:
                    return TryPickCell_Anywhere(terrain, out index, tries);

                case ExpansionAreaFocus.Weighted:
                    {
                        // RollWeightedFocus can return Anywhere if weights are all zero/misconfigured as a sensible default.
                        ExpansionAreaFocus rolled = RollWeightedFocus(in weights, _rng);

                        switch (rolled)
                        {
                            case ExpansionAreaFocus.Edge:
                                {
                                    // Rolled into EDGE range -> try EDGE first
                                    if (TryPickCell_EdgeBandArea(terrain, in weights, out index, tries)) return true;

                                    // fallback choice, next best to originally wanted: Interior, then Anywhere
                                    if (TryPickCell_Interior(terrain, in weights, out index, fallbackTries)) return true;
                                    return TryPickCell_Anywhere(terrain, out index, fallbackTries);
                                }

                            case ExpansionAreaFocus.Interior:
                                {
                                    // Rolled into INTERIOR range -> try INTERIOR first
                                    if (TryPickCell_Interior(terrain, in weights, out index, tries)) return true;

                                    // fallback choice, next best to originally wanted: Anywhere, then Edge
                                    if (TryPickCell_Anywhere(terrain, out index, fallbackTries)) return true;
                                    return TryPickCell_EdgeBandArea(terrain, in weights, out index, fallbackTries);
                                }

                            case ExpansionAreaFocus.Anywhere:
                            default:
                                {
                                    // Rolled into ANYWHERE range -> try ANYWHERE first
                                    if (TryPickCell_Anywhere(terrain, out index, tries)) return true;

                                    // fallback choice, next best to originally wanted: Interior, then Edge 
                                    if (TryPickCell_Interior(terrain, in weights, out index, fallbackTries)) return true;
                                    return TryPickCell_EdgeBandArea(terrain, in weights, out index, fallbackTries);
                                }
                        }
                    }

                default:
                    return TryPickCell_Anywhere(terrain, out index, tries);
            }
        }

        private bool TryPickCell_NearExisting(TerrainTypeData terrain, List<int> chosen, int poolId, int radius, int tries, out int result,
            ExpansionAreaFocus focusArea = ExpansionAreaFocus.Anywhere, int focusThickness = 0)
        {

            result = -1;

            if (focusArea == ExpansionAreaFocus.Weighted)
            {
#if UNITY_EDITOR
                Debug.LogError("TryPickCell_NearExisting: focus must not be Weighted. Roll it first.");
#endif
                focusArea = ExpansionAreaFocus.Anywhere;
            }

            if (chosen == null || chosen.Count == 0) return false;

            for (int i = 0; i < tries; i++)
            {
                int anchor = chosen[_rng.Next(0, chosen.Count)];
                IndexToXY(anchor, out int ax, out int ay);

                var (dx, dy) = Neighbors8[_rng.Next(0, Neighbors8.Length)];

                // bias towards small distances, cluster
                int dist = 1 + (int)(System.Math.Pow(_rng.NextDouble(), 2.0) * radius);

                int x = ax + dx * dist;
                int y = ay + dy * dist;

                if ((uint)x >= (uint)_width || (uint)y >= (uint)_height) continue;

                int candIdx = CoordToIndexUnchecked(x, y);

                // enforce pool membership, only pick from cells that are currently in the pool
                if (_scratch.used[candIdx] != poolId) continue;

                // safety against pooled cell becoming an invalid option between pick and use
                if (!CanPickCell(terrain, candIdx)) continue;   // need to choose between this and or CanUseCell depending on intent, probably keep the picker version 

                // enforce focus region, if terrain uses that
                if (!MatchesFocus(candIdx, focusArea, focusThickness)) continue;

                result = candIdx;
                return true;
            }

            return false;
        }

        private bool TryPickCell_EdgeBandArea(TerrainTypeData terrain, in AreaFocusWeights weights, out int index, int tries)
        {
            index = -1;

            for (int t = 0; t < tries; t++)
            {
                int side = _rng.Next(0, 4);
                if (TryPickCell_EdgeBandArea_Once(terrain, in weights, side, out index))
                    return true;
            }

            return false;
        }

        private bool TryPickCell_EdgeBandAreaOnSide(TerrainTypeData terrain, in AreaFocusWeights weights, int side, out int index, int tries)
        {
            index = -1;
            side = ((side % 4) + 4) % 4;

            for (int t = 0; t < tries; t++)
            {
                if (TryPickCell_EdgeBandArea_Once(terrain, in weights, side, out index))
                    return true;
            }

            return false;
        }

        private bool TryPickCell_EdgeBandArea_Once(TerrainTypeData terrain, in AreaFocusWeights weights, int side, out int index)
        {
            index = -1;

            int band = ComputeEdgeBandCells(in weights);

            // silent fallback to absolute map boarder-edge if no valid edge band on tiny maps
            if (band <= 0)
                return TryPickCell_EdgeLine_Once(terrain, side, out index);

            int x, y;

            switch (side)
            {
                case 0: x = _rng.Next(0, band); y = _rng.Next(0, _height); break;                   // left
                case 1: x = _rng.Next(_width - band, _width); y = _rng.Next(0, _height); break;     // right
                case 2: x = _rng.Next(0, _width); y = _rng.Next(0, band); break;                    // bottom
                default: x = _rng.Next(0, _width); y = _rng.Next(_height - band, _height); break;   // top
            }

            int candIdx = CoordToIndexUnchecked(x, y);
            if (!CanPickCell(terrain, candIdx)) return false;

            index = candIdx;
            return true;
        }

        private bool TryPickCell_Interior(TerrainTypeData terrain, in TerrainTypeData.AreaFocusWeights weights, out int index, int tries)
        {
            index = -1;
            int margin = ComputeInteriorMarginCells(in weights);

            // silent fallback to anywhere if margin too big for map
            if (_width - 2 * margin <= 0 || _height - 2 * margin <= 0)
                return TryPickCell_Anywhere(terrain, out index, tries);

            for (int t = 0; t < tries; t++)
            {
                int x = _rng.Next(margin, _width - margin);
                int y = _rng.Next(margin, _height - margin);

                int candIdx = CoordToIndexUnchecked(x, y);
                if (!CanPickCell(terrain, candIdx)) continue;
                index = candIdx;
                return true;
            }
            return false;
        }


        // maybe add in a version of this method where the startIdx or startSide can be choosen and not rng,
        // move code and make this a a wrapper that feeds in a rng start value to the other method? 
        private bool TryPickCell_EdgePairPreset(TerrainTypeData terrain, in TerrainTypeData.AreaFocusWeights startWeights, in TerrainTypeData.AreaFocusWeights goalWeights, LichtenbergEdgePairMode mode,
            out int startIdx, out int goalIdx, int tries)
        {
            startIdx = -1;
            goalIdx = -1;

            for (int t = 0; t < tries; t++)
            {
                int startSide = _rng.Next(0, 4);

                if (!TryPickCell_EdgeBandAreaOnSide(terrain, in startWeights, startSide, out startIdx, 32))
                    continue;

                int goalSide = PickGoalSideByMode(startSide, mode);

                if (!TryPickCell_EdgeBandAreaOnSide(terrain, in goalWeights, goalSide, out goalIdx, 32))
                    continue;

                if (startIdx == goalIdx) continue;

                return true;
            }

            return false;
        }


        private bool TryPickFromPool_ByFocus(
            ExpansionAreaFocus focusArea,   // expected: Edge/Interior/Anywhere   DO NOT call with ExpansionAreaFocus.Weighted
            int focusThickness,
            int tries,
            out int idx)
        {

            idx = -1;


            if (focusArea == ExpansionAreaFocus.Weighted)
            {
#if UNITY_EDITOR
                Debug.LogError("TryPickFromPool_ByFocus: focus must not be Weighted. Roll it first.");
#endif            
                focusArea = ExpansionAreaFocus.Anywhere;
            }


            int count = _scratch.temp.Count;
            if (count == 0) return false;

            // pick anywhere fast path
            if (focusArea == ExpansionAreaFocus.Anywhere)
            {
                idx = _scratch.temp[_rng.Next(0, count)];
                return true;
            }

            // rejection sample from pool to find cells matching focus area
            for (int i = 0; i < tries; i++)
            {
                int candIdx = _scratch.temp[_rng.Next(0, count)];
                if (!MatchesFocus(candIdx, focusArea, focusThickness)) continue;
                idx = candIdx;
                return true;
            }

#if UNITY_EDITOR
            Debug.LogWarning("TryPickFromPool_ByFocus: had to resort to fallback pick, a silent quality drop");
#endif
            // fallback, give a randomly generated result back anyways 
            idx = _scratch.temp[_rng.Next(0, count)];
            return true;
        }


        #endregion


        #region Helpers

        private bool IsEdgeCell(int idx)
        {
            IndexToXY(idx, out int x, out int y);
            return x == 0 || y == 0 || x == _width - 1 || y == _height - 1;
        }

        private bool IsEdgeBandCell(int idx, int band)
        {
            if (band <= 1) return IsEdgeCell(idx); // fallback
            IndexToXY(idx, out int x, out int y);
            return x < band || x >= _width - band || y < band || y >= _height - band;
        }

        private bool IsInteriorCell(int idx, int interiorMargin)
        {
            int m = Mathf.Clamp(interiorMargin, 0, Mathf.Min(_width, _height) / 2);

            if (_width - 2 * m <= 0 || _height - 2 * m <= 0)
                return false;

            IndexToXY(idx, out int x, out int y);
            return x >= m && x < _width - m && y >= m && y < _height - m;
        }

        private bool MatchesFocus(int idx, ExpansionAreaFocus focus, int focusThickness)
        {
            switch (focus)
            {
                case ExpansionAreaFocus.Edge: 
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (focusThickness <= 0)
                        Debug.LogWarning("MatchesFocus Edge called with band<=0 (degenerates to edge line). Check weight config.");
#endif
                    return IsEdgeBandCell(idx, focusThickness);


                case ExpansionAreaFocus.Interior: 
                    return IsInteriorCell(idx, focusThickness);

                case ExpansionAreaFocus.Anywhere:
                default: 
                    return true;
            }
        }

        private int PickGoalSideByMode(int startSide, LichtenbergEdgePairMode mode)
        {
            int opp = OppositeSide(startSide);

            switch (mode)
            {
                case LichtenbergEdgePairMode.Any:
                    return _rng.Next(0, 4);

                case LichtenbergEdgePairMode.SameEdge:
                    return startSide;

                case LichtenbergEdgePairMode.AdjacentEdge:
                    {
                        // not same edge, not the opposite edge

                        if (startSide < 2)      // For left/right (0/1), adjacent are bottom/top (2/3).
                            return (_rng.NextDouble() < 0.5) ? 2 : 3;
                        else                // For bottom/top (2/3), adjacent are left/right (0/1).
                            return (_rng.NextDouble() < 0.5) ? 0 : 1;
                    }

                case LichtenbergEdgePairMode.OppositeEdgePair:
                    return opp;

                case LichtenbergEdgePairMode.NotOpposite:
                default:
                    {
                        // anything except opposite: {same, adjacent left, adjacent right} = 3 choices
                        int roll = _rng.Next(0, 3);
                        if (roll == 0) return startSide;

                        if (startSide < 2)
                            return (roll == 1) ? 2 : 3;
                        else
                            return (roll == 1) ? 0 : 1;
                    }
            }
        }

        private static ExpansionAreaFocus RollWeightedFocus(in AreaFocusWeights weights, System.Random rng)
        {
            float edgeWeight = Mathf.Max(0f, weights.EdgeWeight);
            float interiorWeight = Mathf.Max(0f, weights.InteriorWeight);
            float anywhereWeight = Mathf.Max(0f, weights.AnywhereWeight);

            float total = edgeWeight + interiorWeight + anywhereWeight;
            if (total <= 0f) return ExpansionAreaFocus.Anywhere;

            double r = rng.NextDouble() * total;

            if (r < edgeWeight) return ExpansionAreaFocus.Edge;
            if (r < edgeWeight + interiorWeight) return ExpansionAreaFocus.Interior;
            return ExpansionAreaFocus.Anywhere;
        }


        private void RemoveFromPool(int idx, int poolMark)
        {
            if (_scratch.used[idx] != poolMark) return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (poolMark == 0) Debug.LogError("RemoveFromPool called without pool indexing enabled.");
#endif

            int pos = _scratch.poolPos[idx];
            int lastPos = _scratch.temp.Count - 1;
            int lastIdx = _scratch.temp[lastPos];

            _scratch.temp[pos] = lastIdx;
            _scratch.poolPos[lastIdx] = pos;

            _scratch.temp.RemoveAt(lastPos);
            _scratch.used[idx] = 0; // not in pool
        }


        #endregion


    }


}
