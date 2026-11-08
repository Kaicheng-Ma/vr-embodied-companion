using UnityEngine;
using UnityEngine.AI;

// Formal Stage 5B-1 bridge from runtime locomotion state to the visual Animator.
// This component never owns movement; it only reflects the existing movement pipeline.
public class CP_CompanionAnimatorDriver : MonoBehaviour
{
    [Header("References")]
    public Animator animator;
    public NavMeshAgent navMeshAgent;
    public CP_MovementController movementController;

    [Header("Settings")]
    public string movingBoolName = "isMoving";
    public float movingVelocityThreshold = 0.05f;
    public float movementRemainingDistancePadding = 0.05f;
    public bool resetMovingOnDisable = true;
    public bool logMissingParameterWarning = true;

    [Header("Runtime Debug")]
    public bool animatorDriverBound;
    public bool controllerRequestsMovement;
    public bool agentHasMovementIntent;
    public bool latestAppliedMoving;

    private int movingBoolHash;
    private bool parameterValidated;
    private bool parameterExists;
    private bool missingParameterWarningLogged;

    private void Awake()
    {
        CacheParameterHash();
        ValidateAnimatorParameter();
    }

    private void OnValidate()
    {
        CacheParameterHash();
        parameterValidated = false;
        parameterExists = false;
        missingParameterWarningLogged = false;
    }

    private void Update()
    {
        if (!parameterValidated)
        {
            ValidateAnimatorParameter();
        }

        animatorDriverBound = animator != null && navMeshAgent != null && movementController != null;

        controllerRequestsMovement =
            movementController != null &&
            movementController.Status == CP_PrimitiveExecutionStatus.Running &&
            movementController.activePrimitiveType == "move_within_scope";

        float safeVelocityThreshold = Mathf.Max(0f, movingVelocityThreshold);
        float thresholdSquared = safeVelocityThreshold * safeVelocityThreshold;
        bool agentReady = navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh;
        bool velocityAboveThreshold = agentReady && navMeshAgent.velocity.sqrMagnitude > thresholdSquared;
        bool desiredVelocityAboveThreshold = agentReady && navMeshAgent.desiredVelocity.sqrMagnitude > thresholdSquared;
        bool finiteRemainingDistance = agentReady &&
                                       !float.IsNaN(navMeshAgent.remainingDistance) &&
                                       !float.IsInfinity(navMeshAgent.remainingDistance);
        float remainingTravelThreshold = agentReady
            ? Mathf.Max(0f, navMeshAgent.stoppingDistance) + Mathf.Max(0f, movementRemainingDistancePadding)
            : 0f;
        bool pathStillRequiresTravel =
            agentReady &&
            navMeshAgent.hasPath &&
            finiteRemainingDistance &&
            navMeshAgent.remainingDistance > remainingTravelThreshold;

        // hasPath can linger after arrival, so visual locomotion follows actual travel intent.
        agentHasMovementIntent =
            agentReady &&
            (navMeshAgent.pathPending ||
             velocityAboveThreshold ||
             desiredVelocityAboveThreshold ||
             pathStillRequiresTravel);

        bool shouldPlayMove = controllerRequestsMovement && agentHasMovementIntent;
        ApplyMovingStateOnlyWhenChanged(shouldPlayMove);
    }

    private void OnDisable()
    {
        if (resetMovingOnDisable)
        {
            ApplyMovingState(forceValue: false, allowMissingAnimator: true);
        }
    }

    private void CacheParameterHash()
    {
        movingBoolHash = Animator.StringToHash(string.IsNullOrWhiteSpace(movingBoolName) ? "isMoving" : movingBoolName);
    }

    private void ValidateAnimatorParameter()
    {
        parameterValidated = true;
        parameterExists = false;

        if (animator == null)
        {
            return;
        }

        string parameterName = string.IsNullOrWhiteSpace(movingBoolName) ? "isMoving" : movingBoolName;
        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            AnimatorControllerParameter parameter = parameters[i];
            if (parameter.type == AnimatorControllerParameterType.Bool && parameter.name == parameterName)
            {
                parameterExists = true;
                return;
            }
        }

        if (logMissingParameterWarning && !missingParameterWarningLogged)
        {
            Debug.LogWarning(
                $"CP_CompanionAnimatorDriver: Animator on '{animator.gameObject.name}' is missing Bool parameter '{parameterName}'.",
                this
            );
            missingParameterWarningLogged = true;
        }
    }

    private void ApplyMovingStateOnlyWhenChanged(bool moving)
    {
        if (latestAppliedMoving == moving)
        {
            return;
        }

        ApplyMovingState(moving, allowMissingAnimator: false);
    }

    private void ApplyMovingState(bool forceValue, bool allowMissingAnimator)
    {
        latestAppliedMoving = forceValue;

        if (animator == null)
        {
            return;
        }

        if (!parameterValidated)
        {
            ValidateAnimatorParameter();
        }

        if (!parameterExists)
        {
            if (!allowMissingAnimator && logMissingParameterWarning && !missingParameterWarningLogged)
            {
                Debug.LogWarning(
                    $"CP_CompanionAnimatorDriver: Animator parameter '{movingBoolName}' is unavailable, so isMoving cannot be updated.",
                    this
                );
                missingParameterWarningLogged = true;
            }
            return;
        }

        animator.SetBool(movingBoolHash, forceValue);
    }
}
