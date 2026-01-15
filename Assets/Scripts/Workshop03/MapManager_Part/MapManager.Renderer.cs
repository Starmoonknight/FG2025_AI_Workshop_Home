using System;
using UnityEngine;


namespace AI_Workshop03
{
    // MapManager.Renderer.cs               -   Purpose (visual 2D): Texture2D + UV flipping + “upload to material” + grid shading / overlays + dirty flag / uploading
    public partial class MapManager
    {

        #region Fields - Grid Visualization

        private Color32[] _cellColors;
        private Color32[] _texturePixels;
        private Texture2D _gridTexture;
        private bool _textureDirty;


        #endregion



        private void RefreshTexture()
        {
            if (_gridTexture == null) return;

            // fast path if visuals don't need to be fliped 
            if (!_flipTextureX && !_flipTextureY)
            {
                _gridTexture.SetPixels32(_cellColors);
                _gridTexture.Apply(false);
                return;
            }

            // when switching from XY Quad to a XZ Plane visuals flipped. The Plane’s UV orientation may not match the grid row/column order, fix is below.
            // Use DebugCornerColorTest to see what _flipTextureX/Y needs to be on.

            if (_texturePixels == null || _texturePixels.Length != _cellCount)
                _texturePixels = new Color32[_cellCount];

            for (int y = 0; y < _height; y++)
            {
                int srcRowBase = y * _width;

                int dstRowY = _flipTextureY ? (_height - 1 - y) : y;
                int dstRowBase = dstRowY * _width;

                if (!_flipTextureX)
                {
                    // if only Y needed to be flipped 
                    Array.Copy(_cellColors, srcRowBase, _texturePixels, dstRowBase, _width);
                }
                else
                {
                    for (int x = 0; x < _width; x++)
                    {
                        int srcIndex = srcRowBase + x;

                        int dstX = _width - 1 - x;
                        int dstIndex = dstRowBase + dstX;

                        _texturePixels[dstIndex] = _cellColors[srcIndex];
                    }
                }
            }

            _gridTexture.SetPixels32(_texturePixels);
            _gridTexture.Apply(false);
        }

        public void FlushTexture()
        {
            _textureDirty = false;
            RefreshTexture();
        }

        public void ResetColorsToBase()
        {
            RebuildCellColorsFromBase();
            _textureDirty = true;

        }

        private void RebuildCellColorsFromBase()
        {
            for (int i = 0; i < _cellCount; i++)
            {
                IndexToXY(i, out int x, out int y);
                bool odd = ((x + y) & 1) == 1;
                _cellColors[i] = ApplyGridShading(_baseCellColors[i], odd);
            }

            _textureDirty = true;
        }

        private static Color32 ApplyGridShading(Color32 c, bool odd)
        {
            // Small change so it’s visible but not ugly
            const int delta = 12;

            int d = odd ? +delta : -delta;

            byte r = (byte)Mathf.Clamp(c.r + d, 0, 255);
            byte g = (byte)Mathf.Clamp(c.g + d, 0, 255);
            byte b = (byte)Mathf.Clamp(c.b + d, 0, 255);

            return new Color32(r, g, b, c.a);
        }

        private static Color32 LerpColor32(Color32 a, Color32 b, float t)
        {
            t = Mathf.Clamp01(t);
            int ti = Mathf.RoundToInt(t * 255f);
            int inv = 255 - ti;

            byte r = (byte)((a.r * inv + b.r * ti + 127) / 255);
            byte g = (byte)((a.g * inv + b.g * ti + 127) / 255);
            byte bl = (byte)((a.b * inv + b.b * ti + 127) / 255);

            // should keep fully opaque for an opaque material
            return new Color32(r, g, bl, 255);
        }




    }

}
