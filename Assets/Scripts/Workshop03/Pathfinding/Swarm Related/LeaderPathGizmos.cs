using UnityEngine;
using AI_Workshop03.AI;



namespace AI_Workshop03
{


    [RequireComponent(typeof(SwarmingAgent))]
    [RequireComponent(typeof(AgentPathBuffer))]
    [RequireComponent(typeof(AgentMapSense))]
    public sealed class LeaderPathGizmos : MonoBehaviour
    {
        public Color pathColor = Color.yellow;
        [Min(0.01f)] public float nodeRadius = 0.08f;


        private SwarmingAgent _agent;
        private AgentPathBuffer _buffer;
        private AgentMapSense _sense;

        private void Awake()
        {
            _agent = GetComponent<SwarmingAgent>();
            _buffer = GetComponent<AgentPathBuffer>();
            _sense = GetComponent<AgentMapSense>();
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;
            if (_buffer == null || _sense == null) return;

            if (_agent != null && !_agent.IsLeader) return;

            var data = _sense.Data;
            if (data == null) return;
            if (!_buffer.HasPath || _buffer.Path == null || _buffer.Path.Count < 2) return;

            Gizmos.color = pathColor;

            // draw full polyline
            for (int i = 0; i < _buffer.Path.Count - 1; i++)
            {
                Vector3 a = data.IndexToWorldCenterXZ(_buffer.Path[i], 0f);
                Vector3 b = data.IndexToWorldCenterXZ(_buffer.Path[i + 1], 0f);
                Gizmos.DrawLine(a, b);
                Gizmos.DrawSphere(a, nodeRadius);
            }

            // highlight current waypoint
            int c = Mathf.Clamp(_buffer.Cursor, 0, _buffer.Path.Count - 1);
            Vector3 wp = data.IndexToWorldCenterXZ(_buffer.Path[c], 0f);
            Gizmos.DrawSphere(wp, nodeRadius * 1.6f);
        }



    }
}