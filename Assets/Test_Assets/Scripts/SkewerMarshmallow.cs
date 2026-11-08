using UnityEngine;
using UnityEngine.InputSystem;

public class SkewerMarshmallow : MonoBehaviour
{
    [Header("References")]
    public RoastingStateBlackboard blackboard;
    public GameObject marshmallowPrefab;

    [Header("Input")]
    public InputActionReference skewerAction;
    public bool allowKeyboardDebug = true;

    [Header("Debug")]
    public bool logSpawnInfo = true;

    private void OnEnable()
    {
        if (skewerAction != null && skewerAction.action != null)
        {
            skewerAction.action.Enable();
        }
    }

    private void OnDisable()
    {
        if (skewerAction != null && skewerAction.action != null)
        {
            skewerAction.action.Disable();
        }
    }

    private void Update()
    {
        if (blackboard == null)
            return;

        if (blackboard.marshmallow_skewered)
            return;

        bool pressed = false;

        if (skewerAction != null && skewerAction.action != null)
        {
            pressed = skewerAction.action.WasPressedThisFrame();
        }

        if (allowKeyboardDebug && Keyboard.current != null)
        {
            pressed = pressed || Keyboard.current.mKey.wasPressedThisFrame;
        }

        if (pressed)
        {
            TrySkewer();
        }
    }

    private void TrySkewer()
    {
        if (!blackboard.stick_picked)
        {
            Debug.Log("Cannot skewer: stick is not held.");
            return;
        }

        if (marshmallowPrefab == null)
        {
            Debug.LogWarning("SkewerMarshmallow: marshmallowPrefab is not assigned.");
            return;
        }

        if (blackboard.marshmallowTipAnchor == null)
        {
            Debug.LogWarning("SkewerMarshmallow: marshmallowTipAnchor is not assigned.");
            return;
        }

        Transform anchor = blackboard.marshmallowTipAnchor;

        GameObject marshmallow = Instantiate(marshmallowPrefab);

        marshmallow.transform.position = anchor.position;
        marshmallow.transform.rotation = anchor.rotation;

        // true = 保持世界坐标/世界缩放，避免被 Stick 的缩放压得太小
        marshmallow.transform.SetParent(anchor, true);

        blackboard.currentMarshmallowInstance = marshmallow;

        MarshmallowRoastVisualState visualState =
            marshmallow.GetComponent<MarshmallowRoastVisualState>();

        if (visualState != null)
        {
            visualState.SetRaw();
        }

        blackboard.marshmallow_skewered = true;

        // Reset per-marshmallow states for a new roasting cycle
        blackboard.marshmallow_eaten = false;
        blackboard.marshmallow_in_roasting_zone = false;
        blackboard.roasting_duration_reached = false;
        blackboard.currentRoastingTime = 0f;

        if (logSpawnInfo)
        {
            Debug.Log(
                "Marshmallow skewered at world position: " +
                marshmallow.transform.position +
                ", lossyScale: " +
                marshmallow.transform.lossyScale
            );
        }
        else
        {
            Debug.Log("Marshmallow skewered.");
        }
    }
}