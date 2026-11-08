using UnityEngine;

// Passive Unity-side attention used only while no explicit normal policy can execute.
// Stage 5B-1 keeps this as a scheduler-owned request layer; visual output is delegated to CP_LookController.
public class CP_DefaultAttentionController : MonoBehaviour
{
    [Header("References")]
    public CP_MotionExecutor motionExecutor;
    public CP_LookController lookController;

    [Header("Settings")]
    public bool defaultAttentionEnabled = true;
    public bool logDefaultAttention = true;

    [Header("Runtime Debug")]
    public bool defaultAttentionActive;
    public bool defaultAttentionRequested;
    public bool defaultAttentionControlling;
    public string defaultAttentionTarget = "player_head";
    public string defaultAttentionReason = string.Empty;

    private bool schedulerAllowsAttention;
    private bool policyErrorVisualHold;

    public void SetSchedulerState(bool mayRun, bool policyError, string reason)
    {
        schedulerAllowsAttention = mayRun;
        policyErrorVisualHold = policyError;
        defaultAttentionReason = reason ?? string.Empty;
    }

    private void OnDisable()
    {
        schedulerAllowsAttention = false;
        policyErrorVisualHold = false;
        defaultAttentionRequested = false;
        defaultAttentionControlling = false;
        defaultAttentionActive = false;

        if (lookController != null)
        {
            lookController.SetDefaultAttentionRequested(false);
        }
    }

    private void Update()
    {
        bool mayRun =
            defaultAttentionEnabled &&
            schedulerAllowsAttention &&
            (motionExecutor == null || !motionExecutor.IsBusy) &&
            lookController != null &&
            !policyErrorVisualHold;

        if (defaultAttentionRequested != mayRun && logDefaultAttention)
        {
            Debug.Log("CP_DefaultAttentionController " + (mayRun ? "active" : "inactive") + ": " + defaultAttentionReason, this);
        }

        defaultAttentionRequested = mayRun;

        if (lookController == null)
        {
            defaultAttentionControlling = false;
            defaultAttentionActive = false;
            defaultAttentionRequested = false;
            return;
        }

        lookController.SetDefaultAttentionTarget(defaultAttentionTarget);
        lookController.SetDefaultAttentionRequested(defaultAttentionRequested);

        defaultAttentionControlling = defaultAttentionRequested && lookController.IsDefaultLookControlling;
        defaultAttentionActive = defaultAttentionControlling;
    }
}
