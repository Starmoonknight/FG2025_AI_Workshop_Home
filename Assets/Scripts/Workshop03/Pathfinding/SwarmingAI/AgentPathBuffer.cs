using System.Collections.Generic;
using UnityEngine;


// Pure Data holder + Stepping  
namespace AI_Workshop03.AI
{

    public sealed class AgentPathBuffer : MonoBehaviour
    {
        public IReadOnlyList<int> Path => _path;
        public int Cursor => _cursor;

        private List<int> _path;
        private int _cursor;


        public bool HasPath => _path != null && _path.Count > 0 && _cursor < _path.Count;
        public bool IsComplete => _path == null || _path.Count == 0 || _cursor >= _path.Count;

        // Metadata for current path 
        public int StartIndex { get; private set; } = -1;
        public int GoalIndex { get; private set; } = -1;
        public int PathRequestId { get; private set; } = 0;      // For later use, when multi pathing exists.



        public void Clear()
        {
            StartIndex = -1;
            GoalIndex = -1;
            PathRequestId = 0;

            _path = null;
            _cursor = 0;
        }

        public void SetPath(List<int> path, int startIdx, int goalIdx, int requestId)
        {
            if (path == null || path.Count == 0)
            {
                Clear();
                return;
            }

            _path = path;
            _cursor = 0;

            StartIndex = startIdx;
            GoalIndex = goalIdx;
            PathRequestId = requestId;
        }

        public int CurrentIndexOrMinusOne()
        {
            if (!HasPath) return -1;
            return _path[_cursor];
        }

        public Vector3 CurrentWaypointWorld(MapData data, float yOffset = 0f)
        {
            int idx = CurrentIndexOrMinusOne();
            if (idx < 0) return transform.position;

            // make use of existing grid-to-world helper that the map data offers 
            return data.IndexToWorldCenterXZ(idx, yOffset);
        }

        public bool TryAdvance(Vector3 currentPos, Vector3 currentWaypoint, float waypointRadius)
        {
            if (!HasPath) return false;

            float r2 = waypointRadius * waypointRadius;
            if ((currentPos - currentWaypoint).sqrMagnitude <= r2)
            {
                _cursor++;
                return true;
            }

            return false;
        }

        public void Advance()
        {
            if (!HasPath) return;
            _cursor++;
        }

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



    }

}