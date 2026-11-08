using System.Collections.Generic;
using UnityEngine;

public class CP_RoastingAnchorManager : MonoBehaviour
{
    [Header("Core References")]
    public RoastingStateBlackboard blackboard;
    public Transform playerTransform;
    public Transform companionRoot;
    public Transform anchorsParent;

    [Header("Anchor Settings")]
    public float anchorRadius = 1.45f;
    public float anchorHeightOffset = 0f;
    public float companionArriveDistance = 0.25f;
    public bool regenerateAnchorsEveryFrame = true;

    [Header("Runtime Anchor Results")]
    public string playerOccupiedAnchorId;
    public string nearestFreeAnchorId;
    public bool companionAtValidRoastingAnchor;

    [Header("Runtime Distance Debug")]
    public float companionDistanceToNearestFreeAnchor = -1f;
    public float playerDistanceToAnchor0 = -1f;
    public float playerDistanceToAnchor1 = -1f;
    public float playerDistanceToAnchor2 = -1f;
    public float companionDistanceToAnchor0 = -1f;
    public float companionDistanceToAnchor1 = -1f;
    public float companionDistanceToAnchor2 = -1f;

    [Header("Runtime Position Debug")]
    public Vector3 fireCenterWorldPosition;
    public Vector3 playerWorldPosition;
    public Vector3 companionWorldPosition;
    public Vector3 nearestFreeAnchorWorldPosition;

    [Header("Debug Visualization")]
    public bool showDebugGizmos = true;
    public bool createAnchorVisualObjects = true;
    public float visualSphereSize = 0.15f;

    [Header("Manual Debug")]
    public bool manualRefreshAnchorsNow = false;

    private readonly List<Transform> anchorTransforms = new List<Transform>();

    private Transform cachedFireCenter;

    private void Start()
    {
        EnsureAnchorObjects();
        UpdateFireCenterReference();
        UpdateAnchors();
        UpdateAnchorStatus();
    }

    private void Update()
    {
        if (manualRefreshAnchorsNow)
        {
            manualRefreshAnchorsNow = false;
            EnsureAnchorObjects();
            UpdateFireCenterReference();
            UpdateAnchors();
        }

        UpdateFireCenterReference();

        if (regenerateAnchorsEveryFrame)
        {
            UpdateAnchors();
        }

        UpdateAnchorStatus();
    }

    private void EnsureAnchorObjects()
    {
        if (anchorsParent == null)
        {
            Debug.LogWarning("CP_RoastingAnchorManager: anchorsParent is not assigned.");
            return;
        }

        anchorTransforms.Clear();

        for (int i = 0; i < 3; i++)
        {
            string anchorName = $"Anchor_{i}";
            Transform existing = anchorsParent.Find(anchorName);

            if (existing == null)
            {
                GameObject anchorObject = new GameObject(anchorName);

                // false = keep local transform simple under the anchorsParent.
                // Actual world position is set later by UpdateAnchors().
                anchorObject.transform.SetParent(anchorsParent, false);
                anchorObject.transform.localPosition = Vector3.zero;
                anchorObject.transform.localRotation = Quaternion.identity;
                anchorObject.transform.localScale = Vector3.one;

                existing = anchorObject.transform;
            }

            anchorTransforms.Add(existing);
        }
    }

    private void UpdateFireCenterReference()
    {
        cachedFireCenter = null;
        fireCenterWorldPosition = Vector3.zero;

        if (blackboard == null)
        {
            return;
        }

        // Prefer runtime generated Campfire_Prefab(Clone) -> FireCenterPoint.
        if (blackboard.currentCampfireInstance != null)
        {
            Transform fireCenter = FindChildByName(
                blackboard.currentCampfireInstance.transform,
                "FireCenterPoint"
            );

            if (fireCenter != null)
            {
                cachedFireCenter = fireCenter;
                fireCenterWorldPosition = cachedFireCenter.position;
                return;
            }
        }

        // Fallback before campfire is generated.
        if (blackboard.campfireSpawnPoint != null)
        {
            cachedFireCenter = blackboard.campfireSpawnPoint;
            fireCenterWorldPosition = cachedFireCenter.position;
        }
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

    private void UpdateAnchors()
    {
        if (anchorsParent == null)
        {
            return;
        }

        if (anchorTransforms.Count != 3)
        {
            EnsureAnchorObjects();
        }

        if (cachedFireCenter == null)
        {
            UpdateFireCenterReference();
        }

        if (cachedFireCenter == null)
        {
            return;
        }

        Vector3 center = cachedFireCenter.position;
        center.y += anchorHeightOffset;

        fireCenterWorldPosition = center;

        for (int i = 0; i < 3; i++)
        {
            if (anchorTransforms[i] == null)
            {
                continue;
            }

            float angleDeg = i * 120f;
            float angleRad = angleDeg * Mathf.Deg2Rad;

            Vector3 offset = new Vector3(
                Mathf.Cos(angleRad) * anchorRadius,
                0f,
                Mathf.Sin(angleRad) * anchorRadius
            );

            // Important:
            // center is world position, so we must assign world position here.
            anchorTransforms[i].position = center + offset;
            anchorTransforms[i].rotation = Quaternion.identity;
        }
    }

    private void UpdateAnchorStatus()
    {
        playerOccupiedAnchorId = null;
        nearestFreeAnchorId = null;
        companionAtValidRoastingAnchor = false;

        companionDistanceToNearestFreeAnchor = -1f;
        nearestFreeAnchorWorldPosition = Vector3.zero;

        playerDistanceToAnchor0 = -1f;
        playerDistanceToAnchor1 = -1f;
        playerDistanceToAnchor2 = -1f;

        companionDistanceToAnchor0 = -1f;
        companionDistanceToAnchor1 = -1f;
        companionDistanceToAnchor2 = -1f;

        if (anchorTransforms.Count != 3)
        {
            return;
        }

        if (playerTransform == null || companionRoot == null)
        {
            return;
        }

        playerWorldPosition = playerTransform.position;
        companionWorldPosition = companionRoot.position;

        UpdateDistanceDebugValues();

        int playerOccupiedIndex = GetNearestAnchorIndex2D(playerTransform.position);

        if (playerOccupiedIndex >= 0)
        {
            playerOccupiedAnchorId = anchorTransforms[playerOccupiedIndex].name;
        }

        int nearestFreeIndex = GetNearestFreeAnchorIndex2D(companionRoot.position, playerOccupiedIndex);

        if (nearestFreeIndex >= 0)
        {
            Transform nearestFreeAnchor = anchorTransforms[nearestFreeIndex];

            nearestFreeAnchorId = nearestFreeAnchor.name;
            nearestFreeAnchorWorldPosition = nearestFreeAnchor.position;

            companionDistanceToNearestFreeAnchor = DistanceXZ(
                companionRoot.position,
                nearestFreeAnchor.position
            );

            companionAtValidRoastingAnchor =
                companionDistanceToNearestFreeAnchor <= companionArriveDistance;
        }
    }

    private void UpdateDistanceDebugValues()
    {
        if (anchorTransforms.Count != 3)
        {
            return;
        }

        if (playerTransform != null)
        {
            if (anchorTransforms[0] != null)
                playerDistanceToAnchor0 = DistanceXZ(playerTransform.position, anchorTransforms[0].position);

            if (anchorTransforms[1] != null)
                playerDistanceToAnchor1 = DistanceXZ(playerTransform.position, anchorTransforms[1].position);

            if (anchorTransforms[2] != null)
                playerDistanceToAnchor2 = DistanceXZ(playerTransform.position, anchorTransforms[2].position);
        }

        if (companionRoot != null)
        {
            if (anchorTransforms[0] != null)
                companionDistanceToAnchor0 = DistanceXZ(companionRoot.position, anchorTransforms[0].position);

            if (anchorTransforms[1] != null)
                companionDistanceToAnchor1 = DistanceXZ(companionRoot.position, anchorTransforms[1].position);

            if (anchorTransforms[2] != null)
                companionDistanceToAnchor2 = DistanceXZ(companionRoot.position, anchorTransforms[2].position);
        }
    }

    private int GetNearestAnchorIndex2D(Vector3 fromPosition)
    {
        if (anchorTransforms.Count == 0)
        {
            return -1;
        }

        int nearestIndex = -1;
        float nearestDistance = float.MaxValue;

        for (int i = 0; i < anchorTransforms.Count; i++)
        {
            if (anchorTransforms[i] == null)
            {
                continue;
            }

            float distance = DistanceXZ(fromPosition, anchorTransforms[i].position);

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }

        return nearestIndex;
    }

    private int GetNearestFreeAnchorIndex2D(Vector3 fromPosition, int playerOccupiedIndex)
    {
        int nearestIndex = -1;
        float nearestDistance = float.MaxValue;

        for (int i = 0; i < anchorTransforms.Count; i++)
        {
            if (i == playerOccupiedIndex)
            {
                continue;
            }

            if (anchorTransforms[i] == null)
            {
                continue;
            }

            float distance = DistanceXZ(fromPosition, anchorTransforms[i].position);

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }

        return nearestIndex;
    }

    public Transform GetNearestFreeAnchorTransform()
    {
        return GetAnchorByName(nearestFreeAnchorId);
    }

    public string NearestFreeAnchorId => nearestFreeAnchorId;
    public bool CompanionAtValidRoastingAnchor => companionAtValidRoastingAnchor;

    public Transform GetPlayerOccupiedAnchorTransform()
    {
        return GetAnchorByName(playerOccupiedAnchorId);
    }

    public Transform GetAnchorByName(string anchorId)
    {
        if (string.IsNullOrEmpty(anchorId))
        {
            return null;
        }

        foreach (Transform anchor in anchorTransforms)
        {
            if (anchor != null && anchor.name == anchorId)
            {
                return anchor;
            }
        }

        return null;
    }

    public bool TryGetAnchorTransform(string anchorId, out Transform anchor)
    {
        anchor = GetAnchorByName(anchorId);
        return anchor != null;
    }

    public bool IsPlayerOccupiedAnchor(string anchorId)
    {
        return !string.IsNullOrWhiteSpace(anchorId) &&
               string.Equals(anchorId, playerOccupiedAnchorId, System.StringComparison.Ordinal);
    }

    public float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;

        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private void OnDrawGizmos()
    {
        if (!showDebugGizmos)
        {
            return;
        }

        DrawRuntimeAnchorGizmos();
    }

    private void DrawRuntimeAnchorGizmos()
    {
        if (anchorTransforms == null || anchorTransforms.Count == 0)
        {
            return;
        }

        foreach (Transform anchor in anchorTransforms)
        {
            if (anchor == null)
            {
                continue;
            }

            if (anchor.name == playerOccupiedAnchorId)
            {
                Gizmos.DrawWireSphere(anchor.position, visualSphereSize * 1.6f);
            }
            else if (anchor.name == nearestFreeAnchorId)
            {
                Gizmos.DrawSphere(anchor.position, visualSphereSize);
            }
            else
            {
                Gizmos.DrawWireCube(anchor.position, Vector3.one * visualSphereSize);
            }
        }

        if (cachedFireCenter != null)
        {
            Gizmos.DrawWireSphere(cachedFireCenter.position, anchorRadius);
        }

        if (companionRoot != null)
        {
            Gizmos.DrawWireSphere(companionRoot.position, visualSphereSize * 1.2f);
        }

        Transform nearestFreeAnchor = GetNearestFreeAnchorTransform();

        if (companionRoot != null && nearestFreeAnchor != null)
        {
            Gizmos.DrawLine(companionRoot.position, nearestFreeAnchor.position);
        }
    }
}
