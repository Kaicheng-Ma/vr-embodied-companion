using UnityEngine;

public class RoastingStateBlackboard : MonoBehaviour
{
    [Header("Core Truth Flags")]
    public bool campfire_started = false;
    public bool stick_picked = false;
    public bool marshmallow_skewered = false;
    public bool marshmallow_in_roasting_zone = false;
    public bool roasting_duration_reached = false;

    [Header("Campfire References")]
    public Transform campfireSpawnPoint;
    public Transform campfireRuntimeParent;
    public GameObject currentCampfireInstance;

    [Header("Stick / Marshmallow References")]
    public GameObject stickObject;
    public Transform marshmallowTipAnchor;
    public GameObject currentMarshmallowInstance;

    [Header("Roasting Timer")]
    public float currentRoastingTime = 0f;
    public float requiredRoastingDuration = 10f;

    [Header("Optional End Interaction")]
    public bool marshmallow_eaten = false;

    public void ResetRoastingProgress()
    {
        marshmallow_in_roasting_zone = false;
        roasting_duration_reached = false;
        currentRoastingTime = 0f;
    }
}