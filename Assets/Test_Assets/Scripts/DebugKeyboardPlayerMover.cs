using UnityEngine;
using UnityEngine.InputSystem;

public class DebugKeyboardPlayerMover : MonoBehaviour
{
    [Header("Enable / Disable")]
    public bool debugMovementEnabled = true;

    [Header("Player Hierarchy")]
    [Tooltip("Usually XR Origin (VR). If empty, this object's Transform is used.")]
    public Transform playerRoot;

    [Tooltip("Usually Main Camera.")]
    public Transform cameraTransform;

    [Tooltip(
        "Transform used for keyboard pitch. " +
        "Usually Camera Offset, NOT Main Camera."
    )]
    public Transform pitchPivot;

    [Header("Movement")]
    public float moveSpeed = 2.0f;

    [Tooltip(
        "WASD follows the horizontal direction controlled by keyboard yaw."
    )]
    public bool useKeyboardFacingForMovement = true;

    [Header("View Turning")]
    public bool allowKeyboardTurning = true;

    [Tooltip("Left / Right Arrow yaw speed in degrees per second.")]
    public float turnSpeed = 90.0f;

    [Header("View Pitch")]
    public bool allowKeyboardPitch = true;

    [Tooltip("Up / Down Arrow pitch speed in degrees per second.")]
    public float pitchSpeed = 70.0f;

    [Tooltip("Maximum downward viewing angle.")]
    public float minPitch = -75.0f;

    [Tooltip("Maximum upward viewing angle.")]
    public float maxPitch = 75.0f;

    [Header("Height Control")]
    public bool allowVerticalMovement = false;
    public float verticalSpeed = 1.0f;

    [Header("Safety")]
    public bool onlyMoveInPlayMode = true;

    private CharacterController characterController;

    // Independent horizontal facing used by keyboard movement.
    // It is intentionally separated from XR camera tracking.
    private Vector3 keyboardForward = Vector3.forward;

    // Pitch is stored separately so it does not affect WASD direction.
    private float keyboardPitch = 0f;
    private Quaternion pitchBaseLocalRotation = Quaternion.identity;

    private void Awake()
    {
        if (playerRoot == null)
        {
            playerRoot = transform;
        }

        characterController =
            playerRoot.GetComponent<CharacterController>();

        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }

        // In the current XR hierarchy this should normally become:
        //
        // XR Origin
        // └── Camera Offset   <- pitchPivot
        //     └── Main Camera
        //
        if (pitchPivot == null &&
            cameraTransform != null &&
            cameraTransform.parent != null)
        {
            pitchPivot = cameraTransform.parent;
        }

        if (pitchPivot != null)
        {
            pitchBaseLocalRotation =
                pitchPivot.localRotation;
        }

        InitializeKeyboardFacing();
    }

    private void InitializeKeyboardFacing()
    {
        Vector3 initialForward;

        if (cameraTransform != null)
        {
            initialForward = cameraTransform.forward;
        }
        else
        {
            initialForward = playerRoot.forward;
        }

        initialForward.y = 0f;

        if (initialForward.sqrMagnitude <= 0.0001f)
        {
            initialForward = playerRoot.forward;
            initialForward.y = 0f;
        }

        keyboardForward = initialForward.normalized;
    }

    private void Update()
    {
        if (!debugMovementEnabled)
            return;

        if (onlyMoveInPlayMode && !Application.isPlaying)
            return;

        if (Keyboard.current == null)
            return;

        HandleKeyboardYaw();
        HandleKeyboardPitch();
        HandleKeyboardMovement();
    }

    private void HandleKeyboardYaw()
    {
        if (!allowKeyboardTurning)
            return;

        float turnInput = 0f;

        if (Keyboard.current.leftArrowKey.isPressed)
            turnInput -= 1f;

        if (Keyboard.current.rightArrowKey.isPressed)
            turnInput += 1f;

        if (Mathf.Abs(turnInput) <= 0.0001f)
            return;

        float turnAmount =
            turnInput * turnSpeed * Time.deltaTime;

        // Rotate the whole XR Origin around the current camera position.
        Vector3 pivot =
            cameraTransform != null
                ? cameraTransform.position
                : playerRoot.position;

        playerRoot.RotateAround(
            pivot,
            Vector3.up,
            turnAmount
        );

        // Keep keyboard movement facing synchronized with yaw.
        keyboardForward =
            Quaternion.AngleAxis(
                turnAmount,
                Vector3.up
            ) * keyboardForward;

        keyboardForward.y = 0f;

        if (keyboardForward.sqrMagnitude > 0.0001f)
        {
            keyboardForward.Normalize();
        }
    }

    private void HandleKeyboardPitch()
    {
        if (!allowKeyboardPitch)
            return;

        if (pitchPivot == null)
            return;

        float pitchInput = 0f;

        // Unity positive X rotation looks downward,
        // therefore Up Arrow subtracts pitch.
        if (Keyboard.current.upArrowKey.isPressed)
            pitchInput -= 1f;

        if (Keyboard.current.downArrowKey.isPressed)
            pitchInput += 1f;

        if (Mathf.Abs(pitchInput) <= 0.0001f)
            return;

        keyboardPitch +=
            pitchInput *
            pitchSpeed *
            Time.deltaTime;

        keyboardPitch =
            Mathf.Clamp(
                keyboardPitch,
                minPitch,
                maxPitch
            );

        pitchPivot.localRotation =
            pitchBaseLocalRotation *
            Quaternion.Euler(
                keyboardPitch,
                0f,
                0f
            );
    }

    private void HandleKeyboardMovement()
    {
        Vector3 moveInput = Vector3.zero;

        if (Keyboard.current.wKey.isPressed)
            moveInput.z += 1f;

        if (Keyboard.current.sKey.isPressed)
            moveInput.z -= 1f;

        if (Keyboard.current.aKey.isPressed)
            moveInput.x -= 1f;

        if (Keyboard.current.dKey.isPressed)
            moveInput.x += 1f;

        Vector3 moveDirection =
            GetMoveDirection(moveInput);

        if (allowVerticalMovement)
        {
            if (Keyboard.current.eKey.isPressed)
                moveDirection.y += verticalSpeed;

            if (Keyboard.current.qKey.isPressed)
                moveDirection.y -= verticalSpeed;
        }

        if (moveDirection.sqrMagnitude <= 0.0001f)
            return;

        Vector3 displacement =
            moveDirection *
            moveSpeed *
            Time.deltaTime;

        MovePlayer(displacement);
    }

    private Vector3 GetMoveDirection(Vector3 input)
    {
        if (input.sqrMagnitude > 1f)
        {
            input.Normalize();
        }

        if (!useKeyboardFacingForMovement)
        {
            return new Vector3(
                input.x,
                0f,
                input.z
            );
        }

        Vector3 forward = keyboardForward;

        forward.y = 0f;

        if (forward.sqrMagnitude <= 0.0001f)
        {
            forward = playerRoot.forward;
            forward.y = 0f;
        }

        forward.Normalize();

        Vector3 right =
            Vector3.Cross(
                Vector3.up,
                forward
            ).normalized;

        Vector3 direction =
            right * input.x +
            forward * input.z;

        if (direction.sqrMagnitude > 1f)
        {
            direction.Normalize();
        }

        return direction;
    }

    private void MovePlayer(Vector3 displacement)
    {
        if (characterController != null &&
            characterController.enabled)
        {
            characterController.Move(displacement);
        }
        else
        {
            playerRoot.position += displacement;
        }
    }
}