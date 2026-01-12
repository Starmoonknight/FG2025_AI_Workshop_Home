using UnityEngine;


namespace AI_Workshop02
{

    public enum PlacementMode { Static, Blob, Lichtenberg }


    public enum TerrainID { 
        Land            = 0,    // (0 = base) Normal ground without any modifiers 
        Subterarrian    = 1,   
        Liquid          = 2,         
        Air             = 3,            
    }


    [CreateAssetMenu(menuName = "Board/Terrain Rule (Workshop02)")]
    public sealed class TerrainRule : ScriptableObject
    { 

        [Header("Identity")]
        public string DisplayName = "New Rule";

        [Tooltip("Color painted into the board base colors.")]
        public Color32 Color = new Color32(255, 255, 255, 255);

        [Tooltip(
            "Movement cost written into the board's terrainCost[] for painted WALKABLE cells.\n\n" +
            "Note: When IsObstacle is true, BoardGenerator.ApplyObstacles() sets cost to 0 regardless of this value."
            )]
        [Min(0)] public int Cost = 10;



        [Header("Classification")]
        [Tooltip(
            "Classification value written into terrainKind[] (byte).\n\n" +
            "Workshop02 pathfinding does not currently use this (it uses blocked[] + terrainCost[]), " +
            "but it can be useful for debugging or future rules."
            )]
        public TerrainID TerrainID = TerrainID.Land; 

        [Tooltip("If true: this rule places obstacles (sets blocked=true). If false: paints walkable terrain.")]
        public bool IsObstacle = false;



        [Header("Seeding")]
        [Tooltip(
            "When picking initial seed / start / goal cells, require the chosen cells to be unblocked.\n\n" +
            "Useful so roads/blobs never start inside obstacles."
            )]
        public bool ForceUnblockedSeed = false;



        [Header("Placement Model")]
        public PlacementMode Mode = PlacementMode.Static;

        [Tooltip(
            "How much of the map to aim for.\n\n" +
            "- Static: target = CoveragePercent * eligibleCells\n" +
            "- Blob: desiredCells = CoveragePercent * totalCells (then used to estimate blobCount)\n" +
            "- Lichtenberg: desiredCells = CoveragePercent * totalCells (then used to estimate pathCount)"
            )]
        [Range(0f, 1f)] public float CoveragePercent = 0.10f;



        [Header("Overwrite Rules")]
        [Tooltip(
            "If true: this rule may paint on top of blocked cells.\n\n" +
            "- For walkable terrain: also clears blocked=false on those painted cells.\n" +
            "- For obstacles: allows placing obstacles on top of existing obstacles (no visible change)."
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





        [Header("Blob Params")]
        public BlobParams Blob = new BlobParams
        {

            AvgBlobSize = 120,
            BlobSizeJitter = 60,
            MinBlobCount = 6,
            MaxBlobCount = 30,
            GrowChance = 0.55f,
            SmoothPasses = 1
        };

        [Header("Lichtenberg Params")]
        public LichtenbergParams Lichtenberg = new LichtenbergParams
        {            
            RequireOppositeEdgePair = true,
            
            MinPathCount = 4,
            MaxPathCount = 16,
            CellsPerPath = 180,
            StepBudgetScale = 1.8f,
            MaxActiveWalkers = 14,
            
            BranchSpawnChance = 0.18f,
            GoalGrowthBias = 0.72f,
            WidenPasses = 0,
            
            PreferUnusedCells = true,
            AllowReuseIfStuck = true,
           
            HeatRepelStrength = 2.0f,
            HeatRepelRadius = 1,
            HeatAdd = 5,
            HeatFalloff = 2,
            
            RepelPenaltyFromExisting = true,
            ExistingCellPenalty = 8
        };



        [System.Serializable] public struct BlobParams 
        {

            [Tooltip(
                "Base target size for each blob (before jitter).\n\n" +
                "Also used to estimate how many blobs to try:\n" +
                "blobCount = desiredCells / AvgBlobSize, then clamped by Min/Max Blob Count.\n\n" +
                "Bigger AvgBlobSize -> fewer, larger blobs.\n" +
                "Smaller AvgBlobSize -> more, smaller blobs."
                )]
            [Min(1)] public int AvgBlobSize;

            [Tooltip(
                "Per-blob variation in target size:\n" +
                "maxCells = max(10, AvgSize ± SizeJitter).\n\n" +
                "Higher jitter makes blob sizes more varied (some small, some large)."
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
                "During BFS-like blob growth: each candidate neighbor (4-neighbor) is accepted with this probability.\n\n" +
                "Low values -> patchy blobs / broken edges.\n" +
                "High values -> compact blobs that more reliably reach their target size."
                )]
            [Range(0f, 1f)] public float GrowChance;

            [Tooltip(
                "Post-process passes that add nearby neighbors (4-neighbor) to fill small gaps.\n\n" +
                "Important: still capped by the blob's maxCells budget, so it does NOT grow beyond the target size.\n" +
                "0 = disabled."
                )]
            [Range(0, 8)] public int SmoothPasses;
        }

        [System.Serializable] public struct LichtenbergParams 
        {

            [Tooltip(
                "Endpoint selection mode for this Workshop02 generator:\n\n" +
                "- True: START and GOAL are always picked from OPPOSITE edges (left<->right or bottom<->top).\n" +
                "- False: START and GOAL are picked independently from any edge (can be same or opposite)."
            )]
            public bool RequireOppositeEdgePair; 

            [Tooltip(
                "Minimum number of independent Lichtenberg paths (roads) to generate for this terrain layer.\n" +
                "Actual count is estimated from CoveragePercent and CellsPerPath, then clamped to this range."
                )]
            [Min(0)] public int MinPathCount;

            [Tooltip(
                "Maximum number of independent Lichtenberg paths (roads) to generate for this terrain layer.\n" +
                "Actual count is estimated from CoveragePercent and CellsPerPath, then clamped to this range."
                )]
            [Min(0)] public int MaxPathCount;

            [Tooltip(
                "Used ONLY to estimate pathCount = desiredCells / CellsPerPath.\n\n" +
                "This does NOT cap how many cells one path can paint; StepBudgetScale, branching, heat and widen passes do."
                )]
            [Min(1)] public int CellsPerPath;

            [Tooltip(
                "Per-path step budget: maxSteps = (width + height) * StepBudgetScale.\n\n" +
                "This is the TOTAL number of moves shared across all walkers/branches for ONE path.\n" +
                "Higher values allow longer roads and give branches more steps to extend."
                )]
            [Range(0.5f, 6f)] public float StepBudgetScale;

            [Tooltip(
                "Upper bound on the number of simultaneous walkers (active branch tips) for a single path.\n" +
                "BranchSpawnChance can spawn walkers until this cap is reached."
                )]
            [Range(1, 64)] public int MaxActiveWalkers;

            [Tooltip(
                "Per move: chance to spawn a new walker at the current step, creating a branch.\n" +
                "Branching is capped by MaxActiveWalkers and limited by the step budget."
                )]
            [Range(0f, 1f)] public float BranchSpawnChance;

            [Tooltip(
                "How strongly candidate steps are biased toward moving directly toward the goal.\n" +
                "0 = mostly wander. 1 = prefers steps that reduce distance to goal, giving straighter roads."
                )]
            [Range(0f, 1f)] public float GoalGrowthBias;

            [Tooltip(
                "After a path is generated, each pass expands it by one 4-neighbor ring (Manhattan radius +1).\n" +
                "0 = single-tile path, 1 = ~3-wide in places, etc."
                )]
            [Range(0, 6)] public int WidenPasses;

            [Tooltip(
                "When picking the next step, try to avoid cells already used by earlier paths in THIS same Lichtenberg run.\n" +
                "This mainly reduces clumping/crossing."
                )]
            public bool PreferUnusedCells;                  

            [Tooltip(
                "Only used when PreferUnusedCells is enabled.\n\n" +
                "If the path cannot find any unused neighbor, allow it to step onto used cells as a fallback.\n" +
                "If disabled, the path ends early when stuck."
                )]
            public bool AllowReuseIfStuck;

            [Tooltip(
                "Multiplier for heat penalty when scoring candidate steps: repel = RepelStrength * heat[candidate]. " +
                "Higher values make paths spread out more and avoid recently visited regions.")]
            [Min(0f)] public float HeatRepelStrength;

            [Tooltip(
                "Each time a cell is visited, heat is added to that cell and nearby cells within this Manhattan radius.\n" +
                "Higher radius creates a wider 'avoid' halo around the path.")]
            [Min(0)] public int HeatRepelRadius;

            [Tooltip(
                "Base heat added to the visited cell each move (and used as the starting value for neighbors before falloff).")]
            [Min(0)] public int HeatAdd;

            [Tooltip(
                "Heat reduction per Manhattan-distance step when applying heat to neighbors: " +
                "neighborHeat = max(0, HeatAdd - HeatFalloff * distance). Larger values keep heat tight to the path."
                )]
            [Min(0)] public int HeatFalloff;

            [Tooltip(
                "If enabled, adds ExistingCellPenalty when stepping onto a cell already painted with THIS terrain layer\n" +
                "(painterId == this terrain's generated id).\n\n" +
                "Note: during the initial generation pass, painterId is written AFTER generation, so this usually matters\n" +
                "only if there later is a re-run generation on an existing board (incremental updates)."
                )]
            public bool RepelPenaltyFromExisting;

            [Tooltip("Extra penalty applied when RepelPenaltyFromExisting is enabled and the candidate cell is already painted by this layer.")]
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

