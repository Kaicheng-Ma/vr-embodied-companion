using UnityEngine;
using UnityEngine.InputSystem;

public class CampfireStartInteraction : MonoBehaviour
{
    [Header("References")]
    public RoastingStateBlackboard blackboard;
    public GameObject campfirePrefab;

    [Header("Placement Hint")]
    [SerializeField] private GameObject campfirePlacementHintVisual;

    [Header("Quest 2 Input")]
    public InputActionReference startAction;

    [Header("Debug Input")]
    public bool allowKeyboardDebug = true;
    public KeyCode debugKey = KeyCode.F;

    private bool playerInRange = false;

    private void Start()
    {
        RefreshPlacementHintVisibility();
    }

    private void OnEnable()
    {
        if (startAction != null && startAction.action != null)
        {
            startAction.action.Enable();
        }
    }

    private void OnDisable()
    {
        if (startAction != null && startAction.action != null)
        {
            startAction.action.Disable();
        }
    }

    private void Update()
    {
        if (!playerInRange)
            return;

        if (blackboard == null)
            return;

        if (IsCampfirePresent())
        {
            return;
        }

        bool inputPressed = false;

        if (startAction != null && startAction.action != null)
        {
            inputPressed = startAction.action.WasPressedThisFrame();
        }

        if (allowKeyboardDebug && Keyboard.current != null)
        {
            inputPressed = inputPressed || Keyboard.current.fKey.wasPressedThisFrame;
        }

        if (inputPressed)
        {
            StartCampfire();
        }
    }

    private void StartCampfire()
    {
        if (campfirePrefab == null)
        {
            Debug.LogWarning("CampfireStartInteraction: campfirePrefab is not assigned.");
            return;
        }

        if (blackboard == null)
        {
            Debug.LogWarning("CampfireStartInteraction: blackboard is not assigned.");
            return;
        }

        if (IsCampfirePresent())
        {
            RefreshPlacementHintVisibility();
            return;
        }

        if (blackboard.campfireSpawnPoint == null)
        {
            Debug.LogWarning("CampfireStartInteraction: campfireSpawnPoint is not assigned.");
            return;
        }

        GameObject newCampfire = Instantiate(
            campfirePrefab,
            blackboard.campfireSpawnPoint.position,
            blackboard.campfireSpawnPoint.rotation,
            blackboard.campfireRuntimeParent
        );

        blackboard.currentCampfireInstance = newCampfire;

        RoastZone roastZone = newCampfire.GetComponentInChildren<RoastZone>();

        if (roastZone != null)
        {
            roastZone.blackboard = blackboard;
            Debug.Log("RoastZone linked to blackboard.");
        }
        else
        {
            Debug.LogWarning("CampfireStartInteraction: no RoastZone found in generated campfire prefab.");
        }

        blackboard.campfire_started = true;
        RefreshPlacementHintVisibility();

        Debug.Log("Campfire started.");
    }

    private void RefreshPlacementHintVisibility()
    {
        if (campfirePlacementHintVisual == null)
            return;

        if (blackboard == null)
            return;

        bool shouldShowHint = !IsCampfirePresent();

        if (campfirePlacementHintVisual.activeSelf != shouldShowHint)
        {
            campfirePlacementHintVisual.SetActive(shouldShowHint);
        }
    }

    // Keep the placement hint in sync with the authoritative campfire state without runtime hierarchy searches.
    private bool IsCampfirePresent()
    {
        return blackboard != null &&
            (blackboard.campfire_started || blackboard.currentCampfireInstance != null);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player") || other.GetComponentInParent<CharacterController>() != null)
        {
            playerInRange = true;
            Debug.Log("Player entered campfire start range.");
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player") || other.GetComponentInParent<CharacterController>() != null)
        {
            playerInRange = false;
            Debug.Log("Player exited campfire start range.");
        }
    }
}
