using UnityEditor;
using UnityEngine;

using TD = AI_Workshop03.TerrainTypeData;


namespace AI_Workshop03.Editor
{

    [CustomEditor(typeof(TD))]
    [CanEditMultipleObjects]
    public sealed class TerrainTypeDataEditor : UnityEditor.Editor
    {
        // Top level fields
        private SerializedProperty _displayName, _visualPrefab, _color, _cost;
        private SerializedProperty _terrainID, _isObstacle, _forceUnblockedSeed;

        private SerializedProperty _mode, _coveragePercent;

        private SerializedProperty _allowOverwriteObstacle, _onlyAffectBase, _allowOverwriteTerrain, _order;

        // Param structs
        private SerializedProperty _static, _blob, _lichtenberg;

        // Static children
        private SerializedProperty _staticPlacementArea, _staticPlacementWeights, _staticClusterBias;

        // Blob children
        private SerializedProperty _blobPlacementArea, _blobPlacementWeights;
        private SerializedProperty _avgBlobSize, _blobSizeJitter, _minBlobCount, _maxBlobCount, _growChance, _smoothPasses;

        // Lichtenberg children
        private SerializedProperty _originArea, _growthAimArea;
        private SerializedProperty _originWeights, _growthAimWeights;
        private SerializedProperty _useEdgePairPresets, _edgePairMode;

        private SerializedProperty _minPathCount, _maxPathCount, _cellsPerPath, _stepBudgetScale, _maxActiveWalkers;
        private SerializedProperty _branchSpawnChance, _goalGrowthBias, _widenPasses;

        private SerializedProperty _preferUnusedCells, _allowReuseIfStuck;

        private SerializedProperty _heatRepelStrength, _heatRepelRadius, _heatAdd, _heatFalloff;

        private SerializedProperty _repelPenaltyFromExisting, _existingCellPenalty;

        // foldouts
        private bool _showIdentity = true;
        private bool _showRules = true;
        private bool _showModeParams = true;


        private static GUIStyle _modeHeaderStyle;
        private static GUIStyle ModeHeaderStyle
        {
            get
            {
                if (_modeHeaderStyle == null)
                {
                    _modeHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 13,
                        wordWrap = false
                    };
                }
                return _modeHeaderStyle;
            }
        }


        private void OnEnable()
        {
            // Top level fields
            _displayName = serializedObject.FindProperty(nameof(TD.DisplayName));
            _visualPrefab = serializedObject.FindProperty(nameof(TD.VisualPrefab));
            _color = serializedObject.FindProperty(nameof(TD.Color));
            _cost = serializedObject.FindProperty(nameof(TD.Cost));

            _terrainID = serializedObject.FindProperty(nameof(TD.TerrainID));
            _isObstacle = serializedObject.FindProperty(nameof(TD.IsObstacle));
            _forceUnblockedSeed = serializedObject.FindProperty(nameof(TD.ForceUnblockedSeed));

            _mode = serializedObject.FindProperty(nameof(TD.Mode));
            _coveragePercent = serializedObject.FindProperty(nameof(TD.CoveragePercent));

            _allowOverwriteObstacle = serializedObject.FindProperty(nameof(TD.AllowOverwriteObstacle));
            _onlyAffectBase = serializedObject.FindProperty(nameof(TD.OnlyAffectBase));
            _allowOverwriteTerrain = serializedObject.FindProperty(nameof(TD.AllowOverwriteTerrain));
            _order = serializedObject.FindProperty(nameof(TD.Order));

            // Param structs
            _static = serializedObject.FindProperty(nameof(TD.Static));
            _blob = serializedObject.FindProperty(nameof(TD.Blob));
            _lichtenberg = serializedObject.FindProperty(nameof(TD.Lichtenberg));

            // Static children
            _staticPlacementArea = _static.FindPropertyRelative(nameof(TD.StaticParams.PlacementArea));
            _staticPlacementWeights = _static.FindPropertyRelative(nameof(TD.StaticParams.PlacementWeights));
            _staticClusterBias = _static.FindPropertyRelative(nameof(TD.StaticParams.ClusterBias));

            // Blob children
            _blobPlacementArea = _blob.FindPropertyRelative(nameof(TD.BlobParams.PlacementArea));
            _blobPlacementWeights = _blob.FindPropertyRelative(nameof(TD.BlobParams.PlacementWeights));
            _avgBlobSize = _blob.FindPropertyRelative(nameof(TD.BlobParams.AvgBlobSize));
            _blobSizeJitter = _blob.FindPropertyRelative(nameof(TD.BlobParams.BlobSizeJitter));
            _minBlobCount = _blob.FindPropertyRelative(nameof(TD.BlobParams.MinBlobCount));
            _maxBlobCount = _blob.FindPropertyRelative(nameof(TD.BlobParams.MaxBlobCount));
            _growChance = _blob.FindPropertyRelative(nameof(TD.BlobParams.GrowChance));
            _smoothPasses = _blob.FindPropertyRelative(nameof(TD.BlobParams.SmoothPasses));

            // Lichtenberg children
            _originArea = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.OriginArea));
            _growthAimArea = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.GrowthAimArea));
            _originWeights = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.OriginWeights));
            _growthAimWeights = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.GrowthAimWeights));

            _useEdgePairPresets = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.UseEdgePairPresets));
            _edgePairMode = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.EdgePairMode));

            _minPathCount = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.MinPathCount));
            _maxPathCount = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.MaxPathCount));
            _cellsPerPath = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.CellsPerPath));
            _stepBudgetScale = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.StepBudgetScale));
            _maxActiveWalkers = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.MaxActiveWalkers));

            _branchSpawnChance = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.BranchSpawnChance));
            _goalGrowthBias = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.GoalGrowthBias));
            _widenPasses = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.WidenPasses));

            _preferUnusedCells = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.PreferUnusedCells));
            _allowReuseIfStuck = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.AllowReuseIfStuck));

            _heatRepelStrength = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.HeatRepelStrength));
            _heatRepelRadius = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.HeatRepelRadius));
            _heatAdd = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.HeatAdd));
            _heatFalloff = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.HeatFalloff));

            _repelPenaltyFromExisting = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.RepelPenaltyFromExisting));
            _existingCellPenalty = _lichtenberg.FindPropertyRelative(nameof(TD.LichtenbergParams.ExistingCellPenalty));
        }


        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawIdentity();
            EditorGUILayout.Space(6);

            DrawClassificationAndSeeding();
            EditorGUILayout.Space(6);

            DrawPlacementModel();
            EditorGUILayout.Space(6);

            DrawOverwriteRules();
            EditorGUILayout.Space(10);

            DrawModeParams();

            serializedObject.ApplyModifiedProperties();
        }


        private void DrawIdentity()
        {
            _showIdentity = EditorGUILayout.BeginFoldoutHeaderGroup(_showIdentity, "Identity");
            if (_showIdentity)
            {
                EditorGUILayout.PropertyField(_displayName);
                EditorGUILayout.PropertyField(_visualPrefab);
                EditorGUILayout.PropertyField(_color);
                EditorGUILayout.PropertyField(_cost);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawClassificationAndSeeding()
        {
            EditorGUILayout.LabelField("Classification", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_terrainID);
            EditorGUILayout.PropertyField(_isObstacle);

            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("Seeding", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_forceUnblockedSeed);
        }

        private void DrawPlacementModel()
        {
            EditorGUILayout.LabelField("Placement Model", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_mode);
            EditorGUILayout.PropertyField(_coveragePercent);
        }

        private void DrawOverwriteRules()
        {
            _showRules = EditorGUILayout.BeginFoldoutHeaderGroup(_showRules, "Overwrite Rules");
            if (_showRules)
            {
                EditorGUILayout.PropertyField(_allowOverwriteObstacle);
                EditorGUILayout.PropertyField(_onlyAffectBase);

                // If OnlyAffectBase is ON for a single selection, AllowOverwriteTerrain is irrelevant -> show but disable.
                bool canDecide = !_onlyAffectBase.hasMultipleDifferentValues;
                bool disableAllowOverwriteTerrain = canDecide && _onlyAffectBase.boolValue;

                EditorGUI.indentLevel++;
                using (new EditorGUI.DisabledScope(disableAllowOverwriteTerrain))
                {
                    EditorGUILayout.PropertyField(_allowOverwriteTerrain);
                }
                EditorGUI.indentLevel--;

                if (disableAllowOverwriteTerrain)
                {
                    EditorGUILayout.HelpBox(
                        "Disabled because Only Affect Base is enabled (this setting won't be used).",
                        MessageType.Info);
                }

                EditorGUILayout.PropertyField(_order);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawModeParams()
        {
            _showModeParams = EditorGUILayout.BeginFoldoutHeaderGroup(_showModeParams, "Mode Parameters");
            if (!_showModeParams)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            // Multi-select safety: if different modes are selected, avoid hiding fields in a confusing way.
            if (_mode.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox(
                    "Multiple TerrainData assets selected with different Mode values. " +
                    "Showing all parameter groups to avoid hiding data unexpectedly.",
                    MessageType.Info);

                DrawStaticParams();
                EditorGUILayout.Space(6);
                DrawBlobParams();
                EditorGUILayout.Space(6);
                DrawLichtenbergParams();

                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            var mode = (PlacementMode)_mode.enumValueIndex;
            switch (mode)
            {
                case PlacementMode.Static:
                    DrawStaticParams();
                    break;

                case PlacementMode.Blob:
                    DrawBlobParams();
                    break;

                case PlacementMode.Lichtenberg:
                    DrawLichtenbergParams();
                    break;
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private static bool ShouldShowWeights(SerializedProperty focusEnumProp)
        {
            // If multi-select has mixed values, show the weights to avoid hiding relevant data.
            if (focusEnumProp.hasMultipleDifferentValues) return true;

            return (ExpansionAreaFocus)focusEnumProp.enumValueIndex == ExpansionAreaFocus.Weighted;
        }

        private void DrawStaticParams()
        {
            EditorGUILayout.LabelField("Static Params", ModeHeaderStyle);

            EditorGUILayout.Space(2);
            EditorGUILayout.PropertyField(_staticPlacementArea);
            if (ShouldShowWeights(_staticPlacementArea))
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_staticPlacementWeights, includeChildren: true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(_staticClusterBias);
        }

        private void DrawBlobParams()
        {
            EditorGUILayout.LabelField("Blob Params", ModeHeaderStyle);

            EditorGUILayout.Space(2);
            EditorGUILayout.PropertyField(_blobPlacementArea);
            if (ShouldShowWeights(_blobPlacementArea))
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_blobPlacementWeights, includeChildren: true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(_avgBlobSize);
            EditorGUILayout.PropertyField(_blobSizeJitter);
            EditorGUILayout.PropertyField(_minBlobCount);
            EditorGUILayout.PropertyField(_maxBlobCount);
            EditorGUILayout.PropertyField(_growChance);
            EditorGUILayout.PropertyField(_smoothPasses);
        }

        private void DrawLichtenbergParams()
        {
            EditorGUILayout.LabelField("Lichtenberg Params", ModeHeaderStyle);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Endpoints", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_useEdgePairPresets);

            if (_useEdgePairPresets.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox(
                    "Mixed UseEdgePairPresets values across selection. Showing both EdgePairMode and Origin/Growth areas.",
                    MessageType.Info);
                EditorGUILayout.PropertyField(_edgePairMode);

                EditorGUILayout.PropertyField(_originArea);
                if (ShouldShowWeights(_originArea))
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_originWeights, includeChildren: true);
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.PropertyField(_growthAimArea);
                if (ShouldShowWeights(_growthAimArea))
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_growthAimWeights, includeChildren: true);
                    EditorGUI.indentLevel--;
                }
            }
            else if (_useEdgePairPresets.boolValue)
            {
                EditorGUILayout.PropertyField(_edgePairMode);
            }
            else
            {
                EditorGUILayout.PropertyField(_originArea);
                if (ShouldShowWeights(_originArea))
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_originWeights, includeChildren: true);
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.PropertyField(_growthAimArea);
                if (ShouldShowWeights(_growthAimArea))
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_growthAimWeights, includeChildren: true);
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Counts / Budget", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_minPathCount);
            EditorGUILayout.PropertyField(_maxPathCount);
            EditorGUILayout.PropertyField(_cellsPerPath);
            EditorGUILayout.PropertyField(_stepBudgetScale);
            EditorGUILayout.PropertyField(_maxActiveWalkers);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Growth", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_branchSpawnChance);
            EditorGUILayout.PropertyField(_goalGrowthBias);
            EditorGUILayout.PropertyField(_widenPasses);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Reuse Policy", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_preferUnusedCells);

            if (_preferUnusedCells.hasMultipleDifferentValues || _preferUnusedCells.boolValue)
            {
                EditorGUILayout.PropertyField(_allowReuseIfStuck);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Heat", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_heatRepelStrength);
            EditorGUILayout.PropertyField(_heatRepelRadius);
            EditorGUILayout.PropertyField(_heatAdd);
            EditorGUILayout.PropertyField(_heatFalloff);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Existing-cell penalty", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_repelPenaltyFromExisting);

            if (_repelPenaltyFromExisting.hasMultipleDifferentValues || _repelPenaltyFromExisting.boolValue)
            {
                EditorGUILayout.PropertyField(_existingCellPenalty);
            }
        }
    }

}
