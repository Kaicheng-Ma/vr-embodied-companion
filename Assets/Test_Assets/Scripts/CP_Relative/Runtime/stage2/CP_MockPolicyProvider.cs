using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class CP_MockPolicyProvider : MonoBehaviour
{
    [Header("References")]
    public CP_PerceptionBuilder perceptionBuilder;
    public CP_PolicyReceiver policyReceiver;

    [Header("Mock Settings")]
    public bool mockEnabled = true;
    public float mockPolicyInterval = 3.0f;
    public bool logMockPolicyJson = true;
    public bool sendOnlyWhenExecutorIdle = true;

    [Header("Latest Mock Policy")]
    public CP_PolicyResponse latestMockPolicyResponse = new CP_PolicyResponse();
    [TextArea(8, 30)]
    public string latestMockPolicyJson = string.Empty;
    [TextArea(4, 12)]
    public string latestMockPolicySummary = string.Empty;
    public float latestMockPolicyBuildTimeSeconds;

    private readonly StringBuilder summaryBuilder = new StringBuilder();
    private float nextMockPolicyTime;
    private string lastLoggedMockPolicyJson = string.Empty;

    private const string SchemaVersion = "version_A_1.0.1";

    private void Start()
    {
        nextMockPolicyTime = Time.time;
        TickIfNeeded(true);
    }

    private void Update()
    {
        TickIfNeeded(false);
    }

    private void TickIfNeeded(bool forceTick)
    {
        float interval = Mathf.Max(0.1f, mockPolicyInterval);
        if (!forceTick && Time.time < nextMockPolicyTime)
        {
            return;
        }

        nextMockPolicyTime = Time.time + interval;
        RefreshMockPolicy();
    }

    private void RefreshMockPolicy()
    {
        CP_PerceptionPacket perceptionPacket = null;
        List<string> warnings = new List<string>();

        if (mockEnabled)
        {
            if (perceptionBuilder != null)
            {
                // Refresh the latest Unity-side perception before generating the local fallback policy.
                perceptionPacket = perceptionBuilder.RefreshPerception();
            }
            else
            {
                warnings.Add("CP_MockPolicyProvider: perceptionBuilder is not assigned.");
            }
        }
        else
        {
            warnings.Add("CP_MockPolicyProvider: mockEnabled is false.");
        }

        latestMockPolicyResponse = BuildMockPolicyResponse(perceptionPacket, warnings);
        latestMockPolicyBuildTimeSeconds = Time.time;
        if (policyReceiver == null)
        {
            warnings.Add("CP_MockPolicyProvider: policyReceiver is not assigned.");
        }

        latestMockPolicyResponse.debug.warnings = DeduplicateWarnings(
            latestMockPolicyResponse.debug != null ? latestMockPolicyResponse.debug.warnings : null,
            warnings
        );

        latestMockPolicyJson = JsonUtility.ToJson(latestMockPolicyResponse, true);
        latestMockPolicySummary = BuildPolicySummary(
            latestMockPolicyResponse,
            latestMockPolicyResponse.debug != null && latestMockPolicyResponse.debug.warnings != null
                ? latestMockPolicyResponse.debug.warnings
                : warnings
        );

        if (sendOnlyWhenExecutorIdle && policyReceiver != null && policyReceiver.IsMotionExecutorBusy())
        {
            latestMockPolicySummary += "\nSend Skipped: executor_busy";
            return;
        }

        if (logMockPolicyJson && latestMockPolicyJson != lastLoggedMockPolicyJson)
        {
            Debug.Log($"CP_MockPolicyProvider JSON:\n{latestMockPolicyJson}", this);
            lastLoggedMockPolicyJson = latestMockPolicyJson;
        }

        if (policyReceiver != null)
        {
            policyReceiver.ReceivePolicyJson(latestMockPolicyJson);
        }
    }

    private CP_PolicyResponse BuildMockPolicyResponse(CP_PerceptionPacket perceptionPacket, List<string> warnings)
    {
        CP_PolicyResponse response = new CP_PolicyResponse();
        EnsureResponseSections(response);
        response.response_meta.schema_version = SchemaVersion;
        response.response_meta.request_id = perceptionPacket != null && perceptionPacket.frame != null
            ? perceptionPacket.frame.request_id
            : $"mock_request_{Time.frameCount}";
        response.response_meta.response_id = $"mock_response_{Guid.NewGuid():N}";
        response.debug.provider_name = nameof(CP_MockPolicyProvider);
        response.debug.source_request_id = response.response_meta.request_id;
        response.debug.build_time_utc = DateTime.UtcNow.ToString("o");

        if (!mockEnabled)
        {
            MarkPolicyError(response, "mock_disabled", "mockEnabled is false.");
            response.debug.source_state = CP_StateNames.UnknownNoBlackboard;
            response.state.current_state = CP_StateNames.UnknownNoBlackboard;
            response.state.previous_state = CP_StateNames.UnknownNoBlackboard;
            return response;
        }

        if (perceptionPacket == null)
        {
            MarkPolicyError(response, "missing_perception", "Perception packet is null.");
            response.debug.source_state = CP_StateNames.UnknownNoBlackboard;
            response.state.current_state = CP_StateNames.UnknownNoBlackboard;
            response.state.previous_state = CP_StateNames.UnknownNoBlackboard;
            return response;
        }

        string currentState = perceptionPacket.companion != null
            ? perceptionPacket.companion.current_state
            : CP_StateNames.UnknownNoBlackboard;
        string previousState = perceptionPacket.companion != null
            ? perceptionPacket.companion.previous_state
            : currentState;
        bool stateChanged = perceptionPacket.companion != null && perceptionPacket.companion.state_changed;
        string stateReason = perceptionPacket.companion != null
            ? perceptionPacket.companion.state_reason
            : string.Empty;

        response.debug.source_state = currentState;
        response.state.current_state = currentState;
        response.state.previous_state = previousState;
        response.state.state_changed = stateChanged;
        response.state.state_change_id = perceptionPacket.events != null ? perceptionPacket.events.state_change_id.ToString() : string.Empty;
        response.state.state_reason = stateReason;
        response.control.companion_should_run = true;
        response.control.shutdown_acknowledged = false;
        response.control.execute_policy = true;
        response.policy.policy_source = "fallback";
        response.policy.policy_id = BuildPolicyId(currentState);
        response.policy.policy_lifetime = 4f;
        response.policy.intent_summary = string.Empty;
        response.policy.activity_mode = string.Empty;
        response.motion_primitives.Clear();
        response.queue.has_pending_transition = false;
        response.queue.queue_length = 0;
        response.queue.current_transition_id = string.Empty;
        response.queue.current_task_status = string.Empty;
        response.queue.current_task_goal_type = string.Empty;
        response.queue.current_task_goal_target = string.Empty;
        response.queue.current_task_completed = true;

        if (string.IsNullOrEmpty(currentState) || currentState == CP_StateNames.Unknown || currentState == CP_StateNames.UnknownNoBlackboard)
        {
            MarkPolicyError(response, "unknown_state", "Perception companion state is not ready.");
            return response;
        }

        switch (currentState)
        {
            case CP_StateNames.S1:
                response.policy.activity_mode = "idle_presence";
                response.policy.intent_summary = "S1 idle presence";
                response.motion_primitives.Add(BuildPrimitive("idle_motion", string.Empty, 2.0f, "calm_idle", "S1 idle presence"));
                break;
            case CP_StateNames.F2:
                response.policy.activity_mode = "co_attention";
                response.policy.intent_summary = "F2 co-attention inside fishing interaction area";
                response.motion_primitives.Add(BuildPrimitive("move_within_scope", "interaction_area", 2.0f, "controlled_reposition", "F2 stay inside the fishing interaction area.", "interaction_area"));
                response.motion_primitives.Add(BuildPrimitive("look_at", "water_focus", 1.5f, string.Empty, "F2 co-attention toward the fishing water focus."));
                break;
            case CP_StateNames.F3:
                response.policy.activity_mode = "parallel_engage";
                response.policy.intent_summary = "F3 hold anchor position and observe fishing";
                response.motion_primitives.Add(BuildPrimitive("hold_position", string.Empty, 1.0f, string.Empty, "F3 hold anchor position"));
                response.motion_primitives.Add(BuildPrimitive("look_at", "assigned_fishing_cast_target", 1.5f, string.Empty, "F3 look toward the assigned fishing cast target."));
                break;
            case CP_StateNames.S2:
                response.policy.activity_mode = "co_attention";
                response.policy.intent_summary = "S2 co-attention toward campfire";
                response.motion_primitives.Add(BuildPrimitive("look_at", "campfire_center", 1.5f, string.Empty, "S2 co-attention toward campfire"));
                response.motion_primitives.Add(BuildPrimitive("idle_motion", string.Empty, 2.0f, "calm_idle", "S2 calm waiting"));
                if (!perceptionPacket.scene.campfire_center_available)
                {
                    warnings.Add("CP_MockPolicyProvider: campfire_center is not available yet; legality checker may skip look_at.");
                }
                break;
            case CP_StateNames.S3:
                response.policy.activity_mode = "parallel_engage";
                response.policy.intent_summary = "S3 hold anchor position and look toward campfire";
                response.motion_primitives.Add(BuildPrimitive("hold_position", string.Empty, 1.0f, string.Empty, "S3 hold anchor position"));
                response.motion_primitives.Add(BuildPrimitive("look_at", "campfire_center", 1.5f, string.Empty, "S3 look toward campfire"));
                if (!perceptionPacket.scene.campfire_center_available)
                {
                    warnings.Add("CP_MockPolicyProvider: campfire_center is not available yet; legality checker may skip look_at.");
                }
                break;
            default:
                MarkPolicyError(response, "unsupported_state", $"No mock policy mapping exists for state '{currentState}'.");
                break;
        }

        response.queue.queue_length = 0;

        return response;
    }

    private void EnsureResponseSections(CP_PolicyResponse response)
    {
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

        if (response.allowed_pools.activity_modes == null || response.allowed_pools.activity_modes.Count == 0)
        {
            response.allowed_pools.activity_modes = new List<string>
            {
                "idle_presence",
                "co_attention",
                "parallel_engage"
            };
        }

        if (response.allowed_pools.motion_primitives == null || response.allowed_pools.motion_primitives.Count == 0)
        {
            response.allowed_pools.motion_primitives = new List<string>
            {
                "look_at",
                "face_toward",
                "hold_position",
                "idle_motion"
            };
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
    }

    private CP_MotionPrimitive BuildPrimitive(string type, string target, float durationSeconds, string style, string notes, string scope = "")
    {
        CP_MotionPrimitive primitive = new CP_MotionPrimitive();
        primitive.type = type;
        primitive.target = target;
        primitive.duration = Mathf.Max(0.1f, durationSeconds);
        primitive.style = style;
        primitive.notes = notes;
        primitive.scope = scope;
        return primitive;
    }

    private void MarkPolicyError(CP_PolicyResponse response, string errorCode, string errorMessage)
    {
        EnsureResponseSections(response);
        response.control.companion_should_run = true;
        response.control.shutdown_acknowledged = false;
        response.control.execute_policy = false;
        response.policy.policy_source = "policy_error";
        response.policy.policy_id = "policy_error";
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
        response.debug.error_code = errorCode;
        response.debug.error_message = errorMessage;
        AddUniqueWarnings(response.debug.warnings, new List<string> { errorMessage });
    }

    private List<string> DeduplicateWarnings(List<string> responseWarnings, List<string> extraWarnings)
    {
        List<string> mergedWarnings = new List<string>();
        AddUniqueWarnings(mergedWarnings, responseWarnings);
        AddUniqueWarnings(mergedWarnings, extraWarnings);
        return mergedWarnings;
    }

    private void AddUniqueWarnings(List<string> targetWarnings, List<string> sourceWarnings)
    {
        if (targetWarnings == null || sourceWarnings == null)
        {
            return;
        }

        for (int i = 0; i < sourceWarnings.Count; i++)
        {
            string warning = sourceWarnings[i];
            if (string.IsNullOrWhiteSpace(warning))
            {
                continue;
            }

            if (!targetWarnings.Contains(warning))
            {
                targetWarnings.Add(warning);
            }
        }
    }

    private string BuildPolicySummary(CP_PolicyResponse response, List<string> warnings)
    {
        summaryBuilder.Clear();

        summaryBuilder.AppendLine("Response ID: " + (response != null && response.response_meta != null ? response.response_meta.response_id : string.Empty));
        summaryBuilder.AppendLine("Request ID: " + (response != null && response.response_meta != null ? response.response_meta.request_id : string.Empty));
        summaryBuilder.AppendLine("Policy Source: " + (response != null && response.policy != null ? response.policy.policy_source : string.Empty));
        summaryBuilder.AppendLine("Activity Mode: " + (response != null && response.policy != null ? response.policy.activity_mode : string.Empty));
        summaryBuilder.AppendLine("State: " + (response != null && response.state != null ? response.state.current_state : string.Empty));
        summaryBuilder.AppendLine("Policy ID: " + (response != null && response.policy != null ? response.policy.policy_id : string.Empty));
        summaryBuilder.AppendLine("Execute Policy: " + (response != null && response.control != null && response.control.execute_policy));
        summaryBuilder.AppendLine("Motion Primitives: " + (response != null && response.motion_primitives != null ? response.motion_primitives.Count : 0));
        summaryBuilder.AppendLine("Motion Primitive Count: " + (response != null && response.motion_primitives != null ? response.motion_primitives.Count : 0));
        summaryBuilder.AppendLine("Warnings: " + (warnings != null ? warnings.Count : 0));

        if (response != null && response.debug != null && (!string.IsNullOrEmpty(response.debug.error_code) || !string.IsNullOrEmpty(response.debug.error_message)))
        {
            summaryBuilder.AppendLine("Error Code: " + response.debug.error_code);
            summaryBuilder.AppendLine("Error Message: " + response.debug.error_message);
        }

        return summaryBuilder.ToString();
    }

    private string BuildPolicyId(string currentState)
    {
        string normalizedState = string.IsNullOrWhiteSpace(currentState) ? "unknown" : currentState;
        return $"mock_{normalizedState}_{Time.frameCount}";
    }
}
