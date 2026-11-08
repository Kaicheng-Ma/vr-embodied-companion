using UnityEngine;

public enum FishingGameplayPhase
{
    Inactive,
    BucketReady,
    RodHeldReady,
    Casting,
    WaitingForBite,
    BiteWindow,
    RecallingEmpty,
    ReelingCatch,
    ReleasingCatch,
    CapacityReached
}

public class FishingStateBlackboard : MonoBehaviour
{
    [Header("Core State")]
    public bool fishingStarted = false;
    public bool rodPicked = false;
    public bool playerInsideFishingArea = false;
    public FishingGameplayPhase currentPhase = FishingGameplayPhase.Inactive;

    [Header("Bobber State")]
    public bool bobberCast = false;
    public bool bobberInWater = false;
    public bool biteActive = false;
    public bool recallInProgress = false;

    [Header("Catch State")]
    public bool catchSucceeded = false;
    public bool catchReleaseInProgress = false;
    public int caughtFishCount = 0;

    [Min(1)]
    public int maxCatchCount = 10;

    public bool activityCompleted = false;

    [Header("Timing")]
    public float currentWaitTime = 0f;
    public float currentBiteDelay = 0f;
    public float currentBiteWindowTime = 0f;

    public float biteDelayMin = 4f;
    public float biteDelayMax = 8f;
    public float biteWindowDuration = 1.5f;

    public float retryDelayMin = 3f;
    public float retryDelayMax = 6f;

    [Header("Random")]
    public bool useFixedRandomSeed = false;
    public int randomSeed = 12345;

    [Header("Bucket References")]
    public Transform bucketSpawnPoint;
    public Transform bucketRuntimeParent;
    public GameObject currentBucketInstance;

    [Header("Fishing Rod References")]
    public GameObject fishingRodObject;
    public Transform rodTip;

    [Header("Player Fishing Spatial")]
    public Transform playerCastTarget;
    public Transform fishReleasePoint;

    [Header("Runtime Parents")]
    public Transform bobberRuntimeParent;
    public Transform caughtFishRuntimeParent;

    [Header("Runtime Instances")]
    public GameObject currentBobberInstance;
    public GameObject currentCatchInstance;

    [Header("Debug")]
    [TextArea]
    public string lastFailureReason = "";
}