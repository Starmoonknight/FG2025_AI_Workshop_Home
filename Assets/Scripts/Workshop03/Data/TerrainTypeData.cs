using UnityEngine;


namespace AI_Workshop03
{

    public enum PlacementMode { Static, Blob, Lichtenberg }

    public enum ExpansionAreaFocus { Anywhere, Interior, Edge, Weighted }

    public enum LichtenbergEdgePairMode
    {
        Any,                // full random placement
        SameEdge,           // both endpoints on the same edge
        AdjacentEdge,       // endpoint edge must be adjacent (no opposite, no same)
        OppositeEdgePair,   // pick endpoints on opposit edges
        NotOpposite         // good "along edges" default, never picks the opposite edge
    }


    public enum TerrainID
    {
        Land            = 0,    // (0 = base) Normal ground without any modifiers 
        Subterranean    = 1,
        Liquid          = 2,
        Air             = 3,
    }


    // Version2 of TerrainRule with more advanced placement options
    [CreateAssetMenu(menuName = "Board/Terrain Type Data (Workshop03)")]
    public sealed class TerrainTypeData : ScriptableObject
    {

        //[Header("Identity")]
        public string DisplayName = "New Rule";

        [Tooltip("Optional: a 3D visual prefab representing this terrain type. " +
                 "TerrainRule does NOT spawn it by itself; another system can use this reference.")]
        public GameObject VisualPrefab;

        [Tooltip("Color painted into the board base colors.")]
        public Color32 Color = new Color32(255, 255, 255, 255);

        [Tooltip("Use the terrain color to tint the attached prefab")]
        public bool ColorToTintPrefab = false;

        [Tooltip(
            "Movement cost written into the board's terrainCost[] for painted WALKABLE cells.\n\n" +
            "Note: When IsObstacle is true, BoardGenerator.ApplyObstacles() sets cost to 0 regardless of this value."
        )]
        [Min(0)] public int Cost = 10;



        //[Header("Classification")]
        [Tooltip("NOTE: only Land is implemented right now")]
        public TerrainID TerrainID = TerrainID.Land;                // rename TerrainTypeID for clarity, need to fix in the editor script as well so inspector won't break

        [Tooltip("If true: this rule places obstacles (sets blocked=true). If false: paints walkable terrain.")]
        public bool IsObstacle = false;



        //[Header("Seeding")]
        [Tooltip(
            "When picking initial seed / start / goal cells, require the chosen cells to be unblocked.\n\n" +
            "Useful so roads/blobs never start inside obstacles."
        )]
        public bool ForceUnblockedSeed = false;



        //[Header("Placement Model")]
        public PlacementMode Mode = PlacementMode.Static;

        [Tooltip("Percentage of total cells to aim for.")]
        [Range(0f, 1f)] public float CoveragePercent = 0.10f;



        //[Header("Overwrite Rules")]
        [Tooltip(
            "If true: this rule may paint on top of blocked cells.\n\n" +
            "- For walkable terrain: also clears blocked=false on those painted cells.\n" +
            "- For obstacles: allows placing obstacles on top of existing obstacles."
            )]
        public bool AllowOverwriteObstacle = false;

        [Tooltip(
            "If true: only paint cells that are still \"base\" (painterId == 0).\n\n" +
            "This is evaluated before AllowOverwriteTerrain. If enabled, it prevents the layer from overwriting other terrain layers."
            )]
        public bool OnlyAffectBase = true;

        [Tooltip(
            "If true: can overwrite other walkable terrain layers types.\n\n" +
            "Ignored when OnlyAffectBase is true.")]
        public bool AllowOverwriteTerrain = false;

        [Tooltip(
            "Sort order used by BoardGenerator:\n" +
            "- Lower Order runs earlier\n" +
            "- Higher Order runs later\n\n" +
            "BoardGenerator also always applies IsObstacle rules first, then non-obstacles (each group still sorted by Order)."
            )]
        [Min(1)] public int Order = 1;

        [Tooltip(
            "Relative weight for this terrain type when the generator picks which terrain to place.\n\n" +
            "Higher values increase the chance of this terrain being selected earlier compared to others.")]
        [Range(0, 1)] public float EarlyPlacementBias = 1;




        [Header("Static Params")]
        public StaticParams Static = new StaticParams
        {
            PlacementArea    = ExpansionAreaFocus.Anywhere,
            PlacementWeights = AreaFocusWeights.Legacy,

            ClusterBias = 0.55f
        };

        [Header("Blob Params")]
        public BlobParams Blob = new BlobParams
        {
            PlacementArea    = ExpansionAreaFocus.Anywhere,
            PlacementWeights = AreaFocusWeights.Legacy,

            AvgBlobSize     = 120,
            BlobSizeJitter  = 60,
            MinBlobCount    = 6,
            MaxBlobCount    = 30,
            GrowChance      = 0.55f,
            SmoothPasses    = 1
        };

        [Header("Lichtenberg Params")]
        public LichtenbergParams Lichtenberg = new LichtenbergParams
        {
            OriginArea    = ExpansionAreaFocus.Anywhere,
            GrowthAimArea = ExpansionAreaFocus.Anywhere,

            OriginWeights    = AreaFocusWeights.Legacy,
            GrowthAimWeights = AreaFocusWeights.Legacy,

            UseEdgePairPresets = true,
            EdgePairMode       = LichtenbergEdgePairMode.OppositeEdgePair,

            MinPathCount     = 4,
            MaxPathCount     = 16,
            CellsPerPath     = 180,
            StepBudgetScale  = 1.8f,
            MaxActiveWalkers = 14,

            BranchSpawnChance = 0.18f,
            GoalGrowthBias    = 0.72f,
            WidenPasses       = 0,

            PreferUnusedCells = true,
            AllowReuseIfStuck = true,

            HeatRepelStrength = 2.0f,
            HeatRepelRadius   = 1,
            HeatAdd           = 5,
            HeatFalloff       = 2,

            RepelPenaltyFromExisting = true,
            ExistingCellPenalty = 8
        };


        [System.Serializable]
        public struct AreaFocusWeights
        {
            [Tooltip(
                "Relative weight for choosing EDGE picks (cells on the outer border of the map).\n\n" +
                "Higher value -> edge is chosen more often.\n" +
                "Weights are relative (they do not need to sum to 1)."
            )]
            [Range(0f, 1f)] public float EdgeWeight;

            [Tooltip(
                "Relative weight for choosing INTERIOR picks (cells kept away from the border by the Interior Margin settings below).\n\n" +
                "Higher value -> interior is chosen more often.\n" +
                "Weights are relative (they do not need to sum to 1)."
            )]
            [Range(0f, 1f)] public float InteriorWeight;

            [Tooltip(
                "Relative weight for choosing ANYWHERE picks (any eligible cell on the map).\n\n" +
                "Higher value -> anywhere is chosen more often.\n" +
                "Weights are relative (they do not need to sum to 1).")]
            [Range(0f, 1f)] public float AnywhereWeight;

            [Header("Interior Margin")]

            [Tooltip(
                "Applies to Interior picks (and Weighted when Interior is selected).\n" +
                "Defines how far from the border a cell must be to count as \"Interior\".\n\n" +
                "Computed margin in cells:\n" +
                "margin = max(InteriorMinMargin, round(min(width, height) * InteriorMarginPercent))\n\n" +
                "Example: 500x500, percent 0.05 -> margin 25\n" +
                "Interior x/y range becomes [25 .. 474].")]
            [Range(0f, 0.49f)] public float InteriorMarginPercent;

            [Tooltip(
                "Applies to Interior picks (and Weighted when Interior is selected).\n" +
                "Minimum interior margin in cells, even if the percent would round smaller.\n\n" +
                "Useful for small maps where InteriorMarginPercent might round to 0.")]
            [Min(0)] public int InteriorMinMargin;

            public static AreaFocusWeights Legacy => new AreaFocusWeights
            {
                EdgeWeight      = 0.15f,
                InteriorWeight  = 0.60f,
                AnywhereWeight  = 0.25f,
                InteriorMarginPercent = 0.05f,
                InteriorMinMargin = 2
            };
        }


        [System.Serializable]
        public struct StaticParams
        {
            public ExpansionAreaFocus PlacementArea;

            public AreaFocusWeights PlacementWeights;

            [Range(0f, 1f)] public float ClusterBias;
        }

        [System.Serializable]
        public struct BlobParams
        {
            public ExpansionAreaFocus PlacementArea;

            public AreaFocusWeights PlacementWeights;

            [Tooltip(
                "Base target size for each blob (before jitter).\n\n" +
                "Also used to estimate how many blobs to try:\n" +
                "blobCount = desiredCells / AvgSize, then clamped by Min/Max Blob Count.\n\n" +
                "Bigger AvgSize -> fewer, larger blobs.\n" +
                "Smaller AvgSize -> more, smaller blobs.\n\n" +
                "Note: CoveragePercent is not a strict cap; total painted cells may end up above/below desiredCells due to clamping, overlap, and blocked cells."
            )]
            [Min(1)] public int AvgBlobSize;

            [Tooltip(
                "Per-blob variation in target size:\n" +
                "maxCells = max(10, AvgSize ± SizeJitter).\n\n" +
                "Higher jitter makes blob sizes more varied (some small, some large) without changing the blobCount estimate directly."
            )]
            [Min(0)] public int BlobSizeJitter;

            [Tooltip(
                "Lower clamp for the number of blob attempts.\n\n" +
                "If CoveragePercent / AvgSize would produce fewer blobs than this, generation will still try at least this many seeds.\n" +
                "This can increase total painted area beyond the intended coverage (since each blob still has its own size target)."
            )]
            [Min(0)] public int MinBlobCount;

            [Tooltip(
                "Upper clamp for the number of blob attempts.\n\n" +
                "Prevents huge numbers of small blobs when AvgSize is low or CoveragePercent is high.\n" +
                "If the estimate exceeds this, you’ll get fewer blobs (often making the result look clumpier / more consolidated)."
            )]
            [Min(0)] public int MaxBlobCount;

            [Tooltip(
                "During BFS-like blob growth: each candidate neighbor (4-neighbor / orthogonal) is accepted with this probability.\n\n" +
                "Low values -> patchy / broken edges, more gaps, blobs may fail to reach their size target if they run out of frontier.\n" +
                "High values -> more compact, filled blobs that more reliably reach maxCells.\n\n" +
                "This is not directional; it affects density/continuity."
            )]
            [Range(0f, 1f)] public float GrowChance;

            [Tooltip(
                "Post-process passes that add neighboring cells (4-neighbor) around the blob to fill small gaps and soften jagged edges.\n\n" +
                "Important: Smoothing is still capped by the blob’s maxCells size budget, so it does NOT grow the blob beyond its target size—\n" +
                "it just spends remaining budget on nearby cells to make the shape less noisy.\n\n" +
                "0 = disabled."
            )]
            [Range(0, 8)] public int SmoothPasses;
        }

        [System.Serializable]
        public struct LichtenbergParams
        {
            public ExpansionAreaFocus OriginArea;
            public ExpansionAreaFocus GrowthAimArea;

            public AreaFocusWeights OriginWeights;
            public AreaFocusWeights GrowthAimWeights;

            [Tooltip("If enabled, START and GOAL are picked using EdgePairMode (edge-based presets).\n" +
                "This overrides OriginArea / GrowthAimArea and their weights.\n\n" +
                "Use this for quick, predictable road styles (along an edge, adjacent edges, opposite edges, etc.).\n" +
                "Disable it when you want art-directable mixes like Edge -> Interior or Interior -> Anywhere.")]
            public bool UseEdgePairPresets;

            [Tooltip("Only used when UseEdgePairPresets is enabled.\n" +
               "Controls how the START and GOAL edges relate:\n- Any: choose any two edges (can be the same/opposite).\n" +
                "- SameEdge: both endpoints on the same edge.\n- AdjacentEdge: endpoints on adjacent edges " +
                "(never same, never opposite).\n- OppositeEdgePair: endpoints on opposite edges (left<->right or bottom<->top).\n" +
                "- NotOpposite: any pair except opposite (good default for \"\"along edges\"\" without forcing a full cross-map line).")]
            public LichtenbergEdgePairMode EdgePairMode;

            [Tooltip("Minimum number of independent Lichtenberg paths (roads) to generate for this terrain layer. " +
                "Actual count is estimated from CoveragePercent and CellsPerPath, then clamped to this range.")]
            [Min(0)] public int MinPathCount;

            [Tooltip("Maximum number of independent Lichtenberg paths (roads) to generate for this terrain layer. " +
                "Actual count is estimated from CoveragePercent and CellsPerPath, then clamped to this range.")]
            [Min(0)] public int MaxPathCount;

            [Tooltip("Used ONLY to estimate pathCount = desiredCells / CellsPerPath.\n\n" +
                "This does NOT cap how many cells one path can paint; StepBudgetScale, branching, heat and widen passes do.")]
            [Min(1)] public int CellsPerPath;

            [Tooltip("Per-path move budget: maxSteps = (width + height) * StepsScale. " +
                "This is the TOTAL number of moves shared across all walkers/branches for ONE path. " +
                "Higher values allow longer roads and give branches more steps to extend.")]
            [Range(0.5f, 6f)] public float StepBudgetScale;

            [Tooltip("Upper bound on the number of simultaneous walkers (active branch tips) for a single path. " +
                "BranchChance can spawn walkers until this cap is reached.")]
            [Range(1, 64)] public int MaxActiveWalkers;

            [Tooltip("Per move: chance to spawn a new walker at the current step, creating a branch. " +
                "Branching is capped by MaxWalkers and limited by the step budget (StepsScale).")]
            [Range(0f, 1f)] public float BranchSpawnChance;

            [Tooltip("How strongly the next step is biased toward moving directly toward the goal. " +
                "0 = mostly wander (heat/noise dominate). 1 = prefers steps that reduce distance to goal, giving straighter roads.")]
            [Range(0f, 1f)] public float GoalGrowthBias;

            [Tooltip("After a path is generated, each pass expands it by one 4-neighbor ring (Manhattan radius +1). " +
                "0 = single-tile path, 1 = ~3-wide in places, etc.")]
            [Range(0, 6)] public int WidenPasses;

            [Tooltip("When picking the next step, try to avoid cells already used by earlier paths in THIS same Lichtenberg run " +
                "(and any cells already painted with this terrain layer). Helps prevent paths clumping/crossing.")]
            public bool PreferUnusedCells;

            [Tooltip(
                "Only used when PreferUnusedCells is enabled.\n\n" +
                "If the path cannot find any unused neighbor, allow it to step onto used cells as a fallback.\n" +
                "If disabled, the path ends early when stuck."
                )]
            public bool AllowReuseIfStuck;

            [Tooltip("Multiplier for heat penalty when scoring candidate steps: repel = RepelStrength * heat[candidate]. " +
                "Higher values make paths spread out more and avoid recently visited regions.")]
            [Min(0f)] public float HeatRepelStrength;

            [Tooltip("Each time a cell is visited, heat is added to that cell and nearby cells within this radius " +
                "(using Manhattan-distance falloff). Higher radius creates a wider 'avoid' halo around the path.")]
            [Min(0)] public int HeatRepelRadius;

            [Tooltip("Base heat added to the visited cell each move (and used as the starting value for neighbors before falloff).")]
            [Min(0)] public int HeatAdd;

            [Tooltip("Heat reduction per Manhattan-distance step when applying heat to neighbors: " +
                "neighborHeat = max(0, HeatAdd - HeatFalloff * distance). Larger values keep heat tight to the path.")]
            [Min(0)] public int HeatFalloff;

            [Tooltip("If enabled, adds ExistingPenalty when stepping onto a cell already painted with THIS terrain layer " +
                 "(painterId == this terrain's generated id). Note: in the current generation order, this often has little effect " +
                 "for walkable terrains because painterId is written after generation.")]
            public bool RepelPenaltyFromExisting;

            [Tooltip("Extra penalty applied when RepelFromExisting is enabled and the candidate cell " +
                "has already been painted by this terrain layer in earlier paths for this run.")]
            [Min(0)] public int ExistingCellPenalty;
        }



#if UNITY_EDITOR
        private void OnValidate()
        {
            // some safet checks 
            Blob.AvgBlobSize = Mathf.Max(1, Blob.AvgBlobSize);
            Blob.BlobSizeJitter = Mathf.Max(0, Blob.BlobSizeJitter);

            if (Blob.MaxBlobCount < Blob.MinBlobCount)
                Blob.MaxBlobCount = Blob.MinBlobCount;

            Lichtenberg.CellsPerPath = Mathf.Max(1, Lichtenberg.CellsPerPath);

            if (Lichtenberg.MaxPathCount < Lichtenberg.MinPathCount)
                Lichtenberg.MaxPathCount = Lichtenberg.MinPathCount;

            Lichtenberg.MaxActiveWalkers = Mathf.Clamp(Lichtenberg.MaxActiveWalkers, 1, 64);
        }
#endif


    }


}
