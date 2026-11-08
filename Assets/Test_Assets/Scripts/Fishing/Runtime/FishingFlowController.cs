using System;
using UnityEngine;
using UnityEngine.InputSystem;

public class FishingFlowController : MonoBehaviour
{
    [Header("Core References")]
    [SerializeField] private FishingStateBlackboard blackboard;
    [SerializeField] private FishingBobberController bobberController;
    [SerializeField] private FishingCatchController catchController;
    [SerializeField] private FishingCapacityPopupController capacityPopupController;

    [Header("Fishing Input")]
    [SerializeField] private InputActionReference fishingAction;

    [Header("Keyboard Debug")]
    [SerializeField] private bool allowKeyboardDebug = true;
    [SerializeField] private Key keyboardDebugKey = Key.G;

    private bool lastRodPicked;
    private bool lastPlayerInsideFishingArea;
    private bool stateInitialized;
    private System.Random biteRandom;
    private bool randomInitialized;

    // Input is owned here so the player fishing flow has one clear entry point.
    private void OnEnable()
    {
        if (fishingAction != null &&
            fishingAction.action != null)
        {
            fishingAction.action.Enable();
        }

        if (bobberController != null)
        {
            bobberController.CastCompleted += OnCastCompleted;
            bobberController.EmptyRecallCompleted += OnEmptyRecallCompleted;
            bobberController.HookedRecallCompleted += OnHookedRecallCompleted;
            bobberController.OperationFailed += OnBobberOperationFailed;
        }

        if (catchController != null)
        {
            catchController.CatchReleaseCompleted += OnCatchReleaseCompleted;
            catchController.CatchOperationFailed += OnCatchOperationFailed;
        }
    }

    private void OnDisable()
    {
        if (fishingAction != null &&
            fishingAction.action != null)
        {
            fishingAction.action.Disable();
        }

        if (bobberController != null)
        {
            bobberController.CastCompleted -= OnCastCompleted;
            bobberController.EmptyRecallCompleted -= OnEmptyRecallCompleted;
            bobberController.HookedRecallCompleted -= OnHookedRecallCompleted;
            bobberController.OperationFailed -= OnBobberOperationFailed;
        }

        if (catchController != null)
        {
            catchController.CatchReleaseCompleted -= OnCatchReleaseCompleted;
            catchController.CatchOperationFailed -= OnCatchOperationFailed;
        }
    }

    private void Start()
    {
        if (blackboard == null)
        {
            Debug.LogWarning(
                "FishingFlowController: Blackboard is not assigned."
            );
            return;
        }

        InitializeObservedTruth();
        InitializeRandomGenerator();
        ReconcilePassivePhase();
    }

    private void Update()
    {
        if (blackboard == null)
            return;

        if (!stateInitialized)
        {
            InitializeObservedTruth();
        }

        ObserveSafetyTruthChanges();

        if (WasFishingActionPressedThisFrame())
        {
            HandleFishingActionPressed();
        }

        TickBiteState();

        if (!IsActiveGameplayPhase())
        {
            ReconcilePassivePhase();
        }
    }

    private void InitializeObservedTruth()
    {
        if (blackboard == null)
            return;

        lastRodPicked = blackboard.rodPicked;
        lastPlayerInsideFishingArea = blackboard.playerInsideFishingArea;
        stateInitialized = true;
    }

    private void ObserveSafetyTruthChanges()
    {
        // The pickup and area reporters own raw truth; this controller reacts to changes.
        bool rodReleasedThisFrame =
            lastRodPicked &&
            !blackboard.rodPicked;

        bool playerLeftFishingAreaThisFrame =
            lastPlayerInsideFishingArea &&
            !blackboard.playerInsideFishingArea;

        lastRodPicked = blackboard.rodPicked;
        lastPlayerInsideFishingArea = blackboard.playerInsideFishingArea;

        if (rodReleasedThisFrame)
        {
            HandleRodReleased();
        }

        if (playerLeftFishingAreaThisFrame)
        {
            HandlePlayerLeftFishingArea();
        }
    }

    private bool WasFishingActionPressedThisFrame()
    {
        bool inputPressed = false;

        if (fishingAction != null &&
            fishingAction.action != null)
        {
            inputPressed = fishingAction.action.WasPressedThisFrame();
        }

        if (allowKeyboardDebug &&
            Keyboard.current != null)
        {
            inputPressed =
                inputPressed ||
                Keyboard.current[keyboardDebugKey].wasPressedThisFrame;
        }

        return inputPressed;
    }

    private void HandleFishingActionPressed()
    {
        switch (blackboard.currentPhase)
        {
            case FishingGameplayPhase.RodHeldReady:
                TryStartCast();
                break;

            case FishingGameplayPhase.WaitingForBite:
                BeginEmptyRecall("voluntary player recall");
                break;

            case FishingGameplayPhase.BiteWindow:
                HandleSuccessfulHook();
                break;

            case FishingGameplayPhase.CapacityReached:
                Debug.Log(
                    "FishingFlowController: Capacity reached. Casting is disabled for this stage."
                );
                ShowCapacityPopup();
                break;
        }
    }

    private void TryStartCast()
    {
        // Casting is accepted only from a clean ready state to prevent duplicate bobbers.
        if (!CanStartCast(out string failureReason))
        {
            RecordFailure(failureReason);
            Debug.LogWarning(
                "FishingFlowController: Cast rejected. " + failureReason
            );
            return;
        }

        ResetForNewCast();

        if (!bobberController.BeginCast())
        {
            ForceReconcilePassivePhase();
            return;
        }

        SetPhase(FishingGameplayPhase.Casting);
        Debug.Log("FishingFlowController: Cast requested.");
    }

    private bool CanStartCast(out string failureReason)
    {
        if (blackboard.fishingStarted == false)
        {
            failureReason = "Fishing has not started.";
            return false;
        }

        if (blackboard.rodPicked == false)
        {
            failureReason = "Fishing rod is not held.";
            return false;
        }

        if (blackboard.playerInsideFishingArea == false)
        {
            failureReason = "Player is outside PlayerFishingArea.";
            return false;
        }

        if (blackboard.activityCompleted)
        {
            failureReason = "Fishing activity is already completed.";
            return false;
        }

        if (bobberController == null)
        {
            failureReason = "BobberController is not assigned.";
            return false;
        }

        if (blackboard.currentBobberInstance != null ||
            blackboard.bobberCast ||
            blackboard.recallInProgress ||
            blackboard.currentCatchInstance != null ||
            blackboard.catchReleaseInProgress ||
            bobberController.IsBusy)
        {
            failureReason = "A bobber or catch operation is already active.";
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

        if (!bobberController.HasValidCastSetup(out failureReason))
        {
            return false;
        }

        failureReason = "";
        return true;
    }

    private void HandleRodReleased()
    {
        if (blackboard.currentPhase == FishingGameplayPhase.Casting ||
            blackboard.currentPhase == FishingGameplayPhase.WaitingForBite ||
            blackboard.currentPhase == FishingGameplayPhase.BiteWindow)
        {
            BeginEmptyRecall("rod released");
        }
    }

    private void HandlePlayerLeftFishingArea()
    {
        if (blackboard.currentPhase == FishingGameplayPhase.Casting ||
            blackboard.currentPhase == FishingGameplayPhase.WaitingForBite ||
            blackboard.currentPhase == FishingGameplayPhase.BiteWindow)
        {
            BeginEmptyRecall("player left PlayerFishingArea");
        }
    }

    private void BeginEmptyRecall(string reason)
    {
        // Empty recall is the safety exit path when no hook has been accepted.
        ClearActiveBiteState(resetDelay: true);

        if (bobberController == null)
        {
            RecordFailure("BobberController is not assigned.");
            ForceReconcilePassivePhase();
            return;
        }

        SetPhase(FishingGameplayPhase.RecallingEmpty);

        if (!bobberController.BeginEmptyRecall())
        {
            RecordFailure("Empty recall failed to start: " + reason);
            ForceReconcilePassivePhase();
            return;
        }

        Debug.Log(
            "FishingFlowController: Empty recall requested because " + reason + "."
        );
    }

    private void OnCastCompleted()
    {
        if (blackboard == null)
            return;

        ScheduleInitialBiteDelay();
        SetPhase(FishingGameplayPhase.WaitingForBite);
        Debug.Log("FishingFlowController: Cast completed. Waiting for bite.");
    }

    private void OnEmptyRecallCompleted()
    {
        if (blackboard == null)
            return;

        Debug.Log("FishingFlowController: Empty recall completed.");
        ForceReconcilePassivePhase();
    }

    private void OnHookedRecallCompleted()
    {
        if (blackboard == null)
            return;

        ClearActiveBiteState(resetDelay: true);

        if (catchController == null)
        {
            RecoverFromCatchFailure("FishingCatchController is not assigned.");
            return;
        }

        if (blackboard.currentCatchInstance == null)
        {
            RecoverFromCatchFailure("No current catch instance exists after hooked recall.");
            return;
        }

        SetPhase(FishingGameplayPhase.ReleasingCatch);
        blackboard.catchReleaseInProgress = true;

        if (!catchController.BeginReleaseToFishReleasePoint())
        {
            return;
        }

        Debug.Log("FishingFlowController: Hooked recall completed. Releasing catch.");
    }

    private void OnCatchReleaseCompleted()
    {
        if (blackboard == null)
            return;

        blackboard.catchReleaseInProgress = false;
        blackboard.caughtFishCount += 1;

        int safeMaxCatchCount = Mathf.Max(1, blackboard.maxCatchCount);

        if (blackboard.caughtFishCount >= safeMaxCatchCount)
        {
            blackboard.activityCompleted = true;
            SetPhase(FishingGameplayPhase.CapacityReached);
            ShowCapacityPopup();
            Debug.Log("FishingFlowController: Fishing capacity reached.");
            return;
        }

        blackboard.activityCompleted = false;
        Debug.Log("FishingFlowController: Catch released and counted.");
        ForceReconcilePassivePhase();
    }

    private void OnCatchOperationFailed(string reason)
    {
        RecoverFromCatchFailure(reason);
    }

    private void OnBobberOperationFailed(string reason)
    {
        if (blackboard == null)
            return;

        ClearActiveBiteState(resetDelay: true);
        RecordFailure(reason);
        Debug.LogWarning(
            "FishingFlowController: Bobber operation failed. " + reason
        );
        ForceReconcilePassivePhase();
    }

    private void ReconcilePassivePhase()
    {
        ReconcilePassivePhase(force: false);
    }

    private void ForceReconcilePassivePhase()
    {
        ReconcilePassivePhase(force: true);
    }

    private void ReconcilePassivePhase(bool force)
    {
        // Passive phases are derived from Blackboard truth when no cast/recall flow owns the moment.
        if (blackboard == null ||
            (!force && IsActiveGameplayPhase()))
        {
            return;
        }

        if (blackboard.activityCompleted)
        {
            SetPhase(FishingGameplayPhase.CapacityReached);
        }
        else if (!blackboard.fishingStarted)
        {
            SetPhase(FishingGameplayPhase.Inactive);
        }
        else if (!blackboard.rodPicked)
        {
            SetPhase(FishingGameplayPhase.BucketReady);
        }
        else
        {
            SetPhase(FishingGameplayPhase.RodHeldReady);
        }
    }

    private bool IsActiveGameplayPhase()
    {
        if (blackboard == null)
            return false;

        return blackboard.currentPhase == FishingGameplayPhase.Casting ||
               blackboard.currentPhase == FishingGameplayPhase.WaitingForBite ||
               blackboard.currentPhase == FishingGameplayPhase.BiteWindow ||
               blackboard.currentPhase == FishingGameplayPhase.RecallingEmpty ||
               blackboard.currentPhase == FishingGameplayPhase.ReelingCatch ||
               blackboard.currentPhase == FishingGameplayPhase.ReleasingCatch;
    }

    private void SetPhase(FishingGameplayPhase newPhase)
    {
        if (blackboard == null ||
            blackboard.currentPhase == newPhase)
        {
            return;
        }

        blackboard.currentPhase = newPhase;
    }

    private void RecordFailure(string reason)
    {
        if (blackboard != null)
        {
            blackboard.lastFailureReason = reason;
        }
    }

    private void InitializeRandomGenerator()
    {
        if (blackboard == null)
            return;

        int seed = blackboard.useFixedRandomSeed
            ? blackboard.randomSeed
            : Environment.TickCount ^ GetInstanceID();

        biteRandom = new System.Random(seed);
        randomInitialized = true;
    }

    private void EnsureRandomGenerator()
    {
        if (!randomInitialized ||
            biteRandom == null)
        {
            InitializeRandomGenerator();
        }
    }

    private float NextRange(float min, float max)
    {
        EnsureRandomGenerator();

        float safeMin = Mathf.Max(0f, min);
        float safeMax = Mathf.Max(0f, max);

        if (safeMin > safeMax)
        {
            float swap = safeMin;
            safeMin = safeMax;
            safeMax = swap;
        }

        if (Mathf.Approximately(safeMin, safeMax))
            return safeMin;

        return safeMin +
               (float)biteRandom.NextDouble() * (safeMax - safeMin);
    }

    private void ScheduleInitialBiteDelay()
    {
        blackboard.currentWaitTime = 0f;
        blackboard.currentBiteDelay = NextRange(
            blackboard.biteDelayMin,
            blackboard.biteDelayMax
        );
        blackboard.currentBiteWindowTime = 0f;
        blackboard.biteActive = false;

        if (bobberController != null)
        {
            bobberController.EndBiteCue();
        }
    }

    private void ScheduleRetryBiteDelay()
    {
        blackboard.currentWaitTime = 0f;
        blackboard.currentBiteDelay = NextRange(
            blackboard.retryDelayMin,
            blackboard.retryDelayMax
        );
        blackboard.currentBiteWindowTime = 0f;
        blackboard.biteActive = false;
    }

    private void TickBiteState()
    {
        if (blackboard == null)
            return;

        if (blackboard.currentPhase == FishingGameplayPhase.WaitingForBite)
        {
            TickWaitingForBite();
        }
        else if (blackboard.currentPhase == FishingGameplayPhase.BiteWindow)
        {
            TickBiteWindow();
        }
    }

    private void TickWaitingForBite()
    {
        if (!CanTickBiteTiming())
        {
            BeginEmptyRecall("bobber lost while waiting for bite");
            return;
        }

        blackboard.currentWaitTime += Time.deltaTime;

        if (blackboard.currentWaitTime >= blackboard.currentBiteDelay)
        {
            BeginBiteWindow();
        }
    }

    private void TickBiteWindow()
    {
        if (!CanTickBiteTiming())
        {
            BeginEmptyRecall("bobber lost during BiteWindow");
            return;
        }

        blackboard.currentBiteWindowTime += Time.deltaTime;

        float safeWindowDuration =
            Mathf.Max(0f, blackboard.biteWindowDuration);

        if (blackboard.currentBiteWindowTime >= safeWindowDuration)
        {
            HandleBiteWindowMiss();
        }
    }

    private bool CanTickBiteTiming()
    {
        return blackboard.currentBobberInstance != null &&
               blackboard.bobberInWater &&
               !blackboard.recallInProgress;
    }

    private void BeginBiteWindow()
    {
        blackboard.biteActive = true;
        blackboard.currentBiteWindowTime = 0f;

        if (bobberController != null)
        {
            bobberController.BeginBiteCue();
        }

        SetPhase(FishingGameplayPhase.BiteWindow);
        Debug.Log("Fishing bite window opened.");
    }

    private void HandleBiteWindowMiss()
    {
        blackboard.biteActive = false;

        if (bobberController != null)
        {
            bobberController.EndBiteCue();
        }

        ScheduleRetryBiteDelay();
        SetPhase(FishingGameplayPhase.WaitingForBite);
        Debug.Log("Fishing bite missed. Waiting for the next bite.");
    }

    private void HandleSuccessfulHook()
    {
        blackboard.biteActive = false;
        blackboard.currentBiteWindowTime = 0f;

        if (bobberController != null)
        {
            bobberController.EndBiteCue();
        }

        if (catchController == null)
        {
            RecoverFromCatchFailure("FishingCatchController is not assigned.");
            return;
        }

        if (!catchController.HasValidSetup(out string failureReason))
        {
            RecoverFromCatchFailure(failureReason);
            return;
        }

        if (!catchController.BeginSuccessfulCatch())
        {
            return;
        }

        blackboard.catchSucceeded = true;

        SetPhase(FishingGameplayPhase.ReelingCatch);

        if (bobberController == null ||
            !bobberController.BeginHookedRecall())
        {
            RecoverFromCatchFailure("Hooked recall failed to start.");
            return;
        }

        Debug.Log("Fishing hook succeeded. Hooked recall started.");
    }

    private void ResetForNewCast()
    {
        ClearActiveBiteState(resetDelay: true);
        blackboard.catchSucceeded = false;
        blackboard.catchReleaseInProgress = false;
    }

    private void ClearActiveBiteState(bool resetDelay)
    {
        if (blackboard == null)
            return;

        blackboard.biteActive = false;
        blackboard.currentWaitTime = 0f;
        blackboard.currentBiteWindowTime = 0f;

        if (resetDelay)
        {
            blackboard.currentBiteDelay = 0f;
        }

        if (bobberController != null)
        {
            bobberController.EndBiteCue();
        }
    }

    private void RecoverFromCatchFailure(string reason)
    {
        RecordFailure(reason);
        Debug.LogWarning(
            "FishingFlowController: Catch operation failed. " + reason
        );
        blackboard.catchReleaseInProgress = false;
        CleanupFailedCatchReference();

        if (blackboard.currentBobberInstance != null &&
            bobberController != null)
        {
            if (blackboard.recallInProgress)
            {
                SetPhase(FishingGameplayPhase.RecallingEmpty);
                return;
            }

            BeginEmptyRecall("catch operation failed");
            return;
        }

        ForceReconcilePassivePhase();
    }

    private void CleanupFailedCatchReference()
    {
        if (blackboard == null)
            return;

        if (blackboard.currentCatchInstance != null)
        {
            Destroy(blackboard.currentCatchInstance);
            blackboard.currentCatchInstance = null;
        }
    }

    private void ShowCapacityPopup()
    {
        if (capacityPopupController != null)
        {
            capacityPopupController.ShowCapacityReached();
        }
    }
}
