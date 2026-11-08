using System.Collections;
using TMPro;
using UnityEngine;

public class FishingCapacityPopupController : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject popupRoot;
    [SerializeField] private TMP_Text messageText;

    [Header("Message")]
    [SerializeField] private string fullMessage = "Basket is full";

    [Min(0.1f)]
    [SerializeField] private float fullMessageDuration = 2.5f;

    private Coroutine hideRoutine;

    private void Awake()
    {
        HidePopup();
    }

    public void ShowCapacityReached()
    {
        if (messageText != null)
        {
            messageText.text = fullMessage;
        }

        if (popupRoot != null)
        {
            popupRoot.SetActive(true);
        }

        if (hideRoutine != null)
        {
            StopCoroutine(hideRoutine);
        }

        hideRoutine = StartCoroutine(HideAfterDelay());
    }

    private IEnumerator HideAfterDelay()
    {
        yield return new WaitForSeconds(fullMessageDuration);
        HidePopup();
        hideRoutine = null;
    }

    private void HidePopup()
    {
        if (popupRoot != null)
        {
            popupRoot.SetActive(false);
        }
    }
}
