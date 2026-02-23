using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;



namespace AI_Workshop03
{


    public enum MapGenFailGate
    {
        None,
        StartBlocked,
        UnblockedTooLow,
        ReachabilityTooLow,
        TerrainStarvation,
        Exception
    }

    public enum MapGenLogVerbosity 
    { 
        Off = 0, 
        Summary = 1, 
        Verbose = 2 
    } 

    // from suggestion 1
    public struct MapGenAttemptTelemetry
    {
        public int attemptIndex;
        public bool success;
        public MapGenFailGate gate;

        // Absolute counts
        public int walkableCount;
        public int unblockedCount;
        public int reachedCount;
        public int reachableTargetCount;

        // Optional derived ratios (set by caller if useful)
        public float unblocked01;
        public float reachable01;

        // Diagnostics
        public int resets;
        public int placementsTried;
        public int anomalies;
        public int checkpoint;

        // Filled by reporter at EndAttempt
        public long elapsedMs;
    }

    public struct MapGenRunTelemetry
    {
        public int baseSeed, genSeed, orderSeed;
        public int width, height, terrainCount;

        public int attemptsUsed, maxAttempts;
        public long elapsedMs;
        public bool success;
    }


    /// <summary>
    /// Ownership policy:
    /// - Run start/end + attempt outcomes: reporter owns logs.
    /// - Reachability returns metrics to caller; it should not log directly.
    /// - Renderer alignment: warnings only on mismatch/change.
    /// - Focus weights: emitted from ONE call-site only.
    /// </summary>
    public sealed class MapGenDebugReporter
    {
        private readonly MapGenLogVerbosity _verbosity;
        private readonly bool _dumpAnomaliesOnSuccess;
        private readonly int _anomalyCapacity;

        private readonly string[] _anomalies;
        private int _anomalySeenCount;

        //private readonly List<MapGenAttemptTelemetry> _attempts = new(32);        // not needed? 
        private readonly Dictionary<MapGenFailGate, int> _gateCounts = new Dictionary<MapGenFailGate, int>(8);
        private readonly Stopwatch _runSw = new();
        private readonly Stopwatch _attemptSw = new();

        private int _seed, _genSeed, _orderSeed;
        private int _width, _height, _terrainCount;
        private int _failedAttempts;
        private int _attemptsTotal;
        private int _fallbacksTotal;
        private bool _runStarted;

        private bool IsSummary => _verbosity >= MapGenLogVerbosity.Summary;
        private bool IsVerbose => _verbosity >= MapGenLogVerbosity.Verbose;


        public MapGenDebugReporter(
            MapGenLogVerbosity verbosity = MapGenLogVerbosity.Summary,
            int anomalyCapacity = 24,
            bool dumpAnomaliesOnSuccess = false)
        {
            _verbosity = verbosity;
            _dumpAnomaliesOnSuccess = dumpAnomaliesOnSuccess;
            _anomalyCapacity = Mathf.Max(1, anomalyCapacity);
            _anomalies = new string[_anomalyCapacity];
        }



        [Conditional("MAPGEN_TRACE")]
        public static void Trace(string msg) => Debug.Log(msg);


        //[Conditional("MAPGEN_TRACE")]
        public void BeginRun(int seed, int genSeed, int orderSeed, int width, int height, int terrainCount)
        {
            if (_verbosity == MapGenLogVerbosity.Off) return;

            ResetRunState();
            _runStarted = true;

            _seed = seed;
            _genSeed = genSeed;
            _orderSeed = orderSeed;
            _width = width;
            _height = height;
            _terrainCount = terrainCount;

            _runSw.Start();

            LogSummary(
                $"[MapGen][RunStart] seed={_seed} genSeed={_genSeed} orderSeed={_orderSeed} " +
                $"size={_width}x{_height} terrains={_terrainCount}");
        }


        //[Conditional("MAPGEN_TRACE")]
        public void BeginAttempt(int attemptIndex)
        {
            if (_verbosity == MapGenLogVerbosity.Off) return;
            if (!_runStarted) return;

            _attemptSw.Restart();
            _attemptsTotal = Mathf.Max(_attemptsTotal, attemptIndex + 1);
        }


        //[Conditional("MAPGEN_TRACE")]
        private void RecordAttempt(in MapGenAttemptTelemetry t)
        {
            _attemptsTotal = Mathf.Max(_attemptsTotal, t.attemptIndex + 1);
            
            if (t.success) return;

            _failedAttempts++;
            _gateCounts[t.gate] = _gateCounts.TryGetValue(t.gate, out var count) ? count + 1 : 1;

            if (t.gate == MapGenFailGate.None)
                RecordAnomaly($"Attempt {t.attemptIndex} failed but gate=None");
        }


        //[Conditional("MAPGEN_TRACE")]
        public void EndAttempt(ref MapGenAttemptTelemetry t)
        {
            if (!IsSummary || !_runStarted) return;

            _attemptSw.Stop();
            t.elapsedMs = _attemptSw.ElapsedMilliseconds;

            RecordAttempt(in t);

            // Verbose: one line per attempt.
            // Summary: keep output compact; only failed attempts get explicit lines.
            if (IsVerbose || !t.success)
            {
                LogSummary(
                    $"[MapGen][Attempt] idx={t.attemptIndex} ok={t.success} gate={t.gate} ms={t.elapsedMs} " +
                    $"walkable={t.walkableCount} unblocked={t.unblockedCount} reached={t.reachedCount}/{t.reachableTargetCount} " +
                    $"u01={t.unblocked01:0.###} r01={t.reachable01:0.###} " +
                    $"resets={t.resets} placed={t.placementsTried} anomalies={t.anomalies} cp={t.checkpoint}");
            }
        }


        //[Conditional("MAPGEN_TRACE")]
        public void MarkFallbackUsed()
        {
            if (_verbosity == MapGenLogVerbosity.Off) return;

            _fallbacksTotal++;
        }


        //[Conditional("MAPGEN_TRACE")]
        public void RecordAnomaly(string message)
        {
            if (_verbosity == MapGenLogVerbosity.Off) return;
            if (string.IsNullOrEmpty(message)) return;

            // Ring behavior: keep the most recent N
            int slot = _anomalySeenCount % _anomalyCapacity;
            _anomalies[slot] = message;
            _anomalySeenCount++;
        }


        //[Conditional("MAPGEN_TRACE")]
        public void EndRun(bool success, Exception ex = null)
        {
            if (ex != null)
                Debug.LogError($"[MapGen][Exception] {ex.GetType().Name}: {ex.Message}");

            if (!IsSummary || !_runStarted) { _runStarted = false; return; }

            _runSw.Stop();
            LogSummary(
                $"[MapGen][RunEnd] ok={success} ms={_runSw.ElapsedMilliseconds} attempts={_attemptsTotal} failed={_failedAttempts} fallbacks={_fallbacksTotal}");

            // Gate summary
            if (_gateCounts.Count > 0)
            {
                // Build compact fail gate histogram only once
                var parts = new List<string>(_gateCounts.Count);
                int gateSum = 0;
                foreach (var kv in _gateCounts)
                {
                    gateSum += kv.Value;
                    parts.Add($"{kv.Key}={kv.Value}");
                }

                LogSummary($"[MapGen][FailBreakdown] {string.Join(", ", parts)}");

                if (IsVerbose && gateSum != _failedAttempts)
                    Debug.LogWarning($"[MapGen][FailBreakdownMismatch] failed={_failedAttempts} gateSum={gateSum}");
            }

            bool dumpAnomalies = !success || _dumpAnomaliesOnSuccess || IsVerbose;
            if (dumpAnomalies)
                DumpAnomalyBuffer();

            _runStarted = false;
        }


        //[Conditional("MAPGEN_TRACE")]
        /// <summary>
        /// Keep this warning-only unless verbose is enabled.
        /// </summary>
        public void WarnIfLayoutMismatch(
            Vector3 minWorld, Vector3 maxWorld,
            Vector3 rendererPos, Vector3 gridCenter,
            int expectedX, int expectedZ,
            int reportedX, int reportedZ,
            bool autoFlipX, bool autoFlipY)
        {
            if (_verbosity == MapGenLogVerbosity.Off) return;

            bool sizeMismatch = expectedX != reportedX || expectedZ != reportedZ;
            bool centerMismatch = Vector3.Distance(rendererPos, gridCenter) > 0.01f;

            if (sizeMismatch || centerMismatch)
            {
                Debug.LogWarning(
                    $"[MapGen][LayoutMismatch] expected={expectedX}x{expectedZ} reported={reportedX}x{reportedZ} " +
                    $"rendererPos={rendererPos} gridCenter={gridCenter} min={minWorld} max={maxWorld} " +
                    $"autoFlip=({autoFlipX},{autoFlipY})");
            }
            else if (_verbosity >= MapGenLogVerbosity.Verbose)
            {
                LogSummary(
                    $"[MapGen][LayoutOK] expected={expectedX}x{expectedZ} reported={reportedX}x{reportedZ} " +
                    $"rendererPos={rendererPos} gridCenter={gridCenter}");
            }
        }


        //[Conditional("MAPGEN_TRACE")]
        /// <summary>
        /// Summary mode: compact one-line-per-terrain.
        /// Verbose mode: expanded section details.
        /// Call this from ONE place only.
        /// </summary>
        public void DumpFocusWeights(
            int seed,
            int width,
            int height,
            TerrainTypeData[] terrainData,
            Func<TerrainTypeData.AreaFocusWeights, int> computeInteriorMarginCells,
            int totalAttemptedBuilds = -1,  // deafault -1 for not provided
            int totalFallbackBuilds = -1)   // deafault -1 for not provided
        {
            if (_verbosity == MapGenLogVerbosity.Off) return;

            if (computeInteriorMarginCells == null)
            {
                Debug.LogWarning("[MapGen][Focus] computeInteriorMarginCells was null; using margin=0.");
                computeInteriorMarginCells = _ => 0;
            }

            var stringBuilder = new StringBuilder(2048);
            bool verbose = _verbosity >= MapGenLogVerbosity.Verbose;

            stringBuilder.AppendLine($"[MapGen][Focus] seed={seed} size={width}x{height} terrains={terrainData?.Length ?? 0}");

            if (totalAttemptedBuilds >= 0 || totalFallbackBuilds >= 0)
            {
                stringBuilder.AppendLine(
                   $"  buildStats attemptsTotal={(totalAttemptedBuilds >= 0 ? totalAttemptedBuilds.ToString() : "n/a")} " +
                   $"fallbacksTotal={(totalFallbackBuilds >= 0 ? totalFallbackBuilds.ToString() : "n/a")}");
            }

            if (terrainData == null || terrainData.Length == 0)
            {
                stringBuilder.AppendLine("  (no terrain data)");
                LogSummary(stringBuilder.ToString());
                return;
            }

            for (int i = 0; i < terrainData.Length; i++)
            {
                TerrainTypeData t = terrainData[i];
                if (t == null) continue;

                ExpansionAreaFocus focus = GetPrimaryFocus(t);
                stringBuilder.AppendLine($"- {t.name} mode={t.Mode} coverage={t.CoveragePercent:0.###} obstacle={t.IsObstacle} focus={focus}");

                // Summary bonus: weighted quick-normalized info
                if (focus == ExpansionAreaFocus.Weighted)
                {
                    TerrainTypeData.AreaFocusWeights w = GetPrimaryWeights(t);
                    NormalizeWeights(w, out float pE, out float pI, out float pA, out float total);
                    int margin = computeInteriorMarginCells(w);
                    int innerW = Mathf.Max(0, width - 2 * margin);
                    int innerH = Mathf.Max(0, height - 2 * margin);

                    if (total <= 0f)
                        stringBuilder.Append(" norm(E/I/A)=0/0/100 interior=fallback-anywhere");
                    else
                        stringBuilder.Append($" norm(E/I/A)={(pE * 100f):0.#}/{(pI * 100f):0.#}/{(pA * 100f):0.#} interior={innerW}x{innerH}");
                }

                stringBuilder.AppendLine();

                if (verbose)
                {
                    switch (t.Mode)
                    {
                    case PlacementMode.Static:
                            AppendFocusVerbose(stringBuilder, "Static.Placement", t.Static.PlacementArea, in t.Static.PlacementWeights, width, height, computeInteriorMarginCells);
                            break;

                    case PlacementMode.Blob:
                            AppendFocusVerbose(stringBuilder, "Blob.Placement", t.Blob.PlacementArea, in t.Blob.PlacementWeights, width, height, computeInteriorMarginCells);
                            break;

                    case PlacementMode.Lichtenberg:
                            AppendFocusVerbose(stringBuilder, "Lichtenberg.Origin", t.Lichtenberg.OriginArea, in t.Lichtenberg.OriginWeights, width, height, computeInteriorMarginCells);
                            AppendFocusVerbose(stringBuilder, "Lichtenberg.GrowthAim", t.Lichtenberg.GrowthAimArea, in t.Lichtenberg.GrowthAimWeights, width, height, computeInteriorMarginCells);
                            stringBuilder.AppendLine($"    EdgePresets use={t.Lichtenberg.UseEdgePairPresets} mode={t.Lichtenberg.EdgePairMode}");
                            stringBuilder.AppendLine($"    Paths [{t.Lichtenberg.MinPathCount}..{t.Lichtenberg.MaxPathCount}] cellsPerPath={t.Lichtenberg.CellsPerPath}");
                            break;
                    }
                }
            }

            LogSummary(stringBuilder.ToString());
        }


        private static ExpansionAreaFocus GetPrimaryFocus(TerrainTypeData t)
        {
            switch (t.Mode)
            {
                case PlacementMode.Static: return t.Static.PlacementArea;
                case PlacementMode.Blob: return t.Blob.PlacementArea;
                case PlacementMode.Lichtenberg: return t.Lichtenberg.OriginArea;
                default: return ExpansionAreaFocus.Anywhere;
            }
        }


        private static TerrainTypeData.AreaFocusWeights GetPrimaryWeights(TerrainTypeData t)
        {
            switch (t.Mode)
            {
                case PlacementMode.Static: return t.Static.PlacementWeights;
                case PlacementMode.Blob: return t.Blob.PlacementWeights;
                case PlacementMode.Lichtenberg: return t.Lichtenberg.OriginWeights;
                default: return default;
            }
        }


        private static void NormalizeWeights(
            in TerrainTypeData.AreaFocusWeights w,
            out float pEdge, out float pInterior, out float pAnywhere, out float total)
        {
            float e = Mathf.Max(0f, w.EdgeWeight);
            float i = Mathf.Max(0f, w.InteriorWeight);
            float a = Mathf.Max(0f, w.AnywhereWeight);
            total = e + i + a;

            if (total <= 0f)
            {
                pEdge = 0f;
                pInterior = 0f;
                pAnywhere = 1f;
                return;
            }

            pEdge = e / total;
            pInterior = i / total;
            pAnywhere = a / total;
        }


        private static void AppendFocusVerbose(
            StringBuilder stringBuilder,
            string label,
            ExpansionAreaFocus focus,
            in TerrainTypeData.AreaFocusWeights weights,
            int width,
            int height,
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
                NormalizeWeights(weights, out float pE, out float pI, out float pA, out float total);

                if (total <= 0f)
                {
                    stringBuilder.AppendLine("      raw: all<=0 -> fallback Anywhere=100%");
                }
                else
                {
                    stringBuilder.AppendLine(
                        $"      raw: Edge={Mathf.Max(0f, weights.EdgeWeight):0.###} " +
                        $"Interior={Mathf.Max(0f, weights.InteriorWeight):0.###} " +
                        $"Anywhere={Mathf.Max(0f, weights.AnywhereWeight):0.###} (sum={total:0.###})");
                    stringBuilder.AppendLine($"      normalized: Edge={pE:P1} Interior={pI:P1} Anywhere={pA:P1}");
                }

                stringBuilder.AppendLine($"      interior margin={marginCells} -> {innerW}x{innerH} ({interiorCellCount} cells)");
            }
            else if (focus == ExpansionAreaFocus.Interior)
            {
                stringBuilder.AppendLine($"      interior margin={marginCells} -> {innerW}x{innerH} ({interiorCellCount} cells)");
            }
            else
            {
                stringBuilder.AppendLine(
                    $"      (weights ignored unless Weighted) raw Edge={weights.EdgeWeight:0.###} " +
                    $"Interior={weights.InteriorWeight:0.###} Anywhere={weights.AnywhereWeight:0.###}");
            }
        }


        //[Conditional("MAPGEN_TRACE")]
        private void DumpAnomalyBuffer()
        {
            int stored = Math.Min(_anomalySeenCount, _anomalyCapacity);
            if (stored <= 0) return;

            LogSummary($"[MapGen][Anomalies] stored={stored} totalSeen={_anomalySeenCount}");

            // Print oldest -> newest in ring order
            int start = (_anomalySeenCount >= _anomalyCapacity) ? (_anomalySeenCount % _anomalyCapacity) : 0;
            for (int i = 0; i < stored; i++)
            {
                int idx = (start + i) % _anomalyCapacity;
                Debug.LogWarning($"[MapGen][Anomaly {i}] {_anomalies[idx]}");
            }
        }


        private void LogSummary(string msg)
        {
            if (_verbosity >= MapGenLogVerbosity.Summary)
                Debug.Log(msg);
        }


        private void ResetRunState()
        {
            //_attempts.Clear();
            _gateCounts.Clear();
            _failedAttempts = 0;    
            _attemptsTotal = 0;
            _fallbacksTotal = 0;
            _anomalySeenCount = 0;
            _runSw.Reset();
            _attemptSw.Reset();
        }




    }

}

       
            




        // ----------------------------------------------------------------------------------------------
        // REMOVE OR KEEP PARTS OF BELOWE CODE? 





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