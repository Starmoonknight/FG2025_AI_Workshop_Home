using System.Text;
using UnityEngine;



namespace AI_Workshop03
{

    // MapDataGenerator.Debug.cs         -   Purpose: debugging dump tools 
    public sealed partial class MapDataGenerator
    {

        private void DebugDumpFocusWeights(int seed, TerrainTypeData[] terrainData)
        {
            if (!Debug_DumpFocusWeights) return;

            var stringBuilder = new StringBuilder(2048);
            stringBuilder.AppendLine($"[MapGen Focus Weights] seed={seed} size={_width}x{_height} terrains={terrainData?.Length ?? 0}");

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
                        AppendFocus(stringBuilder, "Static.Placement", t.Static.PlacementArea, in t.Static.PlacementWeights);
                        break;

                    case PlacementMode.Blob:
                        AppendFocus(stringBuilder, "Blob.Placement", t.Blob.PlacementArea, in t.Blob.PlacementWeights);
                        break;

                    case PlacementMode.Lichtenberg:
                        AppendFocus(stringBuilder, "Lichtenberg.Origin", t.Lichtenberg.OriginArea, in t.Lichtenberg.OriginWeights);
                        AppendFocus(stringBuilder, "Lichtenberg.GrowthAim", t.Lichtenberg.GrowthAimArea, in t.Lichtenberg.GrowthAimWeights);

                        if (Debug_DumpFocusWeightsVerbose)
                        {
                            stringBuilder.AppendLine($"    EdgePresets: Use={t.Lichtenberg.UseEdgePairPresets} Mode={t.Lichtenberg.EdgePairMode}");
                            stringBuilder.AppendLine($"    Paths: [{t.Lichtenberg.MinPathCount}..{t.Lichtenberg.MaxPathCount}] Cells/Path={t.Lichtenberg.CellsPerPath}");
                        }
                        break;
                }
            }

            Debug.Log(stringBuilder.ToString());
        }


        private void AppendFocus(
            StringBuilder stringBuilder,
            string label,
            ExpansionAreaFocus focus,
            in TerrainTypeData.AreaFocusWeights weights)
        {

            // Interior margin is only relevant for Interior or Weighted (since Weighted may choose Interior)
            int marginCells = ComputeInteriorMarginCells(in weights);

            int innerW = Mathf.Max(0, _width - 2 * marginCells);
            int innerH = Mathf.Max(0, _height - 2 * marginCells);
            int interiorCellCount = innerW * innerH;

            stringBuilder.AppendLine($"    {label}: focus={focus}");

            if (focus == ExpansionAreaFocus.Weighted)
            {
                float edgeWeights = Mathf.Max(0f, weights.EdgeWeight);
                float interiorWeights = Mathf.Max(0f, weights.InteriorWeight);
                float anywhereWeight = Mathf.Max(0f, weights.AnywhereWeight);
                float total = edgeWeights + interiorWeights + anywhereWeight;

                if (total <= 0f)
                {
                    stringBuilder.AppendLine("      weights: (all <= 0) -> effective: Anywhere=100%");
                }
                else
                {
                    float pE = edgeWeights / total;
                    float pI = interiorWeights / total;
                    float pA = anywhereWeight / total;

                    stringBuilder.AppendLine(
                        $"      raw: Edge={edgeWeights:0.###} Interior={interiorWeights:0.###} Anywhere={anywhereWeight:0.###}  (sum={total:0.###})");
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
            else if (Debug_DumpFocusWeightsVerbose)
            {
                // Useful when tuning: still show what the weights *are*, even if not used in non-weighted modes.
                stringBuilder.AppendLine(
                    $"      (weights ignored unless focus=Weighted) raw Edge={weights.EdgeWeight:0.###} Interior={weights.InteriorWeight:0.###} Anywhere={weights.AnywhereWeight:0.###}");
            }
        }



    }


}
