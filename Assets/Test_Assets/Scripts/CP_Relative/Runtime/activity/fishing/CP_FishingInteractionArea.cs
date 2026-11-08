using UnityEngine;

// Fishing-specific semantic area for the future F2 entry boundary.
// This class owns only simple geometry truth and does not perform NavMesh work.
public class CP_FishingInteractionArea : MonoBehaviour
{
    [Header("Geometry")]
    [SerializeField] private Transform areaCenter;
    [SerializeField] private float radius = 2f;
    [SerializeField] private bool useXZDistanceOnly = true;

    [Header("Debug")]
    [SerializeField] private bool showDebugGizmos = true;

    public Transform AreaCenter => areaCenter;
    public float Radius => Mathf.Max(0f, radius);
    public bool UseXZDistanceOnly => useXZDistanceOnly;
    public bool IsSpatialReady => isActiveAndEnabled && Radius > 0f;

    public Vector3 GetAreaCenterWorldPosition()
    {
        return areaCenter != null ? areaCenter.position : transform.position;
    }

    public bool ContainsXZ(Vector3 worldPosition)
    {
        Vector3 center = GetAreaCenterWorldPosition();
        return DistanceXZ(worldPosition, center) <= Radius;
    }

    public float GetDistanceToCenter(Vector3 worldPosition)
    {
        Vector3 center = GetAreaCenterWorldPosition();

        if (useXZDistanceOnly)
        {
            return DistanceXZ(worldPosition, center);
        }

        return Vector3.Distance(worldPosition, center);
    }

    // Returns a geometry-only interior point so shared movement code can later
    // project it onto NavMesh without this component owning path logic.
    public bool TryGetNearestInteriorPoint(
        Vector3 fromWorldPosition,
        float interiorMargin,
        out Vector3 point)
    {
        point = Vector3.zero;

        float effectiveRadius = Radius;
        if (effectiveRadius <= 0f)
        {
            return false;
        }

        Vector3 center = GetAreaCenterWorldPosition();
        Vector3 horizontal = fromWorldPosition - center;
        horizontal.y = 0f;

        float maxRadius = Mathf.Max(0f, effectiveRadius - Mathf.Max(0f, interiorMargin));
        if (maxRadius <= 0f)
        {
            point = center;
            return true;
        }

        if (horizontal.sqrMagnitude <= maxRadius * maxRadius)
        {
            point = new Vector3(fromWorldPosition.x, center.y, fromWorldPosition.z);
            return true;
        }

        Vector3 direction = horizontal.normalized;
        point = center + direction * maxRadius;
        point.y = center.y;
        return true;
    }

    private float DistanceXZ(Vector3 a, Vector3 b)
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

        Vector3 center = GetAreaCenterWorldPosition();
        Gizmos.color = new Color(0.1f, 0.75f, 1f, 1f);
        Gizmos.DrawWireSphere(center, Radius);
        Gizmos.DrawSphere(center, 0.06f);
    }
}
