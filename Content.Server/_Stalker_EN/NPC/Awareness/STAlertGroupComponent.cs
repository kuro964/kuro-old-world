using Robust.Shared.Audio;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._Stalker_EN.NPC.Awareness;

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class STAlertGroupComponent : Component
{
    [DataField(required: true)]
    public string Group = string.Empty;

    [DataField]
    public HashSet<string> AlsoAlerts = new();

    [DataField]
    public float Radius = 10f;

    [DataField]
    public int MaxRecipients = 4;

    [DataField]
    public TimeSpan Cooldown = TimeSpan.FromSeconds(2);

    [DataField]
    public TimeSpan ChainBlock = TimeSpan.FromSeconds(5);

    [DataField]
    public SoundSpecifier? Sound;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextAlert;
}
