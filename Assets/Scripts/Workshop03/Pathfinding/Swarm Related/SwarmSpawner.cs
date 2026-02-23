using AI_Workshop03.AI;
using UnityEngine;


namespace AI_Workshop03
{



    public sealed class SwarmSpawner : MonoBehaviour
    {

        [Header("References")]
        [SerializeField] private NavigationServiceManager m_navigationService;
        [SerializeField] private MapManager m_mapManager;

        [Header("Prefabs")]
        [Tooltip("Prefab that has SwarmingAgent + AgentBrain + AgentPathRequester + AgentPathBuffer + AgentMapSense")]
        [SerializeField] private SwarmingAgent m_agentPrefab;

        [Header("Visualization Colors")]
        [SerializeField] private Color32 m_leaderColor = new(255, 0, 0, 255);
        [SerializeField] private Color32 m_followerColor = new(255, 150, 0, 255);

        [Header("Spawn")]
        [SerializeField, Min(1)] private int m_followerCount = 10;
        [SerializeField] private Vector2 m_spawnAreaSize = new Vector2(10f, 10f);        
        [SerializeField, Min(0f)] private float m_agentPlaneOffsetY = 0.5f;  // Y if XZ, Z if XY     // Lock to XZ plane: prevents drift and ensures grid world->index logic stays consistent.

        [Header("Target")]
        [SerializeField] private Transform m_target; // eg. Player for this Lab


        private bool _spawned;


        private void Awake()
        {
            if (m_navigationService == null) m_navigationService = FindFirstObjectByType<NavigationServiceManager>();
            if (m_mapManager == null) m_mapManager = FindAnyObjectByType<MapManager>();

            if (m_navigationService == null)
                Debug.LogError("SwarmSpawner: No NavigationServiceManager found in scene.", this);

            if (m_mapManager == null)
                Debug.LogError("SwarmSpawner: No MapManager found in scene.", this);
        }

        private void OnEnable()
        {
            if (m_mapManager != null)
            {
                m_mapManager.OnMapRebuiltVisualsReady += HandleMapRebuilt;

                // If map already exists, sync immediately (similar pattern as MapRenderer2D)
                if (m_mapManager.Data != null)
                    HandleMapRebuilt(m_mapManager.Data);
            }
        }

        private void OnDisable()
        {
            if (m_mapManager != null)
                m_mapManager.OnMapRebuiltVisualsReady -= HandleMapRebuilt;
        }



        private void HandleMapRebuilt(MapData data)
        {
            if (_spawned) return;

            if (m_agentPrefab == null || m_navigationService == null || m_mapManager == null) return;

            StartCoroutine(SpawnAfterVisuals());
        }


        private System.Collections.IEnumerator SpawnAfterVisuals()
        {
            // Give render/world-objects a moment to catch up
            yield return new WaitForEndOfFrame();

            // Spawn leader
            var leader = SpawnOne(isLeader: true, leader: null);

            // Spawn followers
            for (int i = 0; i < m_followerCount; i++)
                SpawnOne(isLeader: false, leader: leader);

            _spawned = true;
        }


        private SwarmingAgent SpawnOne(bool isLeader, SwarmingAgent leader)
        {
            var agent = Instantiate(m_agentPrefab, RandomSpawnPosOnWalkableCell(), Quaternion.identity);
            Color32 color = isLeader ? m_leaderColor : m_followerColor;

            InjectServices(agent);                          // sets nav service ref 

            agent.Configure(isLeader, leader, m_target);    // sets IsLeader, follower->leader link and assigns Target
            agent.SetColor(color);                          // visual differentiation of leader vs followers
            return agent;
        }

        private void InjectServices(SwarmingAgent agent)
        {
            // Map
            var requester = agent.GetComponent<AgentPathRequester>();
            if (requester != null && (requester.NavigationService == null || requester.NavigationService != m_navigationService)) 
                requester.SetNavigationService(m_navigationService);

            // Nav
            var mapReader = agent.GetComponent<AgentMapSense>();
            if (mapReader != null && (mapReader.MapManager == null || mapReader.MapManager != m_mapManager)) mapReader.SetMapManager(m_mapManager);
        }


        private Vector3 RandomSpawnPosOnWalkableCell()
        {
            var data = m_mapManager.Data;
            if (data == null) return transform.position;

            // Try a bunch of random cells until it finds a walkable one
            const int attempts = 5000;
            for (int i = 0; i < attempts; i++)
            {
                int idx = Random.Range(0, data.CellCount);
                if (!data.IsBlocked[idx])
                {
                    Vector3 p = data.IndexToWorldCenterXZ(idx, m_agentPlaneOffsetY);
                    return p;
                }
            }

            // Fallback: at least stay on the grid center if everything failed
            Vector3 fallback = data.GridCenter;
            fallback.y = m_agentPlaneOffsetY;
            return fallback;
        }


        private void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position, new Vector3(m_spawnAreaSize.x, 0.1f, m_spawnAreaSize.y));
        }




    }

}