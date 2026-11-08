using UnityEngine;
using UnityEngine.InputSystem;

public class EatMarshmallow : MonoBehaviour
{
    [Header("References")]
    public RoastingStateBlackboard blackboard;

    [Header("Input")]
    public InputActionReference eatAction;
    public bool allowKeyboardDebug = true;

    [Header("Eat Effect Placeholders")]
    public ParticleSystem crumbEffectPrefab;
    public AudioClip eatSound;
    public AudioSource audioSource;

    [Header("Options")]
    public bool requireRoastingDone = true;
    public bool requireStickHeld = true;
    public bool destroyMarshmallowOnEat = true;

    private void OnEnable()
    {
        if (eatAction != null && eatAction.action != null)
        {
            eatAction.action.Enable();
        }
    }

    private void OnDisable()
    {
        if (eatAction != null && eatAction.action != null)
        {
            eatAction.action.Disable();
        }
    }

    private void Update()
    {
        if (blackboard == null)
            return;

        if (blackboard.marshmallow_eaten)
            return;

        bool pressed = false;

        if (eatAction != null && eatAction.action != null)
        {
            pressed = eatAction.action.WasPressedThisFrame();
        }

        if (allowKeyboardDebug && Keyboard.current != null)
        {
            pressed = pressed || Keyboard.current.xKey.wasPressedThisFrame;
        }

        if (pressed)
        {
            TryEat();
        }
    }

    private void TryEat()
    {
        if (requireRoastingDone && !blackboard.roasting_duration_reached)
        {
            Debug.Log("Cannot eat: marshmallow is not roasted yet.");
            return;
        }

        if (requireStickHeld && !blackboard.stick_picked)
        {
            Debug.Log("Cannot eat: stick is not currently held.");
            return;
        }

        if (blackboard.currentMarshmallowInstance == null)
        {
            Debug.LogWarning("EatMarshmallow: no current marshmallow instance.");
            return;
        }

        Vector3 eatPosition = blackboard.currentMarshmallowInstance.transform.position;

        PlayEatEffects(eatPosition);

        if (blackboard.currentCampfireInstance != null)
        {
            RoastZone roastZone = blackboard.currentCampfireInstance.GetComponentInChildren<RoastZone>();

            if (roastZone != null)
            {
                roastZone.ResetZoneState();
            }
        }

        if (destroyMarshmallowOnEat)
        {
            Destroy(blackboard.currentMarshmallowInstance);
        }
        else
        {
            blackboard.currentMarshmallowInstance.SetActive(false);
        }
        
        blackboard.currentMarshmallowInstance = null;
        blackboard.marshmallow_eaten = true;

        // 吃掉后，棉花糖不再处于火区，也不再算插在树枝上
        blackboard.marshmallow_in_roasting_zone = false;
        blackboard.marshmallow_skewered = false;

        Debug.Log("Marshmallow eaten.");
    }

    private void PlayEatEffects(Vector3 position)
    {
        if (crumbEffectPrefab != null)
        {
            Instantiate(crumbEffectPrefab, position, Quaternion.identity);
        }

        if (eatSound != null)
        {
            if (audioSource != null)
            {
                audioSource.PlayOneShot(eatSound);
            }
            else
            {
                AudioSource.PlayClipAtPoint(eatSound, position);
            }
        }
    }
}