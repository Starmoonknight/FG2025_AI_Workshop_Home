using System.Collections.Generic;
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

    public enum RoughTestFollowMode
    {
        FollowPoint,
        Crowd,
        LeaderTail,
        Anchor
    }

    // SwarmingAgent combines:
    // - Global navigation (A* path) for leaders: PathBuffer = list of grid cell waypoints.
    // - Local steering (boids) for followers: Separation + Cohesion + Alignment + FollowLeader.
    // - Local obstacle respect (followers): look-ahead probes that steer away from blocked/out-of-bounds cells.
    // Motion is velocity-based: steering -> acceleration -> velocity -> position (clamped by m_maxAccel/_maxSpeed).
    [RequireComponent(typeof(AgentMapSense))]
    [RequireComponent(typeof(AgentPathBuffer))]
    [RequireComponent(typeof(AgentPathRequester))]
    [RequireComponent(typeof(AgentBrain))]
    public sealed class SwarmingAgent : MonoBehaviour
    {

        // Lock to XZ plane: prevents drift and ensures grid world->index logic stays consistent.
        [SerializeField] private float m_agentPlaneOffsetY = 0.5f;   // Y if XZ, Z if XY


        // --- Static registry (simple + low-bug risk for now. Plan to switch to other event based system later to allow followers to switch between leaders)
        // Static All: O(N^2) neighbor checks. OK for lab-sized swarms (10–50). Later: spatial hash / grid buckets.
        public static readonly List<SwarmingAgent> All = new();

        
        [Header("Role")]
        [SerializeField] private bool m_isLeader;             // If true: move using A* path buffer. If false: move using swarm steering.
        [SerializeField] private SwarmingAgent m_leader;      // swarm leader 
        [SerializeField] private Transform m_target;          // player

        [Header("Movement")]
        [SerializeField] private float m_maxSpeed = 4f;      // max speed this unit can reach 
        [SerializeField] private float m_maxAccel = 20f;     // max change in velocity per second (turning + speeding up + braking)
        [SerializeField] private RoughTestFollowMode m_testFollowMode = RoughTestFollowMode.FollowPoint;

        [Header("Obstacle Avoidance")]
        [SerializeField] private float m_lookAhead = 0.9f;
        [SerializeField] private float m_sideOffset = 0.5f;
        [SerializeField] private float m_avoidWeight = 2.0f;

        [Header("Arrival")]
        [SerializeField] private float m_waypointRadius = 0.3f;         // how close to a waypoint unit needs to determine that it has reached it   (0.4–0.6 * CellTileSize should work)
        [SerializeField, Min(0)] private float m_slowingRadius = 1.2f;  // (reserved) used by Arrive steering when implemented                      (1.0–1.8 * CellTileSize should work)

        [Header("Leader: Path Strict-ness")]
        [SerializeField, Range(0f, 1f)] private float m_strictFollow01 = 0.85f; // 0 = loose/sway, 1 = strict/tight
        [SerializeField] private float m_leaderLookAheadCells = 0.6f;           // 0.4–1.2
        [SerializeField] private float m_leaderLookAheadSpeed = 0.15f;          // how much lookahead grows with speed
        [SerializeField] private float m_lateralDamp = 6f;                      // 3–10: tightness to path segment

        [Header("Flocking: Separation")]
        [SerializeField] private float m_neighborRadius = 2.5f;
        [SerializeField] private float m_separationRadius = 1.1f;
        [SerializeField] private float m_leaderFollowDist = 1.0f;       // Cells behind the leader. On a 1-unit cell grid, 1.0 ends up being crowding on top of leader (2-4 good?)
        //[SerializeField] private float m_leaderSeparationBoost = 2.5f;  // stronger separation from leader            NOT IMPLEMENTED YET
        [SerializeField] private float m_exploreRadiusCells = 2.0f;     // how far they wander around anchor point
        [SerializeField] private float m_followDistanceCells = 2.0f;    // anchor distance behind lead
        [SerializeField] private float m_bandMinCells = 1.5f;           // inside this: no pull (free roam)
        [SerializeField] private float m_bandMaxCells = 6.0f;           // outside this: full pull (rubber band)


        [Header("Flocking: Weights")]
        [SerializeField] private float m_separationWeight = 1.4f;
        [SerializeField] private float m_cohesionWeight = 0.6f;
        [SerializeField] private float m_alignmentWeight = 0.5f;
        [SerializeField] private float m_followLeaderWeight = 1.0f;


        private AgentMapSense m_mapSense;
        private AgentPathBuffer m_pathBuffer;

        private static readonly int ColorId = Shader.PropertyToID("_BaseColor"); // URP Lit
        private static readonly int ColorIdFallback = Shader.PropertyToID("_Color");
        private MaterialPropertyBlock m_mpb;
        private Renderer m_renderer;


        private Vector3 m_velocity;
        private int m_lastSlideAxis; // -1 = Z, +1 = X, 0 = none

        private Vector3 m_wanderOffset;  // curiosity 
        private float m_wanderNextTime;  // curiosity timer / personality knob: how often agents pick a new exploration direction


        public SwarmState State { get; private set; } = SwarmState.STATE_SwarmingFollow;
        public SwarmingAgent Leader => m_leader;
        public Transform Target => m_target;
        public bool IsLeader => m_isLeader;
        public Vector3 Velocity => m_velocity;



        public void SetState(SwarmState state) => State = state;
        public void AssignLeaderStatus(bool isLeader) => m_isLeader = isLeader;
        public void AssignFollowerTheirLeader(SwarmingAgent target) => m_leader = target;
        public void AssignTarget(Transform target) => m_target = target;


        /*


        [Header("Random start/goal")]
        [SerializeField, Range(0f, 1f)] private float m_minManhattanFactor = 0.30f;
        [SerializeField, Min(0)] private int m_minManhattanClampMin = 2;
        [SerializeField, Min(0)] private int m_minManhattanClampMax = 200;   // safety

        [Header("Visualization")] private bool m_visualizeAll = true;    // show pathfinding + path + start/goal tiles
        [SerializeField] private bool m_visualizeFinalPath = true;       // show path
        [SerializeField] private bool m_showStartAndGaol = true;         // show path start/goal tiles

        [Header("Debug")]
        [SerializeField] private bool drawDebug = true;


        private bool isLeader;
        private bool isFollower;

        private List<int> m_pathIndices;
        private int m_pathCursor;

        private Vector3 velocity = Vector3.zero;

        private int m_startIndex = -1;
        private int m_goalIndex = -1;
        private const int MaxPickAttempts = 64;

        private MapData m_data;

        */



        private void Awake()
        {
            m_mapSense = GetComponent<AgentMapSense>();
            m_pathBuffer = GetComponent<AgentPathBuffer>();
            m_renderer = GetComponent<Renderer>();
            m_mpb = new MaterialPropertyBlock();
        }

        private void OnEnable() 
        { 
            if (!All.Contains(this)) All.Add(this); 
        }
        private void OnDisable() 
        { 
            All.Remove(this); 
        }


        public void Configure(bool isLeader, SwarmingAgent leader, Transform target)
        {
            // Reset movement bleedover if reeused through pooling 
            m_velocity = Vector3.zero;
            m_pathBuffer.Clear();

            AssignLeaderStatus(isLeader);
            if (!isLeader) AssignFollowerTheirLeader(leader);

            AssignTarget(target);

            // Tell brain after fields are set, reset any previous per-role state
            GetComponent<AgentBrain>().ApplyRole(isLeader);

            m_wanderOffset = Random.insideUnitSphere;
            m_wanderOffset.y = 0f;
            m_wanderOffset = m_wanderOffset.normalized;
            m_wanderNextTime = 0f;
        }

        public void SetColor(Color32 color)
        {
            if (m_renderer == null) m_renderer = GetComponentInChildren<Renderer>(true);
            if (m_mpb == null) m_mpb = new MaterialPropertyBlock();

            m_renderer.GetPropertyBlock(m_mpb);

            var mat = m_renderer.sharedMaterial;
            if (mat != null && mat.HasProperty(ColorId))
                m_mpb.SetColor(ColorId, color);
            else
                m_mpb.SetColor(ColorIdFallback, color);

            m_renderer.SetPropertyBlock(m_mpb);
        }



        private void Update()
        {

            Movement();
        }



        private void Movement()
        {
            if (m_mapSense == null || m_pathBuffer == null) return;
            MapData mapData = m_mapSense.Data;
            if (mapData == null) return;

            Vector3 steering = Vector3.zero;

            // Pick a steering mode based on leadership status (leader = follow path, follower = follow leader + flock + avoid) 
            if (m_isLeader)
            {
                steering += ComputeLeaderSteer(mapData);
            }
            else
            {
                steering += ComputeFollowerSteer(mapData);
            }

            // Convert boids-like steering into velocity (acceleration-limited)
            m_velocity += Vector3.ClampMagnitude(steering, m_maxAccel) * Time.deltaTime;
            m_velocity = Vector3.ClampMagnitude(m_velocity, m_maxSpeed);


            // --- Movement step (HARD map constraint: never enter blocked tiles, calculated in the Compute Steering right before this) ---
            Vector3 delta = m_velocity * Time.deltaTime;

            // Keep agent on safe y plane for checks + final position
            Vector3 current = transform.position;
            current.y = m_agentPlaneOffsetY;
            transform.position = current;

            // High-speed safety: sub-step to avoid tunneling across thin obstacles
            int steps = Mathf.CeilToInt(delta.magnitude / (mapData.CellTileSize * 0.25f));   // means max step is quarter tile
            steps = Mathf.Clamp(steps, 1, 12);                                                      // Clamp(1, 8) is good. If fast fast agents, bump max to 12 but otherwise revert to that.
            Vector3 step = delta / steps;

            Vector3 appliedTotal = Vector3.zero;


            // If the full move lands on a walkable tile, apply it.
            for (int i = 0; i < steps; i++)
            {
                Vector3 next = transform.position + step;
                next.y = m_agentPlaneOffsetY;

                // If the full step lands on a walkable tile, apply it.
                if (m_mapSense.IsWalkableWorld(next))
                {
                    transform.position = next;
                    appliedTotal += step;
                    m_lastSlideAxis = 0;
                    continue;
                }

                // Try sliding: X only then Z only (should prevent diagonal corner-cutting that was a big problem before)
                Vector3 pos = transform.position;

                Vector3 slideX = pos + new Vector3(step.x, 0f, 0f);
                slideX.y = m_agentPlaneOffsetY;

                Vector3 slideZ = pos + new Vector3(0f, 0f, step.z);
                slideZ.y = m_agentPlaneOffsetY;

                bool okX = m_mapSense.IsWalkableWorld(slideX);
                bool okZ = m_mapSense.IsWalkableWorld(slideZ);

                if (okX && !okZ)
                {
                    transform.position = slideX;
                    appliedTotal += new Vector3(step.x, 0f, 0f);

                    // Removing most of the inerta that pushes agent face-first into a wall (Z) on continous sideways movement
                    m_velocity.z *= 0.1f;
                    m_lastSlideAxis = +1; // X
                    continue;
                }
                if (okZ && !okX)
                {
                    transform.position = slideZ;
                    appliedTotal += new Vector3(0f, 0f, step.z);

                    // Removing most of the inerta that pushes agent face-first into a wall (X) on continous sideways movement
                    m_velocity.x *= 0.1f;
                    m_lastSlideAxis = -1; // Z
                    continue;
                }
                if (okX && okZ)
                {
                    // Prefer previous slide axis to avoid X/Z flip-flop (pirouette)
                    if (m_lastSlideAxis == +1)
                    {
                        transform.position = slideX;
                        appliedTotal += new Vector3(step.x, 0f, 0f);

                        m_velocity.z *= 0.1f;
                        continue;
                    }
                    if (m_lastSlideAxis == -1)
                    {
                        transform.position = slideZ;
                        appliedTotal += new Vector3(0f, 0f, step.z);

                        m_velocity.x *= 0.1f;
                        continue;
                    }

                    // No previous preference: pick bigger component once, then stick next time
                    if (Mathf.Abs(step.x) >= Mathf.Abs(step.z))
                    {
                        transform.position = slideX;
                        appliedTotal += new Vector3(step.x, 0f, 0f);

                        m_velocity.z *= 0.1f;
                        m_lastSlideAxis = +1; // X
                    }
                    else
                    {
                        transform.position = slideZ;
                        appliedTotal += new Vector3(0f, 0f, step.z);

                        m_velocity.x *= 0.1f;
                        m_lastSlideAxis = -1; // Z
                    }
                    continue;
                }

                // Fully blocked on this sub-step: stop (and kill velocity so it doesn't keep pushing)
                m_velocity = Vector3.zero;
                m_lastSlideAxis = 0;
                break;
            }

            float dt = Time.deltaTime;
            if (dt > 1e-6f)
            {
                Vector3 actualVel = appliedTotal / dt;

                // Blend for a bit of inertia, but still anchor to reality
                m_velocity = Vector3.Lerp(m_velocity, actualVel, 0.65f);
            }

            // Rotate to face movement (use appliedDelta so blocked sliding looks correct)
            if (appliedTotal.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(new Vector3(appliedTotal.x, 0f, appliedTotal.z), Vector3.up);
        }



        #region Leader behaviour

        // NOTE WARNING:
        // Path already avoids blocked cells. If I later add smooth/skip waypoints, I must remeber to add a blocked-cell veto like followers use to not walk into obstacles.

        // --- Path A: Leader movement ---
        private Vector3 ComputeLeaderSteer(MapData mapData)
        {
            // NOTE: 
            // Leader steering lacks map “blocked veto” if later I add smoothing/skip waypoints 


            // If unit is a leader: follows its path if it has one, otherwise do nothing
            if (!m_pathBuffer.HasPath) return Vector3.zero;
            if (mapData == null) return Vector3.zero;


            // --- Waypoint advance logic ---

            Vector3 waypoint = m_pathBuffer.CurrentWaypointWorld(mapData, yOffset: m_agentPlaneOffsetY);
            Vector3 toWaypoint = waypoint - transform.position;
            toWaypoint.y = 0f;

            float speed = new Vector3(m_velocity.x, 0f, m_velocity.z).magnitude;
            float cell = mapData.CellTileSize;

            // NOTE: waypointRadius is not enough when moving fast, because it can skip over the waypoint in one frame. Speed-based buffer to ensure it doesn't skip waypoints.
            // Tune multiplier as needed (1.5x is a good starting point, but may need to be higher for very fast agents or lower for slow agents to prevent jittery behavior at the end of the path)
            float passDist = m_waypointRadius + speed * Time.deltaTime * 1.5f;
            passDist = Mathf.Max(passDist, 0.75f * cell);   // cap to ensure it’s not too small for grid centers
            passDist = Mathf.Min(passDist, 1.25f * cell);   // cap so it can’t skip a bunch of cells 

            // advance waypoint if close enough or if overshot and moving away (handles fast movement + prevents jittery back-and-forth at the end of the path)
            float radius = Mathf.Max(m_waypointRadius, 0.45f * cell);
            if (m_pathBuffer.ShouldAdvanceWithPassDistance(toWaypoint, m_velocity, radius, passDist))
            {
                m_pathBuffer.Advance();
                if (!m_pathBuffer.HasPath) return -m_velocity;  // brake / stop steering at end of path

                // steer toward next waypoint immediately for smoother path following (instead of waiting one frame to update the waypoint and then steering)
                Vector3 nextWaypoint = m_pathBuffer.CurrentWaypointWorld(mapData, yOffset: m_agentPlaneOffsetY);
                waypoint = nextWaypoint; 
            }


            // --- LOSE / SWAY steer (not a sense of hurry, does pirouetting more often) ---
            Vector3 loose = ComputeLeaderSteer_Loose(waypoint);

            // --- STRICT / TIGHT steer (segment-carrot + lateral damp + wall slowdown) ---
            Vector3 strict = ComputeLeaderSteer_Strict(mapData);

            // Blend between them (0 = loose, 1 = strict)
            return Vector3.Lerp(loose, strict, m_strictFollow01);
        }


        private Vector3 ComputeLeaderSteer_Loose(Vector3 waypoint)
        {
            // original planned path
            Vector3 steering = ArriveWorld(waypoint, m_slowingRadius);



            Vector3 velXZ = m_velocity;
            velXZ.y = 0f;

            Vector3 toWaypoint = waypoint - transform.position;
            toWaypoint.y = 0f;

            Vector3 probeDir;

            // Probe in the direction unit intend to travel this frame
            if (velXZ.sqrMagnitude > 1e-6f)
            {
                // Moving already: probe where agent actually travelling.
                probeDir = velXZ.normalized;
            }
            else if (toWaypoint.sqrMagnitude > 1e-6f)
            {
                // Nearly stopped: probe toward the waypoint.
                probeDir = toWaypoint.normalized;
            }
            else
            {
                // No meaningful direction to probe.
                return steering;
            }

            // Only avoid if forward is blocked OR BOTH sides are blocked (tight corridor, otherwise leave path alone. Trying to avoid the pirouette problem when path is hugging diagonal obstacle-corners)
            bool needAvoid = ProbePath(probeDir);
            if (!needAvoid)
                return steering;    // path is clear, no need to apply avoidance steer. Just follow path as normal.

            // If forward/sides probe is blocked, then apply avoidance steer. Otherwise, if way ahead is clear, do not apply avoidance and just follow path as normal.
            // This way it won't try to avoid if it's already following a path that is hugging along a wall (otherwise it tries to avoid the wall it's following and ends up pirouetting in place)
            Vector3 avoid = m_mapSense.ComputeObstacleAvoidance(
                       transform.position, probeDir,
                       m_lookAhead, m_sideOffset, m_agentPlaneOffsetY) * m_avoidWeight;

            // avoid can be very strong, clamping it to a reasonable max to prevent erratic behavior. Tune needed. (0.5–0.8 * maxAccel seems good in testing)
            float maxAvoid = 0.75f * m_maxAccel;
            avoid = Vector3.ClampMagnitude(avoid, maxAvoid);

            return steering + avoid;
        }

        private Vector3 ComputeLeaderSteer_Strict(MapData mapData)
        {

            for (int safety = 0; safety < 6; safety++)
            {
                int cursor = m_pathBuffer.Cursor;
                var path = m_pathBuffer.Path;
                if (mapData == null || path == null || cursor < 0 || cursor >= path.Count) return Vector3.zero;

                Vector3 pos = transform.position;
                pos.y = m_agentPlaneOffsetY;

                // Current + previous waypoint (segment)                    // switch to WaypointWorldAtCursorOffset() ?
                Vector3 waypointCur = mapData.IndexToWorldCenterXZ(path[cursor], m_agentPlaneOffsetY);
                Vector3 waypointPrev = (cursor > 0)
                    ? mapData.IndexToWorldCenterXZ(path[cursor - 1], m_agentPlaneOffsetY)
                    : pos;

                Vector3 segment = waypointCur - waypointPrev;
                segment.y = 0f;
                float segmentLength = segment.magnitude;
                if (segmentLength <= 1e-6f)
                {
                    // Degenerate segment (duplicate waypoint) — skip it.
                    m_pathBuffer.Advance();
                    if (!m_pathBuffer.HasPath) return -m_velocity;
                    continue;
                }

                Vector3 segmentDir = segmentLength > 1e-6f ? (segment / segmentLength) : (waypointCur - pos).normalized;
                segmentDir.y = 0f;
                if (segmentDir.sqrMagnitude < 1e-6f)
                    return -m_velocity; // or Vector3.zero; but braking is usually nicer
                segmentDir.Normalize();

                // projection distance along segment (from prev)
                float t = 0f;
                if (segmentLength > 1e-6f)
                    t = Mathf.Clamp(Vector3.Dot((pos - waypointPrev), segmentDir), 0f, segmentLength);

                float advRadius = Mathf.Max(m_waypointRadius, 0.45f * mapData.CellTileSize);

                // If we’re basically already at the end of this segment, advance cursor.
                if (segmentLength > 1e-6f && t >= segmentLength - advRadius)
                {
                    m_pathBuffer.Advance();
                    if (!m_pathBuffer.HasPath) return -m_velocity; // end of path: brake
                    continue; // try again on next segment
                }


                // lookahead grows with speed to still work with high speed agents
                float speed = new Vector3(m_velocity.x, 0f, m_velocity.z).magnitude;
                float lookAhead = (m_leaderLookAheadCells * mapData.CellTileSize) + speed * m_leaderLookAheadSpeed;

                float targetT = Mathf.Clamp(t + lookAhead, 0f, segmentLength);
                Vector3 carrot = waypointPrev + segmentDir * targetT;
                carrot.y = m_agentPlaneOffsetY;

                // Lateral damping = tightness
                Vector3 v = m_velocity; v.y = 0f;
                Vector3 vPar = Vector3.Dot(v, segmentDir) * segmentDir; // parallel component is the part of velocity that is moving along the path
                Vector3 vPerp = v - vPar;                               // perpendicular component is the part of velocity that is pushing into the wall when following a path that hugs a wall
                Vector3 lateralDampSteer = (-vPerp) * m_lateralDamp;

                // Slow down if about to face-plant into a wall 
                float speedScale = SpeedScaleFromProbe(segmentDir, pos);
                Vector3 arrive = ArriveWorldScaled(carrot, m_slowingRadius, speedScale);

                return arrive + lateralDampSteer;
            }

            return Vector3.zero; // fallback if we advanced too many times
        }


        #endregion



        #region Flocking behaviour

        // --- Path B: Follower movement ---
        private Vector3 ComputeFollowerSteer(MapData mapData)
        {
            // Only if unit is a follower: use flocking instead of A* pathing to move. But they are still slightly map aware

            Vector3 flock = ComputeFlocking();  // compute Seperation(don’t overlap), Cohesion(stay together), and Alignment(match direction)
            Vector3 follow = Vector3.zero;

            // follow their leader if they have one
            if (m_leader != null)
            {
                Vector3 myPos = transform.position; myPos.y = 0f;
                Vector3 leaderPos = m_leader.transform.position; leaderPos.y = 0f;

                // Vector from follower -> leader (planar)
                Vector3 dirToLeader = leaderPos - myPos;        // REMEMBER: this is a direction vector, not a world position!

                //Vector3 dirToLeaderNorm = dirToLeader.normalized;     // removed them until implemented banding / rejoin logic.
                //float distToLeader = dirToLeader.magnitude;           // removed them until implemented banding / rejoin logic.

                // Leader forward (planar)
                Vector3 leaderFaceDir = m_leader.transform.forward; 
                leaderFaceDir.y = 0f;

                // Leader velocity (planar)
                Vector3 velDir = m_leader.Velocity;             //Vector3 leaderVel = m_leader.m_velocity;  // WAIT WHAT! I can acces a private field of another instance of the same class? 
                velDir.y = 0f;

                // Choose one heading   (prefer velocity if moving, else faceing dir, else .forward as a fallback)
                Vector3 leaderHeading = Vector3.forward;

                if (velDir.sqrMagnitude > 1e-4f)
                {
                    leaderHeading = velDir.normalized;
                }
                else if (leaderFaceDir.sqrMagnitude > 1e-4f)
                {
                    leaderHeading = leaderFaceDir.normalized;
                }


                // --- TEMPORARY TESTING: different follow modes to test different ways of following the leader, before implementing banding/rejoin logic ---

                switch (m_testFollowMode)
                {
                    case RoughTestFollowMode.FollowPoint:
                        follow = FollowModeFollowPoint(leaderPos, leaderHeading, mapData);
                        break;
                    case RoughTestFollowMode.Crowd:
                        follow = FollowModeCrowd(dirToLeader);
                        break;
                    case RoughTestFollowMode.LeaderTail:
                        follow = FollowModeLeaderTail(leaderPos, leaderHeading, mapData);
                        break;
                    case RoughTestFollowMode.Anchor:
                        follow = FollowModeAnchor(leaderPos, leaderHeading, mapData);
                        break;
                }




                //follow = FollowModeFollowPoint(leaderPos, leaderHeading, mapData);

                // later swap:
                // follow = FollowModeCrowd(dirToLeader);
                // follow = FollowModeLeaderTail(leaderPos, leaderHeading, mapData);
                // follow = FollowModeAnchor(leaderPos, leaderHeading, mapData);

            }

            // combine group flocking behaviour + try to follow leader
            Vector3 desired = flock + follow;

            // desiredDir is for obstacle probing (look-ahead direction), NOT the boids alignment rule
            Vector3 desiredDir = desired.sqrMagnitude > 1e-6f ? desired.normalized : Vector3.zero;

            // avoid map-edges and obstacles / blocked tiles 
            Vector3 avoid = m_mapSense.ComputeObstacleAvoidance(transform.position, desiredDir, m_lookAhead, m_sideOffset, m_agentPlaneOffsetY) * m_avoidWeight;

            // decide final movement for this unit 
            return desired + avoid;
        }


        // Use when alerted? To showcase different reactions to being disturbed, more like an organism
        // Crowd / Pond Scum
        private Vector3 FollowModeCrowd(Vector3 dirToLeader)
        {
            // This made the boid collapse into a crowd like aura, or like pond scum. But could be fun to implement as a mode later   
            Vector3 follow = SeekTo(dirToLeader) * m_followLeaderWeight;   
            return follow;
        }

        // Follow the leader directly, like a tail.
        // Can cause crowding on top of leader if followDist is too small, but might look more natural if they are trying to follow closely.
        private Vector3 FollowModeLeaderTail(Vector3 leaderPos, Vector3 leaderHeading, MapData mapData)
        {
            float cell = mapData.CellTileSize;

            // Desired spacing behind leader (in world units)
            float tailDist = m_leaderFollowDist * cell; // set m_leaderFollowDist to 2–4 for clear tail feel when in this mode 

            // Target point behind leader
            Vector3 tailPoint = leaderPos - leaderHeading * tailDist;
            tailPoint.y = m_agentPlaneOffsetY;

            // Banding: no pull if already close, full pull if far
            Vector3 toTail = tailPoint - transform.position;
            toTail.y = 0f;

            float dist = toTail.magnitude;
            float bandMin = m_bandMinCells * cell;
            float bandMax = m_bandMaxCells * cell;

            float followScale = 0f;
            if (dist > bandMin)
                followScale = Mathf.InverseLerp(bandMin, bandMax, dist); // 0..1

            // Modify arrive so agent doesn't overshoot and orbit
            float slowRadius = Mathf.Max(tailDist, 1.5f * cell);

            Vector3 follow = ArriveWorld(tailPoint, slowRadius) * (m_followLeaderWeight * followScale);
            return follow; 
        }

        private Vector3 FollowModeFollowPoint(Vector3 leaderPos, Vector3 leaderHeading, MapData mapData)
        {
            // Follow a point a fixed distance behind the leader. This way it makes sure leader is distinct, and followers follow behind propperly. 
            float followDist = m_leaderFollowDist * mapData.CellTileSize; // e.g. 2–4 cells
            Vector3 followPoint = leaderPos - leaderHeading * followDist;

            Vector3 follow = ArriveWorld(followPoint, slowRadius: followDist) * m_followLeaderWeight;
            return follow; 
        }

        // Follow a moving anchor region.
        // Instead of seeking the leader, seek an anchor point behind the leader and let cohesion/alignment keep them together.
        private Vector3 FollowModeAnchor(Vector3 leaderPos, Vector3 leaderHeading, MapData mapData)
        {
            float cell = mapData.CellTileSize;

            float anchorDist = m_followDistanceCells * mapData.CellTileSize; // e.g. 2–4 cells
            Vector3 anchor = leaderPos - leaderHeading * anchorDist;
            anchor.y = m_agentPlaneOffsetY;


            // --- Wander/explore around anchor (low frequency changes = personality) ---
            Vector3 exploreTarget = WanderCuriosExplorer(cell, anchor, out float exploreRadius);


            // --- Rubber band effect: stronger pull the farther they are, but no pull inside a certain radius to allow free roaming and prevent jitter when close --- 
            Vector3 toAnchor = anchor - transform.position;
            toAnchor.y = 0f;
            float dist = toAnchor.magnitude;

            float bandMin = m_bandMinCells * cell;
            float bandMax = m_bandMaxCells * cell;

            float followScale = 0f;
            if (dist > bandMin)
                followScale = Mathf.InverseLerp(bandMin, bandMax, dist); // 0..1


            // --- Arrive to the anchor, not the leader, scaled by banding ---
            //Vector3 follow = ArriveWorld(anchor, slowRadius: anchorDist) * m_followLeaderWeight;
            Vector3 follow = ArriveWorld(exploreTarget, slowRadius: exploreRadius) * (m_followLeaderWeight * followScale);
            return follow;
        }

        // make another extra method except only FollowModeAnchor() call on this to give random area for agent to walk if no leader, or away from leader too long? 
        private Vector3 WanderCuriosExplorer(float cell, Vector3 anchor, out float exploreRadius)
        {
            // NEW, needs more testing

            // Wander offset / Curiosity, let them wander around a bit
            if (Time.time >= m_wanderNextTime)
            {
                Vector2 r = Random.insideUnitCircle;
                if (r.sqrMagnitude < 1e-6f) r = Vector2.right;

                r.Normalize();
                m_wanderOffset = new Vector3(r.x, 0f, r.y);

                // Personality knob: how often agents pick a new exploration direction
                m_wanderNextTime = Time.time + Random.Range(0.6f, 1.4f);
            }

            exploreRadius = m_exploreRadiusCells * cell; // e.g. 2–5 cells
            Vector3 exploreTarget = anchor + m_wanderOffset * exploreRadius;
            exploreTarget.y = m_agentPlaneOffsetY;

            return exploreTarget; 
        }





        // flock steering, boids-ish style. One neighbor scan, then 3 small rule methods. 
        private Vector3 ComputeFlocking()
        {
            // --- Tuned radii (use squared distance to avoid sqrt) ---
            float neighborRadSqr = m_neighborRadius * m_neighborRadius;
            float separationRadSqr = m_separationRadius * m_separationRadius;

            // --- Aggregates collected from neighbors ---
            Vector3 position = transform.position;

            Vector3 separationSum = Vector3.zero;  // summed repulsion vectors
            Vector3 cohesionPosSum = Vector3.zero; // summed neighbor positions (for centroid)
            Vector3 alignmentVelSum = Vector3.zero;    // summed neighbor velocities (for avg heading)
            int neighborCount = 0;


            // For each nearby neighbor (within m_neighborRadius):
            // - Separation: push away if inside m_separationRadius (stronger when very close).
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
                alignmentVelSum += other.m_velocity;                 // later becomes "average neighbor velocity"

                // separation only applies inside the smaller separation radius
                if (distSqr < separationRadSqr && distSqr > 1e-6f)
                    separationSum -= distanceToNeighbour / distSqr;    // stronger seperation force when closer grouped, repel hard if extremely close 

                // NOTE: Switch above line to below if I want stronger separation from leader 
                /*
                if (distSqr < separationRadSqr && distSqr > 1e-6f)
                {
                    float boost = (other == m_leader) ? m_leaderSeparationBoost : 1f;
                    separationSum -= (distanceToNeighbour / distSqr) * boost;
                }
                */


            }

            if (neighborCount == 0) return Vector3.zero;


            // --- Convert aggregates into 3 steering forces  ---

            // NOTE: weights tune behavior, not correctness.
            // High separation = loose flock; High cohesion = tight blob; High alignment = smooth flow.

            // For each nearby neighbor (within m_neighborRadius):
            Vector3 separation = ComputeSeparationFromAgg(separationSum);
            Vector3 cohesion = ComputeCohesionFromAgg(cohesionPosSum, neighborCount, position);
            Vector3 alignment = ComputeAlignmentFromAgg(alignmentVelSum, neighborCount);

            return separation + cohesion + alignment;
        }



        // Separation: don't overlap (stronger when very close).
        private Vector3 ComputeSeparationFromAgg(Vector3 separationSum)
        {
            // separationSum already encodes distance weighting (1/dist^2)
            return separationSum * m_separationWeight;
        }

        // Cohesion: stay with group, prevent drifting away   (pull toward neighbor centroid)
        private Vector3 ComputeCohesionFromAgg(Vector3 cohesionPosSum, int neighborCount, Vector3 myPos)
        {
            Vector3 center = cohesionPosSum / neighborCount;
            return SeekTo(center - myPos) * m_cohesionWeight;
        }

        // Alignment: go with the flow and match alignment of group direction.   (match avg neighbor velocity)
        private Vector3 ComputeAlignmentFromAgg(Vector3 alignmentVelSum, int neighborCount)
        {
            Vector3 avgVel = alignmentVelSum / neighborCount;

            // Convert avg velocity into a desired velocity at unit's max speed
            Vector3 desiredVel = avgVel.sqrMagnitude > 1e-6f ? avgVel.normalized * m_maxSpeed : Vector3.zero;

            // Steering force tries to change unit's velocity toward desiredVel
            return (desiredVel - m_velocity) * m_alignmentWeight;
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

            Vector3 desiredVel = toTarget.normalized * m_maxSpeed;
            return (desiredVel - m_velocity);
        }


        #endregion



        public bool TrySnapToNearestWalkable(int radius = 6)
        {
            if (m_mapSense == null) return false;
            MapData mapData = m_mapSense.Data;
            if (mapData == null) return false;

            if (!m_mapSense.TryWorldToIndex(transform.position, out int startIdx))
                return false;

            if (!mapData.IsBlocked[startIdx])
                return true; // already safe

            if (m_mapSense.TryGetNearestUnblockedIndex(startIdx, radius, out int safeIdx))
            {
                Vector3 p = mapData.IndexToWorldCenterXZ(safeIdx, m_agentPlaneOffsetY);
                transform.position = p;
                m_velocity = Vector3.zero;
                return true;
            }

            return false;
        }


        // Move this into AgentMapSense.cs or not? Hybrid responsability with local variables  
        private bool ProbePath(Vector3 forward)
        {
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);

            Vector3 probeF = transform.position + forward * m_lookAhead;
            Vector3 probeL = probeF - right * (m_sideOffset * 0.6f); // tighter than follower probes
            Vector3 probeR = probeF + right * (m_sideOffset * 0.6f);

            probeF.y = probeL.y = probeR.y = m_agentPlaneOffsetY;

            bool blockedF = !m_mapSense.IsWalkableWorld(probeF);
            bool blockedL = !m_mapSense.IsWalkableWorld(probeL);
            bool blockedR = !m_mapSense.IsWalkableWorld(probeR);

            bool needAvoid = blockedF || (blockedL && blockedR);
            return needAvoid;
        }


        private float SpeedScaleFromProbe(Vector3 dir, Vector3 pos)
        {
            // “slow down if walking into a wall”, not repel
            float la = m_lookAhead;
            Vector3 p1 = pos + dir * (la * 0.33f);
            Vector3 p2 = pos + dir * (la * 0.66f);
            Vector3 p3 = pos + dir * (la * 1.00f);

            p1.y = p2.y = p3.y = m_agentPlaneOffsetY;

            if (!m_mapSense.IsWalkableWorld(p1)) return 0.0f;
            if (!m_mapSense.IsWalkableWorld(p2)) return 0.33f;
            if (!m_mapSense.IsWalkableWorld(p3)) return 0.66f;
            return 1.0f;
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
            if (dist <= 0.001f) return -m_velocity;  // brake

            float targetSpeed = (dist < slowRadius)
                ? m_maxSpeed * (dist / slowRadius)
                : m_maxSpeed;

            Vector3 desiredVel = toTarget * (targetSpeed / dist);
            return (desiredVel - m_velocity);        // steering
        }


        private Vector3 ArriveWorldScaled(Vector3 worldTarget, float slowRadius, float speedScale)
        {
            Vector3 toTarget = worldTarget - transform.position;
            toTarget.y = 0f;

            float dist = toTarget.magnitude;
            if (dist <= 0.001f) return -m_velocity;

            float targetSpeed = (dist < slowRadius) ? m_maxSpeed * (dist / slowRadius) : m_maxSpeed;
            targetSpeed *= Mathf.Clamp01(speedScale);

            Vector3 desiredVel = toTarget * (targetSpeed / dist);
            return desiredVel - m_velocity;
        }



    }

}