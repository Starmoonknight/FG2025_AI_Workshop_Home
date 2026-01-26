using System.Collections.Generic;
using TMPro;
using UnityEngine;



namespace AI_Workshop03.AI
{


    public enum SwarmState { 
        STATE_Patrolling,
        STATE_Alerted,
        STATE_Engaging,
        STATE_Disengaging,
        STATE_SwarmingFollow
    }

    // SwarmingAgent combines:
    // - Global navigation (A* path) for leaders: PathBuffer = list of grid cell waypoints.
    // - Local steering (boids) for followers: Separation + Cohesion + Alignment + FollowLeader.
    // - Local obstacle respect (followers): look-ahead probes that steer away from blocked/out-of-bounds cells.
    // Motion is velocity-based: steering -> acceleration -> velocity -> position (clamped by _maxAccel/_maxSpeed).
    [RequireComponent(typeof(AgentMapSense))]
    [RequireComponent(typeof(AgentPathBuffer))]
    [RequireComponent(typeof(AgentPathRequester))]
    [RequireComponent(typeof(AgentBrain))]
    public sealed class SwarmingAgent : MonoBehaviour
    {

        // Lock to XZ plane: prevents drift and ensures grid world->index logic stays consistent.
        [SerializeField] private float _agentPlaneOffsetY = 0.5f;   // Y if XZ, Z if XY


        // --- Static registry (simple + low-bug risk for now. Plan to switch to other event based system later to allow followers to switch between leaders)
        // Static All: O(N^2) neighbor checks. OK for lab-sized swarms (10–50). Later: spatial hash / grid buckets.
        public static readonly List<SwarmingAgent> All = new();

        
        [Header("Role")]
        [SerializeField] private bool _isLeader;             // If true: move using A* path buffer. If false: move using swarm steering.
        [SerializeField] private SwarmingAgent _leader;      // swarm leader 
        [SerializeField] private Transform _target;          // player

        [Header("Movement")]
        [SerializeField] private float _maxSpeed = 4f;      // max speed this unit can reach 
        [SerializeField] private float _maxAccel = 20f;     // max change in velocity per second (turning + speeding up + braking)

        [Header("Arrival")]
        [SerializeField] private float _waypointRadius = 0.25f;         // how close to a waypoint unit needs to determine that it has reached it
        [SerializeField, Min(0)] private float _slowingRadius = 3f;     // (reserved) used by Arrive steering when implemented

        [Header("Flocking: Separation")]
        [SerializeField] private float _neighborRadius = 2.5f;
        [SerializeField] private float _separationRadius = 1.1f;
        [SerializeField] private float _leaderFollowDist = 1.0f;

        // NEW: Not Implemented
        /*
        [SerializeField] private float _exploreRadiusCells = 2.0f;
        [SerializeField] private float _followDistanceCells = 2.0f;
        [SerializeField] private float _bandMinCells = 1.5f;
        [SerializeField] private float _bandMaxCells = 6.0f;
        */

        [Header("Flocking: Weights")]
        [SerializeField] private float _separationWeight = 1.4f;
        [SerializeField] private float _cohesionWeight = 0.6f;
        [SerializeField] private float _alignmentWeight = 0.5f;
        [SerializeField] private float _followLeaderWeight = 1.0f;

        [Header("Obstacle Avoidance (followers)")]
        [SerializeField] private float _lookAhead = 0.9f;
        [SerializeField] private float _sideOffset = 0.5f;
        [SerializeField] private float _avoidWeight = 2.0f;


        private AgentMapSense _mapSense;
        private AgentPathBuffer _pathBuffer;

        private Vector3 _velocity;


        private Vector3 _wanderOffset;  // curiosity 
        private float _wanderNextTime;  // curiosity timer 



        public SwarmState State { get; private set; } = SwarmState.STATE_SwarmingFollow;
        public bool IsLeader => _isLeader;
        public SwarmingAgent Leader => _leader;
        public Transform Target => _target;


        public void SetState(SwarmState state) => State = state;
        public void AssignLeaderStatus(bool isLeader) => _isLeader = isLeader;
        public void AssignFollowerTheirLeader(SwarmingAgent target) => _leader = target;
        public void AssignTarget(Transform target) => _target = target;


        /*


        [Header("Random start/goal")]
        [SerializeField, Range(0f, 1f)] private float _minManhattanFactor = 0.30f;
        [SerializeField, Min(0)] private int _minManhattanClampMin = 2;
        [SerializeField, Min(0)] private int _minManhattanClampMax = 200;   // safety

        [Header("Visualization")] private bool _visualizeAll = true;    // show pathfinding + path + start/goal tiles
        [SerializeField] private bool _visualizeFinalPath = true;       // show path
        [SerializeField] private bool _showStartAndGaol = true;         // show path start/goal tiles

        [Header("Debug")]
        [SerializeField] private bool drawDebug = true;


        private bool isLeader;
        private bool isFollower;

        private List<int> _pathIndices;
        private int _pathCursor;

        private Vector3 velocity = Vector3.zero;

        private int _startIndex = -1;
        private int _goalIndex = -1;
        private const int MaxPickAttempts = 64;

        private MapData _data;

        */



        private void Awake()
        {
            _mapSense = GetComponent<AgentMapSense>();
            _pathBuffer = GetComponent<AgentPathBuffer>();
        }

        private void OnEnable() 
        { 
            if (!All.Contains(this)) All.Add(this); 
        }
        private void OnDisable() 
        { 
            All.Remove(this); 
        }


        private void Update()
        {
            if (_mapSense.Data == null) return;

            Vector3 steering = Vector3.zero;

            // Pick a steering mode based on leadership status (leader = follow path, follower = follow leader + flock + avoid) 
            if (_isLeader)
            {
                steering += ComputeLeaderSteer();
            }
            else
            {
                steering += ComputeFollowerSteer();
            }

            // Convert boids-like steering into velocity (acceleration-limited)
            _velocity += Vector3.ClampMagnitude(steering, _maxAccel) * Time.deltaTime;
            _velocity = Vector3.ClampMagnitude(_velocity, _maxSpeed);


            // --- Movement step (HARD map constraint: never enter blocked tiles) ---
            Vector3 delta = _velocity * Time.deltaTime;

            // Keep agent on safe y plane for checks + final position
            Vector3 current = transform.position;
            current.y = _agentPlaneOffsetY;
            transform.position = current;

            // High-speed safety: sub-step to avoid tunneling across thin obstacles
            int steps = Mathf.CeilToInt(delta.magnitude / (_mapSense.Data.CellTileSize * 0.25f));   // means max step is quarter tile
            steps = Mathf.Clamp(steps, 1, 12);                                                      // Clamp(1, 8) is good. If fast fast agents, bump max to 12 but otherwise revert to that.
            Vector3 step = delta / steps;

            Vector3 appliedTotal = Vector3.zero;


            // If the full move lands on a walkable tile, apply it.
            for (int i = 0; i < steps; i++)
            {
                Vector3 next = transform.position + step;
                next.y = _agentPlaneOffsetY;

                // If the full step lands on a walkable tile, apply it.
                if (_mapSense.IsWalkableWorld(next))
                {
                    transform.position = next;
                    appliedTotal += step;
                    continue;
                }

                // Try sliding: X only then Z only (should prevent diagonal corner-cutting that was a big problem before)
                Vector3 pos = transform.position;

                Vector3 slideX = pos + new Vector3(step.x, 0f, 0f);
                slideX.y = _agentPlaneOffsetY;

                Vector3 slideZ = pos + new Vector3(0f, 0f, step.z);
                slideZ.y = _agentPlaneOffsetY;

                bool okX = _mapSense.IsWalkableWorld(slideX);
                bool okZ = _mapSense.IsWalkableWorld(slideZ);

                if (okX && !okZ)
                {
                    transform.position = slideX;
                    appliedTotal += new Vector3(step.x, 0f, 0f);
                    continue;
                }
                if (okZ && !okX)
                {
                    transform.position = slideZ;
                    appliedTotal += new Vector3(0f, 0f, step.z);
                    continue;
                }
                if (okX && okZ)
                {
                    if (Mathf.Abs(step.x) >= Mathf.Abs(step.z))
                    {
                        transform.position = slideX;
                        appliedTotal += new Vector3(step.x, 0f, 0f);
                    }
                    else
                    {
                        transform.position = slideZ;
                        appliedTotal += new Vector3(0f, 0f, step.z);
                    }
                    continue;
                }

                // Fully blocked on this sub-step: stop (and kill velocity so it doesn't keep pushing)
                _velocity = Vector3.zero;
                break;
            }

            // Rotate to face movement (use appliedDelta so blocked sliding looks correct)
            if (appliedTotal.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(new Vector3(appliedTotal.x, 0f, appliedTotal.z), Vector3.up);
        }



        public void Configure(bool isLeader, SwarmingAgent leader, Transform target)
        {
            // Reset movement bleedover if reeused through pooling 
            _velocity = Vector3.zero;
            _pathBuffer.Clear();

            AssignLeaderStatus(isLeader);
            if (!isLeader) AssignFollowerTheirLeader(leader);

            // Reset any previous per-role state
            SetState(isLeader ? SwarmState.STATE_Patrolling : SwarmState.STATE_SwarmingFollow);


            AssignTarget(target);

            // Tell brain after fields are set
            GetComponent<AgentBrain>().ApplyRole(isLeader);

            _wanderOffset = Random.insideUnitSphere;
            _wanderOffset.y = 0f;
            _wanderOffset = _wanderOffset.normalized;
            _wanderNextTime = 0f;
        }





        // NOTE WARNING:
        // Path already avoids blocked cells. If I later add smooth/skip waypoints, I must remeber to add a blocked-cell veto like followers use to not walk into obstacles.

        // --- Path A: Leader movement ---
        private Vector3 ComputeLeaderSteer()
        {
            // NOTE: 
            // Leader steering lacks map “blocked veto” if later I add smoothing/skip waypoints 
            // Using Seek: fast + simple but may overshoot/jitter at waypoint.
            // Update Plan for Later: switch to Arrive(towardsWaypoint, _slowingRadius) for smoother stops.


            // If unit is a leader: follows its path if it has one, otherwise do nothing
            if (_pathBuffer.HasPath)
            {
                Vector3 waypoint = _pathBuffer.CurrentWaypointWorld(_mapSense.Data, yOffset: _agentPlaneOffsetY);
                Vector3 toWaypoint = waypoint - transform.position;
                toWaypoint.y = 0f;

                // advance waypoint 
                if (_pathBuffer.ShouldAdvance(toWaypoint, _velocity, _waypointRadius))
                {
                    _pathBuffer.Advance();
                    return Vector3.zero; // let next frame target the new waypoint
                }

                // original planned path
                Vector3 desired = ArriveWorld(waypoint, _slowingRadius);

                // Probe in the direction unit intend to move
                Vector3 desiredDir = desired.sqrMagnitude > 1e-6f ? desired.normalized :
                                     (_velocity.sqrMagnitude > 1e-6f ? _velocity.normalized : Vector3.zero);

                Vector3 avoid = _mapSense.ComputeObstacleAvoidance(
                                    transform.position, desiredDir,
                                    _lookAhead, _sideOffset, _agentPlaneOffsetY) * _avoidWeight;

                return desired + avoid;
            }

            return Vector3.zero;
        }


        // --- Path B: Follower movement ---
        private Vector3 ComputeFollowerSteer()
        {
            // Only if unit is a follower: use flocking instead of A* pathing to move. But they are still slightly map aware
                        
            Vector3 flock = ComputeFlocking();  // compute Seperation(don’t overlap), Cohesion(stay together), and Alignment(match direction)
            Vector3 follow = Vector3.zero;

            // follow their leader if they have one
            if (_leader != null)
            {
                Vector3 toLeader = (_leader.transform.position - transform.position);


                // This made the boid collapse into a crowd like aura, or like pond scum. But could be fun to implement as a mode later   
                //follow = SeekTo(toLeader) * _followLeaderWeight;


                // If I want the crowd aura, just keep the above line 
                // This way it makes sure leader is distinct, and followers follow behind propperly. Added all lines of code below to fix problem.
                Vector3 leaderDir = _leader.transform.forward; // or use leader velocity if you expose it
                float followDist = _leaderFollowDist; // tune (or 2 * CellTileSize)
                Vector3 followPoint = _leader.transform.position - leaderDir * followDist;

                follow = ArriveWorld(followPoint, slowRadius: followDist) * _followLeaderWeight;


                // Third design option, need to try what feels best or make a hotswap method.
                // Follow a moving anchor region.
                // Instead of seeking the leader, seek an anchor point behind the leader and let cohesion/alignment keep them together.
                /*
                Vector3 leaderDir = _leader.VelocityXZ.sqrMagnitude > 0.01f
                    ? _leader.VelocityXZ.normalized
                    : _leader.transform.forward;

                float followDist = _followDistanceCells * _mapSense.Data.CellTileSize; // e.g. 2–4 cells
                Vector3 anchor = _leader.transform.position - leaderDir * followDist;

                // Arrive to the anchor, not the leader.
                follow = ArriveWorld(anchor, slowRadius: followDist) * _followLeaderWeight;
                */



                // Wander offset 
                /*
                // NEW, needs more testing
                // Curiosity, let them wander around a bit
                if (Time.time >= _wanderNextTime)
                {
                    Vector2 r = Random.insideUnitCircle.normalized;
                    _wanderOffset = new Vector3(r.x, 0f, r.y);
                    _wanderNextTime = Time.time + Random.Range(0.6f, 1.4f);
                }

                float exploreRadius = _exploreRadiusCells * _mapSense.Data.CellTileSize; // e.g. 2–5 cells
                Vector3 exploreTarget = anchor + _wanderOffset * exploreRadius;


                Vector3 follow = ArriveWorld(exploreTarget, slowRadius: exploreRadius) * _followLeaderWeight;
                */


            }

            // combine group flocking behaviour + try to follow leader
            Vector3 desired = flock + follow;

            // desiredDir is for obstacle probing (look-ahead direction), NOT the boids alignment rule
            Vector3 desiredDir = desired.sqrMagnitude > 1e-6f ? desired.normalized : Vector3.zero;

            // avoid map-edges and obstacles / blocked tiles 
            Vector3 avoid = _mapSense.ComputeObstacleAvoidance(transform.position, desiredDir, _lookAhead, _sideOffset, _agentPlaneOffsetY) * _avoidWeight;

            // decide final movement for this unit 
            return desired + avoid;
        }



        #region Flocking behaviour

        // flock steering, boids-ish style. One neighbor scan, then 3 small rule methods. 
        private Vector3 ComputeFlocking()
        {
            // --- Tuned radii (use squared distance to avoid sqrt) ---
            float neighborRadSqr = _neighborRadius * _neighborRadius;
            float separationRadSqr = _separationRadius * _separationRadius;

            // --- Aggregates collected from neighbors ---
            Vector3 position = transform.position;

            Vector3 separationSum = Vector3.zero;  // summed repulsion vectors
            Vector3 cohesionPosSum = Vector3.zero; // summed neighbor positions (for centroid)
            Vector3 alignmentVelSum = Vector3.zero;    // summed neighbor velocities (for avg heading)
            int neighborCount = 0;


            // For each nearby neighbor (within _neighborRadius):
            // - Separation: push away if inside _separationRadius (stronger when very close).
            // - Cohesion: accumulate neighbor positions to compute neighbor center.
            // - Alignment: accumulate neighbor velocities to compute avg heading/speed.
            for (int i = 0; i < All.Count; i++)
            {
                var other = All[i];
                if (other == null || other == this) continue;

                // compute how much unit should repell from other flock-mates, prevent clumping   (local collision avoidance)
                Vector3 distanceToNeighbour = other.transform.position - position;
                float distSqr = distanceToNeighbour.sqrMagnitude;

                // if not a neighbour, ignore 
                if (distSqr > neighborRadSqr) continue;             // if within radius they are a neighbor and should be counted 

                neighborCount++;
                cohesionPosSum += other.transform.position;         // later becomes "center of neighbors"
                alignmentVelSum += other._velocity;                 // later becomes "average neighbor velocity"

                // separation only applies inside the smaller separation radius
                if (distSqr < separationRadSqr && distSqr > 1e-6f)
                    separationSum -= distanceToNeighbour / distSqr;    // stronger seperation force when closer grouped, repel hard if extremely close 
            }

            if (neighborCount == 0) return Vector3.zero;


            // --- Convert aggregates into 3 steering forces  ---

            // NOTE: weights tune behavior, not correctness.
            // High separation = loose flock; High cohesion = tight blob; High alignment = smooth flow.

            // For each nearby neighbor (within _neighborRadius):
            Vector3 separation = ComputeSeparationFromAgg(separationSum);
            Vector3 cohesion = ComputeCohesionFromAgg(cohesionPosSum, neighborCount, position);
            Vector3 alignment = ComputeAlignmentFromAgg(alignmentVelSum, neighborCount);

            return separation + cohesion + alignment;
        }



        // Separation: don't overlap (stronger when very close).
        private Vector3 ComputeSeparationFromAgg(Vector3 separationSum)
        {
            // separationSum already encodes distance weighting (1/dist^2)
            return separationSum * _separationWeight;
        }

        // Cohesion: stay with group, prevent drifting away   (pull toward neighbor centroid)
        private Vector3 ComputeCohesionFromAgg(Vector3 cohesionPosSum, int neighborCount, Vector3 myPos)
        {
            Vector3 center = cohesionPosSum / neighborCount;
            return SeekTo(center - myPos) * _cohesionWeight;
        }

        // Alignment: go with the flow and match alignment of group direction.   (match avg neighbor velocity)
        private Vector3 ComputeAlignmentFromAgg(Vector3 alignmentVelSum, int neighborCount)
        {
            Vector3 avgVel = alignmentVelSum / neighborCount;

            // Convert avg velocity into a desired velocity at unit's max speed
            Vector3 desiredVel = avgVel.sqrMagnitude > 1e-6f ? avgVel.normalized * _maxSpeed : Vector3.zero;

            // Steering force tries to change unit's velocity toward desiredVel
            return (desiredVel - _velocity) * _alignmentWeight;
        }


        // Convenience wrapper: takes a WORLD position and converts it to a local "to target" vector,
        // so call sites stay clean and just pass the waypoint/world goal (matches lab-style Seek).
        private Vector3 SeekWorld(Vector3 worldTarget) =>
            SeekTo(worldTarget - transform.position);

        // Core Seek steering: takes a "TO target" DIRECTION vector (target - currentPos), not a world position.
        // Use this when you already computed a vector (flocking, follow, avoidance), and want planar acceleration.
        private Vector3 SeekTo(Vector3 toTarget)
        {
            toTarget.y = 0f;                        // flatten direction
            if (toTarget.sqrMagnitude < 1e-6f) return Vector3.zero;

            Vector3 desiredVel = toTarget.normalized * _maxSpeed;
            return (desiredVel - _velocity);
        }


        // Convenience wrapper: takes a WORLD position and converts it to a local "to target" vector,
        // so leader/path code can just pass the waypoint position and get smooth "Arrive" behavior.
        private Vector3 ArriveWorld(Vector3 worldTarget, float slowRadius) => 
            ArriveTo(worldTarget - transform.position, slowRadius);

        // Core Arrive steering: takes a "TO target" DIRECTION vector (target - currentPos), not a world position.
        // Slows down inside slowRadius to avoid overshoot/orbiting and to make waypoint following stable.
        private Vector3 ArriveTo(Vector3 toTarget, float slowRadius)
        {
            toTarget.y = 0f;                        // flatten direction
            float dist = toTarget.magnitude;
            if (dist <= 0.001f) return -_velocity;  // brake

            float targetSpeed = (dist < slowRadius)
                ? _maxSpeed * (dist / slowRadius)
                : _maxSpeed;

            Vector3 desiredVel = toTarget * (targetSpeed / dist);
            return (desiredVel - _velocity);        // steering
        }


        #endregion



        public bool TrySnapToNearestWalkable(int radius = 6)
        {
            if (_mapSense == null || _mapSense.Data == null) return false;

            if (!_mapSense.TryWorldToIndex(transform.position, out int startIdx))
                return false;

            if (!_mapSense.Data.IsBlocked[startIdx])
                return true; // already safe

            if (_mapSense.TryGetNearestUnblockedIndex(startIdx, radius, out int safeIdx))
            {
                Vector3 p = _mapSense.Data.IndexToWorldCenterXZ(safeIdx, _agentPlaneOffsetY);
                transform.position = p;
                _velocity = Vector3.zero;
                return true;
            }

            return false;
        }





    }

}