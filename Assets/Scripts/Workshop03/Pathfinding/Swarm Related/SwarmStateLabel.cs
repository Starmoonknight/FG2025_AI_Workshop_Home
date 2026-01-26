using UnityEngine;
using AI_Workshop03.AI;



namespace AI_Workshop03
{

    [RequireComponent(typeof(SwarmingAgent))]
    public sealed class SwarmStateLabel : MonoBehaviour
    {

        private SwarmingAgent _agent;
        private Camera _cam;

        private void Awake()
        {
            _agent = GetComponent<SwarmingAgent>();
            _cam = Camera.main;
        }

        private void OnGUI()
        {
            if (_agent == null || _cam == null) return;

            Vector3 w = transform.position + Vector3.up * 1.2f;
            Vector3 s = _cam.WorldToScreenPoint(w);
            if (s.z <= 0f) return;

            string txt = _agent.IsLeader ? $"LEADER {_agent.State}" : $"FOLLOWER {_agent.State}";
            var r = new Rect(s.x - 60, Screen.height - s.y, 200, 20);
            GUI.Label(r, txt);
        }

    }
}