using Robust.Shared.GameStates;

namespace Content.Shared._Stalker_EN.Movement;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class BarefootSpeedModifierComponent : Component
{
    [DataField, AutoNetworkedField]
    public float SpeedModifier = 0.9f;
}
