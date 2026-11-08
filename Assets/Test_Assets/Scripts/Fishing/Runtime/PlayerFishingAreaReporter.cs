using System.Collections.Generic;
using UnityEngine;

public class PlayerFishingAreaReporter : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private FishingStateBlackboard blackboard;

    [Tooltip("Normally drag XR Origin (VR) here.")]
    [SerializeField] private Transform playerRoot;

    private readonly HashSet<Collider> playerCollidersInArea =
        new HashSet<Collider>();

    private bool warnedMissingPlayerRoot;

    // This reporter only writes playerInsideFishingArea; flow decisions live elsewhere.
    private void Start()
    {
        if (blackboard == null)
        {
            Debug.LogWarning(
                "PlayerFishingAreaReporter: Blackboard is not assigned."
            );
        }

        if (playerRoot == null)
        {
            Debug.LogWarning(
                "PlayerFishingAreaReporter: Player Root is not assigned. " +
                "Player area detection will reject all colliders until it is assigned."
            );
        }

        RefreshBlackboardState();
    }

    private void OnDisable()
    {
        playerCollidersInArea.Clear();

        if (blackboard != null)
        {
            blackboard.playerInsideFishingArea = false;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsPlayerCollider(other))
            return;

        playerCollidersInArea.Add(other);
        RefreshBlackboardState();

        Debug.Log("Player entered PlayerFishingArea.");
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsPlayerCollider(other))
            return;

        playerCollidersInArea.Remove(other);
        RefreshBlackboardState();

        Debug.Log("Player exited PlayerFishingArea.");
    }

    private bool IsPlayerCollider(Collider other)
    {
        // Require the configured player root so random trigger colliders cannot count as the player.
        if (other == null)
            return false;

        if (playerRoot == null)
        {
            if (!warnedMissingPlayerRoot)
            {
                warnedMissingPlayerRoot = true;
                Debug.LogWarning(
                    "PlayerFishingAreaReporter: Player Root is missing. " +
                    "Ignoring trigger collider."
                );
            }

            return false;
        }

        Transform otherTransform = other.transform;

        return otherTransform == playerRoot ||
               otherTransform.IsChildOf(playerRoot);
    }

    private void RefreshBlackboardState()
    {
        if (blackboard == null)
            return;

        blackboard.playerInsideFishingArea =
            playerCollidersInArea.Count > 0;
    }
}
