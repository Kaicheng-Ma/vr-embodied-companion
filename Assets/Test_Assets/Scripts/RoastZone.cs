using UnityEngine;

public class RoastZone : MonoBehaviour
{
    [Header("References")]
    public RoastingStateBlackboard blackboard;

    [Header("Detection")]
    public string marshmallowTag = "Marshmallow";

    private int marshmallowInsideCount = 0;

    private void OnTriggerEnter(Collider other)
    {
        if (!IsMarshmallow(other))
            return;

        marshmallowInsideCount++;

        if (blackboard != null)
        {
            blackboard.marshmallow_in_roasting_zone = true;
        }

        Debug.Log("Marshmallow entered roast zone.");
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsMarshmallow(other))
            return;

        marshmallowInsideCount = Mathf.Max(0, marshmallowInsideCount - 1);

        if (blackboard != null && marshmallowInsideCount == 0)
        {
            blackboard.marshmallow_in_roasting_zone = false;
        }

        Debug.Log("Marshmallow exited roast zone.");
    }

    public void ResetZoneState()
    {
        marshmallowInsideCount = 0;

        if (blackboard != null)
        {
            blackboard.marshmallow_in_roasting_zone = false;
        }
    }

    private bool IsMarshmallow(Collider other)
    {
        if (other.CompareTag(marshmallowTag))
            return true;

        Transform parent = other.transform.parent;
        while (parent != null)
        {
            if (parent.CompareTag(marshmallowTag))
                return true;

            parent = parent.parent;
        }

        return false;
    }
}