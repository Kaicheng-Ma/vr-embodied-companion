using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

public class StickPickupStateReporter : MonoBehaviour
{
    [Header("References")]
    public RoastingStateBlackboard blackboard;

    private UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable;

    private void Awake()
    {
        grabInteractable = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();

        if (grabInteractable == null)
        {
            Debug.LogWarning("StickPickupStateReporter: XRGrabInteractable not found on this object.");
        }
    }

    private void OnEnable()
    {
        if (grabInteractable != null)
        {
            grabInteractable.selectEntered.AddListener(OnStickPicked);
            grabInteractable.selectExited.AddListener(OnStickReleased);
        }
    }

    private void OnDisable()
    {
        if (grabInteractable != null)
        {
            grabInteractable.selectEntered.RemoveListener(OnStickPicked);
            grabInteractable.selectExited.RemoveListener(OnStickReleased);
        }
    }

    private void OnStickPicked(SelectEnterEventArgs args)
    {
        if (blackboard == null)
        {
            Debug.LogWarning("StickPickupStateReporter: Blackboard is not assigned.");
            return;
        }

        blackboard.stick_picked = true;
        Debug.Log("Stick is currently held. stick_picked = true");
    }

    private void OnStickReleased(SelectExitEventArgs args)
    {
        if (blackboard == null)
        {
            Debug.LogWarning("StickPickupStateReporter: Blackboard is not assigned.");
            return;
        }

        blackboard.stick_picked = false;
        Debug.Log("Stick released. stick_picked = false");
    }
}