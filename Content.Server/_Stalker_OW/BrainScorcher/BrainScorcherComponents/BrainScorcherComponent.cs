namespace Content.Server._Stalker_OW.BrainScorcher.BrainScorcherComponents;

/// <summary>
/// Used on Miracle Machine itself, tracks active batteries for shutdown.
/// </summary>
[RegisterComponent]
public sealed partial class BrainScorcherComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public List<EntityUid> Generators = new();
}
