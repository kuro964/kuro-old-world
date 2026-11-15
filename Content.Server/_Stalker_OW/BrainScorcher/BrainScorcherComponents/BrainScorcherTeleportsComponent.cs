using Robust.Shared.Prototypes;

namespace Content.Server._Stalker_OW.BrainScorcher.BrainScorcherComponents;

/// <summary>
/// Put on to teleporter spawners that will enable new routes when the Miracle Machine is disabled.
/// </summary>
[RegisterComponent]
public sealed partial class BrainScorcherTeleportsComponent : Component
{
    [DataField("teleport"), AutoNetworkedField]
    public EntProtoId Teleport = string.Empty;
}
