using System.Collections.Generic;
using UnityEngine;


// Pure Data holder + Stepping  
namespace AI_Workshop03.AI
{

    public sealed class AgentPathBuffer : MonoBehaviour
    {
        private List<int> m_path;
        private int m_cursor;
        
        public IReadOnlyList<int> Path => m_path;
        public int Cursor => m_cursor;



        public bool HasPath => m_path != null && m_path.Count > 0 && m_cursor < m_path.Count;
        public bool IsComplete => m_path == null || m_path.Count == 0 || m_cursor >= m_path.Count;

        // Metadata for current path 
        public int StartIndex { get; private set; } = -1;
        public int GoalIndex { get; private set; } = -1;
        public int PathRequestId { get; private set; } = 0;      // For later use, when multi pathing exists.



        public void Clear()
        {
            StartIndex = -1;
            GoalIndex = -1;
            PathRequestId = 0;

            m_path = null;
            m_cursor = 0;
        }

        public void SetPath(List<int> path, int startIdx, int goalIdx, int requestId)
        {
            if (path == null || path.Count == 0)
            {
                Clear();
                return;
            }

            m_path = path;
            m_cursor = 0;

            StartIndex = startIdx;
            GoalIndex = goalIdx;
            PathRequestId = requestId;
        }

        public int CurrentIndexOrMinusOne()
        {
            if (!HasPath) return -1;
            return m_path[m_cursor];
        }

        public Vector3 CurrentWaypointWorld(MapData data, float yOffset = 0f)
        {
            int idx = CurrentIndexOrMinusOne();
            if (idx < 0) return transform.position;

            // make use of existing grid-to-world helper that the map data offers 
            return data.IndexToWorldCenterXZ(idx, yOffset);
        }

        public Vector3 WaypointWorldAtCursorOffset(int offset, MapData data, float y)
        {
            if (m_path == null) return transform.position;
            int i = Mathf.Clamp(m_cursor + offset, 0, m_path.Count - 1);

            return data.IndexToWorldCenterXZ(m_path[i], y);
        }

        public bool TryAdvance(Vector3 currentPos, Vector3 currentWaypoint, float waypointRadius)
        {
            if (!HasPath) return false;

            float r2 = waypointRadius * waypointRadius;
            if ((currentPos - currentWaypoint).sqrMagnitude <= r2)
            {
                m_cursor++;
                return true;
            }

            return false;
        }

        public void Advance()
        {
            if (!HasPath) return;
            m_cursor++;
        }

        /// <summary>
        /// Determines whether the entity should proceed toward the specified waypoint based on its current velocity and
        /// proximity to the waypoint.
        /// </summary>
        /// <remarks>This method is typically used in pathfinding or movement logic to decide when an
        /// agent should advance to the next waypoint. It considers both the distance to the waypoint and the direction
        /// of movement to handle cases where the agent may have overshot the target.</remarks>
        /// <param name="toWaypoint">A vector representing the position of the target waypoint relative to the entity. The Y component is
        /// ignored.</param>
        /// <param name="vel">The current velocity vector of the entity. The Y component is ignored.</param>
        /// <param name="radius">The distance threshold, in world units, used to determine proximity to the waypoint.</param>
        /// <returns>true if the entity is within the specified radius of the waypoint, or has passed near the waypoint and is
        /// moving away; otherwise, false.</returns>
        public bool ShouldAdvance(Vector3 toWaypoint, Vector3 vel, float radius)
        {
            toWaypoint.y = 0f;
            vel.y = 0f;

            float r2 = radius * radius;
            float d2 = toWaypoint.sqrMagnitude;

            // inside radius
            if (d2 <= r2) return true;

            // “passed it” (near-ish and dot < 0)
            float near2 = (radius * 2f) * (radius * 2f);
            if (d2 <= near2 && Vector3.Dot(vel, toWaypoint) < 0f)
                return true;

            return false;
        }


        /// <summary>
        /// Determines whether the agent should advance towards a waypoint based on its current velocity and proximity
        /// to the waypoint.
        /// </summary>
        /// <remarks>The method evaluates both the agent's proximity to the waypoint and whether it is
        /// moving away from the waypoint within a specified pass distance. This allows for more robust waypoint
        /// advancement decisions, especially when the agent may overshoot or pass the waypoint due to its
        /// velocity.</remarks>
        /// <param name="toWaypoint">A vector representing the direction and distance from the agent to the target waypoint. The Y component is
        /// ignored.</param>
        /// <param name="vel">The agent's current velocity vector. The Y component is ignored.</param>
        /// <param name="radius">The radius within which the agent is considered to have reached the waypoint. Must be non-negative.</param>
        /// <param name="passDist">The distance threshold used to determine if the agent has passed the waypoint, factoring in its velocity.
        /// Must be non-negative.</param>
        /// <returns>true if the agent should advance towards the waypoint; otherwise, false.</returns>
        public bool ShouldAdvanceWithPassDistance(Vector3 toWaypoint, Vector3 vel, float radius, float passDist)
        {
            toWaypoint.y = 0f;
            vel.y = 0f;

            float d2 = toWaypoint.sqrMagnitude;

            // Inside radius = reached
            float r2 = radius * radius;
            if (d2 <= r2) return true;

            // Passed it = moving away + within passDist (speed-scaled)
            float p2 = passDist * passDist;
            if (d2 <= p2 && Vector3.Dot(vel, toWaypoint) < 0f)
                return true;

            return false;
        }



    }

}