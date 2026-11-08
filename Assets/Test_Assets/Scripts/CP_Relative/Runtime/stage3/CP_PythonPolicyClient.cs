using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// HTTP transport only. Stage 4B scheduling decisions are owned by CP_PolicyScheduler.
public class CP_PythonPolicyClient : MonoBehaviour
{
    [Header("References")]
    public CP_PerceptionBuilder perceptionBuilder;
    public CP_PolicyReceiver policyReceiver;
    public CP_PolicyScheduler policyScheduler;

    [Header("Client Settings")]
    public bool pythonClientEnabled = true;
    public string serverUrl = "http://127.0.0.1:8765/policy";
    public float requestInterval = 2f;
    public float timeoutSeconds = 5f;
    public bool sendOnStart = true;
    public bool sendContinuously = true;
    public bool sendOnlyWhenReceiverIdle = true;
    public bool schedulerControlsRequestTiming = true;
    public bool logRawRequest;
    public bool logRawResponse;

    [Header("Latest Client Data")]
    [TextArea(8, 30)] public string latestRequestJson = string.Empty;
    [TextArea(8, 30)] public string latestResponseJson = string.Empty;
    [TextArea(4, 12)] public string latestClientSummary = string.Empty;
    [TextArea(4, 12)] public string latestErrorText = string.Empty;
    public string lastRequestRole = string.Empty;
    public int lastGenerationJobId;

    private readonly StringBuilder summaryBuilder = new StringBuilder();
    private float nextRequestTime;
    private bool requestInFlight;

    public bool IsRequestInFlight => requestInFlight;
    private bool SchedulerModeRequested => schedulerControlsRequestTiming;
    private bool SchedulerOwnsRequests => SchedulerModeRequested && policyScheduler != null && policyScheduler.schedulerEnabled;

    private void Start()
    {
        nextRequestTime = Time.time;
        latestClientSummary = pythonClientEnabled ? "Python client armed." : "Python client disabled.";
        if (SchedulerModeRequested)
        {
            if (!SchedulerOwnsRequests)
            {
                ReportSchedulerOwnershipError();
            }
            return;
        }
        if (sendOnStart)
        {
            TrySendLegacyRequest("start");
        }
    }

    private void Update()
    {
        if (!pythonClientEnabled || !sendContinuously)
        {
            return;
        }
        if (SchedulerModeRequested)
        {
            if (!SchedulerOwnsRequests)
            {
                ReportSchedulerOwnershipError();
            }
            return;
        }

        if (!requestInFlight && Time.time >= nextRequestTime)
        {
            TrySendLegacyRequest("poll");
        }
    }

    public void SendOnce()
    {
        if (SchedulerModeRequested)
        {
            if (SchedulerOwnsRequests)
            {
                policyScheduler.RequestManualPolicy();
            }
            else
            {
                ReportSchedulerOwnershipError();
            }
            return;
        }
        TrySendLegacyRequest("manual");
    }

    // Scheduler requests bypass the legacy busy gate so lookahead can overlap normal execution.
    public bool SendSchedulerRequest(string requestRole, int generationJobId)
    {
        if (!SchedulerOwnsRequests)
        {
            ReportSchedulerOwnershipError();
            return false;
        }
        return BeginRequest(requestRole, requestRole, generationJobId, true);
    }

    private void TrySendLegacyRequest(string reason)
    {
        if (sendOnlyWhenReceiverIdle && policyReceiver != null && policyReceiver.IsMotionExecutorBusy())
        {
            latestClientSummary = "Legacy request skipped: executor is busy.";
            nextRequestTime = Time.time + Mathf.Max(0.1f, requestInterval);
            return;
        }
        BeginRequest(reason, "current_policy", 0, false);
    }

    private bool BeginRequest(string reason, string requestRole, int generationJobId, bool routeToScheduler)
    {
        if (!pythonClientEnabled || requestInFlight || perceptionBuilder == null || string.IsNullOrWhiteSpace(serverUrl))
        {
            latestErrorText = !pythonClientEnabled ? "Python client disabled." : requestInFlight ? "Request already in flight." : perceptionBuilder == null ? "perceptionBuilder is not assigned." : "serverUrl is empty.";
            latestClientSummary = latestErrorText;
            return false;
        }
        if (!routeToScheduler && policyReceiver == null)
        {
            latestErrorText = "policyReceiver is not assigned.";
            latestClientSummary = latestErrorText;
            return false;
        }

        requestInFlight = true;
        latestErrorText = string.Empty;
        nextRequestTime = Time.time + Mathf.Max(0.1f, requestInterval);
        lastRequestRole = requestRole;
        lastGenerationJobId = generationJobId;
        StartCoroutine(SendPolicyRequestCoroutine(reason, requestRole, generationJobId, routeToScheduler));
        return true;
    }

    private IEnumerator SendPolicyRequestCoroutine(string reason, string requestRole, int generationJobId, bool routeToScheduler)
    {
        CP_PerceptionPacket packet = null;
        string requestJson = string.Empty;
        UnityWebRequest request = null;

        try
        {
            packet = perceptionBuilder.RefreshPerception();
            requestJson = perceptionBuilder.GetLatestPerceptionJson();

            if (string.IsNullOrWhiteSpace(requestJson) && packet != null)
            {
                requestJson = JsonUtility.ToJson(packet, true);
            }

            if (string.IsNullOrWhiteSpace(requestJson))
            {
                requestInFlight = false;
                ReportFailure(requestRole, generationJobId, "Perception JSON is empty.", routeToScheduler);
                yield break;
            }

            latestRequestJson = requestJson;

            byte[] payload = Encoding.UTF8.GetBytes(requestJson);

            request = new UnityWebRequest(serverUrl, "POST");
            request.uploadHandler = new UploadHandlerRaw(payload);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            request.timeout = Mathf.Max(1, Mathf.CeilToInt(timeoutSeconds));
        }
        catch (System.Exception ex)
        {
            requestInFlight = false;
            ReportFailure(
                requestRole,
                generationJobId,
                "Python request setup exception: " + ex.Message,
                routeToScheduler
            );
            yield break;
        }


        // IMPORTANT:
        // yield cannot exist inside try blocks with catch clauses.
        yield return request.SendWebRequest();


        try
        {
            string responseText =
                request.downloadHandler != null
                    ? request.downloadHandler.text
                    : string.Empty;

            latestResponseJson = responseText ?? string.Empty;

            if (request.result != UnityWebRequest.Result.Success)
            {
                requestInFlight = false;
                ReportFailure(
                    requestRole,
                    generationJobId,
                    "Python request failed: " + request.error,
                    routeToScheduler
                );
                yield break;
            }

            if (string.IsNullOrWhiteSpace(responseText) ||
                !responseText.TrimStart().StartsWith("{"))
            {
                requestInFlight = false;
                ReportFailure(
                    requestRole,
                    generationJobId,
                    "Python returned a non-JSON policy response.",
                    routeToScheduler
                );
                yield break;
            }


            // Clear transport state before routing.
            // Scheduler can immediately schedule the next intent.
            requestInFlight = false;


            if (routeToScheduler && policyScheduler != null)
            {
                policyScheduler.ReceivePolicyJsonFromPython(
                    responseText,
                    generationJobId,
                    requestRole
                );
            }
            else if (policyReceiver != null)
            {
                policyReceiver.ReceivePolicyJson(responseText);
            }
            else
            {
                ReportFailure(
                    requestRole,
                    generationJobId,
                    "No response target is assigned.",
                    routeToScheduler
                );
                yield break;
            }


            summaryBuilder.Clear();
            summaryBuilder.AppendLine("Python request completed.");
            summaryBuilder.AppendLine("Reason: " + reason);
            summaryBuilder.AppendLine("Request Role: " + requestRole);
            summaryBuilder.AppendLine("Generation Job: " + generationJobId);
            summaryBuilder.AppendLine("HTTP Status: " + request.responseCode);

            latestClientSummary = summaryBuilder.ToString();
        }
        catch (System.Exception ex)
        {
            requestInFlight = false;

            ReportFailure(
                requestRole,
                generationJobId,
                "Python transport exception: " + ex.Message,
                routeToScheduler
            );
        }
        finally
        {
            request?.Dispose();
            requestInFlight = false;
        }
    }

    private void ReportFailure(string requestRole, int generationJobId, string errorText, bool routeToScheduler)
    {
        latestErrorText = errorText;
        latestClientSummary = errorText;
        if (routeToScheduler && policyScheduler != null)
        {
            policyScheduler.NotifyPythonRequestFailed(requestRole, generationJobId, errorText);
        }
    }

    private void ReportSchedulerOwnershipError()
    {
        latestErrorText = "Scheduler-controlled mode is enabled, but CP_PolicyScheduler is missing or disabled. Legacy polling is blocked.";
        latestClientSummary = latestErrorText;
    }
}
