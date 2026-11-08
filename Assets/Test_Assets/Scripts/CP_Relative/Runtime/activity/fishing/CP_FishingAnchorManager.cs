using UnityEngine;

// Fishing-specific spatial truth owner for shoreline anchors and cast targets.
// It owns semantic F3 assignment state, but does not perform movement execution.
public class CP_FishingAnchorManager : MonoBehaviour
{
    private const int AnchorCount = 3;
    private const float DirectionEpsilon = 0.0001f;
    private const float TieEpsilon = 0.0001f;

    [Header("Spatial References")]
    [SerializeField] private Transform fishingStartSite;
    [SerializeField] private Transform castDirectionReference;
    [SerializeField] private Transform playerRoot;
    [SerializeField] private Transform companionRoot;
    [SerializeField] private Transform runtimeParent;
    [SerializeField] private CP_ActivityContextRouter activityContextRouter;

    [Header("Geometry")]
    [SerializeField] private float anchorSpacing = 0.75f;
    [SerializeField] private float anchorHeightOffset = 0f;
    [SerializeField] private float castTargetDistance = 8f;
    [SerializeField] private float castTargetHeightOffset = 0f;
    [SerializeField] private float anchorArrivalTolerance = 0.35f;
    [SerializeField] private bool regenerateEveryFrame = true;

    [Header("Debug")]
    [SerializeField] private bool showDebugGizmos = true;
    [SerializeField] private bool drawCastTargetLines = true;

    [Header("Runtime Read Only")]
    [SerializeField] private bool anchorLayoutReady;
    [SerializeField] private bool fishingSpatialReady;
    [SerializeField] private bool fishingCastTargetsReady;
    [SerializeField] private string playerOccupiedFishingAnchorId = string.Empty;
    [SerializeField] private string companionNearestFishingAnchorId = string.Empty;
    [SerializeField] private bool companionAtFishingAnchor;
    [SerializeField] private float playerNearestAnchorDistance = -1f;
    [SerializeField] private float companionNearestAnchorDistance = -1f;
    [SerializeField] private bool hasFishingAssignment;
    [SerializeField] private string assignedFishingAnchorId = string.Empty;
    [SerializeField] private string assignedFishingCastTargetId = string.Empty;
    [SerializeField] private bool companionAtAssignedFishingAnchor;
    [SerializeField] private float companionDistanceToAssignedFishingAnchor = -1f;
    [SerializeField] private string lastAssignmentReason = string.Empty;
    [SerializeField] private string lastReadinessReason = string.Empty;

    private readonly Transform[] anchorTransforms = new Transform[AnchorCount];
    private readonly Transform[] castTargetTransforms = new Transform[AnchorCount];
    private readonly Vector3[] latestAnchorPositions = new Vector3[AnchorCount];
    private readonly Vector3[] latestCastTargetPositions = new Vector3[AnchorCount];

    private Transform anchorRuntimeRoot;
    private Transform castTargetRuntimeRoot;
    private int assignedFishingAnchorIndex = -1;

    public Transform FishingStartSite => fishingStartSite;
    public Transform CastDirectionReference => castDirectionReference;
    public Transform PlayerRoot => playerRoot;
    public Transform CompanionRoot => companionRoot;
    public CP_ActivityContextRouter ActivityContextRouter => activityContextRouter;
    public bool AnchorLayoutReady => anchorLayoutReady;
    public bool FishingSpatialReady => fishingSpatialReady;
    public bool FishingCastTargetsReady => fishingCastTargetsReady;
    public string PlayerOccupiedFishingAnchorId => playerOccupiedFishingAnchorId;
    public string CompanionNearestFishingAnchorId => companionNearestFishingAnchorId;
    public bool CompanionAtFishingAnchor => companionAtFishingAnchor;
    public float PlayerNearestAnchorDistance => playerNearestAnchorDistance;
    public float CompanionNearestAnchorDistance => companionNearestAnchorDistance;
    public bool HasFishingAssignment => hasFishingAssignment;
    public string AssignedFishingAnchorId => assignedFishingAnchorId;
    public string AssignedFishingCastTargetId => assignedFishingCastTargetId;
    public bool CompanionAtAssignedFishingAnchor => companionAtAssignedFishingAnchor;
    public float CompanionDistanceToAssignedFishingAnchor => companionDistanceToAssignedFishingAnchor;
    public string LastAssignmentReason => lastAssignmentReason;
    public string LastReadinessReason => lastReadinessReason;
    public float AnchorArrivalTolerance => Mathf.Max(0f, anchorArrivalTolerance);

    private void Start()
    {
        RefreshSpatialTruth();
    }

    private void Update()
    {
        if (regenerateEveryFrame)
        {
            RefreshSpatialTruth();
        }
    }

    public void RefreshSpatialTruth()
    {
        EnsureRuntimeObjects();

        bool hasAnchorBasis = TryBuildAnchorBasis(
            out Vector3 anchorBasePosition,
            out Vector3 shoreDirection,
            out string anchorFailureReason
        );

        anchorLayoutReady = hasAnchorBasis;
        fishingCastTargetsReady = false;
        fishingSpatialReady = false;
        lastReadinessReason = anchorFailureReason ?? string.Empty;

        if (!hasAnchorBasis)
        {
            ClearRuntimeState();
            UpdateFishingAssignmentLifecycle();
            return;
        }

        UpdateAnchorPositions(anchorBasePosition, shoreDirection);

        bool hasCastDirection = TryBuildCastDirection(out Vector3 castDirection, out string castFailureReason);
        fishingCastTargetsReady = hasCastDirection;
        fishingSpatialReady = hasCastDirection;
        lastReadinessReason = hasCastDirection ? string.Empty : castFailureReason ?? string.Empty;

        if (hasCastDirection)
        {
            UpdateCastTargetPositions(castDirection);
        }
        else
        {
            HideCastTargets();
        }

        UpdateNearestObservedTruth();
        UpdateFishingAssignmentLifecycle();
    }

    public bool TryGetNearestAnchorIdXZ(Vector3 worldPosition, out string anchorId, out float distance)
    {
        anchorId = string.Empty;
        distance = -1f;

        if (!anchorLayoutReady)
        {
            return false;
        }

        int bestIndex = -1;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < AnchorCount; i++)
        {
            float currentDistance = DistanceXZ(worldPosition, latestAnchorPositions[i]);

            if (bestIndex < 0 || currentDistance < bestDistance - TieEpsilon)
            {
                bestIndex = i;
                bestDistance = currentDistance;
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        anchorId = GetAnchorId(bestIndex);
        distance = bestDistance;
        return true;
    }

    public bool TryGetAnchorTransform(int index, out Transform anchor)
    {
        anchor = null;

        if (index < 0 || index >= AnchorCount)
        {
            return false;
        }

        anchor = anchorTransforms[index];
        return anchor != null;
    }

    public bool TryGetCastTargetTransform(int index, out Transform castTarget)
    {
        castTarget = null;

        if (index < 0 || index >= AnchorCount)
        {
            return false;
        }

        castTarget = castTargetTransforms[index];
        return castTarget != null;
    }

    public bool TryGetAssignedAnchorTransform(out Transform anchorTransform)
    {
        anchorTransform = null;

        if (!hasFishingAssignment || assignedFishingAnchorIndex < 0 || assignedFishingAnchorIndex >= AnchorCount)
        {
            return false;
        }

        anchorTransform = anchorTransforms[assignedFishingAnchorIndex];
        return anchorTransform != null;
    }

    public bool TryGetAssignedCastTargetTransform(out Transform castTargetTransform)
    {
        castTargetTransform = null;

        if (!hasFishingAssignment || assignedFishingAnchorIndex < 0 || assignedFishingAnchorIndex >= AnchorCount)
        {
            return false;
        }

        castTargetTransform = castTargetTransforms[assignedFishingAnchorIndex];
        return castTargetTransform != null;
    }

    public void ClearFishingAssignment()
    {
        hasFishingAssignment = false;
        assignedFishingAnchorIndex = -1;
        assignedFishingAnchorId = string.Empty;
        assignedFishingCastTargetId = string.Empty;
        companionAtAssignedFishingAnchor = false;
        companionDistanceToAssignedFishingAnchor = -1f;
        lastAssignmentReason = string.Empty;
    }

    public Transform GetAnchorByName(string anchorId)
    {
        int index = GetIndexFromSemanticName(anchorId, "Anchor_");
        return index >= 0 ? anchorTransforms[index] : null;
    }

    public Transform GetCastTargetByName(string castTargetId)
    {
        int index = GetIndexFromSemanticName(castTargetId, "CastTarget_");
        return index >= 0 ? castTargetTransforms[index] : null;
    }

    public float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private void EnsureRuntimeObjects()
    {
        Transform parent = runtimeParent != null ? runtimeParent : transform;
        anchorRuntimeRoot = EnsureRuntimeRoot(parent, anchorRuntimeRoot, "FishingAnchorRuntime");
        castTargetRuntimeRoot = EnsureRuntimeRoot(parent, castTargetRuntimeRoot, "FishingCastTargetRuntime");

        for (int i = 0; i < AnchorCount; i++)
        {
            anchorTransforms[i] = EnsureRuntimeChild(anchorRuntimeRoot, anchorTransforms[i], GetAnchorId(i));
            castTargetTransforms[i] = EnsureRuntimeChild(castTargetRuntimeRoot, castTargetTransforms[i], GetCastTargetId(i));
        }
    }

    private Transform EnsureRuntimeRoot(Transform parent, Transform existing, string objectName)
    {
        if (existing != null)
        {
            existing.SetParent(parent, false);
            existing.name = objectName;
            return existing;
        }

        Transform child = parent.Find(objectName);
        if (child != null)
        {
            return child;
        }

        GameObject runtimeObject = new GameObject(objectName);
        runtimeObject.transform.SetParent(parent, false);
        return runtimeObject.transform;
    }

    private Transform EnsureRuntimeChild(Transform parent, Transform existing, string objectName)
    {
        if (existing != null)
        {
            existing.SetParent(parent, false);
            existing.name = objectName;
            existing.gameObject.SetActive(true);
            return existing;
        }

        Transform child = parent.Find(objectName);
        if (child != null)
        {
            child.gameObject.SetActive(true);
            return child;
        }

        GameObject childObject = new GameObject(objectName);
        childObject.transform.SetParent(parent, false);
        return childObject.transform;
    }

    private bool TryBuildAnchorBasis(
        out Vector3 anchorBasePosition,
        out Vector3 shoreDirection,
        out string failureReason)
    {
        anchorBasePosition = Vector3.zero;
        shoreDirection = Vector3.zero;
        failureReason = string.Empty;

        if (fishingStartSite == null)
        {
            failureReason = "missing_fishing_start_site";
            return false;
        }

        Vector3 projectedRight = Vector3.ProjectOnPlane(fishingStartSite.right, Vector3.up);
        if (projectedRight.sqrMagnitude <= DirectionEpsilon)
        {
            failureReason = "degenerate_start_site_local_positive_x";
            return false;
        }

        shoreDirection = projectedRight.normalized;
        anchorBasePosition = fishingStartSite.position + Vector3.up * anchorHeightOffset;
        return true;
    }

    private bool TryBuildCastDirection(out Vector3 castDirection, out string failureReason)
    {
        castDirection = Vector3.zero;
        failureReason = string.Empty;

        if (fishingStartSite == null)
        {
            failureReason = "missing_fishing_start_site";
            return false;
        }

        if (castDirectionReference == null)
        {
            failureReason = "missing_cast_direction_reference";
            return false;
        }

        Vector3 projectedDirection = Vector3.ProjectOnPlane(
            castDirectionReference.position - fishingStartSite.position,
            Vector3.up
        );

        if (projectedDirection.sqrMagnitude <= DirectionEpsilon)
        {
            failureReason = "degenerate_cast_direction_reference";
            return false;
        }

        castDirection = projectedDirection.normalized;
        return true;
    }

    private void UpdateAnchorPositions(Vector3 anchorBasePosition, Vector3 shoreDirection)
    {
        for (int i = 0; i < AnchorCount; i++)
        {
            Vector3 anchorPosition = anchorBasePosition + shoreDirection * Mathf.Max(0f, anchorSpacing) * i;
            latestAnchorPositions[i] = anchorPosition;

            if (anchorTransforms[i] != null)
            {
                anchorTransforms[i].position = anchorPosition;
                anchorTransforms[i].rotation = Quaternion.identity;
                anchorTransforms[i].gameObject.SetActive(true);
            }
        }
    }

    private void UpdateCastTargetPositions(Vector3 castDirection)
    {
        for (int i = 0; i < AnchorCount; i++)
        {
            Vector3 castTargetPosition =
                latestAnchorPositions[i] +
                castDirection * Mathf.Max(0f, castTargetDistance) +
                Vector3.up * castTargetHeightOffset;

            latestCastTargetPositions[i] = castTargetPosition;

            if (castTargetTransforms[i] != null)
            {
                castTargetTransforms[i].position = castTargetPosition;
                castTargetTransforms[i].rotation = Quaternion.identity;
                castTargetTransforms[i].gameObject.SetActive(true);
            }
        }
    }

    private void UpdateNearestObservedTruth()
    {
        playerOccupiedFishingAnchorId = string.Empty;
        companionNearestFishingAnchorId = string.Empty;
        companionAtFishingAnchor = false;
        playerNearestAnchorDistance = -1f;
        companionNearestAnchorDistance = -1f;

        if (playerRoot != null &&
            TryGetNearestAnchorIdXZ(playerRoot.position, out string playerAnchorId, out float playerDistance))
        {
            playerOccupiedFishingAnchorId = playerAnchorId;
            playerNearestAnchorDistance = playerDistance;
        }

        if (companionRoot != null &&
            TryGetNearestAnchorIdXZ(companionRoot.position, out string companionAnchorId, out float companionDistance))
        {
            companionNearestFishingAnchorId = companionAnchorId;
            companionNearestAnchorDistance = companionDistance;
            companionAtFishingAnchor = companionDistance <= AnchorArrivalTolerance;
        }
    }

    private void UpdateFishingAssignmentLifecycle()
    {
        bool isFishingEngagementNow =
            activityContextRouter != null &&
            activityContextRouter.ActiveActivity == "fishing" &&
            activityContextRouter.ActivityPhase == "engagement";

        if (!isFishingEngagementNow)
        {
            if (hasFishingAssignment)
            {
                ClearFishingAssignment();
            }

            lastAssignmentReason = activityContextRouter == null
                ? "assignment_idle_missing_activity_context_router"
                : "assignment_idle_outside_fishing_engagement";
            return;
        }

        if (hasFishingAssignment)
        {
            RefreshAssignedAnchorTruth();
            return;
        }

        TryCreateFishingAssignmentOnce();
    }

    private void TryCreateFishingAssignmentOnce()
    {
        if (activityContextRouter == null)
        {
            lastAssignmentReason = "assignment_failed_missing_activity_context_router";
            return;
        }

        if (!anchorLayoutReady)
        {
            lastAssignmentReason = "assignment_failed_anchor_layout_not_ready";
            return;
        }

        if (!fishingCastTargetsReady)
        {
            lastAssignmentReason = "assignment_failed_cast_targets_not_ready";
            return;
        }

        if (playerRoot == null)
        {
            lastAssignmentReason = "assignment_failed_player_root_missing";
            return;
        }

        if (companionRoot == null)
        {
            lastAssignmentReason = "assignment_failed_companion_root_missing";
            return;
        }

        if (!TryGetNearestAnchorIdXZ(playerRoot.position, out string playerAnchorId, out _))
        {
            lastAssignmentReason = "assignment_failed_player_anchor_unresolved";
            return;
        }

        int excludedIndex = GetIndexFromSemanticName(playerAnchorId, "Anchor_");
        if (excludedIndex < 0)
        {
            lastAssignmentReason = "assignment_failed_player_anchor_index_invalid";
            return;
        }

        int bestIndex = -1;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < AnchorCount; i++)
        {
            if (i == excludedIndex)
            {
                continue;
            }

            if (anchorTransforms[i] == null)
            {
                lastAssignmentReason = $"assignment_failed_missing_anchor_transform_{i}";
                return;
            }

            if (castTargetTransforms[i] == null)
            {
                lastAssignmentReason = $"assignment_failed_missing_cast_target_transform_{i}";
                return;
            }

            float candidateDistance = DistanceXZ(companionRoot.position, latestAnchorPositions[i]);
            if (bestIndex < 0 || candidateDistance < bestDistance)
            {
                bestIndex = i;
                bestDistance = candidateDistance;
            }
        }

        if (bestIndex < 0)
        {
            lastAssignmentReason = "assignment_failed_no_valid_candidate";
            return;
        }

        assignedFishingAnchorIndex = bestIndex;
        assignedFishingAnchorId = GetAnchorId(bestIndex);
        assignedFishingCastTargetId = GetCastTargetId(bestIndex);
        hasFishingAssignment = true;
        lastAssignmentReason = $"assignment_created_excluding_{playerAnchorId}_selected_{assignedFishingAnchorId}";
        RefreshAssignedAnchorTruth();
    }

    private void RefreshAssignedAnchorTruth()
    {
        companionAtAssignedFishingAnchor = false;
        companionDistanceToAssignedFishingAnchor = -1f;

        if (!hasFishingAssignment || assignedFishingAnchorIndex < 0 || assignedFishingAnchorIndex >= AnchorCount)
        {
            return;
        }

        Transform assignedAnchorTransform = anchorTransforms[assignedFishingAnchorIndex];
        if (assignedAnchorTransform == null)
        {
            lastAssignmentReason = "assignment_anchor_transform_missing_after_assignment";
            return;
        }

        if (companionRoot == null)
        {
            lastAssignmentReason = "assignment_active_companion_root_missing";
            return;
        }

        companionDistanceToAssignedFishingAnchor = DistanceXZ(companionRoot.position, assignedAnchorTransform.position);
        companionAtAssignedFishingAnchor = companionDistanceToAssignedFishingAnchor <= AnchorArrivalTolerance;
    }

    private void ClearRuntimeState()
    {
        playerOccupiedFishingAnchorId = string.Empty;
        companionNearestFishingAnchorId = string.Empty;
        companionAtFishingAnchor = false;
        playerNearestAnchorDistance = -1f;
        companionNearestAnchorDistance = -1f;
        companionAtAssignedFishingAnchor = false;
        companionDistanceToAssignedFishingAnchor = -1f;

        for (int i = 0; i < AnchorCount; i++)
        {
            latestAnchorPositions[i] = Vector3.zero;
            latestCastTargetPositions[i] = Vector3.zero;

            if (anchorTransforms[i] != null)
            {
                anchorTransforms[i].gameObject.SetActive(false);
            }
        }

        HideCastTargets();
    }

    private void HideCastTargets()
    {
        for (int i = 0; i < AnchorCount; i++)
        {
            latestCastTargetPositions[i] = Vector3.zero;

            if (castTargetTransforms[i] != null)
            {
                castTargetTransforms[i].gameObject.SetActive(false);
            }
        }
    }

    private int GetIndexFromSemanticName(string semanticName, string prefix)
    {
        if (string.IsNullOrEmpty(semanticName) || !semanticName.StartsWith(prefix))
        {
            return -1;
        }

        string suffix = semanticName.Substring(prefix.Length);
        if (!int.TryParse(suffix, out int parsedIndex))
        {
            return -1;
        }

        if (parsedIndex < 0 || parsedIndex >= AnchorCount)
        {
            return -1;
        }

        return parsedIndex;
    }

    private string GetAnchorId(int index)
    {
        return $"Anchor_{index}";
    }

    private string GetCastTargetId(int index)
    {
        return $"CastTarget_{index}";
    }

    private void OnDrawGizmos()
    {
        if (!showDebugGizmos)
        {
            return;
        }

        if (!TryBuildAnchorBasis(out Vector3 anchorBasePosition, out Vector3 shoreDirection, out _))
        {
            return;
        }

        Vector3[] anchorPositions = new Vector3[AnchorCount];
        for (int i = 0; i < AnchorCount; i++)
        {
            anchorPositions[i] = anchorBasePosition + shoreDirection * Mathf.Max(0f, anchorSpacing) * i;
        }

        Gizmos.color = new Color(1f, 0.65f, 0.1f, 1f);
        for (int i = 0; i < AnchorCount; i++)
        {
            Gizmos.DrawSphere(anchorPositions[i], 0.07f);
        }

        Gizmos.DrawLine(anchorPositions[0], anchorPositions[1]);
        Gizmos.DrawLine(anchorPositions[1], anchorPositions[2]);

        if (!drawCastTargetLines || !TryBuildCastDirection(out Vector3 castDirection, out _))
        {
            return;
        }

        Gizmos.color = new Color(0.2f, 0.85f, 1f, 1f);
        for (int i = 0; i < AnchorCount; i++)
        {
            Vector3 castTargetPosition =
                anchorPositions[i] +
                castDirection * Mathf.Max(0f, castTargetDistance) +
                Vector3.up * castTargetHeightOffset;

            Gizmos.DrawSphere(castTargetPosition, 0.05f);
            Gizmos.DrawLine(anchorPositions[i], castTargetPosition);
        }
    }
}
