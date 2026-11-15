using Robust.Shared.GameStates;

namespace Content.Shared._Stalker_OW.PublicName;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(STPublicNameSystem), Other = AccessPermissions.ReadExecute)]
public sealed partial class STPublicNameComponent : Component
{
    [DataField, AutoNetworkedField]
    public string Nickname = string.Empty;

    [DataField, AutoNetworkedField]
    public STNicknameDisplay DisplayMode;
}
