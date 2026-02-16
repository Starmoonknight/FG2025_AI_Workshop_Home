using System.Collections.Generic;
using UnityEngine;


namespace AI_Workshop03
{

    // MapGenerator.ModeStatic.cs         -   Purpose:  Static mode generation and expansion internals    
    public sealed partial class MapGenerator
    {

        /*
         *  NOTE: Future plan: 
         * 
         *  Current Static mode pool-building scans the whole map, O(N) scan per Static terrain rule.
         *  Risk being a large CPU sink on larger maps
         * 
         *  Consider switching to a hybrid strategy of rejection sampling:
         *      - If the terrain’s target count is “small” relative to eligible space, do rejection sampling without building a full pool.
         *      - Only build the full pool when target is large (or clustering is enabled and needs pool removal).
         * 
         *      Rule of thumb suggested to start with: 
         *      - If target < 0.01 * _cellCount (1%), skip pool build and just pick randomly with a uniqueness stamp. 
         *      - Else use pool as you do now.
         *      - Avoid scanning 1,000,000 cells just to pick e.g. 500. Also need to look into how to make it work with the weighte selection zones.
         *      
         */

        private void ExpandRandomStatic(TerrainTypeData terrain, List<int> outCells)
        {
            outCells.Clear();

            float coverage01 = Mathf.Clamp01(terrain.CoveragePercent);
            if (coverage01 <= 0) return;

            AssertBuffersReady();        //EnsureGenBuffers();

            ExpansionAreaFocus placement = terrain.Static.PlacementArea;
            TerrainTypeData.AreaFocusWeights weights = terrain.Static.PlacementWeights;

            int focusThickness = ComputeEdgeBandCells(in weights);
            float clusterBias = Mathf.Clamp01(terrain.Static.ClusterBias);

            ExpansionAreaFocus poolFilter =
                (placement == ExpansionAreaFocus.Weighted) ? ExpansionAreaFocus.Anywhere : placement;

            _scratch.temp.Clear();

            // Only needed if we will remove from pool by cell-index (needs poolPos mapping),
            // or during near existing membership checks.
            bool needsPoolIndexing = (placement == ExpansionAreaFocus.Weighted) || (clusterBias > 0f);
            int poolMark = needsPoolIndexing ? NextMarkId() : 0;


            for (int i = 0; i < _cellCount; i++)
            {
                if (!CanUseCell(terrain, i)) continue;
                if (!MatchesFocus(i, poolFilter, focusThickness)) continue;

                int pos = _scratch.temp.Count;
                _scratch.temp.Add(i);

                if (needsPoolIndexing)
                {
                    _scratch.used[i] = poolMark;
                    _scratch.poolPos[i] = pos;
                }
            }

            int eligible = _scratch.temp.Count;
            if (eligible == 0) return;

            int targetTotal = Mathf.RoundToInt(coverage01 * _cellCount);
            int target = Mathf.Min(targetTotal, eligible);
            if (target <= 0) return;


            // Selection picking eligible candidates
            if (clusterBias <= 0f)
            {
                if (placement == ExpansionAreaFocus.Weighted)
                {
                    // still uses RemoveFromPool => needsPoolIndexing is true here
                    // for weighted search of viable cells 
                    for (int k = 0; k < target; k++)
                    {
                        ExpansionAreaFocus pickFocus = RollWeightedFocus(in weights, _rng);

                        int focusTries = Mathf.Clamp(_scratch.temp.Count / 8, 8, 64);
                        if (!TryPickFromPool_ByFocus(pickFocus, focusThickness, focusTries, out int pickedIdx))
                            break;

                        RemoveFromPool(pickedIdx, poolMark);
                        outCells.Add(pickedIdx);
                    }
                    return;
                }

                // Uniform + no clustering: never removes => no poolPos/used writes above
                // non-weighted uniform pick by Partial Fisher-Yates: pick 'target' unique cells     // Note to self; look more into Fisher-Yates and Partial Fisher-Yates later
                for (int k = 0; k < target; k++)
                {
                    int swap = _rng.Next(k, eligible);
                    (_scratch.temp[k], _scratch.temp[swap]) = (_scratch.temp[swap], _scratch.temp[k]);
                    outCells.Add(_scratch.temp[k]);
                }
                return;
            }

            // Cluster branch: uses RemoveFromPool / membership => needsPoolIndexing true            
            int loose = Mathf.Max(4, Mathf.RoundToInt(Mathf.Min(_width, _height) * 0.02f));         // should scale with map size
            int tight = 2;            
            int maxRadius = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(loose, tight, clusterBias)));  // higer bias should make the static expansion cluster more into groups 

            int nearTries = Mathf.Max(8, Mathf.RoundToInt(Mathf.Lerp(8f, 32f, clusterBias)));
            for (int k = 0; k < target; k++)
            {
                if (_scratch.temp.Count == 0) break;

                ExpansionAreaFocus pickFocus =
                    (placement == ExpansionAreaFocus.Weighted)
                        ? RollWeightedFocus(in weights, _rng)
                        : placement;

                int pickedIdx = -1;

                // tries cluster-near pick first inside enforced focus region
                if (outCells.Count > 0 && _rng.NextDouble() < clusterBias)
                {
                    if (TryPickCell_NearExisting(terrain, outCells, poolMark, maxRadius, nearTries, out int near,
                            focusArea: pickFocus, focusThickness: focusThickness))
                    {
                        pickedIdx = near;
                    }
                }

                // if clustering didn’t pick anything, pick from pool respecting focus
                if (pickedIdx < 0)
                {
                    // the tries = how hard it will try to find a cell that matches focus area from available in pool
                    int focusTries = Mathf.Clamp(_scratch.temp.Count / 8, 8, 64);

                    if (!TryPickFromPool_ByFocus(pickFocus, focusThickness, focusTries, out pickedIdx))
                        break; // pool empty or something very wrong
                }

                RemoveFromPool(pickedIdx, poolMark);
                outCells.Add(pickedIdx);
            }

        }



    }

}