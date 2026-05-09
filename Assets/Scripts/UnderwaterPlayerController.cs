using UnityEngine;
using UnityEngine.InputSystem;

namespace Underwater
{
    public sealed class UnderwaterPlayerController : MonoBehaviour
    {
        private const float MaxBoost = 100f;

        private UnderwaterGameDirector director;
        private CharacterController characterController;
        private Transform viewPivot;

        private Vector3 velocity;
        private float yaw;
        private float pitch;
        private float boostEnergy = MaxBoost;
        private float swimCycle;

        public float BoostNormalized => boostEnergy / MaxBoost;

        public bool HasPointerLock => Cursor.lockState == CursorLockMode.Locked;

        public void Initialize(UnderwaterGameDirector owningDirector, CharacterController controller, Transform pivot, Camera camera)
        {
            director = owningDirector;
            characterController = controller;
            viewPivot = pivot;
            yaw = transform.eulerAngles.y;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            UpdateCursorState();
            HandleLook();
            HandleMovement();
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
            float verticalInput = 0f;
            bool boostHeld = false;

            if (Keyboard.current != null)
            {
                Keyboard keyboard = Keyboard.current;
                moveInput.x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
                moveInput.y = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
                verticalInput = (keyboard.spaceKey.isPressed ? 1f : 0f) -
                    ((keyboard.leftCtrlKey.isPressed || keyboard.cKey.isPressed) ? 1f : 0f);
                boostHeld = keyboard.leftShiftKey.isPressed;
            }

            if (Gamepad.current != null)
            {
                moveInput += Gamepad.current.leftStick.ReadValue();
                verticalInput += (Gamepad.current.rightShoulder.isPressed ? 1f : 0f) - (Gamepad.current.leftShoulder.isPressed ? 1f : 0f);
                boostHeld |= Gamepad.current.buttonSouth.isPressed;
            }

            Vector3 desiredDirection =
                (viewPivot.forward * moveInput.y) +
                (transform.right * moveInput.x) +
                (Vector3.up * verticalInput);

            if (desiredDirection.sqrMagnitude > 1f)
            {
                desiredDirection.Normalize();
            }

            bool boosting = boostHeld && boostEnergy > 0.1f && desiredDirection.sqrMagnitude > 0.05f;
            float targetSpeed = boosting ? 11.5f : 6.2f;
            float acceleration = boosting ? 18f : 12f;

            Vector3 targetVelocity = desiredDirection * targetSpeed;
            velocity = Vector3.MoveTowards(velocity, targetVelocity, acceleration * Time.deltaTime);

            if (desiredDirection.sqrMagnitude < 0.05f)
            {
                velocity = Vector3.MoveTowards(velocity, Vector3.zero, 5f * Time.deltaTime);
            }

            if (boosting)
            {
                boostEnergy = Mathf.Max(0f, boostEnergy - 24f * Time.deltaTime);
            }
            else
            {
                boostEnergy = Mathf.Min(MaxBoost, boostEnergy + 18f * Time.deltaTime);
            }

            swimCycle += velocity.magnitude * Time.deltaTime * 0.45f;
            float bob = Mathf.Sin(swimCycle * 5.5f) * Mathf.Clamp01(velocity.magnitude / 9f) * 0.08f;
            Vector3 localPivotPosition = new Vector3(0f, 0.62f + bob, 0f);
            viewPivot.localPosition = Vector3.Lerp(viewPivot.localPosition, localPivotPosition, Time.deltaTime * 9f);

            characterController.Move(velocity * Time.deltaTime);
            transform.position = director.ClampPoint(transform.position, 0.8f);
        }
    }
}
