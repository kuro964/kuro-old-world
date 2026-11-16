using System.Numerics;
using Robust.Shared.Audio;
using Robust.Shared.Map;

namespace Content.Server._Stalker_EN.NPC.Brain;

public enum STBehaviour : byte
{
    None,
    Idle,
    Fight,
    Finish,
    Investigate,
    Search,
}

public enum STBrainKind : byte
{
    Auto,
    Gunner,
    Rusher,
    Pack,
}

[RegisterComponent]
public sealed partial class STBrainComponent : Component
{
    [DataField]
    public STBrainKind Kind;

    [ViewVariables]
    public bool AutoKind;

    [DataField]
    public int MaxBiters = 99;

    [DataField]
    public float Suppression = 1f;

    [DataField]
    public bool ChaseNearest;

    [DataField]
    public bool HangBack;

    [DataField]
    public bool EatsFood;

    [DataField]
    public float BreakFraction;

    [ViewVariables]
    public bool Broken;

    [ViewVariables]
    public int Losses;

    [DataField]
    public bool ChargeWhenShielded;

    [ViewVariables]
    public EntityUid? Food;

    [DataField]
    public bool Skirmish;

    [DataField]
    public bool Stalk;

    [DataField]
    public TimeSpan StalkTime = TimeSpan.FromSeconds(8);

    [DataField]
    public float StalkCone = 70f;

    [ViewVariables]
    public TimeSpan StalkUntil;

    [ViewVariables]
    public bool Commit;

    [DataField]
    public float RingRadius = 4.5f;

    [DataField]
    public TimeSpan BiteTime = TimeSpan.FromSeconds(3.5);

    [DataField]
    public TimeSpan RestTime = TimeSpan.FromSeconds(2.5);

    [DataField]
    public SoundSpecifier? BarkSound;

    [DataField]
    public TimeSpan BarkDelay;

    [DataField]
    public bool Grenades;

    [DataField]
    public string GrenadeProto = "RGD5Grenade";

    [DataField]
    public float WoundedFraction = 0.6f;

    [ViewVariables]
    public TimeSpan GrenadeAt;

    [ViewVariables]
    public TimeSpan? ThrowAt;

    [ViewVariables]
    public MapCoordinates ThrowSpot;

    [ViewVariables]
    public MapCoordinates? HitFrom;

    [ViewVariables]
    public TimeSpan HitAt;

    [ViewVariables]
    public int PeakMates;

    [ViewVariables]
    public TimeSpan ShakenUntil;

    [ViewVariables]
    public TimeSpan FleeUntil;

    [ViewVariables]
    public bool Biting;

    [ViewVariables]
    public TimeSpan BiteUntil;

    [ViewVariables]
    public TimeSpan RestUntil;

    [ViewVariables]
    public int OrbitSign;

    [ViewVariables]
    public bool Barked;

    [DataField]
    public HashSet<string> Roots = new();

    [DataField]
    public string TargetQuery = string.Empty;

    [DataField]
    public string DownedQuery = string.Empty;

    [DataField]
    public float RangeMin = 6f;

    [DataField]
    public float RangeMax = 14f;

    [DataField]
    public float FireRange;

    [DataField]
    public float Leash;

    [ViewVariables]
    public MapCoordinates? FightOrigin;

    [ViewVariables]
    public TimeSpan? LeashSince;

    [ViewVariables]
    public bool Returning;

    [DataField]
    public float WanderRange = 3f;

    [DataField]
    public float ClearRange = 6f;

    [ViewVariables]
    public TimeSpan BusyUntil;

    [ViewVariables]
    public EntityCoordinates? Override;

    [ViewVariables]
    public TimeSpan OverrideUntil;

    [ViewVariables]
    public EntityUid? TargetLock;

    [ViewVariables]
    public TimeSpan TargetLockUntil;

    [ViewVariables]
    public STBehaviour Behaviour;

    [ViewVariables]
    public string Doing = string.Empty;

    [ViewVariables]
    public string LastDoing = string.Empty;

    [ViewVariables]
    public readonly List<(TimeSpan At, string Doing)> Recent = new();

    [ViewVariables]
    public TimeSpan NextThink;

    [ViewVariables]
    public TimeSpan NextScan;

    [ViewVariables]
    public byte LastState;

    [ViewVariables]
    public EntityUid? LastTarget;

    [ViewVariables]
    public MapCoordinates? Goal;

    [ViewVariables]
    public TimeSpan GoalSince;

    [ViewVariables]
    public int PathRetries;

    public System.Threading.Tasks.Task<Content.Server.NPC.Pathfinding.PathResultEvent>? Partial;

    [ViewVariables]
    public TimeSpan PartialUntil;

    [ViewVariables]
    public float GoalBest;

    [ViewVariables]
    public TimeSpan GoalBestAt;

    [ViewVariables]
    public MapCoordinates? BadGoal;

    [ViewVariables]
    public TimeSpan BadGoalUntil;

    [ViewVariables]
    public TimeSpan CoverBanUntil;

    [ViewVariables]
    public TimeSpan? StalemateSince;

    [ViewVariables]
    public TimeSpan StaleUntil;

    [ViewVariables]
    public MapCoordinates? FlankSpot;

    [ViewVariables]
    public MapCoordinates FlankFor;

    [ViewVariables]
    public bool FlankDone;

    [ViewVariables]
    public bool Closing;

    [ViewVariables]
    public TimeSpan HideBanUntil;

    [ViewVariables]
    public MapCoordinates? DangerSpot;

    [ViewVariables]
    public TimeSpan DangerUntil;

    [ViewVariables]
    public EntityUid? DangerSource;

    [DataField]
    public float RetreatSpeed = 1f;

    [ViewVariables]
    public float SpeedBoost = 1f;

    [ViewVariables]
    public TimeSpan SlotBanUntil;

    [ViewVariables]
    public int SlotShift;

    [ViewVariables]
    public readonly Dictionary<EntityUid, (TimeSpan At, float Damage)> Attackers = new();

    [ViewVariables]
    public TimeSpan TargetSince;

    [ViewVariables]
    public TimeSpan FanUntil;

    [ViewVariables]
    public TimeSpan? DeniedSince;

    [ViewVariables]
    public TimeSpan LastKick;

    [ViewVariables]
    public TimeSpan RetryPush;

    [ViewVariables]
    public bool Hiding;

    [ViewVariables]
    public bool InCover;

    [ViewVariables]
    public bool Pushing;

    [ViewVariables]
    public Vector2 StillAt;

    [ViewVariables]
    public TimeSpan StillSince;

    [ViewVariables]
    public MapCoordinates? SuppressAim;

    [ViewVariables]
    public TimeSpan SuppressUntil;

    [ViewVariables]
    public TimeSpan NextSuppress;

    [ViewVariables]
    public EntityCoordinates? Home;

    [ViewVariables]
    public TimeSpan NextWander;
}
