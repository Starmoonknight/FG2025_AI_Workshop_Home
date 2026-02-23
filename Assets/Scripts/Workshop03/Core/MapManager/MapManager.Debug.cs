using System.Collections.Generic;
using UnityEngine;


namespace AI_Workshop03
{

    // MapManager.Debug.cs        -   Purpose: seed HUD + cost overlay + editor context menus
    public partial class MapManager
    {

        #region Fields - Debug Options


        [Header("Debug: Seed HUD")]
        [SerializeField] private bool _showSeedHud = true;
        [SerializeField] private TMPro.TextMeshProUGUI _seedHudLabel;
        [SerializeField] private string _seedHudPrefix = "Seed: ";

        [Header("Debug: Generation")]
        [SerializeField] private bool _dumpFocusWeights = true;
        [SerializeField] private bool _dumpFocusWeightsVerbose = false;

        [Header("Debug: MapGen Reporter")]
        [SerializeField] private MapGenLogVerbosity _mapGenLogVerbosity = MapGenLogVerbosity.Summary;
        [SerializeField, Min(1)] private int _mapGenAnomalyCapacity = 24;
        [SerializeField] private bool _mapGenDumpAnomaliesOnSuccess = false;


        /*
        [Header("Debug: Generation Attempts")]
        [SerializeField] private bool _logGenAttempts = true;
        [SerializeField] private bool _logGenAttemptFailures = true;
        [SerializeField] private bool _logPerTerrainSummary = true;
        [SerializeField] private bool _logObstacleBudgetHits = true;
        */

        [Header("Debug: A* Costs Overlay")]
        [SerializeField] private bool _showDebugCosts = true;
        [SerializeField] private TMPro.TextMeshPro _costLabelPrefab;
        [SerializeField] private Transform _costLabelRoot;
        [SerializeField] private float _costLabelOffsetY = 0.05f;


        [Header("Debug: A* Costs Overlay Perf")]
        [SerializeField, Min(1)] private int _maxCostLabelUpdatesPerFrame = 300;
        [SerializeField] private bool _onlyUpdateCostTextWhenChanged = true;

        private int _costOverlayFrame = -1;
        private int _costOverlayUpdatesThisFrame = 0;

        private int[] _lastG;
        private int[] _lastH;
        private int[] _lastF;

        private TMPro.TextMeshPro[] _costLabels;
        private readonly List<int> _costLabelsTouched = new();


        #endregion


        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void UpdateSeedHud()
        {
            if (!_showSeedHud) return;
            if (_seedHudLabel == null) return;

            string mode = (_seed == 0) ? " (random)" : "";
            _seedHudLabel.text = $"{_seedHudPrefix}{_lastGeneratedSeed}{mode}";
        }


        private void EnsureCostOverlayBuffers(int n)
        {
            if (_costLabels == null || _costLabels.Length != n)
                _costLabels = new TMPro.TextMeshPro[n];

            if (_lastG == null || _lastG.Length != n)
            {
                _lastG = new int[n];
                _lastH = new int[n];
                _lastF = new int[n];

                for (int i = 0; i < n; i++)
                {
                    _lastG[i] = int.MinValue;
                    _lastH[i] = int.MinValue;
                    _lastF[i] = int.MinValue;
                }
            }

            if (_costLabelsTouched.Capacity < n)
                _costLabelsTouched.Capacity = n;
        }


        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public void SetDebugCosts(int index, int g, int h, int f)
        {
            if (!_showDebugCosts) return;
            if (m_data == null) return;
            if (_costLabelPrefab == null || _costLabelRoot == null) return;
            if (!m_data.IsValidCellIndex(index)) return;

            if (_costOverlayFrame != Time.frameCount)
            {
                _costOverlayFrame = Time.frameCount;
                _costOverlayUpdatesThisFrame = 0;
            }

            if (_costOverlayUpdatesThisFrame >= _maxCostLabelUpdatesPerFrame)
                return;


            int n = m_data.CellCount;
            EnsureCostOverlayBuffers(n);

            var label = _costLabels[index];
            if (label == null)
            {
                label = Instantiate(_costLabelPrefab, _costLabelRoot);
                label.alignment = TMPro.TextAlignmentOptions.Center;
                _costLabels[index] = label;
            }

            if (!label.gameObject.activeSelf)
            {
                label.gameObject.SetActive(true);
                _costLabelsTouched.Add(index);
            }

            label.transform.position = m_data.IndexToWorldCenterXZ(index, _costLabelOffsetY);

            bool changed = (_lastG[index] != g) || (_lastH[index] != h) || (_lastF[index] != f);
            if (!_onlyUpdateCostTextWhenChanged || changed)
            {
                // show approx tiles step cost by dividing by 10. Layout: g and h small, f big. Format to one decimal place
                label.SetText(
                    "<size=60%>G{0:0.0} H{1:0.0}</size>\n<size=100%><b>F{2:0.0}</b></size>",
                    g * 0.1f, h * 0.1f, f * 0.1f
                );

                _lastG[index] = g;
                _lastH[index] = h;
                _lastF[index] = f;
            }

            _costOverlayUpdatesThisFrame++;
        }


        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public void ClearDebugCostsTouched()
        {
            if (_costLabelsTouched.Count == 0) return;

            for (int i = 0; i < _costLabelsTouched.Count; i++)
            {
                int index = _costLabelsTouched[i];
                var label = _costLabels?[index];
                if (label != null) label.gameObject.SetActive(false);
            }

            _costLabelsTouched.Clear();
        }



        private MapGenDebugReporter CreateReporter()
        {
            return new MapGenDebugReporter(
                _mapGenLogVerbosity,
                _mapGenAnomalyCapacity,
                _mapGenDumpAnomaliesOnSuccess
            );
        }


        private void ReportLayoutTelemetry()
        {
            if (_mapGenReporter == null || m_data == null || _boardRenderer == null) return;

            bool autoFlipX = _renderer2D != null && _renderer2D.FlipTextureX;         // intresting syntax that means the same: _renderer2D?.FlipTextureX ?? false
            bool autoFlipY = _renderer2D != null && _renderer2D.FlipTextureY;         // intresting syntax that means the same: _renderer2D?.FlipTextureY ?? false

            float safeCellSize = Mathf.Max(0.0001f, m_data.CellTileSize);
            int reportedX = Mathf.RoundToInt((_boardRenderer.transform.localScale.x * UNITY_PLANE_SIZE) / safeCellSize);
            int reportedZ = Mathf.RoundToInt((_boardRenderer.transform.localScale.z * UNITY_PLANE_SIZE) / safeCellSize);

            _mapGenReporter.WarnIfLayoutMismatch(
                m_data.MinWorld, m_data.MaxWorld,
                _boardRenderer.transform.position, m_data.GridCenter,
                expectedX: m_data.Width,
                expectedZ: m_data.Height,
                reportedX: reportedX,
                reportedZ: reportedZ,
                autoFlipX: autoFlipX,
                autoFlipY: autoFlipY
            );
        }




#if UNITY_EDITOR
        private void OnValidate() => ValidateGridSize();

        [ContextMenu("Debug/Corner Color Test")]
        private void DebugCornerColorTest()
        {
            if (m_data == null || m_data.CellCount <= 0) return;
            if (_renderer2D == null) return;

            // Paint corners WITHOUT checker shading and WITHOUT skipping obstacles
            _renderer2D.PaintCell(m_data.CoordToIndex(0, 0), new Color32(255, 0, 0, 255),            shadeLikeGrid: false, skipIfObstacle: false);               // (0,0) red
            _renderer2D.PaintCell(m_data.CoordToIndex(_width - 1, 0), new Color32(0, 255, 0, 255),   shadeLikeGrid: false, skipIfObstacle: false);               // (w-1,0) green
            _renderer2D.PaintCell(m_data.CoordToIndex(0, _height - 1), new Color32(0, 0, 255, 255),  shadeLikeGrid: false, skipIfObstacle: false);               // (0,h-1) blue
            _renderer2D.PaintCell(m_data.CoordToIndex(_width - 1, _height - 1), new Color32(255, 255, 255, 255), shadeLikeGrid: false, skipIfObstacle: false);   // (w-1,h-1) white

            _renderer2D.FlushTexture();
        }


        [ContextMenu("Seed/Copy LastGeneratedSeed -> Seed")]
        private void CopyLastSeedToSeed()
        {
            _seed = _lastGeneratedSeed;
            Debug.Log($"[MapManager] Copied last seed {_lastGeneratedSeed} into _seed.");
        }


        [ContextMenu("Seed/Copy LastGeneratedSeed -> Clipboard")]
        private void CopyLastSeedToClipboard()
        {
            UnityEditor.EditorGUIUtility.systemCopyBuffer = _lastGeneratedSeed.ToString();
            Debug.Log($"[MapManager] Copied seed {_lastGeneratedSeed} to clipboard.");
        }

#endif

    }

}