using UnityEngine;

public class RoastTimer : MonoBehaviour
{
    [Header("References")]
    public RoastingStateBlackboard blackboard;

    [Header("Options")]
    public bool requireCampfireStarted = true;
    public bool requireMarshmallowSkewered = true;

    private void Update()
    {
        if (blackboard == null)
            return;

        if (blackboard.roasting_duration_reached)
            return;

        if (requireCampfireStarted && !blackboard.campfire_started)
            return;

        if (requireMarshmallowSkewered && !blackboard.marshmallow_skewered)
            return;

        if (!blackboard.marshmallow_in_roasting_zone)
            return;

        blackboard.currentRoastingTime += Time.deltaTime;

        if (blackboard.currentRoastingTime >= blackboard.requiredRoastingDuration)
        {
            blackboard.currentRoastingTime = blackboard.requiredRoastingDuration;
            blackboard.roasting_duration_reached = true;

            if (blackboard.currentMarshmallowInstance != null)
            {
                MarshmallowRoastVisualState visualState =
                    blackboard.currentMarshmallowInstance.GetComponent<MarshmallowRoastVisualState>();

                if (visualState != null)
                {
                    visualState.SetRoasted();
                }
            }

            Debug.Log("Roasting duration reached.");
        }
    }
}