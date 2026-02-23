using System;
using System.Collections.Generic;
using UnityEngine;


namespace AI_Workshop03.AI
{

    [RequireComponent(typeof(AgentMapSense))]
    [RequireComponent(typeof(AgentPathBuffer))]
    public sealed class AgentPathRequester : MonoBehaviour
    {

        [Header("References")]
        [SerializeField] private NavigationServiceManager m_navigationService;

        [Header("Optional: debug visuals per request")]
        [SerializeField] private bool m_visualizeAll = false;
        [SerializeField] private bool m_showFinalPath = false;
        [SerializeField] private bool m_showStartAndGoal = false;

        private AgentMapSense m_mapSense;
        private AgentPathBuffer m_pathBuffer;
                
        // maybe a dictionary? to keep track if "this" request got denied / accepted. Probably overdoing it for no real gain
        private int m_requestId;     // stale callback guard if you spam requests.
        private bool m_pathRequestAccepted = false; 

        public NavigationServiceManager NavigationService => m_navigationService;
        public bool IsRequestInFlight => m_navigationService != null && m_navigationService.IsPathComputing;
        public bool PathAccepted => m_pathRequestAccepted;


        // a setter for spawner injection
        public void SetNavigationService(NavigationServiceManager svc) => m_navigationService = svc;



        private void Awake()
        {
            m_mapSense = GetComponent<AgentMapSense>();
            m_pathBuffer = GetComponent<AgentPathBuffer>();

            // NOTE: The spawner is currently handling this
            //if (_navigationService == null) _navigationService = FindFirstObjectByType<NavigationServiceManager>();
        }


                
        /// <summary>
        /// Request an A* path from the agent's current cell to a world-space goal.
        /// Returns false if validation fails (no map, invalid cells, blocked, unreachable, etc.)
        /// </summary>
        public bool RequestPathToWorld(Vector3 worldGoal)
        {

            // --- called by manager when leader needs a new path ---

            if (m_mapSense == null) return false;
            
            if (!m_mapSense.TryGetValidStartIndexFromCurrentPos( out int startIdx)) 
                return false;   // agent standing in invalid/blocked cell

            if (!m_mapSense.TryWorldToIndex(worldGoal, out int goalIdx)) 
                return false;   // world position not on grid

            if (!m_mapSense.IsWalkableIndex(startIdx) || !m_mapSense.IsWalkableIndex(goalIdx))
                return false;   // make sure requested cells are walkable 

            return RequestPathIndices(startIdx, goalIdx);
        }


        /// <summary>
        /// Request an A* path using already-known grid indices.
        /// </summary>
        public bool RequestPathIndices(int startIdx, int goalIdx)
        {
            m_pathRequestAccepted = false;

            var data = m_mapSense.Data;
            if (data == null) return false;

            // validate walkability of start/goal cell 
            if (!m_mapSense.IsWalkableIndex(startIdx)) return false;
            if (!m_mapSense.IsWalkableIndex(goalIdx)) return false;

            if (m_navigationService == null) return false;

            // reachability pre-check, if Start <-!/!-> Goal has no chance of being reachable       (fast “don’t even try A* if disconnected”)
            if (m_navigationService != null && !m_navigationService.TryValidateReachablePair(startIdx, goalIdx))
                return false;


            // NOTE IMPORTANT:
            // current NavigationServiceManager is single-flight, it cannot process multiple paths at same time.  
            //
            // could do this if I wanted paths to not be interupted: 
            // if (_navigationService.IsPathComputing) return false; // deny request, try again later, move responsability into AgentPathRequester when set up properly
            //
            //  Design issue (not a crash, but will bite later): global single-flight path cancels other agents
            //          WARNING: CURRENT SOLUTION WILL INTERUPT ANY AND ALL PATHS!
            //
            // Simple temp solution for Lab: cancel old computation and start new.
            if (m_navigationService.IsPathComputing)
                m_navigationService.CancelPath(clearVisuals: false);

            m_pathBuffer.Clear();        // data ownership belongs in buffer

            int myReq = ++m_requestId;

            m_navigationService.RequestTravelPath(
                startIdx,
                goalIdx,
                path => OnPathFound(myReq, path, startIdx, goalIdx),
                m_visualizeAll,
                m_showFinalPath,
                m_showStartAndGoal
            );

            m_pathRequestAccepted = true;
            return true;
        }




        // private void OnPathFound(int requestId, List<int> path, int startIdx, int goalIdx, int pathReqId)
        // dont remember where I left of, myReq was used for both ID and think the idea was to later introduce two-part ID system: 
        // - a service-side job id (queue slot, worker id)
        // - a path version id independent of request spam
        //
        // but nothing at current stage reflects that so guess I will remember what the idea was later.. 


        private void OnPathFound(int pathReqId, List<int> path, int startIdx, int goalIdx)
        {
            if (pathReqId != m_requestId) return;    // stale callback guard

            m_pathBuffer.SetPath(path, startIdx, goalIdx, pathReqId);  // store path data in buffer



            //if (_pathBuffer.HasPath)
            //    transform.position = _mapSense.IndexToWorldCenter(_pathBuffer.Path[0], _agentPlaneOffsetY);
        }

        public void ClearPath()
        {
            m_pathBuffer.Clear();
        }




    }
}
