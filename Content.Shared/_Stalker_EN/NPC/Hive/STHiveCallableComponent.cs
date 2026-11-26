using Robust.Shared.GameStates;

namespace Content.Shared._Stalker_EN.NPC.Hive;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class STHiveCallableComponent : Component
{
    [AutoNetworkedField]
    public EntityUid? Leader;
}
