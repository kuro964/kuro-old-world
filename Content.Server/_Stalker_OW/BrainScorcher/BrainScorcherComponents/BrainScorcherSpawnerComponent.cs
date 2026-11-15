using Robust.Shared.Prototypes;

namespace Content.Server._Stalker_OW.BrainScorcher.BrainScorcherComponents;

/// <summary>
/// This is used for...
/// </summary>
[RegisterComponent]
public sealed partial class BrainScorcherSpawnerComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public List<EntityUid> Inside = new();

    [DataField("ghosts"), AutoNetworkedField]
    public List<EntProtoId> Ghosts = new();

    /// <summary>
    /// If true, this spawner will be removed when the miracle machine is disabled.
    /// </summary>
    [DataField("brainScorcher"), AutoNetworkedField]
    public bool BrainScorcher = true;
}
