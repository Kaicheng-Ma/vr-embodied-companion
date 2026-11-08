using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class FishingRodPickupStateReporter : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private FishingStateBlackboard blackboard;

    [Tooltip("Normally found automatically on this GameObject.")]
    [SerializeField] private XRGrabInteractable grabInteractable;

    [Header("Keyboard Debug")]
    [SerializeField] private bool allowKeyboardDebug = true;
    [SerializeField] private Key debugToggleHeldKey = Key.H;

    private bool lastHeldState;
    private bool stateInitialized;

    private bool keyboardHeldOverride = false;

    private void Awake()
    {
        if (grabInteractable == null)
        {
            grabInteractable = GetComponent<XRGrabInteractable>();
        }

        if (grabInteractable == null)
        {
            Debug.LogWarning(
                "FishingRodPickupStateReporter: " +
                "XRGrabInteractable not found on this object."
            );
        }
    }

    private void Start()
    {
        UpdateHeldState(forceUpdate: true);
    }

    private void Update()
    {
        HandleKeyboardDebug();
        UpdateHeldState(forceUpdate: false);
    }

    private void HandleKeyboardDebug()
    {
        if (!allowKeyboardDebug)
            return;

        if (Keyboard.current == null)
            return;

        if (Keyboard.current[debugToggleHeldKey].wasPressedThisFrame)
        {
            keyboardHeldOverride = !keyboardHeldOverride;

            Debug.Log(
                "Fishing rod keyboard debug held override = " +
                keyboardHeldOverride
            );
        }
    }

    private void UpdateHeldState(bool forceUpdate)
    {
        if (blackboard == null)
            return;

        bool xrHeld =
            grabInteractable != null &&
            grabInteractable.isSelected;

        bool currentlyHeld =
            xrHeld || keyboardHeldOverride;

        if (!forceUpdate &&
            stateInitialized &&
            currentlyHeld == lastHeldState)
        {
            return;
        }

        stateInitialized = true;
        lastHeldState = currentlyHeld;
        blackboard.rodPicked = currentlyHeld;

        Debug.Log(
            currentlyHeld
                ? "Fishing rod held. rodPicked = true"
                : "Fishing rod released. rodPicked = false"
        );
    }
}