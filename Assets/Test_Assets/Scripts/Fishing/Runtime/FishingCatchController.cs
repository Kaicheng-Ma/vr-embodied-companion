using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class FishingCatchController : MonoBehaviour
{
    [Header("Core References")]
    [SerializeField] private FishingStateBlackboard blackboard;
    [SerializeField] private GameObject fishPrefab;

    [Header("Motion")]
    [Min(0.05f)]
    [SerializeField] private float releaseDuration = 0.8f;

    public event Action CatchReleaseCompleted;
    public event Action<string> CatchOperationFailed;

    private Rigidbody controlledRigidbody;
    private XRGrabInteractable controlledGrabInteractable;
    private Collider[] controlledColliders = Array.Empty<Collider>();
    private Vector3 releaseStartPosition;
    private float releaseElapsed;
    private bool catchControlled;
    private bool releaseInProgress;
    private bool releasedPhysicalFish;
    private bool releaseCompleted;

    private void LateUpdate()
    {
        FollowHookedBobber();
    }

    private void Update()
    {
        if (releaseInProgress)
        {
            UpdateReleaseMotion();
        }
    }

    private void OnDisable()
    {
        CleanupControlledCatch(destroyFish: !releasedPhysicalFish);
    }

    public bool HasValidSetup(out string failureReason)
    {
        if (blackboard == null)
        {
            failureReason = "FishingCatchController: Blackboard is not assigned.";
            return false;
        }

        if (fishPrefab == null)
        {
            failureReason = "FishingCatchController: Fish Prefab is not assigned.";
            return false;
        }

        if (blackboard.fishReleasePoint == null)
        {
            failureReason = "FishingCatchController: FishReleasePoint is not assigned.";
            return false;
        }

        failureReason = "";
        return true;
    }

    public bool BeginSuccessfulCatch()
    {
        if (!HasValidSetup(out string failureReason))
        {
            FailCatch(failureReason, cleanupFish: true);
            return false;
        }

        if (blackboard.currentBobberInstance == null)
        {
            FailCatch(
                "FishingCatchController: Cannot begin catch because no bobber exists.",
                cleanupFish: true
            );
            return false;
        }

        if (blackboard.currentCatchInstance != null ||
            catchControlled)
        {
            FailCatch(
                "FishingCatchController: A catch instance is already active.",
                cleanupFish: true
            );
            return false;
        }

        GameObject fishInstance = Instantiate(
            fishPrefab,
            blackboard.currentBobberInstance.transform.position,
            blackboard.currentBobberInstance.transform.rotation
        );

        if (blackboard.caughtFishRuntimeParent != null)
        {
            fishInstance.transform.SetParent(
                blackboard.caughtFishRuntimeParent,
                true
            );
        }

        blackboard.currentCatchInstance = fishInstance;

        if (!CacheFishComponents(fishInstance, out failureReason))
        {
            FailCatch(failureReason, cleanupFish: true);
            return false;
        }

        SetControlledFishState();

        releaseElapsed = 0f;
        releaseInProgress = false;
        catchControlled = true;
        releasedPhysicalFish = false;
        releaseCompleted = false;

        Debug.Log("FishingCatchController: Successful catch fish created.");
        return true;
    }

    public bool BeginReleaseToFishReleasePoint()
    {
        if (blackboard == null)
        {
            FailCatch(
                "FishingCatchController: Blackboard is not assigned.",
                cleanupFish: true
            );
            return false;
        }

        if (blackboard.currentCatchInstance == null)
        {
            FailCatch(
                "FishingCatchController: Cannot release because no catch instance exists.",
                cleanupFish: true
            );
            return false;
        }

        if (blackboard.fishReleasePoint == null)
        {
            FailCatch(
                "FishingCatchController: FishReleasePoint is not assigned.",
                cleanupFish: true
            );
            return false;
        }

        releaseStartPosition = blackboard.currentCatchInstance.transform.position;
        releaseElapsed = 0f;
        releaseInProgress = true;
        blackboard.catchReleaseInProgress = true;

        SetControlledFishState();
        Debug.Log("FishingCatchController: Catch release started.");
        return true;
    }

    private void FollowHookedBobber()
    {
        if (!catchControlled ||
            releaseInProgress ||
            blackboard == null ||
            blackboard.currentCatchInstance == null)
        {
            return;
        }

        if (blackboard.currentPhase != FishingGameplayPhase.ReelingCatch)
            return;

        if (blackboard.currentBobberInstance == null)
        {
            FailCatch(
                "FishingCatchController: Bobber was lost while fish was reeling.",
                cleanupFish: true
            );
            return;
        }

        blackboard.currentCatchInstance.transform.position =
            blackboard.currentBobberInstance.transform.position;
    }

    private void UpdateReleaseMotion()
    {
        if (blackboard == null ||
            blackboard.currentCatchInstance == null ||
            blackboard.fishReleasePoint == null)
        {
            FailCatch(
                "FishingCatchController: Catch release lost a required reference.",
                cleanupFish: true
            );
            return;
        }

        releaseElapsed += Time.deltaTime;

        float t = Mathf.Clamp01(releaseElapsed / releaseDuration);
        Vector3 releaseTarget = blackboard.fishReleasePoint.position;

        blackboard.currentCatchInstance.transform.position =
            Vector3.Lerp(releaseStartPosition, releaseTarget, t);

        if (t < 1f)
            return;

        blackboard.currentCatchInstance.transform.position = releaseTarget;
        EnablePhysicalFishState();
        CompleteRelease();
    }

    private bool CacheFishComponents(
        GameObject fishInstance,
        out string failureReason
    )
    {
        controlledRigidbody = fishInstance.GetComponentInChildren<Rigidbody>(true);
        controlledGrabInteractable =
            fishInstance.GetComponentInChildren<XRGrabInteractable>(true);
        controlledColliders =
            fishInstance.GetComponentsInChildren<Collider>(true);

        if (controlledRigidbody == null)
        {
            failureReason = "FishingCatchController: Fish Prefab needs a Rigidbody.";
            return false;
        }

        if (controlledGrabInteractable == null)
        {
            failureReason =
                "FishingCatchController: Fish Prefab needs an XRGrabInteractable.";
            return false;
        }

        if (controlledColliders == null ||
            controlledColliders.Length == 0)
        {
            failureReason = "FishingCatchController: Fish Prefab needs a Collider.";
            return false;
        }

        failureReason = "";
        return true;
    }

    private void SetControlledFishState()
    {
        if (controlledRigidbody != null)
        {
            controlledRigidbody.isKinematic = true;
            controlledRigidbody.useGravity = false;
        }

        if (controlledGrabInteractable != null)
        {
            controlledGrabInteractable.enabled = false;
        }

        SetCollidersEnabled(false);
    }

    private void EnablePhysicalFishState()
    {
        if (controlledRigidbody != null)
        {
            controlledRigidbody.isKinematic = false;
            controlledRigidbody.useGravity = true;
        }

        SetCollidersEnabled(true);

        if (controlledGrabInteractable != null)
        {
            controlledGrabInteractable.enabled = true;
        }

        releasedPhysicalFish = true;
    }

    private void SetCollidersEnabled(bool enabled)
    {
        for (int i = 0; i < controlledColliders.Length; i++)
        {
            if (controlledColliders[i] != null)
            {
                controlledColliders[i].enabled = enabled;
            }
        }
    }

    private void CompleteRelease()
    {
        if (releaseCompleted)
            return;

        releaseCompleted = true;
        releaseInProgress = false;
        catchControlled = false;

        if (blackboard != null)
        {
            blackboard.currentCatchInstance = null;
            blackboard.catchReleaseInProgress = false;
        }

        ClearCachedComponents();
        Debug.Log("FishingCatchController: Catch release completed.");
        CatchReleaseCompleted?.Invoke();
    }

    private void FailCatch(string reason, bool cleanupFish)
    {
        Debug.LogWarning(reason);
        CleanupControlledCatch(destroyFish: cleanupFish);
        CatchOperationFailed?.Invoke(reason);
    }

    private void CleanupControlledCatch(bool destroyFish)
    {
        GameObject catchInstance =
            blackboard != null ? blackboard.currentCatchInstance : null;

        if (destroyFish &&
            catchInstance != null)
        {
            Destroy(catchInstance);
        }

        if (blackboard != null)
        {
            blackboard.currentCatchInstance = null;
            blackboard.catchReleaseInProgress = false;
        }

        releaseInProgress = false;
        catchControlled = false;
        releaseCompleted = false;
        ClearCachedComponents();
    }

    private void ClearCachedComponents()
    {
        controlledRigidbody = null;
        controlledGrabInteractable = null;
        controlledColliders = Array.Empty<Collider>();
    }
}
