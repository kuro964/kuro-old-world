using Robust.Shared.GameStates;

namespace Content.Shared._Stalker_EN.NPC.Awareness;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class STNoticeMeterComponent : Component
{
    public const byte Full = 254;
    public const byte Noticed = 255;

    public override bool SendOnlyToOwner => true;

    [AutoNetworkedField]
    public Dictionary<NetEntity, byte> Meters = new();
}
