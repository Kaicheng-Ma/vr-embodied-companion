using UnityEngine;

public class CP_InteractionArea : MonoBehaviour
{
    [Header("References")]
    public Transform companionRoot;

    [Header("Area Settings")]
    public float areaRadius = 2.0f;
    public bool useXZDistanceOnly = true;

    [Header("Runtime Results")]
    public bool companionInsideArea = false;
    public float companionDistanceToAreaCenter = -1f;

    [Header("Runtime Position Debug")]
    public Vector3 areaCenterWorldPosition;
    public Vector3 companionWorldPosition;

    [Header("Debug Visualization")]
    public bool showDebugGizmos = true;
    public bool drawCompanionLine = true;

    private void Update()
    {
        UpdateAreaStatus();
    }

    private void UpdateAreaStatus()
    {
        companionInsideArea = false;
        companionDistanceToAreaCenter = -1f;

        areaCenterWorldPosition = transform.position;

        if (companionRoot == null)
        {
            companionWorldPosition = Vector3.zero;
            return;
        }

        companionWorldPosition = companionRoot.position;

        if (useXZDistanceOnly)
        {
            companionDistanceToAreaCenter = DistanceXZ(
                companionRoot.position,
                transform.position
            );
        }
        else
        {
            companionDistanceToAreaCenter = Vector3.Distance(
                companionRoot.position,
                transform.position
            );
        }

        companionInsideArea = companionDistanceToAreaCenter <= areaRadius;
    }

    public float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;

        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    public Vector3 GetAreaCenterWorldPosition()
    {
        return transform.position;
    }

    public Vector3 CenterWorldPosition => transform.position;
    public float Radius => areaRadius;

    public bool IsCompanionInsideArea()
    {
        return companionInsideArea;
    }

    public float GetCompanionDistanceToAreaCenter()
    {
        return companionDistanceToAreaCenter;
    }

    public bool ContainsXZ(Vector3 worldPosition)
    {
        float distance = useXZDistanceOnly
            ? DistanceXZ(worldPosition, transform.position)
            : Vector3.Distance(worldPosition, transform.position);

        return distance <= areaRadius;
    }

    // Stage 5A helper only: returns a semantic interior candidate that MovementController
    // can later project onto the NavMesh. This does not own movement execution.
    public bool TryGetNearestInteriorPoint(
        Vector3 fromWorldPosition,
        float interiorMargin,
        out Vector3 point
    )
    {
        Vector3 center = transform.position;
        point = center;

        float safeRadius = Mathf.Max(0f, areaRadius - Mathf.Max(0f, interiorMargin));
        if (safeRadius <= 0f)
        {
            point = center;
            return true;
        }

        Vector3 direction = fromWorldPosition - center;
        if (useXZDistanceOnly)
        {
            direction.y = 0f;
        }

        if (direction.sqrMagnitude <= 0.0001f)
        {
            point = center;
            return true;
        }

        float sourceDistance = useXZDistanceOnly
            ? DistanceXZ(fromWorldPosition, center)
            : direction.magnitude;

        if (sourceDistance <= safeRadius)
        {
            point = fromWorldPosition;
            if (useXZDistanceOnly)
            {
                point.y = center.y;
            }
            return true;
        }

        Vector3 directionNormalized = direction.normalized;
        point = center + directionNormalized * safeRadius;
        if (useXZDistanceOnly)
        {
            point.y = center.y;
        }

        return true;
    }

    private void OnDrawGizmos()
    {
        if (!showDebugGizmos)
            return;

        Vector3 center = transform.position;

        Gizmos.DrawWireSphere(center, areaRadius);

        if (drawCompanionLine && companionRoot != null)
        {
            Gizmos.DrawLine(center, companionRoot.position);
            Gizmos.DrawWireSphere(companionRoot.position, 0.15f);
        }
    }
}
