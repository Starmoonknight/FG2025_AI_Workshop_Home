using System;
using System.Collections.Generic;
using UnityEngine;


namespace AI_Workshop03
{

    public sealed class GenScratch
    {
        public int[] heat;      // temp int storage
        public int[] poolPos;   // temp int storage
        public int[] queue;     // buffer
        public int stampId;

        public int[] stamp;     // stamp based marker
        public int[] used;      // stamp based marker
        public readonly List<int> cells = new(4096);
        public readonly List<int> temp = new(2048);     // buffer
    }


    // MapDataGenerator.Core.cs         -   Purpose: engine room / brain + orchestration of generation: (entry point + shared state + terrain ordering + dispatch)
    public sealed partial class MapDataGenerator
    {

        //Generator fields
        private readonly GenScratch _scratch = new();
        private int _width;
        private int _height;
        private int _cellCount;
        private int _blockedCount;
        private int _maxBlockedBudget;

        // Board array references
        private bool[] _blocked;
        private int[] _terrainCost;
        private Color32[] _baseColors;
        private byte[] _terrainKey;
        private byte[] _lastPaintLayerId;

        private Color32 _baseWalkableColor;
        private int _baseWalkableCost;

        // Rng instances
        private System.Random _rng;
        private System.Random _rngOrder;

        // Constants: Early/Late placement bias weights
        private const double MinEarlyWeight = 0.1;   // bias=0 still has a chance
        private const double MaxEarlyWeight = 10.0;  // bias=1 strongly favored

        // Neighbor direction offsets
        private static readonly (int dirX, int dirY)[] Neighbors4 =
        {
            (-1, 0), ( 1, 0), (0, -1), (0,  1)
        };

        private static readonly (int dirX, int dirY)[] Neighbors8 =
        {
            (-1, 0), ( 1, 0), (0, -1), (0,  1),
            (-1,-1), (-1, 1), (1, -1), (1,  1)
        };

        // Helper: get opposite side index
        private static int OppositeSide(int side) => side ^ 1; // 0<->1, 2<->3



        #region Terrain ordering / ID assignment helpers

        private static double BiasToWeight(float bias01)
        {
            bias01 = Mathf.Clamp01(bias01);

            // Geometric lerp: min * (max/min)^t
            // makes 0.5 actually be the midpoint between min and max in multiplicative scale
            double ratio = MaxEarlyWeight / MinEarlyWeight;
            return MinEarlyWeight * Math.Pow(ratio, bias01);
        }


        /// <summary>
        /// Weighted random permutation (without replacement) on a subrange.
        /// Uses Efraimidis–Spirakis style keys: key = -ln(U)/w, sort ascending.
        /// </summary>
        private static void WeightedShuffleRangeByEarlyBias(
            List<TerrainTypeData> list,
            int start,
            int count,
            System.Random rngOrder)
        {

            if (count <= 1) return;

            var keys = new double[count];
            var items = new TerrainTypeData[count];

            for (int k = 0; k < count; k++)
            {
                TerrainTypeData terrain = list[start + k];
                items[k] = terrain;

                float bias = (terrain != null) ? terrain.EarlyPlacementBias : 0f;
                double w = BiasToWeight(bias);
                if (w <= 0) w = 1e-9;

                double u = 1.0 - rngOrder.NextDouble(); // (0,1]
                keys[k] = -Math.Log(u) / w; // smaller key => earlier
            }

            Array.Sort(keys, items);        // ascending
            for (int k = 0; k < count; k++)
                list[start + k] = items[k];
        }


        /// <summary>
        /// Preserves Order as "priority buckets": sorts by Order, then weighted-shuffles each equal-Order run.
        /// </summary>
        private static void ShuffleWithinOrderBucketsByEarlyBias(
            List<TerrainTypeData> list,
            System.Random rngOrder
            )
        {
            // first sort by order
            list.Sort((a, b) => (a?.Order ?? 0).CompareTo(b?.Order ?? 0));

            // then shuffle each bucket by weighted early bias 
            int bucketStart = 0;
            while (bucketStart < list.Count)
            {
                int bucketOrder = list[bucketStart]?.Order ?? 0;
                int bucketEndExclusive = bucketStart + 1;

                while (bucketEndExclusive < list.Count && ((list[bucketEndExclusive]?.Order ?? 0) == bucketOrder))
                    bucketEndExclusive++;

                int bucketCount = bucketEndExclusive - bucketStart;
                WeightedShuffleRangeByEarlyBias(list, bucketStart, bucketCount, rngOrder);

                bucketStart = bucketEndExclusive;
            }
        }


        #endregion



        #region Terrain Dispatch - (switches between Static/Blob/Lichtenberg)

        private void ApplyTerrainData(TerrainTypeData terrain, byte terrainLayerId, bool isObstacle)
        {
            _scratch.cells.Clear();

            switch (terrain.Mode)                                                              // what "paint brush" is used to generate this tiles structure
            {
                case PlacementMode.Static:
                    ExpandRandomStatic(terrain, _scratch.cells);
                    break;

                case PlacementMode.Blob:
                    GenerateBlobs(terrain, _scratch.cells);
                    break;

                case PlacementMode.Lichtenberg:
                    GenerateLichtenberg(terrain, terrainLayerId, _scratch.cells);
                    break;
            }

            if (_scratch.cells.Count == 0) return;

            if (isObstacle)
                ApplyObstacles(terrain, terrainLayerId, _scratch.cells);
            else
                ApplyTerrain(terrain, terrainLayerId, _scratch.cells);
        }


        #endregion



        #region Overwrite / Apply Cell Data

        private void ApplyTerrain(TerrainTypeData terrain, byte terrainLayerId, List<int> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                int index = cells[i];
                if (!IsValidCell(index)) continue;
                if (!CanUseCell(terrain, index)) continue;

                if (_blocked[index] && terrain.AllowOverwriteObstacle)
                    _blocked[index] = false;

                _terrainKey[index] = (byte)terrain.TerrainID;
                _terrainCost[index] = terrain.Cost;
                _baseColors[index] = terrain.Color;
                _lastPaintLayerId[index] = terrainLayerId;
            }
        }

        private void ApplyObstacles(TerrainTypeData terrain, byte terrainLayerId, List<int> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (_blockedCount >= _maxBlockedBudget)
                    break; // reached max blocked budget, early out stop

                int index = cells[i];
                if (!IsValidCell(index)) continue;
                if (!CanUseCell(terrain, index)) continue;

                if (_blocked[index])
                    continue; // already blocked

                _blocked[index] = true;
                _blockedCount++;

                _terrainKey[index] = (byte)terrain.TerrainID;
                _terrainCost[index] = 0;
                _lastPaintLayerId[index] = terrainLayerId;
                _baseColors[index] = terrain.Color;
            }
        }

        #endregion



        #region Internal Suport, Coordinates and Stamp data 

        private void EnsureGenBuffers()
        {
            if (_scratch.queue == null || _scratch.queue.Length != _cellCount)
                _scratch.queue = new int[_cellCount];

            if (_scratch.stamp == null || _scratch.stamp.Length != _cellCount)
                _scratch.stamp = new int[_cellCount];

            if (_scratch.used == null || _scratch.used.Length != _cellCount)
                _scratch.used = new int[_cellCount];

            if (_scratch.heat == null || _scratch.heat.Length != _cellCount)
                _scratch.heat = new int[_cellCount];

            if (_scratch.poolPos == null || _scratch.poolPos.Length != _cellCount)
                _scratch.poolPos = new int[_cellCount];
        }

        private int NextMarkId()
        {
            int next = _scratch.stampId + 1;
            if (next <= 0 || next == int.MaxValue)
            {
                Array.Clear(_scratch.stamp, 0, _scratch.stamp.Length);
                Array.Clear(_scratch.used, 0, _scratch.used.Length);

                _scratch.stampId = 1;
                return 1;
            }

            // restart at 1 (0 means unmarked)
            _scratch.stampId = next;
            return next;
        }


        private int CoordToIndex(int x, int y) => x + y * _width;

        private bool TryCoordToIndex(int x, int y, out int index)
        {
            if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
            {
                index = -1;
                return false;
            }
            index = x + y * _width;
            return true;
        }

        private void IndexToXY(int index, out int x, out int y)
        {
            x = index % _width;
            y = index / _width;
        }

        private Vector3 IndexToWorldCenterXZ(int index, float y = 0f)
        {
            IndexToXY(index, out int x, out int z);
            return new Vector3(x + 0.5f, y, z + 0.5f);
        }


        private int ComputeInteriorMarginCells()
        {
            // 5% of min dimension, at least 2 cells
            return Mathf.Max(2, Mathf.RoundToInt(Mathf.Min(_width, _height) * 0.05f));
        }

        private int ComputeInteriorMarginCells(in TerrainTypeData.AreaFocusWeights weights)
        {
            float percent = Mathf.Clamp(weights.InteriorMarginPercent, 0f, 0.49f);
            int minDim = Mathf.Min(_width, _height);

            // ensures _rng.Next(low, high) has low < high.
            int maxMargin = Mathf.Max(0, (minDim - 1) / 2);

            int marginByPercent = Mathf.RoundToInt(minDim * percent);
            int margin = Mathf.Max(Mathf.Max(0, weights.InteriorMinMargin), marginByPercent);

            return Mathf.Clamp(margin, 0, maxMargin);
        }

        private int ComputeEdgeBandCells(in TerrainTypeData.AreaFocusWeights weights)
        {
            int minDim = Mathf.Min(_width, _height);
            int maxBand = Mathf.Max(0, (minDim - 1) / 2);

            int band = ComputeInteriorMarginCells(in weights);
            return Mathf.Clamp(band, 0, maxBand);
        }


        private int HeuristicManhattan(int a, int b)
        {
            IndexToXY(a, out int ax, out int ay);
            IndexToXY(b, out int bx, out int by);
            return Math.Abs(ax - bx) + Math.Abs(ay - by);
        }


        #endregion




    }


}
