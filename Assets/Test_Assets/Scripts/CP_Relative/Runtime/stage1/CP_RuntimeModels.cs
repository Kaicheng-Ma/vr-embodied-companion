using System;
using System.Collections.Generic;
using UnityEngine;

public static class CP_StateNames
{
    // Canonical state labels used by Stage 1 and later policy layers.
    public const string Unknown = "UNKNOWN_STATE";
    public const string UnknownNoBlackboard = "UNKNOWN_NO_BLACKBOARD";
    public const string S1 = "S1_pre_fire_free_companion";
    public const string F2 = "F2_fishing_ready_companion";
    public const string F3 = "F3_fishing_engagement";
    public const string S2 = "S2_fire_ready_companion";
    public const string S3 = "S3_roasting_engagement";
}

[Serializable]
public class CP_TransformSnapshot
{
    public string objectName;
    public bool isAssigned;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 forward;
    public Vector3 up;
    public Vector3 lossyScale;

    public void Capture(string name, Transform source)
    {
        objectName = name;
        isAssigned = source != null;

        if (source == null)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            forward = Vector3.forward;
            up = Vector3.up;
            lossyScale = Vector3.one;
            return;
        }

        position = source.position;
        rotation = source.rotation;
        forward = source.forward;
        up = source.up;
        lossyScale = source.lossyScale;
    }
}

[Serializable]
public class CP_InteractionAreaSnapshot
{
    public bool hasInteractionArea;
    public string areaName;
    public Vector3 areaCenterWorldPosition;
    public float areaRadius;
    public bool useXZDistanceOnly;
    public bool companionInsideArea;
    public float companionDistanceToAreaCenter;
    public Vector3 companionWorldPosition;
}

[Serializable]
public class CP_AnchorSnapshot
{
    public string anchorName;
    public bool isAssigned;
    public Vector3 position;
    public float distanceToPlayerXZ = -1f;
    public float distanceToCompanionXZ = -1f;
    public bool isPlayerOccupied;
    public bool isNearestFree;
    public bool isCompanionAtAnchor;
}

[Serializable]
public class CP_RoastingAnchorInfo
{
    public string id;
    public bool is_assigned;
    public Vector3 position;
    public bool is_occupied_by_player;
    public bool is_valid_for_companion;
    public bool is_nearest_free_anchor;
    public bool is_companion_at_anchor;
    public float distance_to_player_2d = -1f;
    public float distance_to_companion_2d = -1f;
}

[Serializable]
public class CP_FrameSection
{
    public string schema_version;
    public string request_id;
    public int frame_index;
    public float build_time_seconds;
    public float time_since_startup;
    public float delta_time;
    public float unscaled_delta_time;
    // Scheduler metadata is transport context only; it does not change Blackboard truth.
    public string request_role = "current_policy";
    public int policy_index;
    public int current_state_epoch;
}

[Serializable]
public class CP_ControlSection
{
    public bool companion_enabled;
    public bool shutdown_signal;
    public bool ai_enabled;
    public bool fallback_enabled;
    public string scheduler_mode = string.Empty;
    public bool allow_lookahead_while_motion_busy;
    public string state_change_interruption_mode = string.Empty;
}

[Serializable]
public class CP_ActivitySection
{
    public string id = "common";
    public string phase = "free";
    public string detail_phase = "none";
    public bool fishing_completed;
    public string context_key = "common/free";
    public int context_epoch;
}

[Serializable]
public class CP_TaskSection
{
    public bool campfire_started;
    public bool stick_picked;
    public bool marshmallow_skewered;
    public bool marshmallow_in_roasting_zone;
    public bool roasting_duration_reached;
    public bool marshmallow_eaten;
    public float current_roasting_time;
    public float required_roasting_duration;
    public float roasting_progress;
}

[Serializable]
public class CP_PlayerSection
{
    public Vector3 position;
    public Vector3 forward;
    public bool has_player_root;
    public bool has_player_head;
    public string occupied_roasting_anchor_id;
    public string occupied_fishing_anchor_id;
    public float distance_to_campfire = -1f;
    public bool is_inside_interaction_area;
}

[Serializable]
public class CP_CompanionSection
{
    public Vector3 position;
    public Vector3 forward;
    public string current_state;
    public string previous_state;
    public bool state_changed;
    public string state_reason;
    public bool is_inside_interaction_area;
    public bool is_at_valid_roasting_anchor;
    public string nearest_free_roasting_anchor_id;
    public string current_anchor_id;
    public bool is_inside_fishing_interaction_area;
    public bool is_at_fishing_anchor;
    public string nearest_fishing_anchor_id;
    public string assigned_fishing_anchor_id;
    public string assigned_fishing_cast_target_id;
    public bool is_at_valid_fishing_anchor;
    public bool is_moving;
    public int state_epoch;
    public string state_transition_label = string.Empty;
    public string current_hand_pose = string.Empty;
    public string target_hand_pose = string.Empty;
    public bool hand_pose_runtime_available;
    public bool companion_prop_runtime_available;
    public bool fishing_rod_runtime_available;
    public bool has_companion_stick;
    public bool has_companion_marshmallow;
    public bool has_companion_fishing_rod;
    public string companion_stick_state = string.Empty;
    public string companion_marshmallow_state = string.Empty;
    public string companion_fishing_rod_state = string.Empty;
}

[Serializable]
public class CP_SceneSection
{
    public bool campfire_available;
    public bool campfire_center_available;
    public bool interaction_area_available;
    public bool fishing_spatial_ready;
    public bool fishing_cast_targets_ready;
    public List<CP_RoastingAnchorInfo> roasting_anchors = new List<CP_RoastingAnchorInfo>();
    public List<string> look_targets_available = new List<string>();
}

[Serializable]
public class CP_EntryChecksSection
{
    public bool companion_inside_interaction_area;
    public bool companion_at_valid_roasting_anchor;
    public string nearest_free_roasting_anchor_id;
    public string nearest_valid_interaction_area_point_id;
    public bool companion_inside_fishing_interaction_area;
    public bool companion_at_valid_fishing_anchor;
    public bool fishing_rod_ready;
}

[Serializable]
public class CP_EventsSection
{
    public bool state_changed;
    public int state_change_id;
    public List<string> recent_events = new List<string>();
}

[Serializable]
public class CP_PerceptionPacket
{
    public CP_FrameSection frame = new CP_FrameSection();
    public CP_ControlSection control = new CP_ControlSection();
    public CP_ActivitySection activity = new CP_ActivitySection();
    public CP_TaskSection task = new CP_TaskSection();
    public CP_PlayerSection player = new CP_PlayerSection();
    public CP_CompanionSection companion = new CP_CompanionSection();
    public CP_SceneSection scene = new CP_SceneSection();
    public CP_EntryChecksSection entry_checks = new CP_EntryChecksSection();
    public CP_EventsSection events = new CP_EventsSection();
}

[Serializable]
public class CP_PolicyResponseMetaSection
{
    public string schema_version = "version_A_1.0.1";
    public string response_id = string.Empty;
    public string request_id = string.Empty;
}

[Serializable]
public class CP_PolicyResponse
{
    public CP_PolicyResponseMetaSection response_meta = new CP_PolicyResponseMetaSection();
    public CP_PolicyControlSection control = new CP_PolicyControlSection();
    public CP_PolicyStateSection state = new CP_PolicyStateSection();
    public CP_PolicySection policy = new CP_PolicySection();
    public CP_AllowedPoolsSection allowed_pools = new CP_AllowedPoolsSection();
    public List<CP_MotionPrimitive> motion_primitives = new List<CP_MotionPrimitive>();
    public CP_PolicyQueueSection queue = new CP_PolicyQueueSection();
    public CP_PolicyDebugSection debug = new CP_PolicyDebugSection();
}

[Serializable]
public class CP_PolicyControlSection
{
    public bool companion_should_run = true;
    public bool shutdown_acknowledged;
    public bool execute_policy = true;
    public bool policy_error;
}

[Serializable]
public class CP_PolicyStateSection
{
    public string current_state = CP_StateNames.Unknown;
    public string previous_state = CP_StateNames.Unknown;
    public bool state_changed;
    public string state_change_id = string.Empty;
    public string state_reason = string.Empty;
}

[Serializable]
public class CP_PolicySection
{
    public string policy_id = string.Empty;
    public string policy_source = "fallback";
    public string activity_mode = string.Empty;
    public float policy_lifetime = 4f;
    public string intent_summary = string.Empty;
}

[Serializable]
public class CP_AllowedPoolsSection
{
    public List<string> activity_modes = new List<string>();
    public List<string> motion_primitives = new List<string>();
    public List<string> allowed_targets = new List<string>();
    public List<string> forbidden_motion_primitives = new List<string>();
    public List<string> allowed_scopes = new List<string>();
    public List<string> allowed_poses = new List<string>();
    public List<string> allowed_props = new List<string>();
    public List<string> allowed_gestures = new List<string>();
}

[Serializable]
public class CP_MotionPrimitive
{
    public string type = string.Empty;
    public string target = string.Empty;
    public float duration = 1f;
    public string style = string.Empty;
    public string notes = string.Empty;
    public string scope = string.Empty;
    public float radius_min;
    public float radius_max;
    public string pose = string.Empty;
    public string prop = string.Empty;
    public string state = string.Empty;
    public string gesture = string.Empty;
}

[Serializable]
public class CP_PolicyQueueSection
{
    public bool has_pending_transition;
    public int queue_length;
    public string current_transition_id = string.Empty;
    public string current_task_status = string.Empty;
    public string current_task_goal_type = string.Empty;
    public string current_task_goal_target = string.Empty;
    public bool current_task_completed = true;
}

[Serializable]
public class CP_PolicyDebugSection
{
    public string provider_name = string.Empty;
    public string source_request_id = string.Empty;
    public string source_state = string.Empty;
    public string build_time_utc = string.Empty;
    public string error_code = string.Empty;
    public string error_message = string.Empty;
    public bool ai_enabled;
    public bool fallback_enabled;
    public bool force_fallback_for_test;
    public string ai_status = string.Empty;
    public string ai_error_status = string.Empty;
    public string ai_error_message = string.Empty;
    public string provider = string.Empty;
    public string model = string.Empty;
    public float latency_ms;
    public List<string> validator_error_codes = new List<string>();
    public List<string> validator_warnings = new List<string>();
    public CP_PolicyValidatorDebugSection validator_debug = new CP_PolicyValidatorDebugSection();
    public string fallback_reason = string.Empty;
    public string request_role = string.Empty;
    public int source_state_epoch = -1;
    public int source_policy_index = -1;
    public string ai_job_status = string.Empty;
    public string pending_policy_status = string.Empty;
    public bool state_change_invalidated_policy;
    public string fallback_status = string.Empty;
    public List<string> warnings = new List<string>();
}

[Serializable]
public class CP_PolicyValidatorDebugSection
{
    public string current_state = string.Empty;
    public List<string> allowed_activity_modes = new List<string>();
    public List<string> allowed_motion_primitives = new List<string>();
    public List<string> allowed_targets = new List<string>();
    public List<string> forbidden_motion_primitives = new List<string>();
    public List<string> allowed_scopes = new List<string>();
    public List<string> allowed_poses = new List<string>();
    public List<string> allowed_props = new List<string>();
    public List<string> allowed_gestures = new List<string>();
    public string state_description = string.Empty;
}

[Serializable]
public class CP_PerceptionSnapshot
{
    public string currentStateName = CP_StateNames.UnknownNoBlackboard;
    public string previousStateName = CP_StateNames.UnknownNoBlackboard;
    public string stateReason = string.Empty;
    public float buildTimeSeconds;

    public bool campfireStarted;
    public bool stickPicked;
    public bool marshmallowSkewered;
    public bool marshmallowInRoastingZone;
    public bool roastingDurationReached;
    public bool marshmallowEaten;
    public float currentRoastingTime;
    public float requiredRoastingDuration;

    public CP_TransformSnapshot blackboardCampfireSpawnPoint = new CP_TransformSnapshot();
    public CP_TransformSnapshot blackboardCampfireRuntimeParent = new CP_TransformSnapshot();
    public CP_TransformSnapshot currentCampfireInstance = new CP_TransformSnapshot();
    public bool currentCampfireHasFireCenterPoint;
    public CP_TransformSnapshot currentCampfireFireCenterPoint = new CP_TransformSnapshot();

    public CP_TransformSnapshot stickObject = new CP_TransformSnapshot();
    public CP_TransformSnapshot marshmallowTipAnchor = new CP_TransformSnapshot();
    public CP_TransformSnapshot currentMarshmallowInstance = new CP_TransformSnapshot();

    public CP_TransformSnapshot playerRoot = new CP_TransformSnapshot();
    public CP_TransformSnapshot playerHead = new CP_TransformSnapshot();
    public CP_TransformSnapshot companionRoot = new CP_TransformSnapshot();

    public CP_InteractionAreaSnapshot interactionArea = new CP_InteractionAreaSnapshot();
    public List<CP_AnchorSnapshot> roastingAnchors = new List<CP_AnchorSnapshot>();
    public List<string> warnings = new List<string>();
}
