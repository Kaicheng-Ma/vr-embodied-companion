using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// Companion-only Fishing visual runtime:
// owns bobber, line, and visual fish lifecycle without touching player Fishing truth.
public class CP_CompanionFishingVisualController : MonoBehaviour
{
    private enum VisualMotionPhase
    {
        None,
        Casting,
        Reeling,
    }

    [Header("References")]
    public CP_ActivityContextRouter activityContextRouter;
    public CP_FishingAnchorManager fishingAnchorManager;
    public Transform companionRodTip;
    public Transform companionFishHangAnchor;
    public LineRenderer companionFishingLine;
    public GameObject companionBobberVisualPrefab;
    public GameObject companionFishVisualPrefab;
    public Transform runtimeParent;

    [Header("Player-Matched Motion Defaults")]
    [Min(0.05f)] public float castDurationSeconds = 0.9f;
    [Min(0f)] public float castArcHeight = 1.0f;
    [Min(0.05f)] public float reelDurationSeconds = 0.6f;

    [Header("Runtime Debug")]
    public bool visualRuntimeReady;
    public string lastFailureReason = string.Empty;
    public string activeVisualPhase = "none";
    public bool lineVisible;
    public bool hasActiveBobber;
    public bool hasActiveReelFish;
    public bool hasPersistentCaughtFish;
    public string resolvedCastTargetId = string.Empty;

    private VisualMotionPhase motionPhase = VisualMotionPhase.None;
    private GameObject activeBobber;
    private GameObject activeReelFish;
    private GameObject persistentCaughtFish;
    private Vector3 castStartPosition;
    private Vector3 castTargetPosition;
    private Vector3 reelStartPosition;
    private float motionElapsedSeconds;

    public float CastDurationSeconds => Mathf.Max(0.05f, castDurationSeconds);
    public float ReelDurationSeconds => Mathf.Max(0.05f, reelDurationSeconds);
    public bool HasPersistentCaughtFish => persistentCaughtFish != null;
    public bool HasActiveBobber => activeBobber != null;

    private void Awake()
    {
        HideFishingLine();
        RefreshDebugState();
    }

    private void Update()
    {
        UpdateActivityExitCleanup();
        UpdateVisualMotion();
        RefreshDebugState();
    }

    private void LateUpdate()
    {
        UpdateFishingLine();
    }

    private void OnDisable()
    {
        CleanupAllFishingVisuals();
        RefreshDebugState();
    }

    public bool HasCycleVisualRuntimeReady(out string failureReason)
    {
        failureReason = string.Empty;

        if (activityContextRouter == null)
        {
            failureReason = "companion_fishing_activity_context_missing";
            return false;
        }

        if (fishingAnchorManager == null)
        {
            failureReason = "companion_fishing_anchor_manager_missing";
            return false;
        }

        if (companionRodTip == null)
        {
            failureReason = "companion_fishing_rod_tip_missing";
            return false;
        }

        if (companionFishHangAnchor == null)
        {
            failureReason = "companion_fishing_fish_hang_anchor_missing";
            return false;
        }

        if (companionFishingLine == null)
        {
            failureReason = "companion_fishing_line_missing";
            return false;
        }

        if (companionBobberVisualPrefab == null)
        {
            failureReason = "companion_fishing_bobber_prefab_missing";
            return false;
        }

        if (companionFishVisualPrefab == null)
        {
            failureReason = "companion_fishing_fish_prefab_missing";
            return false;
        }

        if (HierarchyHasVisualPhysicsOrFishingOwnership(companionBobberVisualPrefab))
        {
            failureReason = "companion_fishing_bobber_prefab_unsafe";
            return false;
        }

        if (HierarchyHasVisualPhysicsOrFishingOwnership(companionFishVisualPrefab))
        {
            failureReason = "companion_fishing_fish_prefab_unsafe";
            return false;
        }

        if (!fishingAnchorManager.TryGetAssignedCastTargetTransform(out Transform castTarget) ||
            castTarget == null)
        {
            failureReason = "assigned_fishing_cast_target_unresolved";
            return false;
        }

        resolvedCastTargetId = fishingAnchorManager.AssignedFishingCastTargetId;
        visualRuntimeReady = true;
        lastFailureReason = string.Empty;
        return true;
    }

    // Called exactly once when a newly accepted cast cycle becomes the active owner.
    public bool PrepareForNewCycle(out string failureReason)
    {
        if (!HasCycleVisualRuntimeReady(out failureReason))
        {
            visualRuntimeReady = false;
            lastFailureReason = failureReason;
            return false;
        }

        if (persistentCaughtFish != null)
        {
            Destroy(persistentCaughtFish);
            persistentCaughtFish = null;
        }

        CancelTransientCycleVisuals();
        return true;
    }

    public bool BeginCast(out string failureReason)
    {
        if (!HasCycleVisualRuntimeReady(out failureReason))
        {
            visualRuntimeReady = false;
            lastFailureReason = failureReason;
            return false;
        }

        CancelTransientCycleVisuals();

        if (!fishingAnchorManager.TryGetAssignedCastTargetTransform(out Transform castTarget) ||
            castTarget == null)
        {
            failureReason = "assigned_fishing_cast_target_unresolved";
            lastFailureReason = failureReason;
            return false;
        }

        activeBobber = Instantiate(
            companionBobberVisualPrefab,
            companionRodTip.position,
            companionRodTip.rotation
        );

        if (runtimeParent != null)
        {
            activeBobber.transform.SetParent(runtimeParent, true);
        }

        castStartPosition = companionRodTip.position;
        castTargetPosition = castTarget.position;
        motionElapsedSeconds = 0f;
        motionPhase = VisualMotionPhase.Casting;
        lastFailureReason = string.Empty;
        UpdateFishingLine();
        return true;
    }

    public bool IsCastMotionComplete()
    {
        return motionPhase == VisualMotionPhase.None && activeBobber != null;
    }

    public void BeginWaiting()
    {
        if (activeBobber != null)
        {
            activeBobber.transform.position = castTargetPosition;
        }
    }

    public bool BeginReel(out string failureReason)
    {
        failureReason = string.Empty;

        if (activeBobber == null)
        {
            failureReason = "companion_fishing_bobber_missing_for_reel";
            lastFailureReason = failureReason;
            return false;
        }

        if (companionFishVisualPrefab == null)
        {
            failureReason = "companion_fishing_fish_prefab_missing";
            lastFailureReason = failureReason;
            return false;
        }

        Destroy(activeReelFish);
        activeReelFish = Instantiate(
            companionFishVisualPrefab,
            activeBobber.transform.position,
            activeBobber.transform.rotation
        );

        if (runtimeParent != null)
        {
            activeReelFish.transform.SetParent(runtimeParent, true);
        }

        reelStartPosition = activeBobber.transform.position;
        motionElapsedSeconds = 0f;
        motionPhase = VisualMotionPhase.Reeling;
        lastFailureReason = string.Empty;
        UpdateFishingLine();
        return true;
    }

    public bool IsReelMotionComplete()
    {
        return motionPhase == VisualMotionPhase.None && activeBobber != null && activeReelFish != null;
    }

    public bool CompleteReelAndPersistFish(out string failureReason)
    {
        failureReason = string.Empty;

        if (activeReelFish == null)
        {
            failureReason = "companion_fishing_reel_fish_missing";
            lastFailureReason = failureReason;
            return false;
        }

        Destroy(activeBobber);
        activeBobber = null;

        if (companionFishHangAnchor == null)
        {
            failureReason = "companion_fishing_fish_hang_anchor_missing";
            lastFailureReason = failureReason;
            return false;
        }

        activeReelFish.transform.SetParent(companionFishHangAnchor, false);
        activeReelFish.transform.localPosition = Vector3.zero;
        activeReelFish.transform.localRotation = Quaternion.identity;
        persistentCaughtFish = activeReelFish;
        activeReelFish = null;
        motionPhase = VisualMotionPhase.None;
        HideFishingLine();
        lastFailureReason = string.Empty;
        return true;
    }

    public void CancelTransientCycleVisuals()
    {
        motionPhase = VisualMotionPhase.None;
        motionElapsedSeconds = 0f;

        if (activeBobber != null)
        {
            Destroy(activeBobber);
            activeBobber = null;
        }

        if (activeReelFish != null)
        {
            Destroy(activeReelFish);
            activeReelFish = null;
        }

        HideFishingLine();
    }

    public void CleanupAllFishingVisuals()
    {
        CancelTransientCycleVisuals();

        if (persistentCaughtFish != null)
        {
            Destroy(persistentCaughtFish);
            persistentCaughtFish = null;
        }

        resolvedCastTargetId = string.Empty;
    }

    private void UpdateActivityExitCleanup()
    {
        bool fishingStillActive =
            activityContextRouter != null &&
            activityContextRouter.ActiveActivity == "fishing" &&
            !activityContextRouter.FishingCompleted;

        if (fishingStillActive)
        {
            return;
        }

        if (activeBobber != null || activeReelFish != null || persistentCaughtFish != null || companionFishingLine != null && companionFishingLine.enabled)
        {
            CleanupAllFishingVisuals();
        }
    }

    private void UpdateVisualMotion()
    {
        switch (motionPhase)
        {
            case VisualMotionPhase.Casting:
                UpdateCastMotion();
                break;
            case VisualMotionPhase.Reeling:
                UpdateReelMotion();
                break;
        }
    }

    private void UpdateCastMotion()
    {
        if (activeBobber == null)
        {
            motionPhase = VisualMotionPhase.None;
            lastFailureReason = "companion_fishing_bobber_lost_during_cast";
            return;
        }

        motionElapsedSeconds += Time.deltaTime;
        float t = Mathf.Clamp01(motionElapsedSeconds / CastDurationSeconds);
        Vector3 basePosition = Vector3.Lerp(castStartPosition, castTargetPosition, t);
        Vector3 arcOffset = Vector3.up * (4f * castArcHeight * t * (1f - t));
        activeBobber.transform.position = basePosition + arcOffset;

        if (t < 1f)
        {
            return;
        }

        activeBobber.transform.position = castTargetPosition;
        motionPhase = VisualMotionPhase.None;
    }

    private void UpdateReelMotion()
    {
        if (activeBobber == null)
        {
            motionPhase = VisualMotionPhase.None;
            lastFailureReason = "companion_fishing_bobber_lost_during_reel";
            return;
        }

        if (companionRodTip == null)
        {
            motionPhase = VisualMotionPhase.None;
            lastFailureReason = "companion_fishing_rod_tip_missing";
            return;
        }

        motionElapsedSeconds += Time.deltaTime;
        float t = Mathf.Clamp01(motionElapsedSeconds / ReelDurationSeconds);
        Vector3 recallTarget = companionRodTip.position;
        activeBobber.transform.position = Vector3.Lerp(reelStartPosition, recallTarget, t);

        if (activeReelFish != null)
        {
            activeReelFish.transform.position = activeBobber.transform.position;
        }

        if (t < 1f)
        {
            return;
        }

        activeBobber.transform.position = recallTarget;
        if (activeReelFish != null)
        {
            activeReelFish.transform.position = recallTarget;
        }

        motionPhase = VisualMotionPhase.None;
    }

    private void UpdateFishingLine()
    {
        if (companionFishingLine == null)
        {
            return;
        }

        if (companionRodTip == null || activeBobber == null)
        {
            HideFishingLine();
            return;
        }

        if (!companionFishingLine.enabled)
        {
            companionFishingLine.enabled = true;
        }

        companionFishingLine.positionCount = 2;
        companionFishingLine.SetPosition(0, companionRodTip.position);
        companionFishingLine.SetPosition(1, activeBobber.transform.position);
    }

    private void HideFishingLine()
    {
        if (companionFishingLine == null)
        {
            return;
        }

        companionFishingLine.positionCount = 0;
        companionFishingLine.enabled = false;
    }

    private bool HierarchyHasVisualPhysicsOrFishingOwnership(GameObject prefab)
    {
        if (prefab == null)
        {
            return false;
        }

        if (prefab.GetComponentsInChildren<Rigidbody>(true).Length > 0)
        {
            return true;
        }

        if (prefab.GetComponentsInChildren<Collider>(true).Length > 0)
        {
            return true;
        }

        if (prefab.GetComponentsInChildren<XRGrabInteractable>(true).Length > 0)
        {
            return true;
        }

        if (prefab.GetComponentsInChildren<FishingBobberController>(true).Length > 0 ||
            prefab.GetComponentsInChildren<FishingCatchController>(true).Length > 0 ||
            prefab.GetComponentsInChildren<FishingFlowController>(true).Length > 0)
        {
            return true;
        }

        return false;
    }

    private void RefreshDebugState()
    {
        hasActiveBobber = activeBobber != null;
        hasActiveReelFish = activeReelFish != null;
        hasPersistentCaughtFish = persistentCaughtFish != null;
        lineVisible = companionFishingLine != null && companionFishingLine.enabled;
        activeVisualPhase = motionPhase == VisualMotionPhase.None ? "none" : motionPhase.ToString().ToLowerInvariant();
    }
}
