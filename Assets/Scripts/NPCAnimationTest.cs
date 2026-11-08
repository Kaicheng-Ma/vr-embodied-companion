using UnityEngine;

public class NPCAnimationTest : MonoBehaviour
{
    [Header("Animation")]
    public Animator animator;
    public string movingBoolName = "isMoving";

    [Header("Movement")]
    public bool movementEnabled = true;
    public float moveSpeed = 1.5f;
    public float turnSpeed = 120f;

    [Header("Input")]
    public KeyCode moveForwardKey = KeyCode.W;
    public KeyCode moveBackwardKey = KeyCode.S;
    public KeyCode turnLeftKey = KeyCode.A;
    public KeyCode turnRightKey = KeyCode.D;

    [Header("Safety")]
    public bool lockYPosition = false;
    public float lockedY = 0f;

    private void Start()
    {
        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>();
        }

        if (animator == null)
        {
            Debug.LogError("NPCAnimationTest: Animator is not assigned and could not be found in children.");
        }

        if (lockYPosition)
        {
            lockedY = transform.position.y;
        }
    }

    private void Update()
    {
        if (!movementEnabled)
        {
            SetMoving(false);
            return;
        }

        bool movingForward = Input.GetKey(moveForwardKey);
        bool movingBackward = Input.GetKey(moveBackwardKey);
        bool turningLeft = Input.GetKey(turnLeftKey);
        bool turningRight = Input.GetKey(turnRightKey);

        bool isMoving =
            (movingForward && !movingBackward) ||
            (movingBackward && !movingForward);

        SetMoving(isMoving);

        if (turningLeft && !turningRight)
        {
            transform.Rotate(0f, -turnSpeed * Time.deltaTime, 0f);
        }
        else if (turningRight && !turningLeft)
        {
            transform.Rotate(0f, turnSpeed * Time.deltaTime, 0f);
        }

        if (movingForward && !movingBackward)
        {
            transform.position += transform.forward * moveSpeed * Time.deltaTime;
        }
        else if (movingBackward && !movingForward)
        {
            transform.position -= transform.forward * moveSpeed * Time.deltaTime;
        }

        if (lockYPosition)
        {
            Vector3 pos = transform.position;
            pos.y = lockedY;
            transform.position = pos;
        }
    }

    private void SetMoving(bool isMoving)
    {
        if (animator == null)
            return;

        animator.SetBool(movingBoolName, isMoving);
    }
}