using UnityEngine;
using UnityEngine.InputSystem; 

namespace AI_Workshop01
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public class PlayerMovement : MonoBehaviour
    {

        [SerializeField]
        private float _playerMoveSpeed = 1f;

        private Vector2 _inputDirection;

        [Header("Jumping")]
        [SerializeField]
        private float _jumpForce = 3f;
        [SerializeField]
        private float _coyoteBuffer = 0.1f;
        [SerializeField]
        private float _jumpBuffer = 0.1f;

        private float _coyoteTimer;
        private float _jumpTimer;

        [Header("Ground Check")]
        //[SerializeField] private LayerMask _groundMask;
        [SerializeField] 
        private float _groundCheckDistance = 1.2f;
        [SerializeField] 
        private float _maxSlopeAngle = 45f;

        /*   Sliding: fix later maybe, spent to much time on this part
        [Header("Slope Handling")]
        [SerializeField] 
        private float _slideAcceleration = 10f;
        [SerializeField] 
        private float _maxGroundedUpwardVelocity = 2f;

        private int _steepSlopeCount = 0;
        private bool _onSteepSlope;
        private Vector3 _steepNormal;
        */

        private Rigidbody _rb;
        private Collider _col;
        private float _cosMaxSlope;
        private float _halfWidth;
        private float _halfDepth;
        private float _halfHeight;



        private void Awake()
        {
            _rb         = GetComponent<Rigidbody>();
            _col        = GetComponent<Collider>();
            
            _cosMaxSlope = Mathf.Cos(_maxSlopeAngle * Mathf.Deg2Rad);

            if (_rb == null)
                Debug.LogWarning("Rigidbody missing");

            if (_col == null)
            {
                Debug.LogWarning("Collider missing");
                return; 
            }
            
            // get the size of players collider componet to not recalculate every time  (only if SURE collider wont change size/shape!)
            var ext     = _col.bounds.extents;
            _halfWidth  = ext.x;
            _halfDepth  = ext.z;
            _halfHeight = ext.y;
        }


        private void FixedUpdate()
        {
            //bool touchingAnyGround = groundedNow || _onSteepSlope;        //  Sliding: fix later maybe, spent to much time on this part
            
            bool groundedNow       = IsGrounded();      // check if on ground and if that ground is angled 
            bool wantsJump         = _jumpTimer > 0f;   // remove line for de-clutter if var is not used again 
            bool canUseCoyote      = !groundedNow && _coyoteTimer > 0f; 
            bool doJump            = wantsJump && (groundedNow || canUseCoyote);

            if (groundedNow)
            {
                _coyoteTimer = _coyoteBuffer; 
            }
            else if (_coyoteTimer > 0f)                 // allow a short grace period to still jump after leaving the ground
            {
                _coyoteTimer -= Time.fixedDeltaTime;    // coyote-buffer, minus time passed since last grounded. If the value is still above 0 player can jump  
                if (_coyoteTimer < 0f) _coyoteTimer = 0f;
            }

            if (wantsJump)                              // allows jump to be pressed in a small timeframe just before landing
            {
                _jumpTimer -= Time.fixedDeltaTime;      // jump-buffer, minus time passed since pressed. If the value is still above 0 when landing player jumps
                if (_jumpTimer < 0f) _jumpTimer = 0f;
            }

            Vector2 move = _inputDirection; 
            if (move.sqrMagnitude > 1f)                 // should prevent diagonals from being faster than cardinal direction-movement 
                move = move.normalized;

            Vector3 horizontalVelocity = new Vector3(move.x, 0f, move.y) * _playerMoveSpeed;

            /*   Sliding: fix later maybe, spent to much time on this part
            float steepSeverity = 0f;
            bool doSliding      = false;
            Vector3 slideDir    = Vector3.zero;

            if (!groundedNow && _onSteepSlope && _steepNormal != Vector3.zero)      // slide down on steep slopes acording to the global down direction
            {
                steepSeverity = Mathf.InverseLerp(2f, 5f, _steepSlopeCount);        // more parts of player standing on steep ground means more uphill block
                doSliding = true;

                slideDir = Vector3.ProjectOnPlane(Vector3.down, _steepNormal).normalized;
                Vector3 upSlopeDir = -slideDir; 

                float uphillAmount = Vector3.Dot(horizontalVelocity, upSlopeDir);
                if (uphillAmount > 0f)
                {
                    if(_steepSlopeCount >= 5 || steepSeverity >= 0.9f)
                    {
                        horizontalVelocity -= upSlopeDir * uphillAmount;                    // hard block movement uppwards depending on angle / groundchecks 
                    }
                    else
                    {
                        horizontalVelocity -= upSlopeDir * (uphillAmount * steepSeverity);  // slow down uppwards movement depending on angle / groundchecks 
                    }
                }
            }
            */

            Vector3 velocity = _rb.linearVelocity;
            velocity.x       = horizontalVelocity.x;
            velocity.z       = horizontalVelocity.z;

            if (doJump)                                 // can only jump if grounded and has pressed jump, + buffers giving extra leeway 
            {
                velocity.y = _jumpForce;
                Debug.Log("Jump performed!");

                _jumpTimer   = 0f;
                _coyoteTimer = 0f;
            }

            /*   Sliding: fix later maybe, spent to much time on this part
            if (doSliding)
            {
                float slideStrength = Mathf.Lerp(0f, _slideAcceleration, steepSeverity); 
                velocity += slideDir * slideStrength * Time.fixedDeltaTime;
            }
            */

            _rb.linearVelocity = velocity;
        }


        void Update()
        {
        
        }


        /// <summary>
        /// Uses 5 downward raycasts (center + 4 sides) based on the collider bounds
        /// to determine if the player is standing on walkable ground.
        /// 
        /// Returns <c>true</c> if any ray hits a surface whose slope is <= _maxSlopeAngle.
        /// In that case the player is considered grounded.
        /// 
        /// For rays that hit too-steep surfaces, the method:
        /// - Increments <see cref="_steepSlopeCount"/>.
        /// - Stores the last hit normal in <see cref="_steepNormal"/>.
        /// - Sets <see cref="_onSteepSlope"/> to true if at least one ray hit a steep surface.
        /// 
        /// There is currently no layer mask / ground filter; all colliders are treated as potential ground.
        /// </summary>
        private bool IsGrounded()
        {
            if (_col == null) return false;

            /*   Sliding: fix later maybe, spent to much time on this part
            _onSteepSlope = false;
            _steepSlopeCount = 0;
            _steepNormal = Vector3.zero;
            */

            Bounds bounds   = _col.bounds;
            float rayLength = _halfHeight + _groundCheckDistance;
            Vector3 center  = bounds.center;

            Vector3 right   = new Vector3(_halfWidth, 0f, 0f);
            Vector3 forward = new Vector3(0f, 0f, _halfDepth);

            bool CheckOrigin(Vector3 origin)
            {
                if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, rayLength))
                {
                    // check if ground that was hit is at an angle 
                    float upDot = Vector3.Dot(hit.normal, Vector3.up);
                    bool okSlope = upDot >= _cosMaxSlope;

                    // green for ok hit, yellow for a hit but to steep slope, red for not grounded 
                    if (okSlope)
                    {
                        Debug.DrawRay(origin, Vector3.down * hit.distance, Color.green); 

                        return true;
                    }
                    else
                    {
                        /*   Sliding: fix later maybe, spent to much time on this part
                        _steepSlopeCount ++; 
                        _steepNormal = hit.normal;
                        */

                        Debug.DrawRay(origin, Vector3.down * hit.distance, Color.yellow);
                        return false;
                    }
                }
                else
                {
                    Debug.DrawRay(origin, Vector3.down * rayLength, Color.red);
                    return false;
                }
            }

            // check center + 4 cardinal points for grounded + angled ground
            if (CheckOrigin(center)) return true;
            if (CheckOrigin(center + right)) return true;
            if (CheckOrigin(center - right)) return true;
            if (CheckOrigin(center + forward)) return true;
            if (CheckOrigin(center - forward)) return true;

            /*   Sliding: fix later maybe, spent to much time on this part
            _onSteepSlope = _steepSlopeCount > 0;
            */

            return false;
        }


        public void OnMove(InputAction.CallbackContext callbackContext)
        {
            _inputDirection = callbackContext.ReadValue<Vector2>();
        }

        public void OnJump(InputAction.CallbackContext callbackContext)
        {
            if (callbackContext.performed)
            {
                Debug.Log("Jump pressed!");
                _jumpTimer = _jumpBuffer; 
            }
        }
        
    }

}
