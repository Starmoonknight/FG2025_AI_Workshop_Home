using UnityEngine;


namespace AI_Workshop03.AI
{

    /// <summary>
    /// High-level decision maker:
    /// - Leader: patrol + chase + alerted fallback (requests A* via AgentPathRequester)
    /// - Follower: stays in swarm-follow (movement handled by SwarmingAgent steering)
    /// </summary>
    [RequireComponent(typeof(AgentMapSense))]
    [RequireComponent(typeof(AgentPathBuffer))]
    [RequireComponent(typeof(AgentPathRequester))]
    [RequireComponent(typeof(SwarmingAgent))]
    public sealed class AgentBrain : MonoBehaviour
    {

        // NOTE: If I in the future would want any inheritance, say for example:    " class BossBrain : AgentBrain "
        //       and other override logic, then the class must be changed to not use sealed anymore.
        //       Use sealed when the script is a final component, like most of the other swarm Agent scripts will be (AgentMapSense, AgentPathBuffer, etc.)


        [Header("Ranges")]
        [SerializeField, Min(0f)] private float m_engageRange = 6f;
        [SerializeField, Min(0f)] private float m_disengageRange = 9f;

        [Header("Timers")]
        [SerializeField, Min(0.05f)] private float m_repathInterval = 0.5f;
        [SerializeField, Min(0f)] private float m_alertedDuration = 2.5f;

        [Header("Patrol")]
        [SerializeField, Min(1)] private int m_patrolPickAttempts = 64;

        [Header("Repath gates")]
        [SerializeField, Min(0f)] private float m_blockedLookAhead = 0.9f; // similar to avoidance probe
        [SerializeField] private bool m_enableBlockedRepath = true;

        [Header("Patrol reachable goals")]
        [SerializeField, Range(0f, 1f)] private float m_patrolMinManhattanFactor = 0.30f;
        [SerializeField, Min(0)] private int m_patrolMinManhattanClampMin = 2;
        [SerializeField, Min(0)] private int m_patrolMinManhattanClampMax = 200;

        [Header("Debug")]
        [SerializeField] private bool m_logStateChanges = false;

        private SwarmingAgent m_agent;
        private AgentMapSense m_mapSense;
        private AgentPathRequester m_pathRequester;
        private AgentPathBuffer m_pathBuffer;

        private float m_nextRepathTime;
        private float m_alertedUntil;
        private Vector3 m_lastSeenPos;

        private SwarmState m_state;




        private void Awake()
        {
            m_agent = GetComponent<SwarmingAgent>();
            m_mapSense = GetComponent<AgentMapSense>();
            m_pathRequester = GetComponent<AgentPathRequester>();
            m_pathBuffer = GetComponent<AgentPathBuffer>();
        }

        private void OnEnable()
        {
            // Initialize state based on role, is this a Leader or a Follower? Can be updated to change roll during runtime
            if (m_agent != null && m_agent.IsLeader)
                TransitionTo(SwarmState.STATE_Patrolling);
            else
                TransitionTo(SwarmState.STATE_SwarmingFollow);

            if (m_mapSense != null)
                m_mapSense.OnDataChanged += HandleMapDataChanged;
        }

        private void Update()
        {
            if (m_agent == null || m_mapSense == null) return;
            if (m_mapSense.Data == null) return;

            if (m_agent.IsLeader)
                UpdateLeader();
            else
                UpdateFollower();
        }

        private void OnDisable()
        {
            if (m_mapSense != null)
                m_mapSense.OnDataChanged -= HandleMapDataChanged;
        }



        private void HandleMapDataChanged(MapData _)
        {
            if (m_mapSense == null || m_mapSense.Data == null) return;

            // Reset path and timers so it doesn't keep following stale path indices
            m_pathBuffer.Clear();
            m_nextRepathTime = 0f;

            // If unit is standing on a blocked tile after rebuild, try to "snap" to nearest walkable (movement-layer method).
            if (m_mapSense.TryWorldToIndex(transform.position, out int startIdx))
            {
                if (m_mapSense.Data.IsBlocked[startIdx])
                    m_agent.TrySnapToNearestWalkable(radius: 6);
            }
            else
            {
                // Outside grid: try to recover (snap may also handle this if you extend it later)
                return;
            }


            // Recompute a valid start index AFTER potential snap
            if (!m_mapSense.TryGetValidStartIndexFromCurrentPos(out startIdx))
                return;


            // Decide what to do based on role + current state. Followers don't need to do anything else here
            if (!m_agent.IsLeader)
                return;


            switch (m_state)
            {
                case SwarmState.STATE_Engaging:
                    {
                        // If unit still have a target, immediately request a fresh chase path.
                        Transform target = m_agent.Target;
                        if (target == null) 
                        { 
                            TransitionTo(SwarmState.STATE_Patrolling); 
                            TryStartPatrolNow(); 
                            return; 
                        }

                        if (m_mapSense.TryWorldToIndex(target.position, out int goalIdx))
                            m_pathRequester.RequestPathIndices(startIdx, goalIdx);
                        else
                        {
                            // If unit can’t index the target (outside map), fall back.
                            TransitionTo(SwarmState.STATE_Patrolling);
                            TryStartPatrolNow();
                        }
                        break;
                    }

                case SwarmState.STATE_Alerted:
                    {
                        // have a min amount of required alerted time after build, If rebuild happened mid-alerted
                        m_alertedUntil = Mathf.Max(m_alertedUntil, Time.time + 0.1f);

                        // Continue “alerted wander” around last seen position (no target required). Similar wander logic to TickAlerted
                        Vector3 wander = m_lastSeenPos + new Vector3(
                            Random.Range(-2f, 2f),
                            0f,
                            Random.Range(-2f, 2f)
                        );

                        if (m_mapSense.TryWorldToIndex(wander, out int wanderIdx))
                            m_pathRequester.RequestPathIndices(startIdx, wanderIdx);
                        else
                        {
                            TransitionTo(SwarmState.STATE_Patrolling);
                            TryStartPatrolNow();
                        }
                        break;
                    }

                case SwarmState.STATE_Patrolling:
                default:
                    TryStartPatrolNow();    // If leader and was patrolling, request a new patrol path immediately
                    break;
            }
        }


        public void ApplyRole(bool isLeader)
        {
            // Reset timers / path / cached goals so role switch is clean
            m_nextRepathTime = 0f;
            m_alertedUntil = 0f;
            m_pathBuffer.Clear();

            // Reset any previous per-role state at role change during playtime 
            TransitionTo(isLeader ? SwarmState.STATE_Patrolling : SwarmState.STATE_SwarmingFollow);
        }




        #region State Handling

        private void UpdateFollower()
        {
            // v1: followers always swarm-follow their leader.
            // SwarmingAgent already handles local steering + obstacle avoidance.

            if (m_state != SwarmState.STATE_SwarmingFollow)
                TransitionTo(SwarmState.STATE_SwarmingFollow);
        }

        private void UpdateLeader()
        {
            Transform target = m_agent.Target;
            Vector3 myPos = transform.position;

            bool hasTarget = (target != null);
            float distToTarget = hasTarget ? Vector3.Distance(myPos, target.position) : float.PositiveInfinity;

            // Engage / disengage gates
            if (hasTarget && distToTarget <= m_engageRange)
            {
                m_lastSeenPos = target.position;
                if (m_state != SwarmState.STATE_Engaging)
                {
                    m_nextRepathTime = 0;
                    TransitionTo(SwarmState.STATE_Engaging);
                }
            }
            else if (m_state == SwarmState.STATE_Engaging && (!hasTarget || distToTarget >= m_disengageRange))
            {
                // Lost target -> alerted
                m_alertedUntil = Time.time + m_alertedDuration;
                m_nextRepathTime = 0;
                TransitionTo(SwarmState.STATE_Alerted);
            }


            // Update behaviour according to current state
            switch (m_state)
            {
                case SwarmState.STATE_Engaging:
                    TickEngaging();
                    break;

                case SwarmState.STATE_Alerted:
                    TickAlerted();
                    break;

                case SwarmState.STATE_Patrolling:
                default:
                    TickPatrolling();
                    break;
            }
        }

        private void TransitionTo(SwarmState next)
        {
            if (m_state == next) return;
            m_state = next;

            // Update SwarmingAgent.State on what it's new state is, to keep everything in sync and so the manager can read accurate data
            m_agent.SetState(next);

            if (m_logStateChanges)
                Debug.Log($"{name} -> {m_state}");
        }


        #endregion



        #region Update State-specific methods

        // Update path for chasing after target  
        private void TickEngaging()
        {
            // Use actuall target location instead of _lastSeenPos to make chase responsive, and have no stale memmory lag 
            Transform target = m_agent.Target; 
            if (target == null) return;

            // Remember the target goal location for this path 
            if (!m_mapSense.TryWorldToIndex(target.position, out int goalIdxNow))
                return;

            bool noPath = (m_pathBuffer == null) || !m_pathBuffer.HasPath || m_pathBuffer.IsComplete;  // do unit currently have a path?
            bool goalChanged = noPath || (m_pathBuffer.GoalIndex != goalIdxNow);                     // is target is still in same cell-tile? 
            bool blockedSoon = IsPathBlockedSoon();                                                 // has something on the map changed? 

            // If nothing meaningful changed, then no need to re-compute path 
            if (!goalChanged && !blockedSoon)
                return;

            // Cooldown-limited to avoid request-spam
            if (Time.time < m_nextRepathTime) return;
            m_nextRepathTime = Time.time + m_repathInterval;

            // Request by indices instead of current world position to make full use of the map grid system (avoids world jitter)
            if (!m_mapSense.TryGetValidStartIndexFromCurrentPos(out int startIdx))
                return;

            // Request a path to target's current map-cell index 
            m_pathRequester.RequestPathIndices(startIdx, goalIdxNow);
        }


        private void TickAlerted()
        {
            // If alerted time finished -> back to patrol.
            if (Time.time >= m_alertedUntil)
            {
                TransitionTo(SwarmState.STATE_Patrolling);
                return;
            }

            // Small biased wander around lastSeenPos, decides the goal unit wants for this tick. Uses _lastSeenPos as the target is not currently available
            Vector3 wander = m_lastSeenPos + new Vector3(
                Random.Range(-2f, 2f),
                0f,
                Random.Range(-2f, 2f)
            );

            // Remember the target goal location for this path 
            if (!m_mapSense.TryWorldToIndex(wander, out int wanderIdxNow))
                return;

            bool noPath = (m_pathBuffer == null) || !m_pathBuffer.HasPath || m_pathBuffer.IsComplete;  // do unit currently have a path?
            bool goalChanged = noPath || (m_pathBuffer.GoalIndex != wanderIdxNow);                   // is current goal cell the same cell-tile as before? 
            bool blockedSoon = IsPathBlockedSoon();                                                 // has something on the map changed? 

            // If nothing meaningful changed, then no need to re-compute path 
            if (!goalChanged && !blockedSoon)
                return;

            // Keep moving toward last seen position, cooldown-limited to avoid request-spam
            if (Time.time < m_nextRepathTime) return;
            m_nextRepathTime = Time.time + m_repathInterval;

            if (!m_mapSense.TryGetValidStartIndexFromCurrentPos(out int startIdx))
                return;

            // Request a path to the wander goal's current map-cell index 
            m_pathRequester.RequestPathIndices(startIdx, wanderIdxNow);
        }


        private void TickPatrolling()
        {
            // If there is a path continue following it, do nothing here the motor will handle it 
            if (m_pathBuffer != null && m_pathBuffer.HasPath) return;


            // If no path (or path is completed), pick a new random walkable goal and request a path.
            TryStartPatrolNow();
        }


        #endregion


        private void TryStartPatrolNow()
        {

            if (!m_mapSense.TryGetValidStartIndexFromCurrentPos(out int startIdx))
                return;

            int minManhattan = Mathf.Clamp(
                Mathf.RoundToInt(m_mapSense.Data.Width * m_patrolMinManhattanFactor),
                m_patrolMinManhattanClampMin,
                m_patrolMinManhattanClampMax
            );

            var navigationService = m_pathRequester.NavigationService;
            if (navigationService != null)
            {
                for (int attempt = 0; attempt < m_patrolPickAttempts; attempt++)
                {
                    if (navigationService.TryPickRandomReachableGoal(
                            navigationService.GoalRng,
                            startIdx,
                            minManhattan,
                            out int goalIdx))
                    {
                        m_pathRequester.RequestPathIndices(startIdx, goalIdx);
                        return;
                    }
                }
            }

            // If it fails to pick a reachable goal (or service missing), fallback to walkable-only goal
            if (!TryPickRandomWalkableIndex(out int fallbackGoalIdx))
                return;

            m_pathRequester.RequestPathIndices(startIdx, fallbackGoalIdx);

        }


        private bool TryPickRandomWalkableIndex(out int index)
        {
            index = -1;
            var data = m_mapSense.Data;
            if (data == null || data.IsBlocked == null) return false;

            int cellCount = data.IsBlocked.Length;

            for (int i = 0; i < m_patrolPickAttempts; i++)
            {
                int candidate = Random.Range(0, cellCount);
                if (m_mapSense.IsWalkableIndex(candidate))
                {
                    index = candidate;
                    return true;
                }
            }

            return false;
        }

        // Grid based obstacle detection in case path becomes blocked /unreachable on the way to the goal 
        private bool IsPathBlockedSoon()
        {
            if (!m_enableBlockedRepath) return false;
            if (m_pathBuffer == null || !m_pathBuffer.HasPath) return false;

            var data = m_mapSense.Data;
            if (data == null) return false;

            Vector3 myPos = transform.position;

            // Next waypoint direction
            Vector3 waypoint = m_pathBuffer.CurrentWaypointWorld(data);
            Vector3 toWaypoint = waypoint - myPos;
            toWaypoint.y = 0f;

            if (toWaypoint.sqrMagnitude < 1e-6f) return false;

            Vector3 dir = toWaypoint.normalized;
            Vector3 probe = myPos + dir * m_blockedLookAhead;

            // If the probe cell is blocked/outside grid -> consider the path to be blocked soon
            if (!m_mapSense.TryWorldToIndex(probe, out int probeIdx))
                return true;

            return !m_mapSense.IsWalkableIndex(probeIdx);
        }




    }
}
