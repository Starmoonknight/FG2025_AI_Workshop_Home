using System;
using UnityEngine;


namespace AI_Workshop03.AI
{


    public sealed class AgentMapSense : MonoBehaviour
    {

        [SerializeField] private MapManager _mapManager;

        private MapData _data;

        public MapManager MapManager => _mapManager;    
        public MapData Data => _data;


        public event Action<MapData> OnDataChanged;



        private void Awake()
        {
            // NOTE: The spawner is currently handling this
            //if (_mapManager == null) _mapManager = FindFirstObjectByType<MapManager>();
        }

        private void OnEnable()
        {
            // NOTE: The spawner is currently handling this
            /*
            if (_mapManager == null)
                _mapManager = FindFirstObjectByType<MapManager>();
            */

            if (_mapManager != null)
            {
                _mapManager.OnMapRebuiltDataReady += HandleMapRebuilt;

                // If map already exists, sync immediately
                var current = _mapManager.Data;
                if (current != null)
                    HandleMapRebuilt(current);
            }
        }

        private void OnDisable()
        {
            if (_mapManager != null)
                _mapManager.OnMapRebuiltDataReady -= HandleMapRebuilt;
        }




        // a setter for spawner injection, also ensures correct MapManager is selected if multile exists
        public void SetMapManager(MapManager mapM)
        {
            if (_mapManager == mapM) return;

            // Unhook old
            if (_mapManager != null)
                _mapManager.OnMapRebuiltDataReady -= HandleMapRebuilt;

            _mapManager = mapM;

            // Hook new + sync
            if (_mapManager != null)
            {
                _mapManager.OnMapRebuiltDataReady += HandleMapRebuilt;

                var current = _mapManager.Data;
                if (current != null)
                    HandleMapRebuilt(current);
            }
        }


        private void HandleMapRebuilt(MapData data)
        {
            _data = data;

            OnDataChanged?.Invoke(_data);
        }



        public bool TryWorldToIndex(Vector3 worldPos, out int index)
        {
            index = -1;
            if (_data == null) return false;

            return _data.TryWorldToIndexXZ(worldPos, out index);
        }

        public bool IsWalkableIndex(int index)
        {
            if (_data == null) return false;
            if (!_data.IsValidCellIndex(index)) return false;

            return !_data.IsBlocked[index];
        }

        public bool IsWalkableWorld(Vector3 worldPos)
        {
            if (_data == null) return false;
            if (!TryWorldToIndex(worldPos, out int idx)) return false;
            return !_data.IsBlocked[idx];
        }

        public Vector3 IndexToWorldCenter(int index, float yOffset = 0f)
        {
            if (_data == null) return transform.position;

            return _data.IndexToWorldCenterXZ(index, yOffset);
        }

        public bool TryGetValidStartIndexFromCurrentPos(out int currentStartIdx)
        {
            if (_data == null)
            {
                currentStartIdx = -1; 
                return false;
            }

            MapData data = _data;

            if (!data.TryWorldToIndexXZ(transform.position, out currentStartIdx))
                return false;

            if (!data.IsValidCellIndex(currentStartIdx))
                return false;

            if (data.IsBlocked[currentStartIdx])
                return false;

            return true; 
        }

        public bool TryGetNearestUnblockedIndex(int startIdx, int radius, out int foundIdx)
        {
            MapData data = _data;
            if (data == null) { foundIdx = -1; return false; }
            if (!data.IsValidCellIndex(startIdx)) { foundIdx = -1; return false; }
            if (!data.IsBlocked[startIdx]) { foundIdx = startIdx; return true; }

            data.IndexToXY(startIdx, out int startX, out int startY);

            for (int r = 1; r <= radius; r++)
            {
                for (int dirY = -r; dirY <= r; dirY++)
                    for (int dirX = -r; dirX <= r; dirX++)
                    {
                        int x = startX + dirX;
                        int y = startY + dirY;
                        if (!GridMath.IsValidCoord(x, y, data.Width, data.Height)) continue;

                        int idx = data.CoordToIndex(x, y);
                        if (!data.IsBlocked[idx])
                        {
                            foundIdx = idx;
                            return true;
                        }
                    }
            }

            foundIdx = -1;
            return false;
        }



        /// <summary>
        /// Cheap "respect map" for non-A* followers:
        /// probe a few points ahead; if blocked, add a sideways avoidance steer.
        /// </summary>
        public Vector3 ComputeObstacleAvoidance(Vector3 pos, Vector3 desiredDirNorm, float lookAheadDist, float sideOffsetDist, float agentPlaneOffsetY)
        {
            if (_data == null) return Vector3.zero;
            if (desiredDirNorm.sqrMagnitude < 1e-6f) return Vector3.zero;

            Vector3 forward = new Vector3(desiredDirNorm.x, 0f, desiredDirNorm.z);
            if (forward.sqrMagnitude < 1e-6f) return Vector3.zero;
            forward.Normalize();

            Vector3 right = new Vector3(forward.z, 0f, -forward.x); // 90 deg in XZ

            // probe points
            Vector3 probeF = pos + forward * lookAheadDist;
            Vector3 probeL = probeF - right * sideOffsetDist;
            Vector3 probeR = probeF + right * sideOffsetDist;

            probeF.y = agentPlaneOffsetY;
            probeL.y = agentPlaneOffsetY;
            probeR.y = agentPlaneOffsetY;

            bool blockedF = IsBlockedWorld(probeF);
            bool blockedL = IsBlockedWorld(probeL);
            bool blockedR = IsBlockedWorld(probeR);

            // steer away from blocked directions
            Vector3 avoid = Vector3.zero;
            if (blockedF) avoid -= forward;
            if (blockedL) avoid += right;
            if (blockedR) avoid -= right;

            return avoid;
        }

        private bool IsBlockedWorld(Vector3 worldPos)
        {
            if (!TryWorldToIndex(worldPos, out int idx)) return true; // outside map treated as blocked
            return !IsWalkableIndex(idx);
        }






    }
}