using Robust.Shared.GameStates;

namespace Content.Shared._Stalker_EN.NPC.Hive;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class STHiveSightComponent : Component
{
    [DataField, AutoNetworkedField]
    public List<EntityUid> Eyes = new();

    [DataField]
    public float Range = 12f;

    [DataField]
    public float Lift = 4f;

    [DataField]
    public Color Tint = Color.FromHex("#C8A0FFD9");

    public override bool SendOnlyToOwner => true;
}
