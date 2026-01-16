using System;
using System.Collections.Generic;
using UnityEngine;

namespace AI_Workshop03
{
    public static class TerrainOrderUtility
    {

        // Constants: Early/Late placement bias weights
        private const double MinEarlyWeight = 0.1;   // bias=0 still has a chance
        private const double MaxEarlyWeight = 10.0;  // bias=1 strongly favored



        // Terrain ordering / ID assignment helpers

        /// <summary>
        /// Preserves Order as "priority buckets": sorts by Order, then weighted-shuffles each equal-Order run.
        /// </summary>
        public static void ShuffleWithinOrderBucketsByEarlyBias(
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


        /// <summary>
        /// Weighted random permutation (without replacement) on a subrange.
        /// Uses Efraimidis–Spirakis style keys: key = -ln(U)/w, sort ascending.
        /// Internal implementation details used by ShuffleWithinOrderBucketsByEarlyBias.
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


        private static double BiasToWeight(float bias01)
        {
            bias01 = Mathf.Clamp01(bias01);

            // Geometric lerp: min * (max/min)^t
            // makes 0.5 actually be the midpoint between min and max in multiplicative scale
            double ratio = MaxEarlyWeight / MinEarlyWeight;
            return MinEarlyWeight * Math.Pow(ratio, bias01);
        }



    }

}
