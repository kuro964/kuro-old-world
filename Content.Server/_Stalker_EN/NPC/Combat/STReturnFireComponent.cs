using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._Stalker_EN.NPC.Combat;

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class STReturnFireComponent : Component
{
    [DataField]
    public float MaxRange = 32f;

    [DataField]
    public float Margin = 2f;

    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(10);

    [ViewVariables]
    public float? BaseVision;

    [ViewVariables]
    public float? BaseAggro;

    [ViewVariables]
    public EntityUid? Attacker;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan Until;
}
