using System;
using System.Collections.Generic;
using UnityEngine;



namespace AI_Workshop03
{
    // MapDataGenerator.Blobs.cs      -   Purpose: Blob mode generation + Blob expansion internals        
    public sealed partial class MapDataGenerator
    {

        private void GenerateBlobs(TerrainTypeData terrain, List<int> outCells)
        {
            outCells.Clear();

            int desiredCells = Mathf.RoundToInt(terrain.CoveragePercent * _cellCount);
            if (desiredCells <= 0) return;

            EnsureGenBuffers();

            // a shared memory stamp for this terrain to keep from overlapping blobs
            int unionId = NextMarkId();

            int avgSize = Mathf.Max(1, terrain.Blob.AvgBlobSize);
            int blobCount = desiredCells / avgSize;
            blobCount = Mathf.Clamp(blobCount, terrain.Blob.MinBlobCount, terrain.Blob.MaxBlobCount);

            for (int b = 0; b < blobCount; b++)
            {
                int seed = -1;
                bool foundSeed = false;


                // try to find a seed that is not already part of this terrain's, up to 64 tries internaly for each attempt
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    if (!TryPickCell_ByFocusArea(terrain, terrain.Blob.PlacementArea, terrain.Blob.PlacementWeights, out seed, 64))
                    {
                        foundSeed = false;
                        break;
                    }

                    if (_scratch.used[seed] == unionId)
                        continue;

                    foundSeed = true;
                    break;
                }

                if (!foundSeed) break;

                int remaining = desiredCells - outCells.Count;
                if (remaining <= 0) break;


                int jitter = Mathf.Max(0, terrain.Blob.BlobSizeJitter);
                int size = avgSize + _rng.Next(-jitter, jitter + 1);

                // blobs should't be tiny but also not exceed remaining cells
                size = Mathf.Max(10, size);
                size = Math.Min(size, remaining);


                _scratch.temp.Clear();
                ExpandRandomBlob(terrain, seed, size, unionId, _scratch.temp);

                // if blob could not grow any cells, stop looping
                if (_scratch.temp.Count == 0) break;

                outCells.AddRange(_scratch.temp);
            }
        }


        private void ExpandRandomBlob(
            TerrainTypeData terrain,
            int seedIndex,
            int maxCells,
            int unionId,
            List<int> outCells)
        {
            outCells.Clear();
            if (!IsValidCell(seedIndex)) return;
            if (!CanUseCell(terrain, seedIndex)) return;

            EnsureGenBuffers();

            if (_scratch.used[seedIndex] == unionId)
                return; // already part of this terrain's union

            int stampId = NextMarkId();

            int head = 0;
            int tail = 0;

            _scratch.stamp[seedIndex] = stampId;
            _scratch.used[seedIndex] = unionId;
            _scratch.queue[tail++] = seedIndex;
            outCells.Add(seedIndex);

            float growChance = Mathf.Clamp01(terrain.Blob.GrowChance);
            int smoothPasses = terrain.Blob.SmoothPasses;
            maxCells = Mathf.Max(1, maxCells);

            // BFS-like growth
            while (head < tail && outCells.Count < maxCells)
            {
                int current = _scratch.queue[head++];
                IndexToXY(current, out int x, out int y);

                for (int neighbor = 0; neighbor < Neighbors4.Length; neighbor++)
                {
                    var (dirX, dirY) = Neighbors4[neighbor];
                    if (!TryCoordToIndex(x + dirX, y + dirY, out int next)) continue;

                    if (_scratch.stamp[next] == stampId) continue;  // allready in this blob
                    if (_scratch.used[next] == unionId) continue;   // already part of a previous blob of same terrain
                    if (!CanUseCell(terrain, next)) continue;
                    if (_rng.NextDouble() > growChance) continue;

                    _scratch.stamp[next] = stampId;
                    _scratch.used[next] = unionId;
                    _scratch.queue[tail++] = next;
                    outCells.Add(next);

                    if (outCells.Count >= maxCells) break;
                }
            }

            // Smoothing passes to fill in small gaps
            for (int pass = 0; pass < smoothPasses; pass++)
            {
                int before = outCells.Count;
                for (int i = 0; i < before; i++) _scratch.queue[i] = outCells[i];

                for (int i = 0; i < before && outCells.Count < maxCells; i++)
                {
                    int current = _scratch.queue[i];
                    IndexToXY(current, out int x, out int y);

                    for (int neighbor = 0; neighbor < Neighbors4.Length && outCells.Count < maxCells; neighbor++)
                    {
                        var (dirX, dirY) = Neighbors4[neighbor];
                        if (!TryCoordToIndex(x + dirX, y + dirY, out int next)) continue;

                        if (_scratch.stamp[next] == stampId) continue;
                        if (_scratch.used[next] == unionId) continue;
                        if (!CanUseCell(terrain, next)) continue;

                        _scratch.stamp[next] = stampId;
                        _scratch.used[next] = unionId;
                        outCells.Add(next);
                    }
                }
            }
        }



    }


}
