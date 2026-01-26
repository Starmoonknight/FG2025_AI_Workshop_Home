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
        [SerializeField, Min(0f)] private float _engageRange = 6f;
        [SerializeField, Min(0f)] private float _disengageRange = 9f;

        [Header("Timers")]
        [SerializeField, Min(0.05f)] private float _repathInterval = 0.5f;
        [SerializeField, Min(0f)] private float _alertedDuration = 2.5f;

        [Header("Patrol")]
        [SerializeField, Min(1)] private int _patrolPickAttempts = 64;

        [Header("Repath gates")]
        [SerializeField, Min(0f)] private float _blockedLookAhead = 0.9f; // similar to avoidance probe
        [SerializeField] private bool _enableBlockedRepath = true;

        [Header("Patrol reachable goals")]
        [SerializeField, Range(0f, 1f)] private float _patrolMinManhattanFactor = 0.30f;
        [SerializeField, Min(0)] private int _patrolMinManhattanClampMin = 2;
        [SerializeField, Min(0)] private int _patrolMinManhattanClampMax = 200;

        [Header("Debug")]
        [SerializeField] private bool _logStateChanges = false;

        private SwarmingAgent _agent;
        private AgentMapSense _mapSense;
        private AgentPathRequester _pathRequester;
        private AgentPathBuffer _pathBuffer;

        private float _nextRepathTime;
        private float _alertedUntil;
        private Vector3 _lastSeenPos;

        private SwarmState _state;




        private void Awake()
        {
            _agent = GetComponent<SwarmingAgent>();
            _mapSense = GetComponent<AgentMapSense>();
            _pathRequester = GetComponent<AgentPathRequester>();
            _pathBuffer = GetComponent<AgentPathBuffer>();
        }

        private void OnEnable()
        {
            // Initialize state based on role, is this a Leader or a Follower? Can be updated to change roll during runtime
            if (_agent != null && _agent.IsLeader)
                TransitionTo(SwarmState.STATE_Patrolling);
            else
                TransitionTo(SwarmState.STATE_SwarmingFollow);

            if (_mapSense != null)
                _mapSense.OnDataChanged += HandleMapDataChanged;
        }

        private void Update()
        {
            if (_agent == null || _mapSense == null) return;
            if (_mapSense.Data == null) return;

            if (_agent.IsLeader)
                UpdateLeader();
            else
                UpdateFollower();
        }

        private void OnDisable()
        {
            if (_mapSense != null)
                _mapSense.OnDataChanged -= HandleMapDataChanged;
        }



        private void HandleMapDataChanged(MapData _)
        {
            if (_mapSense == null || _mapSense.Data == null) return;

            // Reset path and timers so it doesn't keep following stale path indices
            _pathBuffer.Clear();
            _nextRepathTime = 0f;

            // If unit is standing on a blocked tile after rebuild, try to "snap" to nearest walkable (movement-layer method).
            if (_mapSense.TryWorldToIndex(transform.position, out int startIdx))
            {
                if (_mapSense.Data.IsBlocked[startIdx])
                    _agent.TrySnapToNearestWalkable(radius: 6);
            }
            else
            {
                // Outside grid: try to recover (snap may also handle this if you extend it later)
                return;
            }


            // Recompute a valid start index AFTER potential snap
            if (!_mapSense.TryGetValidStartIndexFromCurrentPos(out startIdx))
                return;


            // Decide what to do based on role + current state. Followers don't need to do anything else here
            if (!_agent.IsLeader)
                return;


            switch (_state)
            {
                case SwarmState.STATE_Engaging:
                    {
                        // If unit still have a target, immediately request a fresh chase path.
                        Transform target = _agent.Target;
                        if (target == null) 
                        { 
                            TransitionTo(SwarmState.STATE_Patrolling); 
                            TryStartPatrolNow(); 
                            return; 
                        }

                        if (_mapSense.TryWorldToIndex(target.position, out int goalIdx))
                            _pathRequester.RequestPathIndices(startIdx, goalIdx);
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
                        _alertedUntil = Mathf.Max(_alertedUntil, Time.time + 0.1f);

                        // Continue “alerted wander” around last seen position (no target required). Similar wander logic to TickAlerted
                        Vector3 wander = _lastSeenPos + new Vector3(
                            Random.Range(-2f, 2f),
                            0f,
                            Random.Range(-2f, 2f)
                        );

                        if (_mapSense.TryWorldToIndex(wander, out int wanderIdx))
                            _pathRequester.RequestPathIndices(startIdx, wanderIdx);
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
            _nextRepathTime = 0f;
            _alertedUntil = 0f;
            _pathBuffer.Clear();

            // Reset any previous per-role state at role change during playtime 
            TransitionTo(isLeader ? SwarmState.STATE_Patrolling : SwarmState.STATE_SwarmingFollow);
        }




        #region State Handling

        private void UpdateFollower()
        {
            // v1: followers always swarm-follow their leader.
            // SwarmingAgent already handles local steering + obstacle avoidance.

            if (_state != SwarmState.STATE_SwarmingFollow)
                TransitionTo(SwarmState.STATE_SwarmingFollow);
        }

        private void UpdateLeader()
        {
            Transform target = _agent.Target;
            Vector3 myPos = transform.position;

            bool hasTarget = (target != null);
            float distToTarget = hasTarget ? Vector3.Distance(myPos, target.position) : float.PositiveInfinity;

            // Engage / disengage gates
            if (hasTarget && distToTarget <= _engageRange)
            {
                _lastSeenPos = target.position;
                if (_state != SwarmState.STATE_Engaging)
                {
                    _nextRepathTime = 0;
                    TransitionTo(SwarmState.STATE_Engaging);
                }
            }
            else if (_state == SwarmState.STATE_Engaging && (!hasTarget || distToTarget >= _disengageRange))
            {
                // Lost target -> alerted
                _alertedUntil = Time.time + _alertedDuration;
                _nextRepathTime = 0;
                TransitionTo(SwarmState.STATE_Alerted);
            }


            // Update behaviour according to current state
            switch (_state)
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
            if (_state == next) return;
            _state = next;

            // Update SwarmingAgent.State on what it's new state is, to keep everything in sync and so the manager can read accurate data
            _agent.SetState(next);

            if (_logStateChanges)
                Debug.Log($"{name} -> {_state}");
        }


        #endregion



        #region Update State-specific methods

        // Update path for chasing after target  
        private void TickEngaging()
        {
            // Use actuall target location instead of _lastSeenPos to make chase responsive, and have no stale memmory lag 
            Transform target = _agent.Target; 
            if (target == null) return;

            // Remember the target goal location for this path 
            if (!_mapSense.TryWorldToIndex(target.position, out int goalIdxNow))
                return;

            bool noPath = (_pathBuffer == null) || !_pathBuffer.HasPath || _pathBuffer.IsComplete;  // do unit currently have a path?
            bool goalChanged = noPath || (_pathBuffer.GoalIndex != goalIdxNow);                     // is target is still in same cell-tile? 
            bool blockedSoon = IsPathBlockedSoon();                                                 // has something on the map changed? 

            // If nothing meaningful changed, then no need to re-compute path 
            if (!goalChanged && !blockedSoon)
                return;

            // Cooldown-limited to avoid request-spam
            if (Time.time < _nextRepathTime) return;
            _nextRepathTime = Time.time + _repathInterval;

            // Request by indices instead of current world position to make full use of the map grid system (avoids world jitter)
            if (!_mapSense.TryGetValidStartIndexFromCurrentPos(out int startIdx))
                return;

            // Request a path to target's current map-cell index 
            _pathRequester.RequestPathIndices(startIdx, goalIdxNow);
        }


        private void TickAlerted()
        {
            // If alerted time finished -> back to patrol.
            if (Time.time >= _alertedUntil)
            {
                TransitionTo(SwarmState.STATE_Patrolling);
                return;
            }

            // Small biased wander around lastSeenPos, decides the goal unit wants for this tick. Uses _lastSeenPos as the target is not currently available
            Vector3 wander = _lastSeenPos + new Vector3(
                Random.Range(-2f, 2f),
                0f,
                Random.Range(-2f, 2f)
            );

            // Remember the target goal location for this path 
            if (!_mapSense.TryWorldToIndex(wander, out int wanderIdxNow))
                return;

            bool noPath = (_pathBuffer == null) || !_pathBuffer.HasPath || _pathBuffer.IsComplete;  // do unit currently have a path?
            bool goalChanged = noPath || (_pathBuffer.GoalIndex != wanderIdxNow);                   // is current goal cell the same cell-tile as before? 
            bool blockedSoon = IsPathBlockedSoon();                                                 // has something on the map changed? 

            // If nothing meaningful changed, then no need to re-compute path 
            if (!goalChanged && !blockedSoon)
                return;

            // Keep moving toward last seen position, cooldown-limited to avoid request-spam
            if (Time.time < _nextRepathTime) return;
            _nextRepathTime = Time.time + _repathInterval;

            if (!_mapSense.TryGetValidStartIndexFromCurrentPos(out int startIdx))
                return;

            // Request a path to the wander goal's current map-cell index 
            _pathRequester.RequestPathIndices(startIdx, wanderIdxNow);
        }


        private void TickPatrolling()
        {
            // If there is a path continue following it, do nothing here the motor will handle it 
            if (_pathBuffer != null && _pathBuffer.HasPath) return;


            // If no path (or path is completed), pick a new random walkable goal and request a path.
            TryStartPatrolNow();
        }


        #endregion


        private void TryStartPatrolNow()
        {

            if (!_mapSense.TryGetValidStartIndexFromCurrentPos(out int startIdx))
                return;

            int minManhattan = Mathf.Clamp(
                Mathf.RoundToInt(_mapSense.Data.Width * _patrolMinManhattanFactor),
                _patrolMinManhattanClampMin,
                _patrolMinManhattanClampMax
            );

            var navigationService = _pathRequester.NavigationService;
            if (navigationService != null)
            {
                for (int attempt = 0; attempt < _patrolPickAttempts; attempt++)
                {
                    if (navigationService.TryPickRandomReachableGoal(
                            navigationService.GoalRng,
                            startIdx,
                            minManhattan,
                            out int goalIdx))
                    {
                        _pathRequester.RequestPathIndices(startIdx, goalIdx);
                        return;
                    }
                }
            }

            // If it fails to pick a reachable goal (or service missing), fallback to walkable-only goal
            if (!TryPickRandomWalkableIndex(out int fallbackGoalIdx))
                return;

            _pathRequester.RequestPathIndices(startIdx, fallbackGoalIdx);

        }


        private bool TryPickRandomWalkableIndex(out int index)
        {
            index = -1;
            var data = _mapSense.Data;
            if (data == null || data.IsBlocked == null) return false;

            int cellCount = data.IsBlocked.Length;

            for (int i = 0; i < _patrolPickAttempts; i++)
            {
                int candidate = Random.Range(0, cellCount);
                if (_mapSense.IsWalkableIndex(candidate))
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
            if (!_enableBlockedRepath) return false;
            if (_pathBuffer == null || !_pathBuffer.HasPath) return false;

            var data = _mapSense.Data;
            if (data == null) return false;

            Vector3 myPos = transform.position;

            // Next waypoint direction
            Vector3 waypoint = _pathBuffer.CurrentWaypointWorld(data);
            Vector3 toWaypoint = waypoint - myPos;
            toWaypoint.y = 0f;

            if (toWaypoint.sqrMagnitude < 1e-6f) return false;

            Vector3 dir = toWaypoint.normalized;
            Vector3 probe = myPos + dir * _blockedLookAhead;

            // If the probe cell is blocked/outside grid -> consider the path to be blocked soon
            if (!_mapSense.TryWorldToIndex(probe, out int probeIdx))
                return true;

            return !_mapSense.IsWalkableIndex(probeIdx);
        }




    }
}
