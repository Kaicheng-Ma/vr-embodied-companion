using UnityEngine;
using TMPro;

public class RoastDebugUI : MonoBehaviour
{
    [Header("References")]
    public RoastingStateBlackboard blackboard;
    public TMP_Text stateText;

    private void Update()
    {
        if (blackboard == null || stateText == null)
            return;

        stateText.text =
            $"Campfire: {blackboard.campfire_started}\n" +
            $"Stick Held: {blackboard.stick_picked}\n" +
            $"Marshmallow: {blackboard.marshmallow_skewered}\n" +
            $"In Roast Zone: {blackboard.marshmallow_in_roasting_zone}\n" +
            $"Roast Done: {blackboard.roasting_duration_reached}\n" +
            $"Eaten: {blackboard.marshmallow_eaten}\n" +
            $"Timer: {blackboard.currentRoastingTime:F1} / {blackboard.requiredRoastingDuration:F1}";
    }
}