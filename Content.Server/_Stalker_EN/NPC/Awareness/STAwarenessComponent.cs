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
    public TimeSpan LoseSightGrace = TimeSpan.FromSeconds(0.5);

    [DataField]
    public TimeSpan HunchTime = TimeSpan.FromSeconds(0.75);

    [DataField]
    public TimeSpan MemoryTime = TimeSpan.FromSeconds(10);

    [DataField]
    public TimeSpan PursueTime = TimeSpan.FromSeconds(10);

    [DataField]
    public TimeSpan PursueStallTime = TimeSpan.FromSeconds(1.5);

    [DataField]
    public TimeSpan SearchTime = TimeSpan.FromSeconds(2.5);

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
}
