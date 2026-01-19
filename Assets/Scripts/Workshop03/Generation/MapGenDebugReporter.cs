using System;
using System.Text;
using UnityEngine;



namespace AI_Workshop03
{


    public sealed class MapGenDebugReporter
    {

        public void DumpFocusWeights(
            int seed,
            int width,
            int height,
            TerrainTypeData[] terrainData,
            bool verbose,
            Func<TerrainTypeData.AreaFocusWeights, int> computeInteriorMarginCells,
            int totalAttemptedBuilds = -1,  // deafault -1 for not provided
            int totalFallbackBuilds = -1)   // deafault -1 for not provided
        {

            var stringBuilder = new StringBuilder(2048);
            stringBuilder.AppendLine($"[MapGen Focus Weights] seed={seed} size={width}x{height} terrains={terrainData?.Length ?? 0}");

            if (totalAttemptedBuilds >= 0 || totalFallbackBuilds >= 0)
            {
                stringBuilder.AppendLine(
                    $"  buildStats: attemptsTotal={(totalAttemptedBuilds >= 0 ? totalAttemptedBuilds.ToString() : "n/a")} " +
                    $"fallbacksTotal={(totalFallbackBuilds >= 0 ? totalFallbackBuilds.ToString() : "n/a")}"
                );
            }

            if (terrainData == null || terrainData.Length == 0)
            {
                stringBuilder.AppendLine("  (no terrain data)");
                Debug.Log(stringBuilder.ToString());
                return;
            }

            for (int i = 0; i < terrainData.Length; i++)
            {
                var t = terrainData[i];
                if (t == null) continue;

                stringBuilder.AppendLine($"- {t.name}  Mode={t.Mode}  Coverage={t.CoveragePercent:0.###}  Obstacle={t.IsObstacle}");

                switch (t.Mode)
                {
                    case PlacementMode.Static:
                        AppendFocus(stringBuilder, "Static.Placement", t.Static.PlacementArea, in t.Static.PlacementWeights,
                            width, height, verbose, computeInteriorMarginCells);
                        break;

                    case PlacementMode.Blob:
                        AppendFocus(stringBuilder, "Blob.Placement", t.Blob.PlacementArea, in t.Blob.PlacementWeights,
                            width, height, verbose, computeInteriorMarginCells);
                        break;

                    case PlacementMode.Lichtenberg:
                        AppendFocus(stringBuilder, "Lichtenberg.Origin", t.Lichtenberg.OriginArea, in t.Lichtenberg.OriginWeights,
                            width, height, verbose, computeInteriorMarginCells);

                        AppendFocus(stringBuilder, "Lichtenberg.GrowthAim", t.Lichtenberg.GrowthAimArea, in t.Lichtenberg.GrowthAimWeights,
                            width, height, verbose, computeInteriorMarginCells);


                        if (verbose)
                        {
                            stringBuilder.AppendLine($"    EdgePresets: Use={t.Lichtenberg.UseEdgePairPresets} Mode={t.Lichtenberg.EdgePairMode}");
                            stringBuilder.AppendLine($"    Paths: [{t.Lichtenberg.MinPathCount}..{t.Lichtenberg.MaxPathCount}] Cells/Path={t.Lichtenberg.CellsPerPath}");
                        }
                        break;
                }
            }

            Debug.Log(stringBuilder.ToString());

        }



        private static void AppendFocus(
            StringBuilder stringBuilder,
            string label,
            ExpansionAreaFocus focus,
            in TerrainTypeData.AreaFocusWeights weights,
            int width,
            int height,
            bool verbose,
            Func<TerrainTypeData.AreaFocusWeights, int> computeInteriorMarginCells)
        {

            // Interior margin is only relevant for Interior or Weighted (since Weighted may choose Interior)
            int marginCells = computeInteriorMarginCells(weights);

            int innerW = Mathf.Max(0, width - 2 * marginCells);
            int innerH = Mathf.Max(0, height - 2 * marginCells);
            int interiorCellCount = innerW * innerH;

            stringBuilder.AppendLine($"    {label}: focus={focus}");

            if (focus == ExpansionAreaFocus.Weighted)
            {
                float edgeWeight     = Mathf.Max(0f, weights.EdgeWeight);
                float interiorWeights = Mathf.Max(0f, weights.InteriorWeight);
                float anywhereWeight  = Mathf.Max(0f, weights.AnywhereWeight);
                float total = edgeWeight + interiorWeights + anywhereWeight;

                if (total <= 0f)
                {
                    stringBuilder.AppendLine("      weights: (all <= 0) -> effective: Anywhere=100%");
                }
                else
                {
                    float pE = edgeWeight / total;
                    float pI = interiorWeights / total;
                    float pA = anywhereWeight / total;

                    stringBuilder.AppendLine(
                        $"      raw: Edge={edgeWeight:0.###} Interior={interiorWeights:0.###} Anywhere={anywhereWeight:0.###}  (sum={total:0.###})");
                    stringBuilder.AppendLine(
                        $"      normalized: Edge={pE:P1} Interior={pI:P1} Anywhere={pA:P1}");

                }

                stringBuilder.AppendLine(
                    $"      interior margin: {marginCells} cells  -> interior rect {innerW}x{innerH} ({interiorCellCount} cells)");
            }
            else if (focus == ExpansionAreaFocus.Interior)
            {
                stringBuilder.AppendLine(
                    $"      interior margin: {marginCells} cells  -> interior rect {innerW}x{innerH} ({interiorCellCount} cells)");
            }
            else if (verbose)
            {
                // Useful when tuning: still show what the weights *are*, even if not used in non-weighted modes.
                stringBuilder.AppendLine(
                    $"      (weights ignored unless focus=Weighted) raw Edge={weights.EdgeWeight:0.###} Interior={weights.InteriorWeight:0.###} Anywhere={weights.AnywhereWeight:0.###}");
            }
        }





    }


}

       
            





        /*

        public void DumpFocusWeights(
            int width,
            int height,
            float[] focusWeights,
            string filePath
            )
        {
            int cellCount = checked(width * height);
            if (focusWeights == null) throw new System.ArgumentNullException(nameof(focusWeights));
            if (focusWeights.Length != cellCount) throw new System.ArgumentException("focusWeights length mismatch", nameof(focusWeights));
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("X,Y,FocusWeight");
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = x + y * width;
                    sb.AppendLine($"{x},{y},{focusWeights[index]}");
                }
            }
            System.IO.File.WriteAllText(filePath, sb.ToString());
            Debug.Log($"Focus weights dumped to: {filePath}");
        }

        */