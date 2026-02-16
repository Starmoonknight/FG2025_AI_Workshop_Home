using System;
using System.Collections.Generic;
using UnityEngine;



namespace AI_Workshop03
{

    // MapGenerator.ModeBlobs.cs      -   Purpose: Blob mode generation + Blob expansion internals        
    public sealed partial class MapGenerator
    {

        private void GenerateBlobs(TerrainTypeData terrain, List<int> outCells)
        {
            outCells.Clear();

            float coverage01 = Mathf.Clamp01(terrain.CoveragePercent);
            int desiredCells = Mathf.RoundToInt(coverage01 * _cellCount);
            if (desiredCells <= 0) return;

            EnsureListCapacity(outCells, desiredCells);
            AssertBuffersReady();        //EnsureGenBuffers();

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
                EnsureListCapacity(_scratch.temp, size);   // size == maxCells upper bound
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

            maxCells = Mathf.Clamp(maxCells, 1, _cellCount);
            EnsureListCapacity(outCells, maxCells);

            AssertBuffersReady();        //EnsureGenBuffers();

            if (_scratch.used[seedIndex] == unionId)
                return; // already part of this terrain's union

            int stampId = NextMarkId();

            int head = 0;
            int tail = 0;

            _scratch.stamp[seedIndex] = stampId;
            _scratch.used[seedIndex] = unionId;
            _scratch.queue[tail++] = seedIndex;
            outCells.Add(seedIndex);

            maxCells = Mathf.Clamp(maxCells, 1, _cellCount);

            float growChance = Mathf.Clamp01(terrain.Blob.GrowChance);

            int requestedSmooth = terrain.Blob.SmoothPasses;
            // cap grows slowly with blob size, hard limit to prevent extreme cases from causing perf issues, 16 is a randomly choosen number, but should be diminishing returns after 8~ish passes
            // (maxCells ~ 100 -> cap ~ 1), (maxCells ~ 1,600 -> cap ~ 2), (maxCells ~ 6,400 -> cap ~ 4), (maxCells ~ 25,600 -> cap ~ 8), (maxCells ~ 102,400 -> cap ~ 16)
            int smoothCapRaw = Mathf.CeilToInt(Mathf.Sqrt(maxCells) * 0.05f);
            int smoothCap = Mathf.Clamp(smoothCapRaw, 0, 16);
            int smoothPasses = Mathf.Clamp(requestedSmooth, 0, smoothCap);

            int w = _width;
            int h = _height;

            // BFS-like growth
            while (head < tail && outCells.Count < maxCells)
            {
                int current = _scratch.queue[head++];

                int y = current / w;
                int x = current - (y * w);
                                
                if (x > 0)      // Left    
                {
                    TryGrow(current - 1);
                    if (outCells.Count >= maxCells) break;
                }            
                if (x + 1 < w)  // Right  
                {
                    TryGrow(current + 1);
                    if (outCells.Count >= maxCells) break;
                }              
                if (y > 0)      // Down   
                {
                    TryGrow(current - w);
                    if (outCells.Count >= maxCells) break;
                }             
                if (y + 1 < h)  // Up
                {
                    TryGrow(current + w);
                    if (outCells.Count >= maxCells) break;
                }
            }

            bool TryGrow(int next)
            {
                if (outCells.Count >= maxCells) return false;       // hard stop
                if (_scratch.stamp[next] == stampId) return false;  // allready in this blob
                if (_scratch.used[next] == unionId) return false;   // already part of a previous blob of same terrain
                if (!CanUseCell(terrain, next)) return false;
                if (_rng.NextDouble() > growChance) return false;

                _scratch.stamp[next] = stampId;
                _scratch.used[next] = unionId;
                _scratch.queue[tail++] = next;
                outCells.Add(next);
                return true;
            }

            // Smoothing passes to fill in small gaps
            for (int pass = 0; pass < smoothPasses; pass++)
            {
                if (outCells.Count >= maxCells) break;

                int beforeCount = outCells.Count;
                for (int i = 0; i < beforeCount; i++) _scratch.queue[i] = outCells[i];

                for (int i = 0; i < beforeCount && outCells.Count < maxCells; i++)
                {
                    int current = _scratch.queue[i];
                    int y = current / w;
                    int x = current - (y * w);
                                        
                    if (x > 0)     TrySmooth(current - 1);  // Left                    
                    if (x + 1 < w) TrySmooth(current + 1);  // Right                    
                    if (y > 0)     TrySmooth(current - w);  // Down                    
                    if (y + 1 < h) TrySmooth(current + w);  // Up
                }

                // If this pass added nothing, further passes probably won't help
                if (outCells.Count == beforeCount) break;
            }

            void TrySmooth(int next)
            {
                if (outCells.Count >= maxCells) return;
                if (_scratch.stamp[next] == stampId) return;
                if (_scratch.used[next] == unionId) return;
                if (!CanUseCell(terrain, next)) return;

                _scratch.stamp[next] = stampId;
                _scratch.used[next] = unionId;
                outCells.Add(next);
            }
        }



    }


}
