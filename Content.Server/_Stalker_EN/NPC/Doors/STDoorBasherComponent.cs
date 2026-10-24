using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._Stalker_EN.NPC.Doors;

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class STDoorBasherComponent : Component
{
    [DataField]
    public TimeSpan GiveUpTime = TimeSpan.FromSeconds(20);

    [DataField]
    public TimeSpan HoldTime = TimeSpan.FromSeconds(1);

    [ViewVariables]
    public bool Active;

    [ViewVariables]
    public bool Hunting;

    [ViewVariables]
    public EntityUid? Door;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan BashStart;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan LastBash;
}
