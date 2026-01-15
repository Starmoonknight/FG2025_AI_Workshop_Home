using System;
using UnityEngine;


namespace AI_Workshop03
{

    // Version2 of BoardManager    -> MapManager
    // MapManager.cs         -   Purpose: the "header / face file", top-level API and properties
    public partial class MapManager : MonoBehaviour
    {

        [Header("Game Camera Settings")]
        [SerializeField] private Camera _mainCamera;
        [SerializeField] private float _cameraPadding = 1f;

        [Header("Board Settings")]
        [SerializeField] private Renderer _boardRenderer;
        [SerializeField, Min(1)] private int _width = 10;
        [SerializeField, Min(1)] private int _height = 10;

        [Header("Texture Mapping")]
        [SerializeField] private bool _flipTextureX = true;
        [SerializeField] private bool _flipTextureY = true;


        [Header("Map Generation Settings")]
        [SerializeField] private int _seed = 0;
        [SerializeField]  private int _lastGeneratedSeed = 0;
        [SerializeField, Range(0f, 1f)] private float _minUnblockedPercent = 0.5f;
        [SerializeField, Range(0f, 1f)] private float _minReachablePercent = 0.75f;
        [SerializeField]  private int _maxGenerateAttempts = 50;

        private System.Random _goalRng;

        [Header("Generation Data")]
        [SerializeField] private TerrainTypeData[] _terrainData;

        private readonly MapDataGenerator _generator = new MapDataGenerator();



        [Header("Colors")]
        [SerializeField]
        private Color32 _walkableColor = new(255, 255, 255, 255);       // White
        [SerializeField]
        private Color32 _obstacleColor = new(0, 0, 0, 255);             // Black
        [SerializeField]
        private Color32 _unReachableColor = new(255, 150, 150, 255);    // Light Red


        public int Width => _width;
        public int Height => _height;
        public int CellCount => _cellCount;
        public int MinTerrainCost => _minTerrainCost;
        public int LastGeneratedSeed => _lastGeneratedSeed;



        private const float UNITY_PLANE_SIZE = 10f; // Plane is 10x10 units at scale 1

        private static readonly (int dirX, int dirY)[] Neighbors4 =
        {
            (-1, 0), (1, 0), (0, -1), (0, 1)
        };

        // Neighbor offsets for 8-directional movement with associated step costs, dx stands for change in x, dy for change in y
        private static readonly (int dirX, int dirY, int stepCost)[] Neighbors8 =
        {
            (-1,  0, 10),  //Left
            ( 1,  0, 10),  //Right
            ( 0, -1, 10),  //Down
            ( 0,  1, 10),  //Up
            (-1, -1, 14),  //Bottom-Left
            ( 1, -1, 14),  //Bottom-Right
            (-1,  1, 14),  //Top-Left
            ( 1,  1, 14)   //Top-Right
        };



        private void Awake()
        {
            GenerateNewGameBoard();

            //DebugCornerColorTest(); 
        }

        private void LateUpdate()
        {
            if (!_textureDirty) return;
            _textureDirty = false;
            RefreshTexture();
        }



    }

}
