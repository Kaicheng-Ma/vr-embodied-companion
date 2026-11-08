using UnityEngine;

public enum CP_ActivityId
{
    Common,
    Fishing,
    Campfire
}

public enum CP_ActivityPhase
{
    Free,
    Ready,
    Engagement
}

// Stage A activity owner: observes existing gameplay blackboards and exposes
// read-only activity context without changing gameplay truth or AI behavior.
public class CP_ActivityContextRouter : MonoBehaviour
{
    [Header("Observed Truth Sources")]
    [SerializeField] private FishingStateBlackboard fishingBlackboard;
    [SerializeField] private RoastingStateBlackboard roastingBlackboard;

    [Header("Debug")]
    [SerializeField] private bool logContextChanges = false;

    [Header("Latest Activity Context")]
    [SerializeField] private string activeActivity = "common";
    [SerializeField] private string activityPhase = "free";
    [SerializeField] private string detailPhase = "none";
    [SerializeField] private bool fishingCompleted = false;
    [SerializeField] private string contextKey = "common/free";
    [SerializeField] private int contextEpoch = 0;

    private string lastContextKey = string.Empty;
    private string lastConflictWarningKey = string.Empty;

    public FishingStateBlackboard FishingBlackboard => fishingBlackboard;
    public RoastingStateBlackboard RoastingBlackboard => roastingBlackboard;
    public string ActiveActivity => activeActivity;
    public string ActivityPhase => activityPhase;
    public string DetailPhase => detailPhase;
    public bool FishingCompleted => fishingCompleted;
    public string ContextKey => contextKey;
    public int ContextEpoch => contextEpoch;
    public string CanonicalState => ResolveCanonicalState();

    private void OnEnable()
    {
        RefreshContext();
    }

    private void Update()
    {
        RefreshContext();
    }

    public void RefreshContext()
    {
        fishingCompleted = fishingBlackboard != null && fishingBlackboard.activityCompleted;

        bool fishingStarted = fishingBlackboard != null && fishingBlackboard.fishingStarted;
        bool campfireStarted = roastingBlackboard != null && roastingBlackboard.campfire_started;

        if (fishingStarted && !fishingCompleted)
        {
            activeActivity = ToActivityString(CP_ActivityId.Fishing);
            activityPhase = ResolveFishingPhase(fishingBlackboard.currentPhase);
            detailPhase = fishingBlackboard.currentPhase.ToString();

            if (campfireStarted)
            {
                MaybeLogConflictWarning("campfire_started_before_fishing_completed");
            }
        }
        else if (campfireStarted)
        {
            activeActivity = ToActivityString(CP_ActivityId.Campfire);
            activityPhase = ResolveCampfirePhase(roastingBlackboard);
            detailPhase = "none";
        }
        else
        {
            activeActivity = ToActivityString(CP_ActivityId.Common);
            activityPhase = ToPhaseString(CP_ActivityPhase.Free);
            detailPhase = "none";
        }

        contextKey = BuildContextKey(activeActivity, activityPhase, detailPhase, fishingCompleted);
        UpdateContextEpoch();
    }

    public void SetBlackboards(FishingStateBlackboard fishing, RoastingStateBlackboard roasting)
    {
        fishingBlackboard = fishing;
        roastingBlackboard = roasting;
        RefreshContext();
    }

    private string ResolveFishingPhase(FishingGameplayPhase phase)
    {
        // BucketReady is the future F2-ready context. All active rod/bobber/catch
        // phases are future F3 engagement, but Stage A only observes the context.
        if (phase == FishingGameplayPhase.Inactive ||
            phase == FishingGameplayPhase.BucketReady)
        {
            return ToPhaseString(CP_ActivityPhase.Ready);
        }

        return ToPhaseString(CP_ActivityPhase.Engagement);
    }

    private string ResolveCampfirePhase(RoastingStateBlackboard source)
    {
        if (source == null || !source.stick_picked)
        {
            return ToPhaseString(CP_ActivityPhase.Ready);
        }

        return ToPhaseString(CP_ActivityPhase.Engagement);
    }

    private string BuildContextKey(string id, string phase, string detail, bool completed)
    {
        if (id == ToActivityString(CP_ActivityId.Common) && completed)
        {
            return $"{id}/{phase}/fishing_completed";
        }

        if (id == ToActivityString(CP_ActivityId.Fishing))
        {
            return $"{id}/{phase}/{detail}";
        }

        return $"{id}/{phase}";
    }

    private void UpdateContextEpoch()
    {
        if (string.IsNullOrEmpty(lastContextKey))
        {
            lastContextKey = contextKey;
            return;
        }

        if (contextKey == lastContextKey)
        {
            return;
        }

        contextEpoch++;

        if (logContextChanges)
        {
            Debug.Log($"CP_ActivityContextRouter: {lastContextKey} -> {contextKey}", this);
        }

        lastContextKey = contextKey;
    }

    private void MaybeLogConflictWarning(string warningKey)
    {
        if (!logContextChanges || warningKey == lastConflictWarningKey)
        {
            return;
        }

        lastConflictWarningKey = warningKey;
        Debug.LogWarning(
            "CP_ActivityContextRouter: Campfire is started while Fishing is still incomplete; keeping Fishing active.",
            this
        );
    }

    public string ResolveCanonicalState()
    {
        switch (activeActivity)
        {
            case "common":
                return activityPhase == ToPhaseString(CP_ActivityPhase.Free)
                    ? CP_StateNames.S1
                    : CP_StateNames.Unknown;
            case "fishing":
                if (activityPhase == ToPhaseString(CP_ActivityPhase.Ready))
                {
                    return CP_StateNames.F2;
                }

                if (activityPhase == ToPhaseString(CP_ActivityPhase.Engagement))
                {
                    return CP_StateNames.F3;
                }

                return CP_StateNames.Unknown;
            case "campfire":
                if (activityPhase == ToPhaseString(CP_ActivityPhase.Ready))
                {
                    return CP_StateNames.S2;
                }

                if (activityPhase == ToPhaseString(CP_ActivityPhase.Engagement))
                {
                    return CP_StateNames.S3;
                }

                return CP_StateNames.Unknown;
            default:
                return CP_StateNames.Unknown;
        }
    }

    private string ToActivityString(CP_ActivityId id)
    {
        switch (id)
        {
            case CP_ActivityId.Fishing:
                return "fishing";
            case CP_ActivityId.Campfire:
                return "campfire";
            default:
                return "common";
        }
    }

    private string ToPhaseString(CP_ActivityPhase phase)
    {
        switch (phase)
        {
            case CP_ActivityPhase.Ready:
                return "ready";
            case CP_ActivityPhase.Engagement:
                return "engagement";
            default:
                return "free";
        }
    }
}
