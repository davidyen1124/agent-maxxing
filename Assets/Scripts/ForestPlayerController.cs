using UnityEngine;
using UnityEngine.InputSystem;

namespace Forest
{
    public sealed class ForestPlayerController : MonoBehaviour
    {
        private const float MaxSprintEnergy = 100f;
        private const float GroundOffset = 0.08f;
        private const float GroundedTolerance = 0.14f;
        private const float WalkSpeed = 4.8f;
        private const float SprintSpeed = 8.2f;
        private const float WalkAcceleration = 18f;
        private const float SprintAcceleration = 24f;
        private const float GroundFriction = 10f;
        private const float Gravity = -24f;
        private const float JumpHeight = 1.65f;

        private ForestGameDirector director;
        private CharacterController characterController;
        private Transform viewPivot;

        private Vector3 velocity;
        private float verticalVelocity;
        private float yaw;
        private float pitch;
        private float sprintEnergy = MaxSprintEnergy;
        private float stepCycle;

        public float SprintEnergyNormalized => sprintEnergy / MaxSprintEnergy;

        public bool HasPointerLock => Cursor.lockState == CursorLockMode.Locked;

        public void Initialize(ForestGameDirector owningDirector, CharacterController controller, Transform pivot)
        {
            director = owningDirector;
            characterController = controller;
            viewPivot = pivot;
            yaw = transform.eulerAngles.y;
            pitch = NormalizeAngle(viewPivot.localEulerAngles.x);

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private static float NormalizeAngle(float angle)
        {
            angle = Mathf.Repeat(angle + 180f, 360f) - 180f;
            return Mathf.Clamp(angle, -82f, 82f);
        }

        private void Update()
        {
            UpdateCursorState();
            HandleLook();
            HandleMovement();
            HandleInteraction();
        }

        private void UpdateCursorState()
        {
            Mouse mouse = Mouse.current;
            Keyboard keyboard = Keyboard.current;

            if (mouse != null && mouse.leftButton.wasPressedThisFrame && !HasPointerLock)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void HandleLook()
        {
            if (!HasPointerLock)
            {
                return;
            }

            Vector2 lookInput = Vector2.zero;

            if (Mouse.current != null)
            {
                lookInput += Mouse.current.delta.ReadValue();
            }

            if (Gamepad.current != null)
            {
                lookInput += Gamepad.current.rightStick.ReadValue() * 180f * Time.deltaTime;
            }

            yaw += lookInput.x * 0.08f;
            pitch = Mathf.Clamp(pitch - lookInput.y * 0.08f, -82f, 82f);

            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            viewPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        private void HandleMovement()
        {
            Vector2 moveInput = Vector2.zero;
            bool sprintHeld = false;
            bool jumpPressed = false;

            if (Keyboard.current != null)
            {
                Keyboard keyboard = Keyboard.current;
                moveInput.x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
                moveInput.y = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
                sprintHeld = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                jumpPressed = keyboard.spaceKey.wasPressedThisFrame;
            }

            if (Gamepad.current != null)
            {
                moveInput += Gamepad.current.leftStick.ReadValue();
                sprintHeld |= Gamepad.current.leftStickButton.isPressed;
                jumpPressed |= Gamepad.current.buttonSouth.wasPressedThisFrame;
            }

            moveInput = Vector2.ClampMagnitude(moveInput, 1f);

            Vector3 desiredDirection =
                (transform.forward * moveInput.y) +
                (transform.right * moveInput.x);

            if (desiredDirection.sqrMagnitude > 1f)
            {
                desiredDirection.Normalize();
            }

            bool sprinting = sprintHeld && sprintEnergy > 0.1f && desiredDirection.sqrMagnitude > 0.05f;
            float targetSpeed = sprinting ? SprintSpeed : WalkSpeed;
            float acceleration = sprinting ? SprintAcceleration : WalkAcceleration;

            Vector3 targetVelocity = desiredDirection * targetSpeed;
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, targetVelocity, acceleration * Time.deltaTime);

            if (desiredDirection.sqrMagnitude < 0.05f)
            {
                horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, Vector3.zero, GroundFriction * Time.deltaTime);
            }

            float groundY = GetGroundY();
            bool grounded = IsGrounded(groundY);

            if (grounded && verticalVelocity < 0f)
            {
                verticalVelocity = -2f;
            }

            if (grounded && jumpPressed)
            {
                verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);
                grounded = false;
            }

            verticalVelocity += Gravity * Time.deltaTime;
            velocity = new Vector3(horizontalVelocity.x, verticalVelocity, horizontalVelocity.z);

            if (sprinting)
            {
                sprintEnergy = Mathf.Max(0f, sprintEnergy - 24f * Time.deltaTime);
            }
            else
            {
                sprintEnergy = Mathf.Min(MaxSprintEnergy, sprintEnergy + 18f * Time.deltaTime);
            }

            stepCycle += horizontalVelocity.magnitude * Time.deltaTime * 0.45f;
            float bob = Mathf.Sin(stepCycle * 7.5f) * Mathf.Clamp01(horizontalVelocity.magnitude / SprintSpeed) * 0.045f;
            Vector3 localPivotPosition = new Vector3(0f, 0.62f + (grounded ? bob : 0f), 0f);
            viewPivot.localPosition = Vector3.Lerp(viewPivot.localPosition, localPivotPosition, Time.deltaTime * 9f);

            characterController.Move(velocity * Time.deltaTime);
            KeepInsideWalkableBounds();
        }

        private float GetGroundY()
        {
            return director.GetSurfaceY(transform.position) + GroundOffset;
        }

        private bool IsGrounded(float groundY)
        {
            return characterController.isGrounded ||
                (verticalVelocity <= 0f && transform.position.y <= groundY + GroundedTolerance);
        }

        private void KeepInsideWalkableBounds()
        {
            Vector3 position = director.ClampPoint(transform.position, 0.8f);
            float groundY = director.GetSurfaceY(position) + GroundOffset;

            if (position.y < groundY)
            {
                position.y = groundY;
                verticalVelocity = Mathf.Max(verticalVelocity, 0f);
            }

            transform.position = position;
        }

        private void HandleInteraction()
        {
            bool voicePressed = false;
            bool voiceReleased = false;
            bool toggleHudPressed = false;
            bool openWebsitePressed = false;

            if (Keyboard.current != null)
            {
                voicePressed = Keyboard.current.vKey.wasPressedThisFrame;
                voiceReleased = Keyboard.current.vKey.wasReleasedThisFrame;
                toggleHudPressed = Keyboard.current.hKey.wasPressedThisFrame;
                openWebsitePressed = Keyboard.current.oKey.wasPressedThisFrame;
            }

            if (toggleHudPressed)
            {
                director.ToggleThreadHudVisibility();
            }

            if (voicePressed)
            {
                director.BeginRealtimeVoiceQuestionFromPlayer();
            }

            if (voiceReleased)
            {
                director.EndRealtimeVoiceQuestionFromPlayer();
            }

            if (openWebsitePressed)
            {
                director.OpenLatestWebsiteFromPlayer();
            }
        }
    }
}
