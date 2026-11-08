using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class FishingBucketStartInteraction : MonoBehaviour
{
    [Header("Core References")]
    [SerializeField] private FishingStateBlackboard blackboard;
    [SerializeField] private GameObject bucketPrefab;
    [SerializeField] private GameObject bucketPlacementHintVisual;

    [Header("Player")]
    [Tooltip("Normally drag XR Origin (VR) here.")]
    [SerializeField] private Transform playerRoot;

    [Header("Quest Input")]
    [SerializeField] private InputActionReference placeBucketAction;

    [Header("Keyboard Debug")]
    [SerializeField] private bool allowKeyboardDebug = true;
    [SerializeField] private Key keyboardDebugKey = Key.F;

    private readonly HashSet<Collider> playerCollidersInRange =
        new HashSet<Collider>();

    private bool PlayerInRange => playerCollidersInRange.Count > 0;

    private void Start()
    {
        if (blackboard == null)
        {
            Debug.LogWarning(
                "FishingBucketStartInteraction: Blackboard is not assigned."
            );
        }

        if (playerRoot == null)
        {
            Debug.LogWarning(
                "FishingBucketStartInteraction: Player Root is not assigned. " +
                "Trigger filtering will fall back to accepting any collider."
            );
        }

        RefreshHintVisibility();
    }

    private void OnEnable()
    {
        if (placeBucketAction != null &&
            placeBucketAction.action != null)
        {
            placeBucketAction.action.Enable();
        }
    }

    private void OnDisable()
    {
        if (placeBucketAction != null &&
            placeBucketAction.action != null)
        {
            placeBucketAction.action.Disable();
        }

        playerCollidersInRange.Clear();
    }

    private void Update()
    {
        if (blackboard == null)
            return;

        // Bucket has already been placed.
        if (blackboard.fishingStarted ||
            blackboard.currentBucketInstance != null)
        {
            RefreshHintVisibility();
            return;
        }

        bool inputPressed = false;

        if (placeBucketAction != null &&
            placeBucketAction.action != null)
        {
            inputPressed =
                placeBucketAction.action.WasPressedThisFrame();
        }

        if (allowKeyboardDebug &&
            Keyboard.current != null)
        {
            inputPressed =
                inputPressed ||
                Keyboard.current[keyboardDebugKey].wasPressedThisFrame;
        }

        if (inputPressed && PlayerInRange)
        {
            TryPlaceBucket();
        }
    }

    private void TryPlaceBucket()
    {
        if (blackboard == null)
        {
            Debug.LogWarning(
                "FishingBucketStartInteraction: Blackboard is not assigned."
            );
            return;
        }

        if (blackboard.fishingStarted ||
            blackboard.currentBucketInstance != null)
        {
            return;
        }

        if (bucketPrefab == null)
        {
            Debug.LogWarning(
                "FishingBucketStartInteraction: Bucket Prefab is not assigned."
            );
            return;
        }

        if (blackboard.bucketSpawnPoint == null)
        {
            Debug.LogWarning(
                "FishingBucketStartInteraction: Bucket Spawn Point is not assigned."
            );
            return;
        }

        GameObject newBucket = Instantiate(
            bucketPrefab,
            blackboard.bucketSpawnPoint.position,
            blackboard.bucketSpawnPoint.rotation
        );

        if (blackboard.bucketRuntimeParent != null)
        {
            newBucket.transform.SetParent(
                blackboard.bucketRuntimeParent,
                true
            );
        }
        else
        {
            Debug.LogWarning(
                "FishingBucketStartInteraction: Bucket Runtime Parent is not assigned. " +
                "Bucket was spawned at Scene root."
            );
        }

        blackboard.currentBucketInstance = newBucket;
        blackboard.fishingStarted = true;

        RefreshHintVisibility();

        Debug.Log(
            "Fishing bucket placed. fishingStarted = true"
        );
    }

    private void RefreshHintVisibility()
    {
        if (bucketPlacementHintVisual == null)
            return;

        bool shouldShow = true;

        if (blackboard != null)
        {
            shouldShow =
                !blackboard.fishingStarted &&
                blackboard.currentBucketInstance == null;
        }

        bucketPlacementHintVisual.SetActive(shouldShow);
    }

    private bool IsPlayerCollider(Collider other)
    {
        if (other == null)
            return false;

        // Compatibility fallback while setting up the Scene.
        if (playerRoot == null)
            return true;

        Transform otherTransform = other.transform;

        return otherTransform == playerRoot ||
               otherTransform.IsChildOf(playerRoot);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsPlayerCollider(other))
            return;

        playerCollidersInRange.Add(other);

        Debug.Log(
            "Player entered fishing bucket start range."
        );
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsPlayerCollider(other))
            return;

        playerCollidersInRange.Remove(other);

        Debug.Log(
            "Player exited fishing bucket start range."
        );
    }
}