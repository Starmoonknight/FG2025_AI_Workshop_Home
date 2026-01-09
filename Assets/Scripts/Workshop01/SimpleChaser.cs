using UnityEngine;
using UnityEngine.AI;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AI_Workshop01
{
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(Collider))]
    public class SimpleChaser : MonoBehaviour
    {
        public enum FSM
        {
            STATE_PATROLLING,
            STATE_ALERTED,
            STATE_ENGAGING,
            STATE_DISENGAGE
        }

        private FSM _currentState = FSM.STATE_PATROLLING;
                
        
        [Header("Patrol")]
        [SerializeField, Tooltip("All points to be patroled, may be done in order or random")]
        private Transform[] _waypoints;
        [SerializeField, Tooltip("How close guard needs to get to its goal")]
        private float _waypointTolerance = 0.5f;
        [SerializeField, Tooltip("False: Patrols way points in order")]
        private bool _randomPatroling = false;
        [SerializeField,Tooltip("Max patrol radius around a way point")]
        private float _patrolRadius = 3f;
        [SerializeField, Tooltip("Max time guard can spend patroling at one spot")]
        private float _maxPatrolDuration = 1.5f;

        private bool _arrivedAtWaypoint = false;

        [Header("Pathing")]
        [SerializeField, Range(0f, 200f), Tooltip("Max angle allowed to move at full speed")] 
        private float _maxWalkAngle = 80f;  
        [SerializeField, Range(0f, 200f), Tooltip("Beyond this facing-angle guard needs to turn before walking")] 
        private float _stopTurnAngle = 100f; 
        private float _patrolTimer = 0f; 
        private int _currentIndex = 0;

        
        [Header("Vision")]
        [SerializeField]
        private LayerMask _visionMask; 
        [SerializeField]
        private LayerMask _obstacleMask;
        [SerializeField, Range(0f, 250f)]
        private float _frontViewAngle = 50f;
        [SerializeField, Range(0f, 250f)]
        private float _maxViewAngle = 140f; 
        [SerializeField]
        private float _viewRadius = 5f;
        [SerializeField]
        private float _loseTargetRadius = 7f;
        [SerializeField]
        private float _maxAlertDuration = 1.0f;
        [SerializeField]
        private float _visionPingInterval = 0.2f;

        private float _alertedTimer = 0;
        private float _visionTimer = 0f;
        private Collider[] _hits = new Collider[32];
        private Transform _target = null;
        private bool _isSuspicious = false;
        private Vector3 _suspiciousPosition; 


        private NavMeshAgent _agent; 
        private float _baseSpeed;
        private Collider _collider;
        private float _eyeHeightY;

        public FSM GuardState => _currentState; 



        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _baseSpeed = _agent.speed; 

            _collider = GetComponent<Collider>();
            float centerY = _collider.bounds.center.y;
            float topY = _collider.bounds.max.y;
            _eyeHeightY = ((centerY + topY) * 0.5f) - transform.position.y;
        }

        private void Start()
        {
            if (_waypoints == null || _waypoints.Length == 0)
            {
                Debug.LogWarning($"{name}: No patrol points placed, setting AutoWaypoint");

                var wpObj = new GameObject(name + "_AutoWaypoint");
                wpObj.transform.position = transform.position;
                _waypoints = new[] { wpObj.transform };
            }

            _agent.SetDestination(_waypoints[_currentIndex].position);
        }

        private void FixedUpdate()
        {
            _visionTimer -= Time.fixedDeltaTime;
            if (_visionTimer <= 0f)
            {
                _visionTimer = _visionPingInterval;
                FieldOfView();
            }

            _arrivedAtWaypoint = (!_agent.pathPending && _agent.remainingDistance <= _waypointTolerance);

            switch (_currentState)
            {
                case FSM.STATE_PATROLLING:
                    UpdatePatrol();
                    break;

                case FSM.STATE_ALERTED:
                    UpdateInvestigation();
                    break;

                case FSM.STATE_ENGAGING:
                    UpdateChase(); 
                    break;

                case FSM.STATE_DISENGAGE:
                    UpdateDisengage(); 
                    break;
            }

        }

        private void LateUpdate()
        {
            AdjustSpeedForTurn();
        }


        private void FieldOfView()
        {
            int hitCount = 0;
            
            hitCount = Physics.OverlapSphereNonAlloc(transform.position, _viewRadius, _hits, _visionMask);
            if (hitCount == 0)
                return; 

            Transform bestFront = null;
            Transform bestSide  = null;
            float bestFrontSqrDist = float.PositiveInfinity;
            float bestSideSqrDist  = float.PositiveInfinity; 

            for (int i = 0; i < hitCount; i++)
            {
                Collider col = _hits[i];
                if (col== null)
                    continue;

                Transform hitTransform = col.transform;
                Vector3 local = transform.InverseTransformPoint(hitTransform.position);
                float x = local.x;
                float z = local.z;

                float sqrDist = x * x + z * z;

                float angleDeg = Mathf.Atan2(x, z) * Mathf.Rad2Deg;   // left/right around Y
                float absAngle = Mathf.Abs(angleDeg);

                float halfMax = _maxViewAngle * 0.5f;
                float halfFront = _frontViewAngle * 0.5f;

                // ignore if outside the vision cone or no line of sight
                if (absAngle > halfMax || !HasLineOfSight(hitTransform))
                    continue;

                // if inside vision cone, see if front or on the sides
                bool isFront = absAngle <= halfFront;

                // proritize front targets over side targets, only need to enter Alert state if no immediate front targets
                if (isFront)
                {
                    if (sqrDist < bestFrontSqrDist)
                    {
                        bestFrontSqrDist = sqrDist;
                        bestFront = hitTransform;
                        continue;
                    }
                }
                else
                {
                    if (sqrDist < bestSideSqrDist && bestFront == null)
                    {
                        bestSideSqrDist = sqrDist;
                        bestSide = hitTransform;
                    }
                }

            }

            if (bestFront != null)
            {
                _target = bestFront;
                _isSuspicious = false;

                if (_currentState != FSM.STATE_ENGAGING)
                {
                    _alertedTimer = 0f;
                    _patrolTimer = 0f;
                    _currentState = FSM.STATE_ENGAGING;
                }
                return;
            }

            if (bestSide != null)
            {
                _isSuspicious = true;
                _suspiciousPosition = bestSide.position;
                _alertedTimer = 0f;
                _patrolTimer = 0f;
                if (_currentState != FSM.STATE_ENGAGING && _currentState != FSM.STATE_ALERTED)
                {
                    _currentState = FSM.STATE_ALERTED;
                }
                return;
            }

            if (_currentState == FSM.STATE_ENGAGING && (bestFront == null && bestSide == null))
            {
                _alertedTimer = 0f; 
                _isSuspicious = true;
                _suspiciousPosition = _target.position;
                _patrolTimer = 0f;
                _currentState = FSM.STATE_ALERTED;
                return;
            }
        }


        private bool HasLineOfSight(Transform seenTarget)
        {
            if (seenTarget == null)
                return false;

            Vector3 eye = transform.position + Vector3.up * _eyeHeightY;
            Vector3 seenPos = seenTarget.position + Vector3.up * _eyeHeightY; 

            Vector3 dir = seenPos - eye;
            float dist = dir.magnitude;

            if (dist <= 0.001)
                return true;

            if (Physics.Raycast(eye, dir.normalized, dist, _obstacleMask))
            {
                return false;
            }

            return true;
        }


        private void AdjustSpeedForTurn()
        {
            if (_agent == null || !_agent.hasPath)
            {
                _agent.speed = _baseSpeed;
                return;
            }

            Vector3 toTarget = _agent.steeringTarget - transform.position;
            toTarget.y = 0; 

            if (toTarget.sqrMagnitude < 0.001f)
            {
                _agent.speed = 0f;
                return;
            }

            toTarget.Normalize();
            float dot = Vector3.Dot(transform.forward, toTarget);
            float angle = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f)) * Mathf.Rad2Deg;

            if (angle >= _stopTurnAngle)  
            {
                _agent.speed = _baseSpeed * 0.05f;  // "stop" and turn in place if faced in wrong direction
            }
            else if (angle > _maxWalkAngle)
            {
                _agent.speed = _baseSpeed * 0.4f;   // slow while turning from a half-off orientation
            }
            else
            {
                _agent.speed = _baseSpeed;          // normal walk speed when facing path correctly 
            }
        }


        private void UpdatePatrol()
        {
            if (_waypoints.Length == 0)
                return;

            if (_patrolTimer > 0f)
            {
                _patrolTimer -= Time.fixedDeltaTime;

                if (_patrolTimer <= 0f)     // if the active PatrolArea ends, choose a new waypoint to travel towards 
                {
                    _patrolTimer = 0f;
                    SetNextWaypointDestination();
                }
                else                        // keep patroling for as long as the timer has time left
                {
                    PatrolArea(_waypoints[_currentIndex].position);
                }
                return;
            }

            // keep traveling towards new waypoint until reached then start patroling timer 
            if (_arrivedAtWaypoint)
                _patrolTimer = _maxPatrolDuration;
        }


        private void PatrolArea(Vector3 objective)
        {
            // Maybe something based on this type of code 
            if (!_agent.pathPending && _agent.remainingDistance <= _waypointTolerance)
            {
                Vector3 center = objective;
                Vector2 offset2D = Random.insideUnitCircle * _patrolRadius;
                Vector3 rawTarget = center + new Vector3(offset2D.x, 0f, offset2D.y);

                NavMeshHit hit;
                if (NavMesh.SamplePosition(rawTarget, out hit, _patrolRadius, NavMesh.AllAreas))
                {
                    _agent.SetDestination(hit.position);
                }
            }
        }


        private void SetNextWaypointDestination()
        {
            if (_waypoints.Length == 0)
                return;

            if (_waypoints.Length == 1)
            {
                _agent.SetDestination(_waypoints[0].position);
                return;
            }

            if (_randomPatroling)
            {
                int newIndex = _currentIndex;
                while (newIndex == _currentIndex)
                    newIndex = Random.Range(0, _waypoints.Length);

                _currentIndex = newIndex;
            }
            else
            {
                _currentIndex = (_currentIndex + 1) % _waypoints.Length;
            }

            _agent.SetDestination(_waypoints[_currentIndex].position);
        }


        private void UpdateChase()
        {
            if (_target == null)
            {
                _currentState = FSM.STATE_DISENGAGE;
                return;
            }

            Vector3 toTarget = _target.position - transform.position;
            float sqrDist = toTarget.sqrMagnitude;

            if ((sqrDist > _loseTargetRadius * _loseTargetRadius) || !HasLineOfSight(_target))
            {
                _isSuspicious = true;
                _suspiciousPosition = _target.position;
                _alertedTimer = 0f;
                _currentState = FSM.STATE_ALERTED;
                return;
            }

            _agent.SetDestination(_target.position);
        }


        private void UpdateDisengage()
        {
            _target = null;
            _isSuspicious = false;
            _alertedTimer = 0f;
            _patrolTimer = 0f;  
            _agent.SetDestination(_waypoints[_currentIndex].position);
            _currentState = FSM.STATE_PATROLLING;
        }


        private void UpdateInvestigation()
        {
            if (!_isSuspicious)
            {
                _currentState = FSM.STATE_DISENGAGE;
                return;
            }

            if (_alertedTimer <= 0f)
            {
                if (_arrivedAtWaypoint)
                {
                    _alertedTimer = _maxAlertDuration;
                }
                else
                {
                    _agent.SetDestination(_suspiciousPosition);
                }
                return;
            }

            _alertedTimer -= Time.fixedDeltaTime;
            if (_alertedTimer <= 0f)
            {
                _currentState = FSM.STATE_DISENGAGE;
            }
            else
            {
                PatrolArea(_suspiciousPosition);
            }
        }



        
#if UNITY_EDITOR


        private void OnValidate()
        {
            _maxWalkAngle = Mathf.Clamp(_maxWalkAngle, 0f, 200f);
            _stopTurnAngle = Mathf.Clamp(_stopTurnAngle, 0f, 200f);

            if (_stopTurnAngle < _maxWalkAngle)
                _stopTurnAngle = _maxWalkAngle;

            _maxViewAngle = Mathf.Clamp(_maxViewAngle, 0f, 250f);
            _frontViewAngle = Mathf.Clamp(_frontViewAngle, 0f, _maxViewAngle);
        }


        // NOTE:    OnDrawGizmos was made with the help of AI to just allow me to visualize the effects 
        //          and se if the math acted as I wanted it to do. 

        private void OnDrawGizmos()
        {
            if (_collider == null)
                _collider = GetComponent<Collider>();

            if (Application.isPlaying && _agent != null && _agent.hasPath)
            {
                var path = _agent.path;
                var corners = path.corners;
                if (corners != null && corners.Length > 1)
                {
                    Gizmos.color = Color.cyan;

                    for (int i = 0; i < corners.Length - 1; i++)
                    {
                        Gizmos.DrawLine(corners[i], corners[i + 1]);
                        Gizmos.DrawSphere(corners[i], 0.1f);
                    }

                    Gizmos.DrawSphere(corners[^1], 0.1f);
                }
            }

            // Use guard position as center
            Vector3 center = transform.position;

            // Lose range (yellow)
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(center, _loseTargetRadius);



            Color _forwardGizmoColor = Color.red;
            float _forwardGizmoLength = 3.0f;

            if (_collider == null)
                return;

            // Start of the arrow: collider center
            Vector3 origin  = _collider.bounds.center;
            Vector3 dir     = transform.forward;

            // FOV visualization
            float halfMax   = _maxViewAngle * 0.5f;
            float halfFront = _frontViewAngle * 0.5f;

            // FRONT boundaries (green)
            Quaternion leftFrontRot     = Quaternion.AngleAxis(-halfFront, Vector3.up);
            Quaternion rightFrontRot    = Quaternion.AngleAxis(halfFront, Vector3.up);
            Vector3 leftFrontDir        = leftFrontRot * dir;
            Vector3 rightFrontDir       = rightFrontRot * dir;

            Gizmos.color = Color.green;
            Gizmos.DrawLine(origin, origin + leftFrontDir * _viewRadius);
            Gizmos.DrawLine(origin, origin + rightFrontDir * _viewRadius);

            // SIDE boundaries (outer edges, yellow)
            Quaternion leftMaxRot   = Quaternion.AngleAxis(-halfMax, Vector3.up);
            Quaternion rightMaxRot  = Quaternion.AngleAxis(halfMax, Vector3.up);
            Vector3 leftMaxDir      = leftMaxRot * dir;
            Vector3 rightMaxDir     = rightMaxRot * dir;


            Handles.color = Color.red;

            // start direction = left edge of vision cone
            Vector3 arcStartDir = leftMaxDir.normalized;

            // Draw an arc from left edge to right edge over _maxViewAngle degrees
            Handles.DrawWireArc(
                origin,             // center
                Vector3.up,         // rotation axis
                arcStartDir,        // starting direction
                _maxViewAngle,      // sweep angle (degrees)
                _viewRadius         // radius of the arc
            );

            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(origin, origin + leftMaxDir * _viewRadius);
            Gizmos.DrawLine(origin, origin + rightMaxDir * _viewRadius);
            Gizmos.color = _forwardGizmoColor;

            // Main arrow line
            Vector3 end = origin + dir * _forwardGizmoLength;
            Gizmos.DrawLine(origin, end);

            // Simple arrow head
            Vector3 right = Quaternion.AngleAxis(20f, Vector3.up) * -dir;
            Vector3 left = Quaternion.AngleAxis(-20f, Vector3.up) * -dir;

            float headSize = _forwardGizmoLength * 0.25f;
            Gizmos.DrawLine(end, end + right * headSize);
            Gizmos.DrawLine(end, end + left * headSize);
        }


        private void OnGUI()
        {
            // simple runtime debug label over the guard
            Camera cam = Camera.main;
            if (cam == null)
                return;

            // position above the guard's head
            Vector3 worldPos = transform.position + Vector3.up * 2f;
            Vector3 screenPos = cam.WorldToScreenPoint(worldPos);

            // if behind camera, skip
            if (screenPos.z < 0f)
                return;

            // GUI coordinates have (0,0) in top-left, but WorldToScreenPoint has (0,0) bottom-left
            screenPos.y = Screen.height - screenPos.y;

            string text = _currentState switch  // e.g. "STATE_PATROLLING"
            {
                FSM.STATE_PATROLLING => "Patrol",
                FSM.STATE_ALERTED => "Alerted",
                FSM.STATE_ENGAGING => "Chase",
                FSM.STATE_DISENGAGE => "Return",
                _ => _currentState.ToString()
            };


            // small centered rect
            Vector2 size = new Vector2(120f, 20f);
            Rect rect = new Rect(
                screenPos.x - size.x * 0.5f,
                screenPos.y - size.y * 0.5f,
                size.x,
                size.y
            );

            GUIStyle style = GUI.skin.label;
            style.alignment = TextAnchor.MiddleCenter;
            style.normal.textColor = Color.white;

            GUI.Label(rect, text, style);
        }


#endif


    }

}
