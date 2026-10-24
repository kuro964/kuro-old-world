using Content.Shared.FixedPoint;

namespace Content.Server._Stalker_EN.NPC.Doors;

[RegisterComponent]
public sealed partial class STBashableDoorComponent : Component
{
    [DataField]
    public FixedPoint2 Strength = 300;

    [ViewVariables]
    public FixedPoint2 Damage;
}
