using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class CP_PerceptionBuilder : MonoBehaviour
{
    [Header("References")]
    public RoastingStateBlackboard blackboard;
    public CP_InteractionArea interactionArea;
    public CP_RoastingAnchorManager roastingAnchorManager;
    public CP_FishingInteractionArea fishingInteractionArea;
    public CP_FishingAnchorManager fishingAnchorManager;
    public Transform playerRoot;
    public Transform playerHead;
    public Transform companionRoot;
    public CP_HandPoseController handPoseController;
    public CP_PropController propController;
    public CP_ActivityContextRouter activityContextRouter;

    [Header("Optional Look Targets")]
    [Tooltip("Legacy compatibility reference only. Stage 5B-2 companion_marshmallow availability comes from CP_PropController, not this Transform.")]
    public Transform companionMarshmallowTarget;
    public Transform environmentDetailTarget;

    [Header("Control")]
    public bool companionEnabled = true;
    public bool shutdownSignal = false;
    public bool aiEnabled = true;
    public bool fallbackEnabled = true;

    [Header("Stage 4B Scheduling Context")]
    public string requestRole = "current_policy";
    public int policyIndex;
    public int currentStateEpoch;
    public string schedulerMode = string.Empty;
    public bool allowLookaheadWhileMotionBusy;
    public string stateChangeInterruptionMode = string.Empty;
    public string stateTransitionLabel = string.Empty;

    [Header("Capture")]
    public bool captureOnStart = true;
    public bool captureContinuously = true;
    public bool logWarnings = true;
    public bool includeSnapshotJson = true;
    public bool prettyPrintJson = false;

    [Header("Latest Perception Packet")]
    public CP_PerceptionPacket latestPerceptionPacket = new CP_PerceptionPacket();
    public string latestPerceptionSummary = string.Empty;
    public string latestPerceptionJson = string.Empty;
    public float latestPerceptionBuildTimeSeconds;

    [Header("Latest Legacy Debug Snapshot")]
    public CP_PerceptionSnapshot latestSnapshot = new CP_PerceptionSnapshot();
    public string latestSnapshotSummary = string.Empty;
    public string latestWarningsText = string.Empty;
    public string latestSnapshotJson = string.Empty;
    public float latestSnapshotBuildTimeSeconds;

    [Header("Fishing C2 Runtime Read Only")]
    [SerializeField] private string fishingDebugActivity = string.Empty;
    [SerializeField] private string fishingDebugPhase = string.Empty;
    [SerializeField] private bool fishingDebugSpatialReady;
    [SerializeField] private bool fishingDebugInsideInteractionArea;
    [SerializeField] private bool fishingDebugF2EntrySatisfied;
    [SerializeField] private string fishingDebugPlayerAnchor = string.Empty;
    [SerializeField] private string fishingDebugAssignedAnchor = string.Empty;
    [SerializeField] private string fishingDebugAssignedCastTarget = string.Empty;
    [SerializeField] private bool fishingDebugAtAssignedAnchor;
    [SerializeField] private bool fishingDebugF3EntrySatisfied;
    [SerializeField] private bool fishingDebugRodReady;

    private readonly StringBuilder summaryBuilder = new StringBuilder();
    private readonly StringBuilder warningBuilder = new StringBuilder();
    private readonly List<string> recentEvents = new List<string>();
    private string lastResolvedStateName = CP_StateNames.UnknownNoBlackboard;
    private bool hasResolvedStateName;
    private int stateChangeId;
    private string lastLoggedWarningsText = string.Empty;

    private const int MaxRecentEvents = 8;
    private const string SchemaVersion = "version_A_1.0.1";

    private void Start()
    {
        if (captureOnStart)
        {
            RefreshPerception();
        }
    }

    private void Update()
    {
        if (captureContinuously)
        {
            RefreshPerception();
        }
    }

    public CP_PerceptionPacket RefreshPerception()
    {
        latestPerceptionPacket = BuildPerceptionPacket();
        UpdateFishingC2DebugFields(latestPerceptionPacket);
        latestPerceptionSummary = BuildPerceptionSummary(latestPerceptionPacket);
        latestPerceptionBuildTimeSeconds = latestPerceptionPacket.frame.build_time_seconds;
        latestPerceptionJson = includeSnapshotJson
            ? JsonUtility.ToJson(latestPerceptionPacket, prettyPrintJson)
            : string.Empty;

        List<string> warnings = BuildPerceptionWarnings(latestPerceptionPacket);
        latestSnapshot = BuildLegacySnapshot(latestPerceptionPacket, warnings);
        latestSnapshotSummary = latestPerceptionSummary;
        latestWarningsText = BuildWarningsText(warnings);
        latestSnapshotBuildTimeSeconds = latestSnapshot.buildTimeSeconds;
        latestSnapshotJson = includeSnapshotJson
            ? JsonUtility.ToJson(latestSnapshot, prettyPrintJson)
            : string.Empty;

        return latestPerceptionPacket;
    }

    public CP_PerceptionSnapshot RefreshSnapshot()
    {
        RefreshPerception();
        return latestSnapshot;
    }

    public CP_PerceptionPacket GetLatestPerceptionPacket()
    {
        return latestPerceptionPacket;
    }

    public CP_PerceptionSnapshot GetLatestSnapshot()
    {
        return latestSnapshot;
    }

    public string GetLatestPerceptionSummary()
    {
        return latestPerceptionSummary;
    }

    public string GetLatestSnapshotSummary()
    {
        return latestSnapshotSummary;
    }

    public string GetLatestWarningsText()
    {
        return latestWarningsText;
    }

    public string GetLatestPerceptionJson()
    {
        return latestPerceptionJson;
    }

    public string GetLatestSnapshotJson()
    {
        return latestSnapshotJson;
    }

    // Inspector-only mirror so Stage C2 can be checked without opening large JSON payloads.
    private void UpdateFishingC2DebugFields(CP_PerceptionPacket packet)
    {
        fishingDebugActivity = packet != null && packet.activity != null ? packet.activity.id : string.Empty;
        fishingDebugPhase = packet != null && packet.activity != null ? packet.activity.phase : string.Empty;
        fishingDebugSpatialReady = packet != null && packet.scene != null && packet.scene.fishing_spatial_ready;
        fishingDebugInsideInteractionArea = packet != null && packet.entry_checks != null && packet.entry_checks.companion_inside_fishing_interaction_area;
        fishingDebugF2EntrySatisfied =
            fishingDebugActivity == "fishing" &&
            fishingDebugPhase == "ready" &&
            fishingDebugSpatialReady &&
            fishingDebugInsideInteractionArea &&
            packet != null &&
            packet.entry_checks != null &&
            packet.entry_checks.fishing_rod_ready &&
            packet.companion != null &&
            packet.companion.current_hand_pose == "fishing_ready";
        fishingDebugPlayerAnchor = packet != null && packet.player != null ? packet.player.occupied_fishing_anchor_id : string.Empty;
        fishingDebugAssignedAnchor = packet != null && packet.companion != null ? packet.companion.assigned_fishing_anchor_id : string.Empty;
        fishingDebugAssignedCastTarget = packet != null && packet.companion != null ? packet.companion.assigned_fishing_cast_target_id : string.Empty;
        fishingDebugAtAssignedAnchor = packet != null && packet.entry_checks != null && packet.entry_checks.companion_at_valid_fishing_anchor;
        fishingDebugRodReady = packet != null && packet.entry_checks != null && packet.entry_checks.fishing_rod_ready;
        fishingDebugF3EntrySatisfied =
            fishingDebugActivity == "fishing" &&
            fishingDebugPhase == "engagement" &&
            !string.IsNullOrEmpty(fishingDebugAssignedAnchor) &&
            fishingDebugAtAssignedAnchor &&
            fishingDebugRodReady &&
            packet != null &&
            packet.companion != null &&
            packet.companion.current_hand_pose == "fishing_ready";
    }

    // Called by the scheduler immediately before it asks the transport to serialize a request.
    public void SetSchedulingContext(string role, int nextPolicyIndex, int nextStateEpoch, string mode, bool allowLookahead, string interruptionMode, string transitionLabel)
    {
        requestRole = string.IsNullOrWhiteSpace(role) ? "current_policy" : role;
        policyIndex = Mathf.Max(0, nextPolicyIndex);
        currentStateEpoch = Mathf.Max(0, nextStateEpoch);
        schedulerMode = mode ?? string.Empty;
        allowLookaheadWhileMotionBusy = allowLookahead;
        stateChangeInterruptionMode = interruptionMode ?? string.Empty;
        stateTransitionLabel = transitionLabel ?? string.Empty;
    }

    private CP_PerceptionPacket BuildPerceptionPacket()
    {
        CP_PerceptionPacket packet = new CP_PerceptionPacket();
        packet.frame = BuildFrameSection();
        packet.control = BuildControlSection();
        packet.activity = BuildActivitySection();
        packet.task = BuildTaskSection();
        packet.scene = BuildSceneSection();
        packet.player = BuildPlayerSection();
        packet.companion = BuildCompanionSection(packet.scene);
        packet.entry_checks = BuildEntryChecksSection(packet.player, packet.companion, packet.scene);
        packet.events = BuildEventsSection(packet.companion);
        return packet;
    }

    private CP_FrameSection BuildFrameSection()
    {
        CP_FrameSection frame = new CP_FrameSection();
        frame.schema_version = SchemaVersion;
        frame.request_id = $"unity_frame_{Time.frameCount}";
        frame.frame_index = Time.frameCount;
        frame.build_time_seconds = Time.time;
        frame.time_since_startup = Time.realtimeSinceStartup;
        frame.delta_time = Time.deltaTime;
        frame.unscaled_delta_time = Time.unscaledDeltaTime;
        frame.request_role = requestRole;
        frame.policy_index = policyIndex;
        frame.current_state_epoch = currentStateEpoch;
        return frame;
    }

    private CP_ControlSection BuildControlSection()
    {
        CP_ControlSection control = new CP_ControlSection();
        control.companion_enabled = companionEnabled;
        control.shutdown_signal = shutdownSignal;
        control.ai_enabled = aiEnabled;
        control.fallback_enabled = fallbackEnabled;
        control.scheduler_mode = schedulerMode;
        control.allow_lookahead_while_motion_busy = allowLookaheadWhileMotionBusy;
        control.state_change_interruption_mode = stateChangeInterruptionMode;
        return control;
    }

    private CP_ActivitySection BuildActivitySection()
    {
        CP_ActivitySection activity = new CP_ActivitySection();

        if (activityContextRouter == null)
        {
            return activity;
        }

        // Activity context is read-only gameplay truth. Stage 1 derives the
        // canonical companion current_state from it for both Unity and Python.
        activityContextRouter.RefreshContext();
        activity.id = activityContextRouter.ActiveActivity;
        activity.phase = activityContextRouter.ActivityPhase;
        activity.detail_phase = activityContextRouter.DetailPhase;
        activity.fishing_completed = activityContextRouter.FishingCompleted;
        activity.context_key = activityContextRouter.ContextKey;
        activity.context_epoch = activityContextRouter.ContextEpoch;
        return activity;
    }

    private CP_TaskSection BuildTaskSection()
    {
        CP_TaskSection task = new CP_TaskSection();

        if (blackboard != null)
        {
            task.campfire_started = blackboard.campfire_started;
            task.stick_picked = blackboard.stick_picked;
            task.marshmallow_skewered = blackboard.marshmallow_skewered;
            task.marshmallow_in_roasting_zone = blackboard.marshmallow_in_roasting_zone;
            task.roasting_duration_reached = blackboard.roasting_duration_reached;
            task.marshmallow_eaten = blackboard.marshmallow_eaten;
            task.current_roasting_time = blackboard.currentRoastingTime;
            task.required_roasting_duration = blackboard.requiredRoastingDuration;

            if (blackboard.requiredRoastingDuration > 0f)
            {
                task.roasting_progress = Mathf.Clamp01(
                    blackboard.currentRoastingTime / blackboard.requiredRoastingDuration
                );
            }
            else
            {
                task.roasting_progress = 0f;
            }
        }

        return task;
    }

    private CP_SceneSection BuildSceneSection()
    {
        CP_SceneSection scene = new CP_SceneSection();
        scene.campfire_available = blackboard != null && blackboard.currentCampfireInstance != null;
        scene.campfire_center_available = TryGetCampfireCenterTransform(out _);
        scene.interaction_area_available = interactionArea != null;
        scene.fishing_spatial_ready =
            fishingInteractionArea != null &&
            fishingInteractionArea.IsSpatialReady &&
            fishingAnchorManager != null &&
            fishingAnchorManager.FishingSpatialReady;
        scene.fishing_cast_targets_ready = fishingAnchorManager != null && fishingAnchorManager.FishingCastTargetsReady;
        scene.roasting_anchors = BuildRoastingAnchorInfoList();
        scene.look_targets_available = BuildLookTargetAvailability();
        return scene;
    }

    private CP_PlayerSection BuildPlayerSection()
    {
        CP_PlayerSection player = new CP_PlayerSection();
        player.has_player_root = playerRoot != null;
        player.has_player_head = playerHead != null;

        if (playerRoot != null)
        {
            player.position = playerRoot.position;
            player.distance_to_campfire = GetDistanceToCampfireCenter(playerRoot.position);
        }
        else
        {
            player.distance_to_campfire = -1f;
        }

        if (playerHead != null)
        {
            player.forward = playerHead.forward;
        }
        else if (playerRoot != null)
        {
            player.forward = playerRoot.forward;
        }
        else
        {
            player.forward = Vector3.forward;
        }

        player.occupied_roasting_anchor_id = roastingAnchorManager != null
            ? roastingAnchorManager.playerOccupiedAnchorId
            : string.Empty;
        player.occupied_fishing_anchor_id = fishingAnchorManager != null
            ? fishingAnchorManager.PlayerOccupiedFishingAnchorId
            : string.Empty;
        player.is_inside_interaction_area = IsPlayerInsideInteractionArea();
        return player;
    }

    private CP_CompanionSection BuildCompanionSection(CP_SceneSection scene)
    {
        CP_CompanionSection companion = new CP_CompanionSection();

        if (companionRoot != null)
        {
            companion.position = companionRoot.position;
            companion.forward = companionRoot.forward;
            companion.is_moving = IsCompanionMoving(companionRoot);
        }
        else
        {
            companion.forward = Vector3.forward;
            companion.is_moving = false;
        }

        string currentStateName = ResolveCanonicalStateName();
        string previousStateName = hasResolvedStateName ? lastResolvedStateName : currentStateName;
        bool stateChanged = hasResolvedStateName && currentStateName != lastResolvedStateName;

        if (stateChanged)
        {
            stateChangeId++;
            AddRecentEvent($"StateChanged:{previousStateName}->{currentStateName}");
        }

        hasResolvedStateName = true;
        lastResolvedStateName = currentStateName;

        companion.current_state = currentStateName;
        companion.previous_state = previousStateName;
        companion.state_changed = stateChanged;
        companion.state_reason = BuildStateReason(currentStateName);
        companion.is_inside_interaction_area = interactionArea != null && interactionArea.companionInsideArea;
        companion.is_at_valid_roasting_anchor = roastingAnchorManager != null && roastingAnchorManager.companionAtValidRoastingAnchor;
        companion.nearest_free_roasting_anchor_id = roastingAnchorManager != null
            ? roastingAnchorManager.nearestFreeAnchorId
            : string.Empty;
        companion.current_anchor_id = ResolveCompanionCurrentAnchorId(scene);
        companion.is_inside_fishing_interaction_area = IsCompanionInsideFishingInteractionArea();
        companion.is_at_fishing_anchor = fishingAnchorManager != null && fishingAnchorManager.CompanionAtFishingAnchor;
        companion.nearest_fishing_anchor_id = fishingAnchorManager != null
            ? fishingAnchorManager.CompanionNearestFishingAnchorId
            : string.Empty;
        companion.assigned_fishing_anchor_id = fishingAnchorManager != null
            ? fishingAnchorManager.AssignedFishingAnchorId
            : string.Empty;
        companion.assigned_fishing_cast_target_id = fishingAnchorManager != null
            ? fishingAnchorManager.AssignedFishingCastTargetId
            : string.Empty;
        companion.is_at_valid_fishing_anchor = fishingAnchorManager != null && fishingAnchorManager.CompanionAtAssignedFishingAnchor;
        companion.state_epoch = currentStateEpoch;
        companion.state_transition_label = stateTransitionLabel;
        companion.current_hand_pose = handPoseController != null ? handPoseController.CurrentPose : string.Empty;
        companion.target_hand_pose = handPoseController != null ? handPoseController.TargetPose : string.Empty;
        companion.hand_pose_runtime_available = handPoseController != null && handPoseController.IsRuntimeReady;
        companion.companion_prop_runtime_available = propController != null && propController.IsRuntimeReady;
        companion.fishing_rod_runtime_available = propController != null && propController.IsFishingRodRuntimeReady;
        companion.has_companion_stick = propController != null && propController.HasCompanionStick;
        companion.has_companion_marshmallow = propController != null && propController.HasCompanionMarshmallow;
        companion.has_companion_fishing_rod = propController != null && propController.HasCompanionFishingRod;
        companion.companion_stick_state = propController != null ? propController.CurrentStickState : string.Empty;
        companion.companion_marshmallow_state = propController != null ? propController.CurrentMarshmallowState : string.Empty;
        companion.companion_fishing_rod_state = propController != null ? propController.CurrentFishingRodState : string.Empty;

        return companion;
    }

    private CP_EntryChecksSection BuildEntryChecksSection(
        CP_PlayerSection player,
        CP_CompanionSection companion,
        CP_SceneSection scene
    )
    {
        CP_EntryChecksSection entryChecks = new CP_EntryChecksSection();
        entryChecks.companion_inside_interaction_area = companion.is_inside_interaction_area;
        entryChecks.companion_at_valid_roasting_anchor = companion.is_at_valid_roasting_anchor;
        entryChecks.nearest_free_roasting_anchor_id = companion.nearest_free_roasting_anchor_id;
        entryChecks.nearest_valid_interaction_area_point_id = scene.interaction_area_available ? "area_center" : string.Empty;
        entryChecks.companion_inside_fishing_interaction_area = companion.is_inside_fishing_interaction_area;
        entryChecks.companion_at_valid_fishing_anchor = companion.is_at_valid_fishing_anchor;
        // Keep rod readiness semantic-only so legal internal cast-cycle poses do
        // not masquerade as a missing Fishing Rod setup.
        entryChecks.fishing_rod_ready =
            activityContextRouter != null &&
            activityContextRouter.ActiveActivity == "fishing" &&
            companion.fishing_rod_runtime_available &&
            companion.has_companion_fishing_rod &&
            companion.companion_fishing_rod_state == "visible_in_hand";
        return entryChecks;
    }

    private CP_EventsSection BuildEventsSection(CP_CompanionSection companion)
    {
        CP_EventsSection events = new CP_EventsSection();
        events.state_changed = companion.state_changed;
        events.state_change_id = stateChangeId;
        events.recent_events = new List<string>(recentEvents);
        return events;
    }

    private List<CP_RoastingAnchorInfo> BuildRoastingAnchorInfoList()
    {
        List<CP_RoastingAnchorInfo> anchors = new List<CP_RoastingAnchorInfo>();

        for (int i = 0; i < 3; i++)
        {
            string anchorId = $"Anchor_{i}";
            Transform anchorTransform = roastingAnchorManager != null
                ? roastingAnchorManager.GetAnchorByName(anchorId)
                : null;

            CP_RoastingAnchorInfo info = new CP_RoastingAnchorInfo();
            info.id = anchorId;
            info.is_assigned = anchorTransform != null;

            if (anchorTransform != null)
            {
                info.position = anchorTransform.position;
                info.distance_to_player_2d = roastingAnchorManager != null && roastingAnchorManager.playerTransform != null
                    ? roastingAnchorManager.DistanceXZ(roastingAnchorManager.playerTransform.position, anchorTransform.position)
                    : -1f;
                info.distance_to_companion_2d = roastingAnchorManager != null && roastingAnchorManager.companionRoot != null
                    ? roastingAnchorManager.DistanceXZ(roastingAnchorManager.companionRoot.position, anchorTransform.position)
                    : -1f;
                info.is_occupied_by_player = roastingAnchorManager != null && anchorId == roastingAnchorManager.playerOccupiedAnchorId;
                info.is_valid_for_companion = info.is_assigned && !info.is_occupied_by_player;
                info.is_nearest_free_anchor = roastingAnchorManager != null && anchorId == roastingAnchorManager.nearestFreeAnchorId;
                info.is_companion_at_anchor = roastingAnchorManager != null
                    && anchorId == roastingAnchorManager.nearestFreeAnchorId
                    && roastingAnchorManager.companionAtValidRoastingAnchor;
            }

            anchors.Add(info);
        }

        return anchors;
    }

    private List<string> BuildLookTargetAvailability()
    {
        List<string> lookTargets = new List<string>();

        if (playerHead != null)
        {
            lookTargets.Add("player_head");
        }

        if (blackboard != null && blackboard.stickObject != null)
        {
            lookTargets.Add("player_hand_item");
        }

        if (TryGetCampfireCenterTransform(out _))
        {
            lookTargets.Add("campfire_center");
        }

        if (blackboard != null && (blackboard.currentMarshmallowInstance != null || blackboard.marshmallowTipAnchor != null))
        {
            lookTargets.Add("player_marshmallow");
        }

        if (propController != null && propController.TryGetCompanionMarshmallowLookTarget(out _))
        {
            lookTargets.Add("companion_marshmallow");
        }

        if (TryGetAssignedFishingCastTargetTransform(out _))
        {
            lookTargets.Add("assigned_fishing_cast_target");
        }

        if (TryGetFishingWaterFocusPosition(out _))
        {
            lookTargets.Add("water_focus");
        }

        FishingStateBlackboard fishingBlackboard = GetFishingBlackboard();
        if (fishingBlackboard != null && fishingBlackboard.fishingRodObject != null)
        {
            lookTargets.Add("player_fishing_rod");
        }

        if (fishingBlackboard != null && fishingBlackboard.currentBobberInstance != null)
        {
            lookTargets.Add("player_bobber");
        }

        if (companionRoot != null)
        {
            lookTargets.Add("neutral_forward");
        }

        if (environmentDetailTarget != null)
        {
            lookTargets.Add("environment_detail");
        }

        return lookTargets;
    }

    private FishingStateBlackboard GetFishingBlackboard()
    {
        return activityContextRouter != null ? activityContextRouter.FishingBlackboard : null;
    }

    private bool TryGetAssignedFishingCastTargetTransform(out Transform castTarget)
    {
        castTarget = null;
        return fishingAnchorManager != null && fishingAnchorManager.TryGetAssignedCastTargetTransform(out castTarget) && castTarget != null;
    }

    private bool TryGetFishingWaterFocusPosition(out Vector3 worldPosition)
    {
        worldPosition = Vector3.zero;

        Transform assignedCastTarget;
        if (TryGetAssignedFishingCastTargetTransform(out assignedCastTarget))
        {
            worldPosition = assignedCastTarget.position;
            return true;
        }

        FishingStateBlackboard fishingBlackboard = GetFishingBlackboard();
        if (fishingBlackboard != null && fishingBlackboard.playerCastTarget != null)
        {
            worldPosition = fishingBlackboard.playerCastTarget.position;
            return true;
        }

        if (fishingAnchorManager != null && fishingAnchorManager.CastDirectionReference != null)
        {
            worldPosition = fishingAnchorManager.CastDirectionReference.position;
            return true;
        }

        return false;
    }

    private List<string> BuildPerceptionWarnings(CP_PerceptionPacket packet)
    {
        List<string> warnings = new List<string>();

        if (blackboard == null)
        {
            warnings.Add("CP_PerceptionBuilder: blackboard is not assigned.");
        }
        else
        {
            if (blackboard.campfireSpawnPoint == null)
            {
                warnings.Add("CP_PerceptionBuilder: blackboard.campfireSpawnPoint is not assigned.");
            }

            if (blackboard.campfireRuntimeParent == null)
            {
                warnings.Add("CP_PerceptionBuilder: blackboard.campfireRuntimeParent is not assigned.");
            }

            if (blackboard.marshmallowTipAnchor == null)
            {
                warnings.Add("CP_PerceptionBuilder: blackboard.marshmallowTipAnchor is not assigned.");
            }

            if (blackboard.requiredRoastingDuration <= 0f)
            {
                warnings.Add("CP_PerceptionBuilder: requiredRoastingDuration is zero or less, roasting_progress is forced to 0.");
            }

            if (blackboard.campfire_started && blackboard.currentCampfireInstance == null)
            {
                warnings.Add("CP_PerceptionBuilder: campfire_started is true but currentCampfireInstance is null.");
            }

            if (blackboard.marshmallow_skewered && blackboard.currentMarshmallowInstance == null)
            {
                warnings.Add("CP_PerceptionBuilder: marshmallow is skewered but currentMarshmallowInstance is null.");
            }
        }

        if (activityContextRouter == null)
        {
            warnings.Add("CP_PerceptionBuilder: activityContextRouter is not assigned; activity context defaults to common/free.");
        }

        if (playerRoot == null)
        {
            warnings.Add("CP_PerceptionBuilder: playerRoot is not assigned.");
        }

        if (playerHead == null)
        {
            warnings.Add("CP_PerceptionBuilder: playerHead is not assigned.");
        }

        if (companionRoot == null)
        {
            warnings.Add("CP_PerceptionBuilder: companionRoot is not assigned.");
        }

        if (interactionArea == null)
        {
            warnings.Add("CP_PerceptionBuilder: interactionArea is not assigned.");
        }

        if (roastingAnchorManager == null)
        {
            warnings.Add("CP_PerceptionBuilder: roastingAnchorManager is not assigned.");
        }

        if (handPoseController == null)
        {
            warnings.Add("CP_PerceptionBuilder: handPoseController is not assigned.");
        }
        else if (!handPoseController.IsRuntimeReady && !string.IsNullOrWhiteSpace(handPoseController.LastFailureReason))
        {
            warnings.Add($"CP_PerceptionBuilder: handPoseController is not runtime-ready ({handPoseController.LastFailureReason}).");
        }

        if (propController == null)
        {
            warnings.Add("CP_PerceptionBuilder: propController is not assigned.");
        }
        else if (!propController.IsRuntimeReady && !string.IsNullOrWhiteSpace(propController.LastFailureReason))
        {
            warnings.Add($"CP_PerceptionBuilder: propController is not runtime-ready ({propController.LastFailureReason}).");
        }

        if (packet.scene.campfire_available == false && blackboard != null && blackboard.currentCampfireInstance != null)
        {
            warnings.Add("CP_PerceptionBuilder: scene.campfire_available should be true when currentCampfireInstance exists.");
        }

        return warnings;
    }

    private CP_PerceptionSnapshot BuildLegacySnapshot(CP_PerceptionPacket packet, List<string> warnings)
    {
        CP_PerceptionSnapshot snapshot = new CP_PerceptionSnapshot();
        snapshot.buildTimeSeconds = packet.frame.build_time_seconds;
        snapshot.currentStateName = packet.companion.current_state;
        snapshot.previousStateName = packet.companion.previous_state;
        snapshot.stateReason = packet.companion.state_reason;
        snapshot.campfireStarted = packet.task.campfire_started;
        snapshot.stickPicked = packet.task.stick_picked;
        snapshot.marshmallowSkewered = packet.task.marshmallow_skewered;
        snapshot.marshmallowInRoastingZone = packet.task.marshmallow_in_roasting_zone;
        snapshot.roastingDurationReached = packet.task.roasting_duration_reached;
        snapshot.marshmallowEaten = packet.task.marshmallow_eaten;
        snapshot.currentRoastingTime = packet.task.current_roasting_time;
        snapshot.requiredRoastingDuration = packet.task.required_roasting_duration;

        FillTransformSnapshot(snapshot.blackboardCampfireSpawnPoint, "campfireSpawnPoint", blackboard != null ? blackboard.campfireSpawnPoint : null);
        FillTransformSnapshot(snapshot.blackboardCampfireRuntimeParent, "campfireRuntimeParent", blackboard != null ? blackboard.campfireRuntimeParent : null);
        FillTransformSnapshot(snapshot.currentCampfireInstance, "currentCampfireInstance", blackboard != null ? GetTransform(blackboard.currentCampfireInstance) : null);
        FillTransformSnapshot(snapshot.stickObject, "stickObject", blackboard != null ? GetTransform(blackboard.stickObject) : null);
        FillTransformSnapshot(snapshot.marshmallowTipAnchor, "marshmallowTipAnchor", blackboard != null ? blackboard.marshmallowTipAnchor : null);
        FillTransformSnapshot(snapshot.currentMarshmallowInstance, "currentMarshmallowInstance", blackboard != null ? GetTransform(blackboard.currentMarshmallowInstance) : null);
        FillTransformSnapshot(snapshot.playerRoot, "playerRoot", playerRoot);
        FillTransformSnapshot(snapshot.playerHead, "playerHead", playerHead);
        FillTransformSnapshot(snapshot.companionRoot, "companionRoot", companionRoot);

        snapshot.currentCampfireHasFireCenterPoint = TryCaptureFireCenterPoint(
            blackboard != null ? blackboard.currentCampfireInstance : null,
            snapshot.currentCampfireFireCenterPoint
        );

        BuildInteractionAreaSnapshot(snapshot, interactionArea);
        snapshot.roastingAnchors = ConvertLegacyAnchors(packet.scene.roasting_anchors);
        snapshot.warnings = warnings != null ? new List<string>(warnings) : new List<string>();
        return snapshot;
    }

    private List<CP_AnchorSnapshot> ConvertLegacyAnchors(List<CP_RoastingAnchorInfo> anchors)
    {
        List<CP_AnchorSnapshot> legacyAnchors = new List<CP_AnchorSnapshot>();

        if (anchors == null)
        {
            return legacyAnchors;
        }

        for (int i = 0; i < anchors.Count; i++)
        {
            CP_RoastingAnchorInfo anchorInfo = anchors[i];
            CP_AnchorSnapshot legacy = new CP_AnchorSnapshot();
            legacy.anchorName = anchorInfo != null ? anchorInfo.id : $"Anchor_{i}";
            legacy.isAssigned = anchorInfo != null && anchorInfo.is_assigned;
            legacy.position = anchorInfo != null ? anchorInfo.position : Vector3.zero;
            legacy.distanceToPlayerXZ = anchorInfo != null ? anchorInfo.distance_to_player_2d : -1f;
            legacy.distanceToCompanionXZ = anchorInfo != null ? anchorInfo.distance_to_companion_2d : -1f;
            legacy.isPlayerOccupied = anchorInfo != null && anchorInfo.is_occupied_by_player;
            legacy.isNearestFree = anchorInfo != null && anchorInfo.is_nearest_free_anchor;
            legacy.isCompanionAtAnchor = anchorInfo != null && anchorInfo.is_companion_at_anchor;
            legacyAnchors.Add(legacy);
        }

        return legacyAnchors;
    }

    private void BuildInteractionAreaSnapshot(CP_PerceptionSnapshot snapshot, CP_InteractionArea area)
    {
        snapshot.interactionArea = new CP_InteractionAreaSnapshot();

        if (area == null)
        {
            snapshot.interactionArea.hasInteractionArea = false;
            snapshot.interactionArea.areaName = "NULL";
            return;
        }

        snapshot.interactionArea.hasInteractionArea = true;
        snapshot.interactionArea.areaName = area.gameObject.name;
        snapshot.interactionArea.areaCenterWorldPosition = area.GetAreaCenterWorldPosition();
        snapshot.interactionArea.areaRadius = area.areaRadius;
        snapshot.interactionArea.useXZDistanceOnly = area.useXZDistanceOnly;
        snapshot.interactionArea.companionInsideArea = area.companionInsideArea;
        snapshot.interactionArea.companionDistanceToAreaCenter = area.companionDistanceToAreaCenter;
        snapshot.interactionArea.companionWorldPosition = area.companionWorldPosition;
    }

    private void FillTransformSnapshot(CP_TransformSnapshot snapshot, string name, Transform source)
    {
        if (snapshot == null)
        {
            return;
        }

        snapshot.Capture(name, source);
    }

    private bool TryCaptureFireCenterPoint(GameObject campfireInstance, CP_TransformSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }

        if (campfireInstance == null)
        {
            snapshot.Capture("FireCenterPoint", null);
            return false;
        }

        Transform fireCenter = FindChildByName(campfireInstance.transform, "FireCenterPoint");
        snapshot.Capture("FireCenterPoint", fireCenter);
        return fireCenter != null;
    }

    private bool TryGetCampfireCenterTransform(out Transform campfireCenter)
    {
        campfireCenter = null;

        if (blackboard == null)
        {
            return false;
        }

        if (blackboard.currentCampfireInstance != null)
        {
            campfireCenter = FindChildByName(blackboard.currentCampfireInstance.transform, "FireCenterPoint");
            if (campfireCenter != null)
            {
                return true;
            }
        }

        if (blackboard.campfireSpawnPoint != null)
        {
            campfireCenter = blackboard.campfireSpawnPoint;
            return true;
        }

        return false;
    }

    private float GetDistanceToCampfireCenter(Vector3 sourcePosition)
    {
        if (!TryGetCampfireCenterTransform(out Transform campfireCenter) || campfireCenter == null)
        {
            return -1f;
        }

        return DistanceXZ(sourcePosition, campfireCenter.position);
    }

    private bool IsPlayerInsideInteractionArea()
    {
        if (interactionArea == null || playerRoot == null)
        {
            return false;
        }

        Vector3 areaCenter = interactionArea.GetAreaCenterWorldPosition();
        if (interactionArea.useXZDistanceOnly)
        {
            return DistanceXZ(playerRoot.position, areaCenter) <= interactionArea.areaRadius;
        }

        return Vector3.Distance(playerRoot.position, areaCenter) <= interactionArea.areaRadius;
    }

    private bool IsCompanionInsideFishingInteractionArea()
    {
        if (fishingInteractionArea == null || companionRoot == null)
        {
            return false;
        }

        return fishingInteractionArea.ContainsXZ(companionRoot.position);
    }

    private bool IsCompanionMoving(Transform companion)
    {
        if (companion == null)
        {
            return false;
        }

        Rigidbody rigidbody = companion.GetComponent<Rigidbody>();
        if (rigidbody == null)
        {
            return false;
        }

        return rigidbody.linearVelocity.sqrMagnitude > 0.0001f;
    }

    private string ResolveCompanionCurrentAnchorId(CP_SceneSection scene)
    {
        if (scene == null || scene.roasting_anchors == null || scene.roasting_anchors.Count == 0)
        {
            return string.Empty;
        }

        if (roastingAnchorManager != null
            && roastingAnchorManager.companionAtValidRoastingAnchor
            && !string.IsNullOrEmpty(roastingAnchorManager.nearestFreeAnchorId))
        {
            return roastingAnchorManager.nearestFreeAnchorId;
        }

        float closestDistance = float.MaxValue;
        string closestAnchorId = string.Empty;

        for (int i = 0; i < scene.roasting_anchors.Count; i++)
        {
            CP_RoastingAnchorInfo anchorInfo = scene.roasting_anchors[i];
            if (anchorInfo == null || !anchorInfo.is_assigned || anchorInfo.distance_to_companion_2d < 0f)
            {
                continue;
            }

            if (anchorInfo.distance_to_companion_2d < closestDistance)
            {
                closestDistance = anchorInfo.distance_to_companion_2d;
                closestAnchorId = anchorInfo.id;
            }
        }

        return closestAnchorId;
    }

    private Transform FindChildByName(Transform root, string targetName)
    {
        if (root == null)
        {
            return null;
        }

        if (root.name == targetName)
        {
            return root;
        }

        foreach (Transform child in root)
        {
            Transform result = FindChildByName(child, targetName);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private Transform GetTransform(GameObject obj)
    {
        return obj != null ? obj.transform : null;
    }

    private string ResolveCanonicalStateName()
    {
        if (activityContextRouter != null)
        {
            string canonicalState = activityContextRouter.CanonicalState;
            if (!string.IsNullOrWhiteSpace(canonicalState) &&
                canonicalState != CP_StateNames.Unknown)
            {
                return canonicalState;
            }

            if (!string.IsNullOrWhiteSpace(activityContextRouter.ActiveActivity))
            {
                return CP_StateNames.Unknown;
            }
        }

        if (blackboard == null)
        {
            return CP_StateNames.UnknownNoBlackboard;
        }

        if (!blackboard.campfire_started)
        {
            return CP_StateNames.S1;
        }

        if (!blackboard.stick_picked)
        {
            return CP_StateNames.S2;
        }

        return CP_StateNames.S3;
    }

    private string BuildStateReason(string currentStateName)
    {
        switch (currentStateName)
        {
            case CP_StateNames.S1:
                return "common_free";
            case CP_StateNames.F2:
                return "fishing_ready";
            case CP_StateNames.F3:
                return "fishing_engagement";
            case CP_StateNames.S2:
                return "campfire_ready";
            case CP_StateNames.S3:
                return "campfire_engagement";
            case CP_StateNames.UnknownNoBlackboard:
                return "blackboard_missing";
            default:
                return "canonical_activity_state_unknown";
        }
    }

    private string BuildPerceptionSummary(CP_PerceptionPacket packet)
    {
        summaryBuilder.Clear();

        summaryBuilder.AppendLine("=== Perception Packet ===");
        summaryBuilder.AppendLine($"schema_version: {packet.frame.schema_version}");
        summaryBuilder.AppendLine($"request_id: {packet.frame.request_id}");
        summaryBuilder.AppendLine($"frame_index: {packet.frame.frame_index}");
        summaryBuilder.AppendLine($"build_time_seconds: {packet.frame.build_time_seconds:F2}");
        summaryBuilder.AppendLine(
            $"control: companion_enabled={packet.control.companion_enabled}, shutdown_signal={packet.control.shutdown_signal}, ai_enabled={packet.control.ai_enabled}, fallback_enabled={packet.control.fallback_enabled}"
        );
        summaryBuilder.AppendLine(
            $"activity: id={packet.activity.id}, phase={packet.activity.phase}, detail_phase={packet.activity.detail_phase}, fishing_completed={packet.activity.fishing_completed}, context_key={packet.activity.context_key}, context_epoch={packet.activity.context_epoch}"
        );
        summaryBuilder.AppendLine(
            $"task: campfire_started={packet.task.campfire_started}, stick_picked={packet.task.stick_picked}, marshmallow_skewered={packet.task.marshmallow_skewered}, roasting_progress={packet.task.roasting_progress:F2}"
        );
        summaryBuilder.AppendLine(
            $"player: has_root={packet.player.has_player_root}, has_head={packet.player.has_player_head}, occupied_anchor={packet.player.occupied_roasting_anchor_id}, occupied_fishing_anchor={packet.player.occupied_fishing_anchor_id}, distance_to_campfire={packet.player.distance_to_campfire:F2}, inside_interaction_area={packet.player.is_inside_interaction_area}"
        );
        summaryBuilder.AppendLine(
            $"companion: current_state={packet.companion.current_state}, previous_state={packet.companion.previous_state}, state_changed={packet.companion.state_changed}, current_anchor_id={packet.companion.current_anchor_id}, nearest_fishing_anchor_id={packet.companion.nearest_fishing_anchor_id}, assigned_fishing_anchor_id={packet.companion.assigned_fishing_anchor_id}, assigned_fishing_cast_target_id={packet.companion.assigned_fishing_cast_target_id}, at_fishing_anchor={packet.companion.is_at_fishing_anchor}, at_valid_fishing_anchor={packet.companion.is_at_valid_fishing_anchor}, inside_fishing_area={packet.companion.is_inside_fishing_interaction_area}, moving={packet.companion.is_moving}"
        );
        summaryBuilder.AppendLine(
            $"companion_runtime: hand_ready={packet.companion.hand_pose_runtime_available}, current_hand_pose={packet.companion.current_hand_pose}, target_hand_pose={packet.companion.target_hand_pose}, prop_ready={packet.companion.companion_prop_runtime_available}, stick_state={packet.companion.companion_stick_state}, marshmallow_state={packet.companion.companion_marshmallow_state}"
        );
        summaryBuilder.AppendLine(
            $"scheduler: request_role={packet.frame.request_role}, policy_index={packet.frame.policy_index}, state_epoch={packet.frame.current_state_epoch}, mode={packet.control.scheduler_mode}, interruption_mode={packet.control.state_change_interruption_mode}"
        );
        summaryBuilder.AppendLine(
            $"scene: campfire_available={packet.scene.campfire_available}, campfire_center_available={packet.scene.campfire_center_available}, interaction_area_available={packet.scene.interaction_area_available}, fishing_spatial_ready={packet.scene.fishing_spatial_ready}, fishing_cast_targets_ready={packet.scene.fishing_cast_targets_ready}, roasting_anchors={packet.scene.roasting_anchors.Count}, look_targets={string.Join(",", packet.scene.look_targets_available)}"
        );
        summaryBuilder.AppendLine(
            $"entry_checks: companion_inside_interaction_area={packet.entry_checks.companion_inside_interaction_area}, companion_at_valid_roasting_anchor={packet.entry_checks.companion_at_valid_roasting_anchor}, nearest_free_roasting_anchor_id={packet.entry_checks.nearest_free_roasting_anchor_id}, nearest_valid_interaction_area_point_id={packet.entry_checks.nearest_valid_interaction_area_point_id}, companion_inside_fishing_interaction_area={packet.entry_checks.companion_inside_fishing_interaction_area}, companion_at_valid_fishing_anchor={packet.entry_checks.companion_at_valid_fishing_anchor}"
        );
        summaryBuilder.AppendLine(
            $"events: state_changed={packet.events.state_changed}, state_change_id={packet.events.state_change_id}, recent_events_count={packet.events.recent_events.Count}"
        );

        return summaryBuilder.ToString();
    }

    private string BuildWarningsText(List<string> warnings)
    {
        warningBuilder.Clear();

        if (warnings == null || warnings.Count == 0)
        {
            string noWarningsText = "No warnings.";
            if (logWarnings && noWarningsText != lastLoggedWarningsText)
            {
                lastLoggedWarningsText = noWarningsText;
            }

            warningBuilder.AppendLine(noWarningsText);
            return warningBuilder.ToString();
        }

        for (int i = 0; i < warnings.Count; i++)
        {
            warningBuilder.AppendLine(warnings[i]);
        }

        string currentWarningsText = warningBuilder.ToString();

        if (logWarnings && currentWarningsText != lastLoggedWarningsText)
        {
            for (int i = 0; i < warnings.Count; i++)
            {
                Debug.LogWarning(warnings[i], this);
            }
        }

        lastLoggedWarningsText = currentWarningsText;
        return currentWarningsText;
    }

    private void AddRecentEvent(string eventText)
    {
        if (string.IsNullOrEmpty(eventText))
        {
            return;
        }

        recentEvents.Add(eventText);

        while (recentEvents.Count > MaxRecentEvents)
        {
            recentEvents.RemoveAt(0);
        }
    }

    private float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
