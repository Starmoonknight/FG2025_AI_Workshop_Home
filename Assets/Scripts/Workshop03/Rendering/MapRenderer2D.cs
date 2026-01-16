using System;
using UnityEngine;



namespace AI_Workshop03
{ 

    public sealed class MapRenderer2D : MonoBehaviour
    {

        #region Fields

        [Header("References")]
        [SerializeField] private MapManager _mapManager;
        [SerializeField] private Renderer _targetRenderer;

        [Header("Texture Settings")]
        [SerializeField] private bool _flipTextureX;
        [SerializeField] private bool _flipTextureY;

        private MapData _data;

        private Color32[] _cellColors;
        private Color32[] _texturePixels;
        private Texture2D _gridTexture;
        private bool _textureDirty;


        #endregion



        private void Awake()
        {
            if (_mapManager == null)
                _mapManager = FindFirstObjectByType<MapManager>();


            if (_targetRenderer == null)
                Debug.LogWarning("[MapRenderer2D] Target Renderer not assigned.", this);
        }

        private void OnEnable()
        {
            if (_mapManager != null)
            {
                _mapManager.OnMapRebuilt += HandleMapRebuilt;

                // If map already exists, sync immediately
                var current = _mapManager.Data;
                if (current != null)
                    HandleMapRebuilt(current);
            }
        }

        private void OnDisable()
        {
            if (_mapManager != null)
                _mapManager.OnMapRebuilt -= HandleMapRebuilt;
        }

        private void LateUpdate()
        {
            if (!_textureDirty) return;
            FlushTexture();
        }



        #region Ensure Internal State

        private void HandleMapRebuilt(MapData data)
        {
            _data = data;
            EnsureBuffers();
            EnsureTexture();
            RebuildCellColorsFromBase();
            FlushTexture();
        }

        private void EnsureBuffers()
        {
            int n = _data.CellCount;

            if (_cellColors == null || _cellColors.Length != n)
                _cellColors = new Color32[n];

            if (_flipTextureX || _flipTextureY)
            {
                if (_texturePixels == null || _texturePixels.Length != n)
                    _texturePixels = new Color32[n];
            }
            else
            {
                _texturePixels = null;
            }
        }

        private void EnsureTexture()
        {
            if (_gridTexture == null || _gridTexture.width != _data.Width || _gridTexture.height != _data.Height)
            {
                // Create new texture
                _gridTexture = new Texture2D(_data.Width, _data.Height, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
            }

            // Assign to material
            if (_targetRenderer != null)
            {
                // Need to really look into this part more, right now this creates a unique material instance per object?
                // Keeping it as .material for now to avoid unintended global texture replacement.
                //      -> Since it's generating a unique _gridTexture per renderer anyway? I think it's fine for now.

                //var mat = _targetRenderer.material;         // Should maybe change to avoid instancing materials later: var mat = _targetRenderer.sharedMaterial; 

                var mat = _targetRenderer.material;

                // Works for many shaders
                mat.mainTexture = _gridTexture;

                // URP Lit / URP Unlit
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", _gridTexture);

                // Built-in / legacy shaders
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", _gridTexture);

                // Make sure color doesn't tint the texture darker
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            }
        }


        #endregion



        #region Public API

        public void DirtyTexture()
        {
            _textureDirty = true;
        }

        public void FlushTexture()
        {
            _textureDirty = false;
            RefreshTexture();
        }

        public void RefreshFromMapData()
        {
            if (_data == null) return;
            EnsureBuffers();
            EnsureTexture();
            RebuildCellColorsFromBase();
        }

        public void MarkCellTruthChanged(int index)
        {
            if (_data == null) return;
            EnsureBuffers();

            if (!_data.IsValidCellIndex(index)) return;

            _data.IndexToXY(index, out int x, out int y);
            bool odd = ((x + y) & 1) == 1;
            _cellColors[index] = ApplyGridShading(_data.BaseCellColors[index], odd);
            _textureDirty = true;
        }


        public void PaintCell(int index, Color32 color, bool shadeLikeGrid = true, bool skipIfObstacle = true)
        {
            if (_data == null) return;
            EnsureBuffers();

            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));
            if (skipIfObstacle && _data.IsBlocked[index]) return;

            if (shadeLikeGrid)
            {
                _data.IndexToXY(index, out int coordX, out int coordY);
                bool odd = ((coordX + coordY) & 1) == 1;
                _cellColors[index] = ApplyGridShading(color, odd);
            }
            else
            {
                _cellColors[index] = color;
            }

            _textureDirty = true;
        }

        public void PaintMultipleCells(ReadOnlySpan<int> indices, Color32 color, bool shadeLikeGrid = true, bool skipIfObstacle = true)
        {
            for (int i = 0; i < indices.Length; i++)
                PaintCell(indices[i], color, shadeLikeGrid, skipIfObstacle);
        }

        public void PaintCellTint(int index, Color32 overlayColor, float strength01 = 0.35f, bool shadeLikeGrid = true, bool skipIfObstacle = true)
        {
            if (_data == null) return;
            EnsureBuffers();

            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));
            if (skipIfObstacle && _data.IsBlocked[index]) return;

            strength01 = Mathf.Clamp01(strength01);

            Color32 basecolor = _cellColors[index];
            Color32 overlay = overlayColor;

            if (shadeLikeGrid)
            {
                _data.IndexToXY(index, out int x, out int y);
                bool odd = ((x + y) & 1) == 1;
                overlay = ApplyGridShading(overlayColor, odd);
            }

            _cellColors[index] = LerpColor32(basecolor, overlay, strength01);
            _textureDirty = true;
        }

        public void PaintMultipleCellTints(ReadOnlySpan<int> indices, Color32 overlayColor, float strength01 = 0.35f, bool shadeLikeGrid = true, bool skipIfObstacle = true)
        {
            for (int i = 0; i < indices.Length; i++)
                PaintCellTint(indices[i], overlayColor, strength01, shadeLikeGrid, skipIfObstacle);
        }

        public void ShowUnreachableOverlay(int[] reachStamp, int reachStampId, Color32 unreachableColor)
        {
            if (_data == null) return;

            // Make sure buffers exist
            RefreshFromMapData(); // rebuild base colors first (and ensures texture/buffers)

            int n = _data.CellCount;

            for (int i = 0; i < n; i++)
            {
                if (_data.IsBlocked[i]) continue;

                bool isReachable = (reachStamp[i] == reachStampId);
                if (!isReachable)
                {
                    _data.IndexToXY(i, out int x, out int y);
                    bool odd = ((x + y) & 1) == 1;
                    _cellColors[i] = ApplyGridShading(unreachableColor, odd);
                }
            }

            _textureDirty = true;
        }


        #endregion



        #region Texture Aplication and Color Management

        private void RefreshTexture()
        {
            if (_data == null) return;
            EnsureTexture();
            EnsureBuffers();


            // fast path if visuals don't need to be fliped 
            if (!_flipTextureX && !_flipTextureY)
            {
                _gridTexture.SetPixels32(_cellColors);
                _gridTexture.Apply(false);
                return;
            }

            // when switching from XY Quad to a XZ Plane visuals flipped. The Plane’s UV orientation may not match the grid row/column order, fix is below.
            // Use DebugCornerColorTest to see what _flipTextureX/Y needs to be on.

            int n = _data.CellCount;
            if (_texturePixels == null || _texturePixels.Length != n)
                _texturePixels = new Color32[n];

            int width = _data.Width;
            int height = _data.Height;

            for (int y = 0; y < height; y++)
            {
                int srcRowBase = y * width;

                int dstRowY = _flipTextureY ? (height - 1 - y) : y;
                int dstRowBase = dstRowY * width;

                if (!_flipTextureX)
                {
                    // if only Y needed to be flipped 
                    Array.Copy(_cellColors, srcRowBase, _texturePixels, dstRowBase, width);
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        int srcIndex = srcRowBase + x;

                        int dstX = width - 1 - x;
                        int dstIndex = dstRowBase + dstX;

                        _texturePixels[dstIndex] = _cellColors[srcIndex];
                    }
                }
            }

            _gridTexture.SetPixels32(_texturePixels);
            _gridTexture.Apply(false);
        }

        private void RebuildCellColorsFromBase()
        {
            int n = _data.CellCount;
            for (int i = 0; i < n; i++)
            {
                _data.IndexToXY(i, out int x, out int y);
                bool odd = ((x + y) & 1) == 1;
                _cellColors[i] = ApplyGridShading(_data.BaseCellColors[i], odd);
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


        #endregion

    }

}

