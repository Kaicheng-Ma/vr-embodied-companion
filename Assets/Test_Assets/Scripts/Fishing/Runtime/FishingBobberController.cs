using System;
using UnityEngine;

public class FishingBobberController : MonoBehaviour
{
    private enum BobberMotionMode
    {
        None,
        Casting,
        Recalling
    }

    private enum BobberRecallKind
    {
        Empty,
        Hooked
    }

    [Header("Core References")]
    [SerializeField] private FishingStateBlackboard blackboard;
    [SerializeField] private GameObject bobberPrefab;
    [SerializeField] private LineRenderer fishingLine;

    [Header("Motion")]
    [Min(0.05f)]
    [SerializeField] private float castDuration = 0.9f;

    [Min(0f)]
    [SerializeField] private float castArcHeight = 1f;

    [Min(0.05f)]
    [SerializeField] private float recallDuration = 0.6f;

    [Header("Bite Visual")]
    [Min(0f)]
    [SerializeField] private float biteDipDistance = 0.12f;

    public event Action CastCompleted;
    public event Action EmptyRecallCompleted;
    public event Action HookedRecallCompleted;
    public event Action<string> OperationFailed;

    public bool IsBusy => motionMode != BobberMotionMode.None;
    public bool HasActiveBobber =>
        blackboard != null &&
        blackboard.currentBobberInstance != null;

    private BobberMotionMode motionMode = BobberMotionMode.None;
    private Vector3 motionStartPosition;
    private Vector3 castTargetPosition;
    private Vector3 waterRestPosition;
    private float motionElapsed;
    private BobberRecallKind activeRecallKind = BobberRecallKind.Empty;
    private bool biteCueActive;

    // The visual line starts hidden and is only enabled while a bobber exists.
    private void Awake()
    {
        HideFishingLine();
    }

    private void Update()
    {
        if (motionMode == BobberMotionMode.Casting)
        {
            UpdateCastMotion();
        }
        else if (motionMode == BobberMotionMode.Recalling)
        {
            UpdateRecallMotion();
        }
    }

    private void LateUpdate()
    {
        UpdateFishingLine();
    }

    private void OnDisable()
    {
        CleanupImmediate(notifyFailure: false, reason: "");
    }

    public bool HasValidCastSetup(out string failureReason)
    {
        if (blackboard == null)
        {
            failureReason = "Blackboard is not assigned.";
            return false;
        }

        if (bobberPrefab == null)
        {
            failureReason = "Bobber Prefab is not assigned.";
            return false;
        }

        if (blackboard.rodTip == null)
        {
            failureReason = "RodTip is not assigned.";
            return false;
        }

        if (blackboard.playerCastTarget == null)
        {
            failureReason = "PlayerFishingCastTarget is not assigned.";
            return false;
        }

        failureReason = "";
        return true;
    }

    public bool BeginCast()
    {
        // Stage 4 uses one deterministic bobber instance and no projectile physics.
        if (!HasValidCastSetup(out string failureReason))
        {
            FailOperation(failureReason);
            return false;
        }

        if (IsBusy ||
            blackboard.currentBobberInstance != null)
        {
            FailOperation("Cannot cast while another bobber operation is active.");
            return false;
        }

        GameObject bobberInstance = Instantiate(
            bobberPrefab,
            blackboard.rodTip.position,
            blackboard.rodTip.rotation
        );

        if (blackboard.bobberRuntimeParent != null)
        {
            bobberInstance.transform.SetParent(
                blackboard.bobberRuntimeParent,
                true
            );
        }

        blackboard.currentBobberInstance = bobberInstance;
        blackboard.bobberCast = true;
        blackboard.bobberInWater = false;
        blackboard.recallInProgress = false;
        blackboard.biteActive = false;

        motionStartPosition = blackboard.rodTip.position;
        castTargetPosition = blackboard.playerCastTarget.position;
        waterRestPosition = castTargetPosition;
        motionElapsed = 0f;
        motionMode = BobberMotionMode.Casting;
        activeRecallKind = BobberRecallKind.Empty;
        biteCueActive = false;

        ShowFishingLine();
        Debug.Log("FishingBobberController: Cast started.");
        return true;
    }

    public bool BeginEmptyRecall()
    {
        // Recall can interrupt a cast or return a bobber already waiting on water.
        return BeginRecall(BobberRecallKind.Empty);
    }

    public bool BeginHookedRecall()
    {
        return BeginRecall(BobberRecallKind.Hooked);
    }

    public void BeginBiteCue()
    {
        if (blackboard == null ||
            blackboard.currentBobberInstance == null)
        {
            return;
        }

        biteCueActive = true;
        blackboard.currentBobberInstance.transform.position =
            waterRestPosition + Vector3.down * biteDipDistance;
    }

    public void EndBiteCue()
    {
        if (!biteCueActive)
            return;

        biteCueActive = false;

        if (blackboard == null ||
            blackboard.currentBobberInstance == null)
        {
            return;
        }

        blackboard.currentBobberInstance.transform.position = waterRestPosition;
    }

    private bool BeginRecall(BobberRecallKind recallKind)
    {
        if (blackboard == null)
        {
            FailOperation("Blackboard is not assigned.");
            return false;
        }

        if (blackboard.currentBobberInstance == null)
        {
            FailOperation("Cannot recall because no bobber instance exists.");
            return false;
        }

        if (blackboard.rodTip == null)
        {
            FailOperation("Cannot recall because RodTip is not assigned.");
            return false;
        }

        EndBiteCue();
        motionStartPosition = blackboard.currentBobberInstance.transform.position;
        motionElapsed = 0f;
        motionMode = BobberMotionMode.Recalling;
        activeRecallKind = recallKind;

        blackboard.recallInProgress = true;
        blackboard.bobberInWater = false;
        blackboard.biteActive = false;

        ShowFishingLine();
        Debug.Log(
            recallKind == BobberRecallKind.Hooked
                ? "FishingBobberController: Hooked recall started."
                : "FishingBobberController: Empty recall started."
        );
        return true;
    }

    public void CleanupImmediateForFailure(string reason)
    {
        CleanupImmediate(notifyFailure: true, reason: reason);
    }

    private void UpdateCastMotion()
    {
        if (!HasRuntimeBobber("Cast failed because the bobber instance was lost."))
            return;

        // Programmatic parabola: base lerp plus a simple vertical arc.
        motionElapsed += Time.deltaTime;

        float t = Mathf.Clamp01(motionElapsed / castDuration);
        Vector3 basePosition = Vector3.Lerp(
            motionStartPosition,
            castTargetPosition,
            t
        );
        Vector3 arcOffset =
            Vector3.up * (4f * castArcHeight * t * (1f - t));

        blackboard.currentBobberInstance.transform.position =
            basePosition + arcOffset;

        if (t < 1f)
            return;

        blackboard.currentBobberInstance.transform.position = castTargetPosition;
        waterRestPosition = castTargetPosition;
        blackboard.bobberInWater = true;
        blackboard.recallInProgress = false;
        motionMode = BobberMotionMode.None;

        Debug.Log("FishingBobberController: Cast completed.");
        CastCompleted?.Invoke();
    }

    private void UpdateRecallMotion()
    {
        if (!HasRuntimeBobber("Recall failed because the bobber instance was lost."))
            return;

        if (blackboard.rodTip == null)
        {
            CleanupImmediate(
                notifyFailure: true,
                reason: "Recall failed because RodTip became missing."
            );
            return;
        }

        motionElapsed += Time.deltaTime;

        // The rod tip may move while held or released, so the recall target is sampled live.
        float t = Mathf.Clamp01(motionElapsed / recallDuration);
        Vector3 recallTarget = blackboard.rodTip.position;

        blackboard.currentBobberInstance.transform.position =
            Vector3.Lerp(motionStartPosition, recallTarget, t);

        if (t < 1f)
            return;

        Destroy(blackboard.currentBobberInstance);
        ResetBobberTruth();
        HideFishingLine();
        motionMode = BobberMotionMode.None;

        if (activeRecallKind == BobberRecallKind.Hooked)
        {
            Debug.Log("FishingBobberController: Hooked recall completed.");
            HookedRecallCompleted?.Invoke();
        }
        else
        {
            Debug.Log("FishingBobberController: Empty recall completed.");
            EmptyRecallCompleted?.Invoke();
        }
    }

    private bool HasRuntimeBobber(string failureReason)
    {
        if (blackboard != null &&
            blackboard.currentBobberInstance != null)
        {
            return true;
        }

        CleanupImmediate(notifyFailure: true, reason: failureReason);
        return false;
    }

    private void UpdateFishingLine()
    {
        // LineRenderer is visual-only: two points, no rope, joints, or physics allocation.
        if (fishingLine == null)
            return;

        if (blackboard == null ||
            blackboard.rodTip == null ||
            blackboard.currentBobberInstance == null)
        {
            HideFishingLine();
            return;
        }

        if (!fishingLine.enabled)
        {
            ShowFishingLine();
        }

        fishingLine.positionCount = 2;
        fishingLine.SetPosition(0, blackboard.rodTip.position);
        fishingLine.SetPosition(
            1,
            blackboard.currentBobberInstance.transform.position
        );
    }

    private void ShowFishingLine()
    {
        if (fishingLine == null)
            return;

        fishingLine.positionCount = 2;
        fishingLine.enabled = true;
    }

    private void HideFishingLine()
    {
        if (fishingLine == null)
            return;

        fishingLine.positionCount = 0;
        fishingLine.enabled = false;
    }

    private void FailOperation(string reason)
    {
        CleanupImmediate(notifyFailure: true, reason: reason);
    }

    private void CleanupImmediate(bool notifyFailure, string reason)
    {
        EndBiteCue();

        if (blackboard != null &&
            blackboard.currentBobberInstance != null)
        {
            Destroy(blackboard.currentBobberInstance);
        }

        ResetBobberTruth();
        HideFishingLine();
        motionMode = BobberMotionMode.None;
        motionElapsed = 0f;
        activeRecallKind = BobberRecallKind.Empty;

        if (notifyFailure)
        {
            Debug.LogWarning(
                "FishingBobberController: " + reason
            );
            OperationFailed?.Invoke(reason);
        }
    }

    private void ResetBobberTruth()
    {
        if (blackboard == null)
            return;

        blackboard.currentBobberInstance = null;
        blackboard.bobberCast = false;
        blackboard.bobberInWater = false;
        blackboard.biteActive = false;
        blackboard.recallInProgress = false;
        blackboard.currentWaitTime = 0f;
        blackboard.currentBiteDelay = 0f;
        blackboard.currentBiteWindowTime = 0f;
    }
}
