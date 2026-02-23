using System;
using UnityEngine;



namespace AI_Workshop03
{ 

    public sealed class MapRenderer2D : MonoBehaviour
    {

        #region Fields

        [Header("References")]
        [SerializeField] private MapManager _mapManager;

        [Header("Texture Settings")]
        [SerializeField] private bool _autoDetectFlip = true;
        [SerializeField] private bool _flipTextureX;
        [SerializeField] private bool _flipTextureY;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private bool _lastFlipX;
        private bool _lastFlipY;
        private bool _flipInitialized;
#endif


        private MapData _data;

        private Color32[] _cellColors;
        private Color32[] _texturePixels;
        private Texture2D _gridTexture;
        private bool _textureDirty;

        private MaterialPropertyBlock _matPropertyBlock;
        private Color _boardTint = Color.white;

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");        // URP
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");        // Built-in
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");    // URP
        private static readonly int ColorId = Shader.PropertyToID("_Color");            // Built-in


        private Renderer TargetRenderer => _mapManager != null ? _mapManager.BoardRenderer : null;
        private Collider BoardCollider => _mapManager != null ? _mapManager.BoardCollider : null;

        public Color BoardTint => _boardTint;
        public bool FlipTextureX => _flipTextureX;
        public bool FlipTextureY => _flipTextureY;
        public bool FlipChangedThisRebuild { get; private set; }


        #endregion


        #region Lifecycle

        private void Awake()
        {
            if (_mapManager == null)
                _mapManager = FindFirstObjectByType<MapManager>();


            if (TargetRenderer == null)
                Debug.LogWarning("[MapRenderer2D] Target Renderer not assigned.", this);
        }

        private void OnEnable()
        {
            if (_mapManager == null)
                _mapManager = FindFirstObjectByType<MapManager>();

            if (_mapManager != null)
            {
                _mapManager.OnMapRebuiltDataReady += HandleMapRebuilt;

                // If map already exists, sync immediately
                var current = _mapManager.Data;
                if (current != null)
                    HandleMapRebuilt(current);
            }
        }

        private void OnDisable()
        {
            if (_mapManager != null)
                _mapManager.OnMapRebuiltDataReady -= HandleMapRebuilt;
        }

        private void LateUpdate()
        {
            if (!_textureDirty) return;
            FlushTexture();
        }


        #endregion



        #region  Map Hooks and Ensure Internal State

        private void HandleMapRebuilt(MapData data)
        {
            _data = data;

            EnsureBuffers();
            EnsureTexture();
            //ApplyTextureToRenderer();     // EnsureTexture() allready calls  ApplyTextureToRenderer(); inside of it, so this line is redundant.

            AutoDetectTextureFlipFromUV();
            RebuildCellColorsFromBase();
            FlushTexture();


            // No always-on Debug.Log here.
            // Layout mismatch warnings should be owned by reporter/MapManager call-site.
            /*
            if (TargetRenderer != null)
                Debug.Log($"RendererPos={TargetRenderer.transform.position} GridCenter={_data.GridCenter}");
            */
        }

        private void EnsureBuffers()
        {
            // See if this is a better check? And if so use everywhere! 
            /*
            if (_data == null && _mapManager.Data != null)
                _data = _mapManager.Data;
            else if (_data == null)
            {
                Debug.LogWarning("MapRenderer2D could not find any map data!");
                return;
            }
            */

            if (_data == null) return;

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
            if (_data == null) return;

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
            ApplyTextureToRenderer();
        }

        private void ApplyTextureToRenderer()
        {
            // Need to really look into this part more, right now this creates a unique material instance per object?
            // Keeping it as .material for now to avoid unintended global texture replacement.
            //      -> Since it's generating a unique _gridTexture per renderer anyway? I think it's fine for now.
            // also look into the use of MaterialPropertyBlock and sharedMaterial

            //var mat = _targetRenderer.material;         // Should maybe change to avoid instancing materials later: var mat = _targetRenderer.sharedMaterial; 
            // but unless the board has it's own material using sharedMaterial on it could affect and ruin all objects useing the unity default material

            // using a property block should allow me to change texture by effecting material properties per renderer,
            // without making a new one each time, and "should" protect other objects using the same mat, I think.. 

            // NO LONGER TRUE
            // Decided to go with using MPB (material property block) for now,
            // but keeping comments for progression lookback. And still need to read up on mpb 


            var rend = TargetRenderer;
            if (rend == null || _gridTexture == null) return;

            _matPropertyBlock ??= new MaterialPropertyBlock();
            rend.GetPropertyBlock(_matPropertyBlock);

            // Texture override (URP + Built-in fallback)
            _matPropertyBlock.SetTexture(BaseMapId, _gridTexture);
            _matPropertyBlock.SetTexture(MainTexId, _gridTexture);

            // Tinting (URP + Built-in fallback)
            _matPropertyBlock.SetColor(BaseColorId, _boardTint);
            _matPropertyBlock.SetColor(ColorId, _boardTint);

            rend.SetPropertyBlock(_matPropertyBlock);
        }

        // The visual flipping caused soooo much problems when having my grid be in XZ space compared to XY space.... 
        // This method should the visuals stay correct as long as the board mesh stays a flat, unrotated XZ plane. And that MapData world mapping matches the board placement.
        private void AutoDetectTextureFlipFromUV()
        {
            if (!_autoDetectFlip) return;
            if (_mapManager == null) return;
            if (TargetRenderer == null) return;
            if (_data == null) return;

            // this check needs at least 2x2 to compare neighboring UV direction
            if (_data.Width < 2 || _data.Height < 2) return;

            var col = BoardCollider; 
            if (col == null) return;

            // sample three points(cell centers) on the map in world space 
            Vector3 p0 = _data.IndexToWorldCenterXZ(_data.CoordToIndex(0, 0), yOffset: 0f);
            Vector3 pX = _data.IndexToWorldCenterXZ(_data.CoordToIndex(1, 0), yOffset: 0f);
            Vector3 pZ = _data.IndexToWorldCenterXZ(_data.CoordToIndex(0, 1), yOffset: 0f);

            // send a raycast at each point, straight down,
            float castHeight = 50f;                              // NOTE: 50f is assumed to be enough, if board ever moves upwards significantly or is rotated, detection can fail
            Vector3 up = Vector3.up * castHeight;

            // check if each comparison point is where assumed to be 
            bool ok0 = TryUVAtWorldPoint(col, p0 + up, out Vector2 uv0);
            bool okX = TryUVAtWorldPoint(col, pX + up, out Vector2 uvX);
            bool okZ = TryUVAtWorldPoint(col, pZ + up, out Vector2 uvZ);

            // if everything is ok leave textures alone, otehrwise attemt to fix the mirroring problem
            if (!ok0 || !okX || !okZ) return;

            // if moving +X makes U go down, the texture should be fixed by flipping X
            bool newX = uvX.x < uv0.x;

            // if moving +Z makes V go down, the texture should be fixed by flipping Y
            bool newY = uvZ.y < uv0.y;


#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_flipInitialized || newX != _lastFlipX || newY != _lastFlipY)
            {
                _flipInitialized = true;
                _lastFlipX = _flipTextureX;
                _lastFlipY = _flipTextureY;

                // change this to call a reporter anomaly hook via MapManager later
            }
#endif

            FlipChangedThisRebuild = (newX != _flipTextureX) || (newY != _flipTextureY);
            _flipTextureX = newX;
            _flipTextureY = newY;

            if (FlipChangedThisRebuild)
            {
                EnsureBuffers();        // flip affects _texturePixels allocation
                _textureDirty = true;   // ensure visuals update
            }
        }

        private static bool TryUVAtWorldPoint(Collider col, Vector3 rayOrigin, out Vector2 uv)
        {
            uv = default;

            Ray ray = new Ray(rayOrigin, Vector3.down);
            if (!col.Raycast(ray, out RaycastHit hit, 999f)) return false;

            uv = hit.textureCoord;
            return true;
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
            EnsureTexture(); 
            RefreshTexture();
        }

        public void RefreshFromMapData()
        {
            if (_data == null) return;
            EnsureBuffers();
            EnsureTexture();
            RebuildCellColorsFromBase();
        }

        public void MarkCellTruthChanged(int index, bool updateVisuals = true)
        {
            if (_data == null) return;
            EnsureBuffers();

            if (!_data.IsValidCellIndex(index)) return;

            _data.IndexToXY(index, out int x, out int z);
            bool odd = ((x + z) & 1) == 1;
            _cellColors[index] = ApplyGridShading(_data.BaseCellColors[index], odd);

            if (updateVisuals) _textureDirty = true;
        }

        public void MarkMultipleCellTruthsChanged(ReadOnlySpan<int> indices)
        {
            for (int i = 0; i < indices.Length; i++)
                MarkCellTruthChanged(indices[i], updateVisuals: false);

            _textureDirty = true; // one final dirty set
        }


        public void PaintCell(int index, Color32 color, 
            bool shadeLikeGrid = true, bool skipIfObstacle = true, 
            bool updateVisuals = true)
        {
            if (_data == null) return;
            EnsureBuffers();

            if (!_data.IsValidCellIndex(index)) throw new ArgumentOutOfRangeException(nameof(index));
            if (skipIfObstacle && _data.IsBlocked[index]) return;

            if (shadeLikeGrid)
            {
                _data.IndexToXY(index, out int coordX, out int coordZ);
                bool odd = ((coordX + coordZ) & 1) == 1;
                _cellColors[index] = ApplyGridShading(color, odd);
            }
            else
            {
                _cellColors[index] = color;
            }

            if (updateVisuals) _textureDirty = true;
        }

        public void PaintMultipleCells(ReadOnlySpan<int> indices, Color32 color, 
            bool shadeLikeGrid = true, 
            bool skipIfObstacle = true)
        {
            for (int i = 0; i < indices.Length; i++)
                PaintCell(indices[i], color, shadeLikeGrid, skipIfObstacle, updateVisuals: false);

            _textureDirty = true;
        }

        public void PaintCellTint(int index, Color32 overlayColor, float strength01 = 0.35f, 
            bool shadeLikeGrid = true, bool skipIfObstacle = true, 
            bool updateVisuals = true)
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
                _data.IndexToXY(index, out int x, out int z);
                bool odd = ((x + z) & 1) == 1;
                overlay = ApplyGridShading(overlayColor, odd);
            }

            _cellColors[index] = LerpColor32(basecolor, overlay, strength01);

            if (updateVisuals) _textureDirty = true;
        }

        public void PaintMultipleCellTints(ReadOnlySpan<int> indices, Color32 overlayColor, float strength01 = 0.35f, 
            bool shadeLikeGrid = true, bool skipIfObstacle = true)
        {
            for (int i = 0; i < indices.Length; i++)
                PaintCellTint(indices[i], overlayColor, strength01, shadeLikeGrid, skipIfObstacle, updateVisuals: false);

            _textureDirty = true;
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
                    _data.IndexToXY(i, out int x, out int z);
                    bool odd = ((x + z) & 1) == 1;
                    _cellColors[i] = ApplyGridShading(unreachableColor, odd);
                }
            }

            _textureDirty = true;
        }

        /// <summary>
        /// NOTE: Big difference from the other color changes, this does not update the MapData and only tints the whole board! 
        ///       used for adding effects like: highlight board red when invalid, dim board during replay, etc.  
        /// </summary>
        public void SetBoardOverlayTint(Color tint)
        {
            _boardTint = tint;
            ApplyBoardTintToRenderer();
        }

        /// <summary>
        /// NOTE: Big difference from the other color changes, this does not update the MapData and only tints the whole board! 
        ///       used for adding effects like: highlight board red when invalid, dim board during replay, etc.  
        /// </summary>
        public void ClearBoardOverlayTint()
        {
            _boardTint = Color.white;
            ApplyBoardTintToRenderer();
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
                    // flip both Y and X
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
                _data.IndexToXY(i, out int x, out int z);
                bool odd = ((x + z) & 1) == 1;
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

        private void ApplyBoardTintToRenderer()
        {
            var rend = TargetRenderer;
            if (rend == null) return;

            _matPropertyBlock ??= new MaterialPropertyBlock();
            rend.GetPropertyBlock(_matPropertyBlock);

            // Apply tint for both URP and Built-in pipelines
            _matPropertyBlock.SetColor(BaseColorId, _boardTint);
            _matPropertyBlock.SetColor(ColorId, _boardTint);

            rend.SetPropertyBlock(_matPropertyBlock);
        }


        #endregion





        private void OnDrawGizmosSelected()
        {
            if (_data == null) return;

            // draws world bounds of the grid, should help with checking that code aligns 
            Gizmos.color = Color.yellow;

            Vector3 min = _data.MinWorld;
            Vector3 max = _data.MaxWorld;

            // builds a box centered on the grid footprint
            Vector3 center = _data.GridCenter;
            Vector3 size = new Vector3(max.x - min.x, 0.02f, max.z - min.z);

            Gizmos.DrawWireCube(center + Vector3.up * 0.01f, size);
        }



    }

}

