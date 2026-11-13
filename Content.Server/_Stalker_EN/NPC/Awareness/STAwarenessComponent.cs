using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._Stalker_EN.NPC.Awareness;

public enum STAwarenessState : byte
{
    Unaware,
    Engaged,
    Lost,
    Searching,
}

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class STAwarenessComponent : Component
{
    [DataField]
    public HashSet<string> ManagedRoots = new();

    [DataField]
    public TimeSpan TrackTime = TimeSpan.FromSeconds(8);

    [DataField]
    public float TrackSlack = 8f;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan TrackUntil;

    [DataField]
    public TimeSpan MemoryTime = TimeSpan.FromSeconds(10);

    [DataField]
    public TimeSpan PursueTime = TimeSpan.FromSeconds(10);

    [DataField]
    public TimeSpan PursueStallTime = TimeSpan.FromSeconds(1.5);

    [DataField]
    public TimeSpan SearchTime = TimeSpan.FromSeconds(10);

    [DataField]
    public TimeSpan UpdateInterval = TimeSpan.FromSeconds(0.2);

    [ViewVariables]
    public STAwarenessState State;

    [ViewVariables]
    public EntityUid? Target;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextUpdate;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan LastSeen;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan StateStart;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan LastPursuing;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan HoldUntil;

    [DataField]
    public float SightFactor = 1f;

    [DataField]
    public float HearingFactor = 1f;

    [DataField]
    public bool NoticeMeter;

    [DataField]
    public Robust.Shared.Audio.SoundSpecifier? NoticeSound;

    [DataField]
    public float NoticeNearRange = 3f;

    [DataField]
    public float NoticeTimeNear = 0.67f;

    [DataField]
    public float NoticeTimeFar = 1.67f;

    [DataField]
    public float NoticeFrontArc = 120f;

    [DataField]
    public float NoticePeripheralFactor = 0.5f;

    [DataField]
    public float NoticeAlertedFactor = 3f;

    [DataField]
    public TimeSpan NoticeDrainDelay = TimeSpan.FromSeconds(1);

    [DataField]
    public TimeSpan NoticeFlash = TimeSpan.FromSeconds(1.5);

    [DataField]
    public int NoticeCandidates = 4;

    [DataField]
    public float SuspicionFraction = 0.25f;

    [DataField]
    public float SuspicionPause = 1.5f;

    [ViewVariables]
    public readonly Dictionary<EntityUid, STNoticeTrack> Notice = new();

    [ViewVariables]
    public readonly HashSet<EntityUid> MeterShown = new();

    [ViewVariables]
    public TimeSpan LastNoticeUpdate;
}

public sealed class STNoticeTrack
{
    public float Value;
    public TimeSpan LastSeen;
    public TimeSpan? NoticedAt;
    public bool Looked;
}
