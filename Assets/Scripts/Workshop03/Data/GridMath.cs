


using System;
using UnityEngine;

namespace AI_Workshop03
{
    public class GridMath
    {

        /// <summary>
        /// Fast bounds check for 2D grid coordinates.
        /// Returns true if (x,y) is inside [0..gridWidth) and [0..gridHeight).
        /// Use when you need to validate coords without allocating or throwing.
        /// </summary>
        public static bool IsValidCoord(int x, int y, int gridWidth, int gridHeight) => 
            (uint)x < (uint)gridWidth && (uint)y < (uint)gridHeight;

        /// <summary>
        /// Fast bounds check for a 1D cell index.
        /// Returns true if index is inside [0..cellCount).
        /// Use before reading/writing arrays when index validity is uncertain.
        /// </summary>
        public static bool IsValidIndex(int index, int cellCount) =>
            (uint)index < (uint)cellCount;


        /// <summary>
        /// Converts (x,y) to a 1D index with NO bounds checking.
        /// Fastest option. Only use when you already know x/y are valid, or in internal loops where you already ensured bounds.
        /// </summary>
        public static int CoordToIndexUnchecked(int x, int y, int width) => x + y * width;

        /// <summary>
        /// Converts (x,y) to a 1D index with bounds checking.
        /// Throws if (x,y) is outside the grid. Use when invalid coords indicate a bug
        /// and you want to fail loudly (debug/editor/strict code paths).
        /// </summary>
        public static int CoordToIndexChecked(int x, int y, int width, int height)
        {
            if (!TryCoordToIndex(x, y, width, height, out int index))
                throw new ArgumentOutOfRangeException($"({x},{y}) outside grid {width}x{height}");

            return index;
        }

        /// <summary>
        /// Safe coord-to-index conversion without exceptions.
        /// Returns true and outputs index if (x,y) is valid, otherwise returns false and sets index = -1.
        /// Use for uncertain inputs (random picks, user-driven coords, boundary scanning).
        /// </summary>
        public static bool TryCoordToIndex(int x, int y, int width, int height, out int index)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            {
                index = -1;
                return false;
            }

            index = x + y * width;
            return true;
        }

        /// <summary>
        /// Converts a 1D index back to (x,y).
        /// No bounds checking: assumes index is already valid.
        /// Use with IsValidIndex(...) or trusted indices from internal loops where you already ensured bounds.
        /// </summary>
        public static void IndexToXY(int index, int width, out int x, out int y)
        {
            x = index % width;
            y = index / width;
        }
    }

}
