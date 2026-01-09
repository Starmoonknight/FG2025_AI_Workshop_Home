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


    [CreateAssetMenu(menuName = "Board/Terrain Rule")]
    public sealed class TerrainData : ScriptableObject
    { 

        [Header("Identity")]
        public string DisplayName = "New Rule";

        [Tooltip("Optional: a 3D visual prefab representing this terrain type. " +
                 "TerrainRule does NOT spawn it by itself; another system can use this reference.")]
        public GameObject VisualPrefab;

        [Tooltip("Color painted into the board base colors.")]
        public Color32 Color = new Color32(255, 255, 255, 255);

        [Tooltip("Terrain movement cost (ignored for obstacles in the current BoardGenerator.ApplyObstacles).")]
        [Min(0)] public int Cost = 10;



        [Header("Classification")]
        [Tooltip("NOTE: only Land is implemented right now")]
        public TerrainID TerrainID = TerrainID.Land; 

        [Tooltip("If true: this rule places obstacles (sets walkable=false). If false: paints walkable terrain.")]
        public bool IsObstacle = false;



        [Header("Seeding")]
        [Tooltip("When picking initial seed/start/goal cells, require the chosen cells to be unblocked.")]
        public bool ForceUnblockedSeed = false;



        [Header("Placement Model")]
        public PlacementMode Mode = PlacementMode.Static;

        [Tooltip("Percentage of total cells to aim for.")]
        [Range(0f, 1f)] public float CoveragePercent = 0.10f;



        [Header("Overwrite Rules")]
        [Tooltip("If true: the generated cells may place on blocked cells (obstacles). If false: blocked cells are forbidden from being overwritten.")]
        public bool AllowOverwriteObstacle = false;

        [Tooltip("If true: can only paint on base tiles (Terrain Order Layer: 0).")]
        public bool OnlyAffectBase = true;

        [Tooltip("If true: can overwrite other terrain types (overwriten by OnlyAffectBase).")]
        public bool AllowOverwriteTerrain = false;

        [Tooltip("Optional ordering: lower first, higher later.")]
        [Min(1)] public int Order = 1;




        [Header("Static Params")]
        public StaticParams Static = new StaticParams
        {
            ScatterBias = 0.55f
        };

        [Header("Blob Params")]
        public BlobParams Blob = new BlobParams
        {
            AvgSize = 120,
            SizeJitter = 60,
            MinBlobs = 6,
            MaxBlobs = 30,
            ExpansionChance = 0.55f,
            SmoothPasses = 1
        };

        [Header("Lichtenberg Params")]
        public LichtenbergParams Lichtenberg = new LichtenbergParams
        {
            MinPaths = 4,
            MaxPaths = 16,
            CellsPerPath = 180,
            StepsScale = 1.8f,
            MaxWalkers = 14,
            GrowthTowardTargetBias = 0.72f,
            BranchChance = 0.18f,
            WidenPasses = 0,
            PreferUnusedCells = true,
            AllowReuseIfStuck = true,
            RequireOppositeEdgePair = true,
            RepelStrength = 2.0f,
            RepelRadius = 1,
            HeatAdd = 5,
            HeatFalloff = 2,
            RepelFromExisting = true,
            ExistingPenalty = 8
        };


        [System.Serializable] public struct StaticParams 
        { 
            [Range(0f, 1f)] public float ScatterBias;       // only used it for distribution feel, not amount.      // Need to fix so it is used
        }

        [System.Serializable] public struct BlobParams 
        {
            [InspectorName("Average Blob Size (cells)")]
            [Tooltip("Used to estimate blobCount = desiredCells / AvgSize.")]
            [Min(1)] public int AvgSize;

            [InspectorName("Blob Size Jitter (+/- cells)")]
            [Tooltip("Per-blob target size = AvgSize +/- jitter.")]
            [Min(0)] public int SizeJitter;

            [InspectorName("Min Blob Count")]
            [Min(0)] public int MinBlobs;

            [InspectorName("Max Blob Count")]
            [Min(0)] public int MaxBlobs;

            [InspectorName("Grow Chance (0-1)")]
            [Tooltip("During blob expansion: chance to accept each neighbor while growing.")]
            [Range(0f, 1f)] public float ExpansionChance;

            [InspectorName("Smoothing Passes")]
            [Tooltip("Post-pass to fill gaps / soften jagged edges. 0 disables.")]
            [Range(0, 8)] public int SmoothPasses;
        }

        [System.Serializable] public struct LichtenbergParams 
        {
            [InspectorName("Min Path Count")] 
            [Min(0)] public int MinPaths;

            [InspectorName("Max Path Count")] 
            [Min(0)] public int MaxPaths;

            [InspectorName("Cells Per Path (target)")]
            [Tooltip("Used to estimate pathCount = desiredCells / CellsPerPath.")] 
            [Min(1)] public int CellsPerPath;

            [InspectorName("Max Steps Scale")]
            [Tooltip("maxSteps = (width + height) * StepsScale.")] 
            [Range(0.5f, 6f)] public float StepsScale;

            [InspectorName("Max Concurrent Walkers")]
            [Tooltip("Caps concurrent walkers during growth.")] 
            [Range(1, 64)] public int MaxWalkers;

            [InspectorName("Bias Toward Goal (0-1)")]
            [Tooltip("Higher = more often pick a step that moves directly toward the goal.")]
            [Range(0f, 1f)] public float GrowthTowardTargetBias;

            [InspectorName("Branch Chance (0-1)")]
            [Tooltip("Chance to spawn a new walker/branch during growth.")]
            [Range(0f, 1f)] public float BranchChance;

            [InspectorName("Widen Passes")]
            [Tooltip("Each pass expands selected cells by ~1 ring after generation.")]
            [Range(0, 6)] public int WidenPasses;
            
            public bool PreferUnusedCells;                  // Decides if it will try awoid clumping in ot itself when spreading 
            public bool AllowReuseIfStuck;                  // Decides if allowed to reuse path at all or not
            public bool RequireOppositeEdgePair;            // If true the growth-start and growth-goal will try to be placed on oposit sides of the map 
            public float RepelStrength;                     // How much heat matters
            public int RepelRadius;                         // 0..2 usually
            public int HeatAdd;                             // Heat added per step
            public int HeatFalloff;                         // Heat decreases with distance
            public bool RepelFromExisting;                  // Repel from already-painted road cells
            public int ExistingPenalty;                     // Discourage stepping on existing road
        }



#if UNITY_EDITOR
        private void OnValidate()
        {
            // some safet checks 
            Blob.AvgSize = Mathf.Max(1, Blob.AvgSize);
            Blob.SizeJitter = Mathf.Max(0, Blob.SizeJitter);

            if (Blob.MaxBlobs < Blob.MinBlobs)
                Blob.MaxBlobs = Blob.MinBlobs;

            Lichtenberg.CellsPerPath = Mathf.Max(1, Lichtenberg.CellsPerPath);

            if (Lichtenberg.MaxPaths < Lichtenberg.MinPaths)
                Lichtenberg.MaxPaths = Lichtenberg.MinPaths;

            Lichtenberg.MaxWalkers = Mathf.Clamp(Lichtenberg.MaxWalkers, 1, 64);
        }
#endif


    }
}

