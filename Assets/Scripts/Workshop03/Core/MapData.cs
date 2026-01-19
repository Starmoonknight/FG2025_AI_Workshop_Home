using System;
using UnityEngine;

namespace AI_Workshop03
{

    public class MapData
    {


        /* SoA or AoS choice:
        * NOTE: In the task it explicitly mentions a Node[,] array.
        * But I represent nodes as per-cell data in 1D arrays indices in arrays rather than Node[,]
        * and then use the cells coordinates as its index value to match between all arrays 
        * I had a feeling it would be faster to be able to just access the parts of data that I needed at any time
        */



        /* Old version notes, delete after looking through them
          
           _generator.Generate(
                _width,
                _height,
                _blocked,               // if true blocks all movement over this tile
                _terrainKind,           // TerrainDataType of each cell  
                _terrainCost,           // cost modifier of moving over this tile
                _baseCellColors,        // base colors before any external modifiers
                _lastPaintLayerId,      // what TerrainData affect this, layer id: 0 = base 
                _walkableColor,         // base color before any terrain modifier
                10,                     // base cost before any terrain modifier
                genSeed,                // seed (0 means random)
                orderSeed,              // terrain paint ordering randomness (rarity shuffle)
                _terrainData,           // TerrainData[]
                _maxGenerateAttempts,   // limit for map generator
                _minUnblockedPercent,   // ratio of allowd un-blocked to blocked tiles 
                _minReachablePercent,   // how much walkable ground is required to be connected when BuildReachableFrom
                BuildReachableFrom      // Func<int,int> reachable count from start
            );

        */



        /*  Additional future fields ideas:
        *
        *  public ushort[] _terrainKey; permaner lookup id that would survive over multiple map generations while lastPaintLayerId is per-generation
        *  public bool[] _protected;  //look into storing as bitArray       // if I in the future want the rng ExpandRandom methods to ignore certain tiles, (start/goal, maybe a border ring) that must never be selected:   if (_protected != null && _protected[i]) continue;
        *  
        *  public (int/ushort) clumpId[idx] _clumpIds;   // if I in the future want to group certain tiles into clumps for generation or pathfinding optimizations, a connected-component labeling system
        *       
        *       Pipeline idea: during generation, after blocking tiles, run a CCL algorithm to assign clump ids to walkable areas, 
        *       then use that to quickly reject unreachable areas or optimize pathfinding by first checking if start and goal are in same clump
        *       
        *       Pipeline design then would be:
        *           > Generate Map
        *           > Compute Reachability
        *           + Compute Clumps
        *  
        *  
        *  
        *  // ? public byte[] TerrainTypeKeys     // stable terrain key (byte), survives multiple generations
        *  
        *  
        *  
        *        
        *  // Something for the future? suggested by CoPilot so need to make my own if I ever want it
        *   
        *           public void CopyFrom(MapData other)
        *        {
        *            if (other == null) throw new ArgumentNullException(nameof(other));
        *            Resize(other.Width, other.Height);
        *            Array.Copy(other.IsBlocked, IsBlocked, CellCount);
        *            Array.Copy(other.TerrainKeys, TerrainKeys, CellCount);
        *            Array.Copy(other.TerrainCosts, TerrainCosts, CellCount);
        *            Array.Copy(other.BaseCellColors, BaseCellColors, CellCount);
        *            Array.Copy(other.LastPaintLayerIds, LastPaintLayerIds, CellCount);
        *        }
        */


        // Board settings 
        public int Width { get; private set; }                  // Grid width in cells (X dimension)
        public int Height { get; private set; }                 // Grid height in cells (Y dimension)
        public int CellCount => Width * Height;                 // Total cells in grid (Width * Height)
        public int MapGenSeed { get; private set; }             // Seed that was used to generate this map stored MapData  (0 = random)
        public int BuildId { get; private set; }                // What generation is this map 

        public float CellTileSize { get; private set; }         // The size of one cell on this map
        public Vector3 GridOriginWorld { get; private set; }    // World space placement, the bottom-left map corner is center minus half-dimensions: origin = center - (Width/2, Height/2)
        public Vector3 MinWorld { get; private set; }           // Bounds / World-space rectangle that the grid occupies
        public Vector3 MaxWorld { get; private set; }           // Bounds / World-space rectangle that the grid occupies
        public Vector3 GridCenter { get; private set; }         // Center of the map


        // Base defaults (NOT per-cell). These are used to reset the map and define "empty baseline state".
        public int BaseTerrainCost { get; private set; } = 10;  // Baseline movement cost applied to every cell during ResetToBase (fallback = 10)
        public byte BaseTerrainType { get; private set; }       // Baseline terrain type applied to every cell during ResetToBase (ex: Land/Grass)
        public Color32 BaseTerrainColor { get; private set; }   // Baseline visual color applied to every cell during ResetToBase


        // Truth arrays (SoA). These represent the CURRENT map state and are the single source of truth for gameplay + visuals.        public bool[] IsBlocked { get; private set; }           // True = cell is blocked/unwalkable (obstacle)
        public bool[] IsBlocked { get; private set; }           // True = cell is blocked/unwalkable (obstacle). False = walkable.
        public byte[] TerrainTypeIds { get; private set; }      // Current terrain kind per cell (ex: Land, Water, Mountain...) (changes during generation)
        public int[] TerrainCosts { get; private set; }         // Current movement cost per cell (pathfinding reads this)
        public Color32[] BaseCellColors { get; private set; }   // Current base color per cell (what the renderer should show as the "ground", written by generator / renderer reads)

        // NOTE: LastPaintLayerIds is NOT terrain type. It is "who painted last" (used for overwrite logic).
        public byte[] LastPaintLayerIds { get; private set; }   // Generation "paint layer ID": used for overwrite rules + debugging.  /Paint provenance: which generation layer last wrote this cell (overwrite rules)



        public MapData(int width, int height)
        {
            Resize(width, height);
        }


        /// <summary>
        /// Ensures arrays match the given size. Re-allocates only if size changed.
        /// Keeps the SAME MapData object identity (prevents stale MapData refs).
        /// </summary>
        public void Resize(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            if (width == Width && height == Height && IsBlocked != null)
                return;

            Width = Mathf.Max(1, width);
            Height = Mathf.Max(1, height);

            int n = width * height;

            // Allocate only when needed:
            if (IsBlocked == null || IsBlocked.Length != n)
                IsBlocked = new bool[n];

            if (TerrainTypeIds == null || TerrainTypeIds.Length != n)
                TerrainTypeIds = new byte[n];

            if (TerrainCosts == null || TerrainCosts.Length != n)
                TerrainCosts = new int[n];

            if (BaseCellColors == null || BaseCellColors.Length != n)
                BaseCellColors = new Color32[n];

            if (LastPaintLayerIds == null || LastPaintLayerIds.Length != n)
                LastPaintLayerIds = new byte[n];

            //if (Protected == null || Protected.Length != n)
            //    Protected = new bool[n];
        }

        /// <summary>
        /// Clears/resets all truth arrays to a default base state WITHOUT reallocating.
        /// </summary>
        public void InitializeToBase(int width, int height, byte baseTerrainKind, int baseTerrainCost, Color32 baseTerrainColor)
        {
            Resize(width, height);
            SetBaseTerrainCost(baseTerrainCost);
            SetBaseTerrainKind(baseTerrainKind);
            SetBaseTerrainColor(baseTerrainColor);

            ResetCellsToBase();
        }

        public void ResetCellsToBase()
        {
            int n = CellCount;
            for (int i = 0; i < n; i++)
            {
                IsBlocked[i] = false;
                TerrainTypeIds[i] = BaseTerrainType;
                TerrainCosts[i] = BaseTerrainCost;
                BaseCellColors[i] = BaseTerrainColor;
                LastPaintLayerIds[i] = 0;
            }
        }


        public void SetMapMeta(int buildId, int mapGenSeed, Vector3 gridOriginWorld, float cellTileSize)
        {
            BuildId = Mathf.Max(0, buildId);
            MapGenSeed = mapGenSeed;

            GridOriginWorld = gridOriginWorld;
            CellTileSize = Mathf.Max(1e-4f, cellTileSize);

            // Derived world bounds
            MinWorld = GridOriginWorld;
            MaxWorld = GridOriginWorld + new Vector3(Width * CellTileSize, 0f, Height * CellTileSize);
            GridCenter = (MinWorld + MaxWorld) * 0.5f; 
        }




        private void SetBaseTerrainCost(int cost) => BaseTerrainCost = Mathf.Max(1, cost);
        private void SetBaseTerrainKind(byte type) => BaseTerrainType = type;
        private void SetBaseTerrainColor(Color32 color) => BaseTerrainColor = color;




        #region Bounds Helpers

        // Checks if cell coordinates or index are within bounds

        public bool IsValidCellCoord(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;
        public bool IsValidCellIndex(int index) => (uint)index < (uint)CellCount;


        public int CoordToIndex(int x, int y)
        {
            if (!TryCoordToIndex(x, y, out int index))
                throw new ArgumentOutOfRangeException();
            return index;
        }

        // Safe version with bounds checking, use when not sure coordinates are valid
        public bool TryCoordToIndex(int x, int y, out int index)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) 
            { 
                index = -1; 
                return false; 
            }
            index = x + y * Width;
            return true;
        }

        public void IndexToXY(int index, out int x, out int y)
        {
            x = index % Width;
            y = index / Width;
        }

        public Vector3 IndexToWorldCenterXZ(int index, float yOffset = 0f)
        {

            IndexToXY(index, out int x, out int z);

            float wx = GridOriginWorld.x + (x + 0.5f) * CellTileSize;
            float wz = GridOriginWorld.z + (z + 0.5f) * CellTileSize;

            return new Vector3(wx, yOffset, wz);
        }

        // NEW
        public bool TryWorldToCoordXZ(Vector3 worldPos, out int x, out int y)
        {
            float localX = (worldPos.x - GridOriginWorld.x) / CellTileSize;
            float localZ = (worldPos.z - GridOriginWorld.z) / CellTileSize;

            x = Mathf.FloorToInt(localX);
            y = Mathf.FloorToInt(localZ);

            return IsValidCellCoord(x, y);
        }

        // NEW
        public bool TryWorldToIndexXZ(Vector3 worldPos, out int index)
        {
            index = -1;

            if (!TryWorldToCoordXZ(worldPos, out int x, out int y))
                return false;

            return TryCoordToIndex(x, y, out index);
        }


        #endregion


    }

}