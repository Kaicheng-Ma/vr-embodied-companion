using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class CP_PolicyReceiver : MonoBehaviour
{
    [Header("References")]
    public CP_LegalityChecker legalityChecker;
    public CP_MotionExecutor motionExecutor;
    public CP_PolicyScheduler policyScheduler;
    public bool schedulerOwnershipEnabled;

    [Header("Latest Policy")]
    [TextArea(8, 30)]
    public string latestPolicyJson = string.Empty;
    [TextArea(4, 12)]
    public string latestPolicySummary = string.Empty;
    [TextArea(8, 30)]
    public string latestReceivedPolicyJson = string.Empty;
    [TextArea(4, 12)]
    public string latestReceivedPolicySummary = string.Empty;
    public CP_PolicyResponse latestReceivedPolicyResponse = new CP_PolicyResponse();
    public CP_PolicyResponse latestPolicyResponse = new CP_PolicyResponse();
    public float latestPolicyReceivedTimeSeconds;

    [Header("Logging")]
    public bool logPolicyWarnings = true;
    public bool logPolicyErrors = true;

    private readonly StringBuilder summaryBuilder = new StringBuilder();

    public void ReceivePolicyJson(string policyJson)
    {
        if (schedulerOwnershipEnabled)
        {
            if (logPolicyErrors)
            {
                Debug.LogWarning("CP_PolicyReceiver rejected legacy direct receive while CP_PolicyScheduler owns routing.", this);
            }
            return;
        }
        TryPreparePolicyJson(policyJson, out CP_PolicyResponse preparedPolicy, out _);
        TryExecutePreparedPolicy(preparedPolicy, out _);
    }

    // Scheduler uses this path to parse and sanitize a response without replacing active execution.
    public bool TryPreparePolicyJson(string policyJson, out CP_PolicyResponse preparedPolicy, out string failureReason)
    {
        preparedPolicy = null;
        failureReason = string.Empty;
        latestPolicyJson = policyJson ?? string.Empty;
        latestPolicyReceivedTimeSeconds = Time.time;

        if (string.IsNullOrWhiteSpace(policyJson))
        {
            CP_PolicyResponse invalidResponse = CreateLocalInvalidResponse("empty_policy_json", "Policy JSON is empty.");
            preparedPolicy = invalidResponse;
            failureReason = invalidResponse.debug.error_message;
            UpdateLocalInvalidDebugSnapshot(invalidResponse);
            return false;
        }

        CP_PolicyResponse parsedPolicy = null;
        try
        {
            parsedPolicy = JsonUtility.FromJson<CP_PolicyResponse>(policyJson);
        }
        catch (System.Exception ex)
        {
            CP_PolicyResponse invalidResponse = CreateLocalInvalidResponse("parse_failed", $"JsonUtility parse failed: {ex.Message}");
            preparedPolicy = invalidResponse;
            failureReason = invalidResponse.debug.error_message;
            UpdateLocalInvalidDebugSnapshot(invalidResponse);
            if (logPolicyErrors)
            {
                Debug.LogWarning($"CP_PolicyReceiver parse_failed: {ex.Message}", this);
            }
            return false;
        }

        if (parsedPolicy == null)
        {
            CP_PolicyResponse invalidResponse = CreateLocalInvalidResponse("parse_failed", "JsonUtility returned a null policy object.");
            preparedPolicy = invalidResponse;
            failureReason = invalidResponse.debug.error_message;
            UpdateLocalInvalidDebugSnapshot(invalidResponse);
            return false;
        }

        EnsurePolicyResponseSections(parsedPolicy);
        latestPolicyJson = JsonUtility.ToJson(parsedPolicy, true);
        latestReceivedPolicyJson = latestPolicyJson;
        latestReceivedPolicySummary = BuildPolicySummary(parsedPolicy, parsedPolicy.debug != null ? parsedPolicy.debug.warnings.Count : 0);
        latestReceivedPolicyResponse = parsedPolicy;
        latestPolicyResponse = parsedPolicy;

        if (parsedPolicy.control != null && (!parsedPolicy.control.execute_policy || (parsedPolicy.policy != null && parsedPolicy.policy.policy_source == "policy_error")))
        {
            latestPolicySummary = BuildPolicySummary(parsedPolicy, parsedPolicy.debug != null ? parsedPolicy.debug.warnings.Count : 0);
            preparedPolicy = parsedPolicy;
            failureReason = parsedPolicy.debug != null ? parsedPolicy.debug.error_message : "policy_not_executable";
            if (logPolicyErrors && parsedPolicy.policy != null && parsedPolicy.policy.policy_source == "policy_error" && !string.IsNullOrEmpty(parsedPolicy.debug.error_message))
            {
                Debug.LogWarning($"CP_PolicyReceiver policy_error: {parsedPolicy.debug.error_message}", this);
            }

            return false;
        }

        if (legalityChecker == null)
        {
            latestPolicySummary = BuildPolicySummary(parsedPolicy, parsedPolicy.debug != null ? parsedPolicy.debug.warnings.Count : 0);
            if (logPolicyErrors)
            {
                Debug.LogWarning("CP_PolicyReceiver: legalityChecker is not assigned.", this);
            }

            preparedPolicy = parsedPolicy;
            failureReason = "legality_checker_missing";
            return false;
        }

        CP_PolicyResponse sanitizedPolicy = legalityChecker.SanitizePolicy(parsedPolicy, out List<string> legalityWarnings);
        EnsurePolicyResponseSections(sanitizedPolicy);
        MergeAiDebugSections(sanitizedPolicy, parsedPolicy);

        if (sanitizedPolicy.debug == null)
        {
            sanitizedPolicy.debug = new CP_PolicyDebugSection();
        }

        if (sanitizedPolicy.debug.warnings == null)
        {
            sanitizedPolicy.debug.warnings = new List<string>();
        }

        if (legalityWarnings != null && legalityWarnings.Count > 0)
        {
            sanitizedPolicy.debug.warnings.AddRange(legalityWarnings);

            if (logPolicyWarnings)
            {
                for (int i = 0; i < legalityWarnings.Count; i++)
                {
                    Debug.LogWarning(legalityWarnings[i], this);
                }
            }
        }

        latestPolicyResponse = sanitizedPolicy;
        latestPolicyJson = JsonUtility.ToJson(sanitizedPolicy, true);
        latestPolicySummary = BuildPolicySummary(sanitizedPolicy, sanitizedPolicy.debug.warnings.Count);
        preparedPolicy = sanitizedPolicy;

        if (motionExecutor == null)
        {
            if (logPolicyErrors)
            {
                Debug.LogWarning("CP_PolicyReceiver: motionExecutor is not assigned.", this);
            }

            failureReason = "motion_executor_missing";
            return false;
        }

        if (sanitizedPolicy.motion_primitives == null || sanitizedPolicy.motion_primitives.Count == 0)
        {
            failureReason = "no_valid_motion_primitives";
            return false;
        }

        return true;
    }

    public bool TryExecutePreparedPolicy(CP_PolicyResponse preparedPolicy, out string failureReason)
    {
        failureReason = string.Empty;

        if (preparedPolicy == null)
        {
            failureReason = "prepared_policy_missing";
            if (logPolicyErrors)
            {
                Debug.LogWarning("CP_PolicyReceiver received a null prepared policy.", this);
            }
            return false;
        }

        EnsurePolicyResponseSections(preparedPolicy);
        latestPolicyResponse = preparedPolicy;
        latestPolicyJson = JsonUtility.ToJson(preparedPolicy, true);
        latestPolicySummary = BuildPolicySummary(preparedPolicy, preparedPolicy.debug.warnings.Count);

        if (IsLocalInvalidResponse(preparedPolicy))
        {
            failureReason = GetPreparedPolicyFailureReason(preparedPolicy, "local_invalid_response");
            return false;
        }

        if (IsShutdownResponse(preparedPolicy))
        {
            failureReason = GetPreparedPolicyFailureReason(preparedPolicy, "shutdown");
            StopMotionExecution(failureReason);
            return false;
        }

        if (IsFormalPolicyError(preparedPolicy))
        {
            failureReason = GetPreparedPolicyFailureReason(preparedPolicy, "policy_error");
            StopMotionExecution(failureReason);
            return false;
        }

        if (!preparedPolicy.control.execute_policy)
        {
            failureReason = GetPreparedPolicyFailureReason(preparedPolicy, "execute_policy_false");
            if (ShouldStopMotionForNonExecutablePolicy(preparedPolicy))
            {
                StopMotionExecution(failureReason);
            }
            return false;
        }

        if (motionExecutor == null)
        {
            failureReason = "motion_executor_missing";
            StopMotionExecution(failureReason);
            return false;
        }

        if (preparedPolicy.motion_primitives == null || preparedPolicy.motion_primitives.Count == 0)
        {
            failureReason = "no_valid_motion_primitives";
            StopMotionExecution(failureReason);
            return false;
        }

        if (schedulerOwnershipEnabled)
        {
            bool loaded = motionExecutor.TryLoadPolicyWhenIdle(preparedPolicy);
            if (!loaded)
            {
                failureReason = "motion_executor_busy";
                if (logPolicyErrors)
                {
                    Debug.LogWarning("CP_PolicyReceiver refused scheduled policy because MotionExecutor is busy.", this);
                }
            }
            return loaded;
        }

        motionExecutor.LoadPolicy(preparedPolicy);
        return true;
    }

    public void ExecutePreparedPolicy(CP_PolicyResponse preparedPolicy)
    {
        TryExecutePreparedPolicy(preparedPolicy, out _);
    }

    public void ReceivePolicy(CP_PolicyResponse policyResponse)
    {
        if (policyResponse == null)
        {
            ReceivePolicyJson(string.Empty);
            return;
        }

        ReceivePolicyJson(JsonUtility.ToJson(policyResponse, true));
    }

    public bool IsMotionExecutorBusy()
    {
        return motionExecutor != null && motionExecutor.IsBusy;
    }

    private void StopMotionExecution(string reason)
    {
        if (motionExecutor != null)
        {
            motionExecutor.StopExecution(reason);
        }
    }

    private CP_PolicyResponse CreateLocalInvalidResponse(string errorCode, string errorMessage)
    {
        CP_PolicyResponse response = new CP_PolicyResponse();
        EnsurePolicyResponseSections(response);
        ApplyPreviousDebugContext(response);
        response.response_meta.schema_version = "version_A_1.0.1";
        response.response_meta.response_id = $"receiver_invalid_{Time.frameCount}";
        response.response_meta.request_id = string.Empty;
        response.control.execute_policy = false;
        response.control.companion_should_run = true;
        response.control.shutdown_acknowledged = false;
        response.control.policy_error = false;
        response.policy.policy_source = string.Empty;
        response.policy.policy_id = "receiver_invalid";
        response.policy.activity_mode = string.Empty;
        response.policy.policy_lifetime = 0f;
        response.policy.intent_summary = errorMessage;
        response.motion_primitives.Clear();
        response.queue.has_pending_transition = false;
        response.queue.queue_length = 0;
        response.queue.current_transition_id = string.Empty;
        response.queue.current_task_status = string.Empty;
        response.queue.current_task_goal_type = string.Empty;
        response.queue.current_task_goal_target = string.Empty;
        response.queue.current_task_completed = true;
        response.debug.provider_name = nameof(CP_PolicyReceiver);
        response.debug.error_code = errorCode;
        response.debug.error_message = errorMessage;
        response.debug.ai_status = "local_invalid_response";
        response.debug.ai_error_status = errorCode;
        response.debug.ai_error_message = errorMessage;
        response.debug.fallback_reason = string.Empty;
        response.debug.validator_error_codes.Clear();
        response.debug.validator_warnings.Clear();
        response.debug.validator_debug = new CP_PolicyValidatorDebugSection();
        response.debug.warnings.Clear();
        response.debug.warnings.Add(errorMessage);
        return response;
    }

    private bool ShouldStopMotionForNonExecutablePolicy(CP_PolicyResponse preparedPolicy)
    {
        return IsShutdownResponse(preparedPolicy) || IsFormalPolicyError(preparedPolicy);
    }

    private void UpdateLocalInvalidDebugSnapshot(CP_PolicyResponse invalidResponse)
    {
        if (invalidResponse == null)
        {
            return;
        }

        latestPolicyJson = JsonUtility.ToJson(invalidResponse, true);
        latestPolicySummary = BuildPolicySummary(invalidResponse, invalidResponse.debug != null && invalidResponse.debug.warnings != null
            ? invalidResponse.debug.warnings.Count
            : 0);
    }

    private void ApplyPreviousDebugContext(CP_PolicyResponse response)
    {
        if (response == null || response.debug == null || latestPolicyResponse == null || latestPolicyResponse.debug == null)
        {
            return;
        }

        CP_PolicyDebugSection previous = latestPolicyResponse.debug;
        response.debug.ai_enabled = previous.ai_enabled;
        response.debug.fallback_enabled = previous.fallback_enabled;
        response.debug.force_fallback_for_test = previous.force_fallback_for_test;
        response.debug.provider = previous.provider;
        response.debug.model = previous.model;
        response.debug.latency_ms = previous.latency_ms;
        response.debug.request_role = previous.request_role;
        response.debug.source_state_epoch = previous.source_state_epoch;
        response.debug.source_policy_index = previous.source_policy_index;
        response.debug.ai_job_status = previous.ai_job_status;
        response.debug.pending_policy_status = previous.pending_policy_status;
        response.debug.state_change_invalidated_policy = previous.state_change_invalidated_policy;
        response.debug.fallback_status = previous.fallback_status;
        response.debug.validator_error_codes = previous.validator_error_codes != null
            ? new List<string>(previous.validator_error_codes)
            : new List<string>();
        response.debug.validator_warnings = previous.validator_warnings != null
            ? new List<string>(previous.validator_warnings)
            : new List<string>();

        if (previous.validator_debug != null)
        {
            response.debug.validator_debug = new CP_PolicyValidatorDebugSection
            {
                current_state = previous.validator_debug.current_state,
                allowed_activity_modes = previous.validator_debug.allowed_activity_modes != null ? new List<string>(previous.validator_debug.allowed_activity_modes) : new List<string>(),
                allowed_motion_primitives = previous.validator_debug.allowed_motion_primitives != null ? new List<string>(previous.validator_debug.allowed_motion_primitives) : new List<string>(),
                allowed_targets = previous.validator_debug.allowed_targets != null ? new List<string>(previous.validator_debug.allowed_targets) : new List<string>(),
                forbidden_motion_primitives = previous.validator_debug.forbidden_motion_primitives != null ? new List<string>(previous.validator_debug.forbidden_motion_primitives) : new List<string>(),
                allowed_scopes = previous.validator_debug.allowed_scopes != null ? new List<string>(previous.validator_debug.allowed_scopes) : new List<string>(),
                allowed_poses = previous.validator_debug.allowed_poses != null ? new List<string>(previous.validator_debug.allowed_poses) : new List<string>(),
                allowed_props = previous.validator_debug.allowed_props != null ? new List<string>(previous.validator_debug.allowed_props) : new List<string>(),
                allowed_gestures = previous.validator_debug.allowed_gestures != null ? new List<string>(previous.validator_debug.allowed_gestures) : new List<string>(),
                state_description = previous.validator_debug.state_description
            };
        }
    }

    private bool IsLocalInvalidResponse(CP_PolicyResponse preparedPolicy)
    {
        return IsLocalUnityParseFailure(preparedPolicy);
    }

    private bool IsShutdownResponse(CP_PolicyResponse preparedPolicy)
    {
        return preparedPolicy != null &&
               preparedPolicy.control != null &&
               preparedPolicy.control.shutdown_acknowledged;
    }

    private bool IsFormalPolicyError(CP_PolicyResponse preparedPolicy)
    {
        return preparedPolicy != null &&
               ((preparedPolicy.control != null && preparedPolicy.control.policy_error) ||
                (preparedPolicy.policy != null && preparedPolicy.policy.policy_source == "policy_error"));
    }

    private bool IsLocalUnityParseFailure(CP_PolicyResponse preparedPolicy)
    {
        return preparedPolicy != null &&
               preparedPolicy.debug != null &&
               (preparedPolicy.debug.error_code == "parse_failed" || preparedPolicy.debug.error_code == "empty_policy_json");
    }

    private string GetPreparedPolicyFailureReason(CP_PolicyResponse preparedPolicy, string fallbackReason)
    {
        if (preparedPolicy != null && preparedPolicy.debug != null && !string.IsNullOrWhiteSpace(preparedPolicy.debug.error_message))
        {
            return preparedPolicy.debug.error_message;
        }

        if (preparedPolicy != null && preparedPolicy.debug != null && !string.IsNullOrWhiteSpace(preparedPolicy.debug.error_code))
        {
            return preparedPolicy.debug.error_code;
        }

        return fallbackReason;
    }

    private void EnsurePolicyResponseSections(CP_PolicyResponse response)
    {
        if (response == null)
        {
            return;
        }

        if (response.response_meta == null)
        {
            response.response_meta = new CP_PolicyResponseMetaSection();
        }

        if (response.control == null)
        {
            response.control = new CP_PolicyControlSection();
        }

        if (response.state == null)
        {
            response.state = new CP_PolicyStateSection();
        }

        if (response.policy == null)
        {
            response.policy = new CP_PolicySection();
        }

        if (response.allowed_pools == null)
        {
            response.allowed_pools = new CP_AllowedPoolsSection();
        }

        if (response.motion_primitives == null)
        {
            response.motion_primitives = new List<CP_MotionPrimitive>();
        }

        if (response.queue == null)
        {
            response.queue = new CP_PolicyQueueSection();
        }

        if (response.debug == null)
        {
            response.debug = new CP_PolicyDebugSection();
        }

        if (response.debug.warnings == null)
        {
            response.debug.warnings = new List<string>();
        }

        if (response.debug.validator_error_codes == null)
        {
            response.debug.validator_error_codes = new List<string>();
        }

        if (response.debug.validator_warnings == null)
        {
            response.debug.validator_warnings = new List<string>();
        }

        if (response.debug.validator_debug == null)
        {
            response.debug.validator_debug = new CP_PolicyValidatorDebugSection();
        }
    }

    private string BuildPolicySummary(CP_PolicyResponse response, int warningCount)
    {
        summaryBuilder.Clear();

        summaryBuilder.AppendLine("Response ID: " + (response != null && response.response_meta != null ? response.response_meta.response_id : string.Empty));
        summaryBuilder.AppendLine("Request ID: " + (response != null && response.response_meta != null ? response.response_meta.request_id : string.Empty));
        summaryBuilder.AppendLine("Policy ID: " + (response != null && response.policy != null ? response.policy.policy_id : string.Empty));
        summaryBuilder.AppendLine("Policy Source: " + (response != null && response.policy != null ? response.policy.policy_source : string.Empty));
        summaryBuilder.AppendLine("Activity Mode: " + (response != null && response.policy != null ? response.policy.activity_mode : string.Empty));
        summaryBuilder.AppendLine("State: " + (response != null && response.state != null ? response.state.current_state : string.Empty));
        summaryBuilder.AppendLine("Execute Policy: " + (response != null && response.control != null && response.control.execute_policy));
        summaryBuilder.AppendLine("Motion Primitives: " + (response != null && response.motion_primitives != null ? response.motion_primitives.Count : 0));
        summaryBuilder.AppendLine("Transition Queue Length: " + (response != null && response.queue != null ? response.queue.queue_length : 0));
        summaryBuilder.AppendLine("Policy Error: " + (response != null && response.control != null && response.control.policy_error));
        summaryBuilder.AppendLine("Server Name: " + (response != null && response.debug != null ? response.debug.provider_name : string.Empty));
        summaryBuilder.AppendLine("AI Enabled: " + (response != null && response.debug != null && response.debug.ai_enabled));
        summaryBuilder.AppendLine("Fallback Enabled: " + (response != null && response.debug != null && response.debug.fallback_enabled));
        summaryBuilder.AppendLine("Force Fallback For Test: " + (response != null && response.debug != null && response.debug.force_fallback_for_test));
        summaryBuilder.AppendLine("AI Status: " + (response != null && response.debug != null ? response.debug.ai_status : string.Empty));
        summaryBuilder.AppendLine("AI Error Status: " + (response != null && response.debug != null ? response.debug.ai_error_status : string.Empty));
        summaryBuilder.AppendLine("AI Error Message: " + (response != null && response.debug != null ? response.debug.ai_error_message : string.Empty));
        summaryBuilder.AppendLine("AI Provider: " + (response != null && response.debug != null ? response.debug.provider : string.Empty));
        summaryBuilder.AppendLine("AI Model: " + (response != null && response.debug != null ? response.debug.model : string.Empty));
        summaryBuilder.AppendLine("AI Latency (ms): " + (response != null && response.debug != null ? response.debug.latency_ms.ToString("F0") : "0"));
        summaryBuilder.AppendLine("Fallback Reason: " + (response != null && response.debug != null ? response.debug.fallback_reason : string.Empty));
        summaryBuilder.AppendLine("Validator Error Codes: " + JoinOrNone(response != null && response.debug != null ? response.debug.validator_error_codes : null));
        summaryBuilder.AppendLine("Validator Warnings: " + JoinOrNone(response != null && response.debug != null ? response.debug.validator_warnings : null));
        summaryBuilder.AppendLine("Policy Warnings: " + JoinOrNone(response != null && response.debug != null ? response.debug.warnings : null));
        summaryBuilder.AppendLine("Validator Debug State: " + (response != null && response.debug != null && response.debug.validator_debug != null ? response.debug.validator_debug.current_state : string.Empty));
        summaryBuilder.AppendLine("Warnings: " + warningCount);

        if (response != null && response.debug != null && (!string.IsNullOrEmpty(response.debug.error_code) || !string.IsNullOrEmpty(response.debug.error_message)))
        {
            summaryBuilder.AppendLine("Error Code: " + response.debug.error_code);
            summaryBuilder.AppendLine("Error Message: " + response.debug.error_message);
        }

        return summaryBuilder.ToString();
    }

    private void MergeAiDebugSections(CP_PolicyResponse target, CP_PolicyResponse source)
    {
        if (target == null || source == null || target.debug == null || source.debug == null)
        {
            return;
        }

        target.debug.ai_enabled = source.debug.ai_enabled;
        target.debug.fallback_enabled = source.debug.fallback_enabled;
        target.debug.force_fallback_for_test = source.debug.force_fallback_for_test;
        target.debug.ai_status = source.debug.ai_status;
        target.debug.ai_error_status = source.debug.ai_error_status;
        target.debug.ai_error_message = source.debug.ai_error_message;
        target.debug.provider = source.debug.provider;
        target.debug.model = source.debug.model;
        target.debug.latency_ms = source.debug.latency_ms;
        target.debug.fallback_reason = source.debug.fallback_reason;
        target.debug.request_role = source.debug.request_role;
        target.debug.source_state_epoch = source.debug.source_state_epoch;
        target.debug.source_policy_index = source.debug.source_policy_index;
        target.debug.ai_job_status = source.debug.ai_job_status;
        target.debug.pending_policy_status = source.debug.pending_policy_status;
        target.debug.state_change_invalidated_policy = source.debug.state_change_invalidated_policy;
        target.debug.fallback_status = source.debug.fallback_status;

        target.debug.validator_error_codes = source.debug.validator_error_codes != null
            ? new List<string>(source.debug.validator_error_codes)
            : new List<string>();
        target.debug.validator_warnings = source.debug.validator_warnings != null
            ? new List<string>(source.debug.validator_warnings)
            : new List<string>();

        if (source.debug.validator_debug != null)
        {
            target.debug.validator_debug = new CP_PolicyValidatorDebugSection
            {
                current_state = source.debug.validator_debug.current_state,
                allowed_activity_modes = source.debug.validator_debug.allowed_activity_modes != null ? new List<string>(source.debug.validator_debug.allowed_activity_modes) : new List<string>(),
                allowed_motion_primitives = source.debug.validator_debug.allowed_motion_primitives != null ? new List<string>(source.debug.validator_debug.allowed_motion_primitives) : new List<string>(),
                allowed_targets = source.debug.validator_debug.allowed_targets != null ? new List<string>(source.debug.validator_debug.allowed_targets) : new List<string>(),
                forbidden_motion_primitives = source.debug.validator_debug.forbidden_motion_primitives != null ? new List<string>(source.debug.validator_debug.forbidden_motion_primitives) : new List<string>(),
                allowed_scopes = source.debug.validator_debug.allowed_scopes != null ? new List<string>(source.debug.validator_debug.allowed_scopes) : new List<string>(),
                allowed_poses = source.debug.validator_debug.allowed_poses != null ? new List<string>(source.debug.validator_debug.allowed_poses) : new List<string>(),
                allowed_props = source.debug.validator_debug.allowed_props != null ? new List<string>(source.debug.validator_debug.allowed_props) : new List<string>(),
                allowed_gestures = source.debug.validator_debug.allowed_gestures != null ? new List<string>(source.debug.validator_debug.allowed_gestures) : new List<string>(),
                state_description = source.debug.validator_debug.state_description
            };
        }
        else if (target.debug.validator_debug == null)
        {
            target.debug.validator_debug = new CP_PolicyValidatorDebugSection();
        }
    }

    private string JoinOrNone(List<string> values)
    {
        if (values == null || values.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", values);
    }
}
