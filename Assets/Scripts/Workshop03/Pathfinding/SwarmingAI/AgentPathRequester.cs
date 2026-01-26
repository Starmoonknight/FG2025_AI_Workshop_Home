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
        [SerializeField] private NavigationServiceManager _navigationService;

        [Header("Optional: debug visuals per request")]
        [SerializeField] private bool _visualizeAll = false;
        [SerializeField] private bool _showFinalPath = false;
        [SerializeField] private bool _showStartAndGoal = false;

        private AgentMapSense _mapSense;
        private AgentPathBuffer _pathBuffer;
                
        // maybe a dictionary? to keep track if "this" request got denied / accepted. Probably overdoing it for no real gain
        private int _requestId;     // stale callback guard if you spam requests.
        private bool _pathRequestAccepted = false; 

        public NavigationServiceManager NavigationService => _navigationService;
        public bool IsRequestInFlight => _navigationService != null && _navigationService.IsPathComputing;
        public bool PathAccepted => _pathRequestAccepted;


        // a setter for spawner injection
        public void SetNavigationService(NavigationServiceManager svc) => _navigationService = svc;



        private void Awake()
        {
            _mapSense = GetComponent<AgentMapSense>();
            _pathBuffer = GetComponent<AgentPathBuffer>();

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

            if (_mapSense == null) return false;
            
            if (!_mapSense.TryGetValidStartIndexFromCurrentPos( out int startIdx)) 
                return false;   // agent standing in invalid/blocked cell

            if (!_mapSense.TryWorldToIndex(worldGoal, out int goalIdx)) 
                return false;   // world position not on grid

            if (!_mapSense.IsWalkableIndex(startIdx) || !_mapSense.IsWalkableIndex(goalIdx))
                return false;   // make sure requested cells are walkable 

            return RequestPathIndices(startIdx, goalIdx);
        }


        /// <summary>
        /// Request an A* path using already-known grid indices.
        /// </summary>
        public bool RequestPathIndices(int startIdx, int goalIdx)
        {
            _pathRequestAccepted = false;

            var data = _mapSense.Data;
            if (data == null) return false;

            // validate walkability of start/goal cell 
            if (!_mapSense.IsWalkableIndex(startIdx)) return false;
            if (!_mapSense.IsWalkableIndex(goalIdx)) return false;

            if (_navigationService == null) return false;

            // reachability pre-check, if Start <-!/!-> Goal has no chance of being reachable       (fast “don’t even try A* if disconnected”)
            if (_navigationService != null && !_navigationService.TryValidateReachablePair(startIdx, goalIdx))
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
            if (_navigationService.IsPathComputing)
                _navigationService.CancelPath(clearVisuals: false);

            _pathBuffer.Clear();        // data ownership belongs in buffer

            int myReq = ++_requestId;

            _navigationService.RequestTravelPath(
                startIdx,
                goalIdx,
                path => OnPathFound(myReq, path, startIdx, goalIdx, myReq),
                _visualizeAll,
                _showFinalPath,
                _showStartAndGoal
            );

            _pathRequestAccepted = true;
            return true;
        }


        private void OnPathFound(int requestId, List<int> path, int startIdx, int goalIdx, int pathReqId)
        {
            if (requestId != _requestId) return;    // stale callback guard

            _pathBuffer.SetPath(path, startIdx, goalIdx, pathReqId);  // store path data in buffer



            //if (_pathBuffer.HasPath)
            //    transform.position = _mapSense.IndexToWorldCenter(_pathBuffer.Path[0], _agentPlaneOffsetY);
        }

        public void ClearPath()
        {
            _pathBuffer.Clear();
        }










    }
}
