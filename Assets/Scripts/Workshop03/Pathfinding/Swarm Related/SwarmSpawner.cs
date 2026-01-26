using AI_Workshop03.AI;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;


namespace AI_Workshop03
{



    public sealed class SwarmSpawner : MonoBehaviour
    {

        [Header("References")]
        [SerializeField] private NavigationServiceManager _navigationService;
        [SerializeField] private MapManager _mapManager;

        [Header("Prefabs")]
        [Tooltip("Prefab that has SwarmingAgent + AgentBrain + AgentPathRequester + AgentPathBuffer + AgentMapSense")]
        [SerializeField] private SwarmingAgent _agentPrefab;

        [Header("Spawn")]
        [SerializeField, Min(1)] private int _followerCount = 10;
        [SerializeField] private Vector2 _spawnAreaSize = new Vector2(10f, 10f);        
        [SerializeField, Min(0f)] private float _agentPlaneOffsetY = 0.5f;  // Y if XZ, Z if XY     // Lock to XZ plane: prevents drift and ensures grid world->index logic stays consistent.

        [Header("Target")]
        [SerializeField] private Transform _target; // eg Player for this Lab


        private bool _spawned;


        private void Awake()
        {
            if (_navigationService == null) _navigationService = FindFirstObjectByType<NavigationServiceManager>();
            if (_mapManager == null) _mapManager = FindAnyObjectByType<MapManager>();

            if (_navigationService == null)
                Debug.LogError("SwarmSpawner: No NavigationServiceManager found in scene.", this);

            if (_mapManager == null)
                Debug.LogError("SwarmSpawner: No MapManager found in scene.", this);
        }

        private void OnEnable()
        {
            if (_mapManager != null)
            {
                _mapManager.OnMapRebuiltVisualsReady += HandleMapRebuilt;

                // If map already exists, sync immediately (same pattern as MapRenderer2D)
                if (_mapManager.Data != null)
                    HandleMapRebuilt(_mapManager.Data);
            }
        }

        private void OnDisable()
        {
            if (_mapManager != null)
                _mapManager.OnMapRebuiltVisualsReady -= HandleMapRebuilt;
        }



        private void HandleMapRebuilt(MapData data)
        {
            if (_spawned) return;

            if (_agentPrefab == null || _navigationService == null || _mapManager == null) return;

            StartCoroutine(SpawnAfterVisuals());
        }


        private System.Collections.IEnumerator SpawnAfterVisuals()
        {
            // Give render/world-objects a moment to catch up
            yield return new WaitForEndOfFrame();

            // Spawn leader
            var leader = SpawnOne(isLeader: true, leader: null);

            // Spawn followers
            for (int i = 0; i < _followerCount; i++)
                SpawnOne(isLeader: false, leader: leader);

            _spawned = true;
        }


        private SwarmingAgent SpawnOne(bool isLeader, SwarmingAgent leader)
        {
            var agent = Instantiate(_agentPrefab, RandomSpawnPosOnWalkableCell(), Quaternion.identity);

            InjectServices(agent);                          // sets nav service ref 

            agent.Configure(isLeader, leader, _target);     // sets IsLeader, follower->leader link and assigns Target
            return agent;
        }

        private void InjectServices(SwarmingAgent agent)
        {
            // Map
            var requester = agent.GetComponent<AgentPathRequester>();
            if (requester != null && (requester.NavigationService == null || requester.NavigationService != _navigationService)) 
                requester.SetNavigationService(_navigationService);

            // Nav
            var mapReader = agent.GetComponent<AgentMapSense>();
            if (mapReader != null && (mapReader.MapManager == null || mapReader.MapManager != _mapManager)) mapReader.SetMapManager(_mapManager);
        }


        private Vector3 RandomSpawnPosOnWalkableCell()
        {
            var data = _mapManager.Data;
            if (data == null) return transform.position;

            // Try a bunch of random cells until we find a walkable one
            const int attempts = 5000;
            for (int i = 0; i < attempts; i++)
            {
                int idx = Random.Range(0, data.CellCount);
                if (!data.IsBlocked[idx])
                {
                    Vector3 p = data.IndexToWorldCenterXZ(idx, _agentPlaneOffsetY);
                    return p;
                }
            }

            // Fallback: at least stay on the grid center if everything failed
            Vector3 fallback = data.GridCenter;
            fallback.y = _agentPlaneOffsetY;
            return fallback;
        }


        private void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position, new Vector3(_spawnAreaSize.x, 0.1f, _spawnAreaSize.y));
        }




    }

}