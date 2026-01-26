using System;
using System.Collections.Generic;
using UnityEngine;



namespace AI_Workshop03
{
    // MapGenerator.ModeLichtenberg.cs      -   Purpose: Lichtenberg mode + widening + heat mechanics
    public sealed partial class MapGenerator
    {

        private void GenerateLichtenberg(TerrainTypeData terrain, byte dataLayerId, List<int> outCells)
        {

            outCells.Clear();
            EnsureGenBuffers();
            Array.Clear(_scratch.heat, 0, _cellCount);

            int usedId = NextMarkId();

            float coverage = Mathf.Clamp01(terrain.CoveragePercent);
            int desiredCells = Mathf.RoundToInt(coverage * _cellCount);
            if (desiredCells <= 0) return;

            int perPath = Mathf.Max(1, terrain.Lichtenberg.CellsPerPath);
            int pathCount = desiredCells / perPath;
            pathCount = Mathf.Clamp(pathCount, terrain.Lichtenberg.MinPathCount, terrain.Lichtenberg.MaxPathCount);

            int maxSteps = Mathf.RoundToInt((_width + _height) * terrain.Lichtenberg.StepBudgetScale);


            for (int r = 0; r < pathCount; r++)
            {
                int start;
                int goal;

                if (terrain.Lichtenberg.UseEdgePairPresets)
                {
                    if (!TryPickCell_EdgePairPreset(terrain, terrain.Lichtenberg.OriginWeights, terrain.Lichtenberg.GrowthAimWeights, terrain.Lichtenberg.EdgePairMode, out start, out goal, 256))
                        break;
                }
                else
                {
                    if (!TryPickCell_ByFocusArea(terrain, terrain.Lichtenberg.OriginArea, terrain.Lichtenberg.OriginWeights, out start, 256))
                        break;

                    if (!TryPickCell_ByFocusArea(terrain, terrain.Lichtenberg.GrowthAimArea, terrain.Lichtenberg.GrowthAimWeights, out goal, 256))
                        break;
                }


                _scratch.temp.Clear();
                ExpandRandomLichtenberg(terrain, dataLayerId, usedId, start, goal, maxSteps, _scratch.temp);

                // if widen passes are set above zero 
                for (int p = 0; p < terrain.Lichtenberg.WidenPasses; p++)
                    WidenOnce(terrain, _scratch.temp);

                // remember used tiles for heat maping 
                for (int i = 0; i < _scratch.temp.Count; i++)
                    _scratch.used[_scratch.temp[i]] = usedId;

                outCells.AddRange(_scratch.temp);
            }
        }


        private void ExpandRandomLichtenberg(
            TerrainTypeData terrain,
            byte terrainPaintId,
            int usedId,
            int startIndex,
            int targetIndex,
            int maxSteps,
            List<int> outCells)
        {
            outCells.Clear();
            if (!IsValidCell(startIndex) || !IsValidCell(targetIndex)) return;
            if (!CanUseCell(terrain, startIndex)) return;
            if (!CanUseCell(terrain, targetIndex)) return;

            EnsureGenBuffers();
            int stampId = NextMarkId();


            bool preferUnused = terrain.Lichtenberg.PreferUnusedCells;
            bool allowReuse = terrain.Lichtenberg.AllowReuseIfStuck;
            float towardTargetBias = Mathf.Clamp01(terrain.Lichtenberg.GoalGrowthBias);
            float branchChance = Mathf.Clamp01(terrain.Lichtenberg.BranchSpawnChance);
            int maxWalkers = Mathf.Clamp(terrain.Lichtenberg.MaxActiveWalkers, 1, 64);
            maxSteps = Mathf.Max(1, maxSteps);

            int walkerCount = 1;
            _scratch.queue[0] = startIndex;

            _scratch.stamp[startIndex] = stampId;
            outCells.Add(startIndex);

            IndexToXY(targetIndex, out int targetX, out int targetY);

            for (int step = 0; step < maxSteps; step++)
            {
                int walkerThisStep = step % walkerCount;
                int current = _scratch.queue[walkerThisStep];

                if (current == targetIndex) break;

                IndexToXY(current, out int x, out int y);

                int stepX = Math.Sign(targetX - x);
                int stepY = Math.Sign(targetY - y);


                // Code memo to remember new words;
                // Span is a stack only ref struct
                // Stackalloc allocates a block of memory on the stack, not the heap. It’s extremely fast and automatically freed when the method scope ends (no GC, no pooling).    
                Span<(int dirX, int dirY)> candidates = stackalloc (int, int)[8];
                int cCount = 0;

                static void AddCandidateUnique(
                    Span<(int dirX, int dirY)> cand,
                    ref int count,
                    int dx,
                    int dy)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (cand[i].dirX == dx && cand[i].dirY == dy)
                            return;
                    }

                    if (count < cand.Length)
                        cand[count++] = (dx, dy);
                }


                bool hasX = stepX != 0;
                bool hasY = stepY != 0;

                if (hasX) AddCandidateUnique(candidates, ref cCount, stepX, 0);
                if (hasY) AddCandidateUnique(candidates, ref cCount, 0, stepY);

                if (hasX) { AddCandidateUnique(candidates, ref cCount, stepX, 1); AddCandidateUnique(candidates, ref cCount, stepX, -1); }
                if (hasY) { AddCandidateUnique(candidates, ref cCount, 1, stepY); AddCandidateUnique(candidates, ref cCount, -1, stepY); }

                if (hasX) AddCandidateUnique(candidates, ref cCount, -stepX, 0);
                if (hasY) AddCandidateUnique(candidates, ref cCount, 0, -stepY);


                int nextIndex = -1;

                // it tries two passes to avoid clumping paths into a ball of spaghetti: unused-first, then allow used if stuck.
                for (int pass = 0; pass < 2 && nextIndex < 0; pass++)
                {
                    bool allowUsedThisPass = (pass == 1);

                    if (allowUsedThisPass && (!allowReuse || !preferUnused))
                        break;

                    float bestScore = float.NegativeInfinity;
                    int bestCand = -1;

                    // for simple shuffle-ish by random start
                    int startC = _rng.Next(0, Math.Max(1, cCount));

                    for (int k = 0; k < cCount; k++)
                    {
                        int c = (startC + k) % cCount;
                        var (dirX, dirY) = candidates[c];

                        int cx = x + dirX;
                        int cy = y + dirY;

                        if (!TryCoordToIndex(cx, cy, out int cand)) continue;
                        if (!CanUseCell(terrain, cand)) continue;

                        bool alreadyInThisRoad = (_scratch.stamp[cand] == stampId);
                        if (alreadyInThisRoad)
                            continue;

                        bool usedByEarlierPaths = (_scratch.used[cand] == usedId);
                        bool usedByThisTerrainAlready = (_lastPaintLayerId[cand] == terrainPaintId);

                        bool used = usedByEarlierPaths || usedByThisTerrainAlready;
                        if (!allowUsedThisPass && used)
                            continue;

                        // heat scoring to biase choise
                        int heat = _scratch.heat[cand];
                        float repel = terrain.Lichtenberg.HeatRepelStrength * heat;

                        // penalize stepping on cells allready targeted 
                        if (terrain.Lichtenberg.RepelPenaltyFromExisting && usedByThisTerrainAlready)
                            repel += terrain.Lichtenberg.ExistingCellPenalty;

                        int candH = Math.Max(Math.Abs(targetX - cx), Math.Abs(targetY - cy));

                        // allow towardTargetBias to influence scoring
                        bool toward =
                            (dirX == stepX && dirY == 0) ||
                            (dirX == 0 && dirY == stepY);

                        float towardBonus = toward ? (towardTargetBias * 0.35f) : -(towardTargetBias * 0.15f);

                        float noise = (float)_rng.NextDouble() * 0.25f;

                        float score = (-candH) + towardBonus + noise - repel;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestCand = cand;
                        }
                    }

                    if (bestCand >= 0)
                        nextIndex = bestCand;
                }

                if (nextIndex < 0) break;

                _scratch.queue[walkerThisStep] = nextIndex;

                // update heat after a walker moves
                AddHeat(nextIndex, terrain.Lichtenberg.HeatRepelRadius, terrain.Lichtenberg.HeatAdd, terrain.Lichtenberg.HeatFalloff);

                if (_scratch.stamp[nextIndex] != stampId)
                {
                    _scratch.stamp[nextIndex] = stampId;
                    outCells.Add(nextIndex);
                }

                if (walkerCount < maxWalkers && _rng.NextDouble() <= branchChance)
                {
                    _scratch.queue[walkerCount++] = nextIndex;
                }
            }
        }


        private void WidenOnce(TerrainTypeData terrain, List<int> cells)
        {
            EnsureGenBuffers();
            int stampId = NextMarkId();

            // Mark existing
            for (int i = 0; i < cells.Count; i++)
            {
                int index = cells[i];
                if (IsValidCell(index))
                    _scratch.stamp[index] = stampId;
            }

            int originalCount = cells.Count;
            for (int i = 0; i < originalCount; i++)
            {
                int current = cells[i];
                IndexToXY(current, out int x, out int y);

                for (int neighbor = 0; neighbor < Neighbors4.Length; neighbor++)
                {
                    var (dirX, dirY) = Neighbors4[neighbor];
                    if (!TryCoordToIndex(x + dirX, y + dirY, out int next)) continue;
                    if (_scratch.stamp[next] == stampId) continue;
                    if (!CanUseCell(terrain, next)) continue;

                    _scratch.stamp[next] = stampId;
                    cells.Add(next);
                }
            }
        }


        private void AddHeat(int idx, int radius, int add, int falloff)
        {
            _scratch.heat[idx] += add;

            if (radius <= 0) return;

            IndexToXY(idx, out int x, out int y);
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (!TryCoordToIndex(x + dx, y + dy, out int n)) continue;

                    int manhattanDist = Mathf.Abs(dx) + Mathf.Abs(dy);
                    int v = Mathf.Max(0, add - falloff * manhattanDist);
                    _scratch.heat[n] += v;
                }
        }



    }


}
