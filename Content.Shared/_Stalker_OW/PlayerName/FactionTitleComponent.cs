using Robust.Shared.GameStates;

namespace Content.Shared._Stalker_OW.PublicName;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(STPublicNameSystem), Other = AccessPermissions.ReadExecute)]
public sealed partial class STFactionTitleComponent : Component
{
    [DataField, AutoNetworkedField]
    public string Title = string.Empty;
}
