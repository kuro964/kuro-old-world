using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Content.Shared._Stalker_OW.Projectiles.Components;

/// <summary>
/// Controls maximum flight times
/// </summary>
[RegisterComponent]
public sealed partial class BoltBallisticsComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float MaxFlightTime = 0.9f;

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float SpeedMultiplier = 1f;

    [ViewVariables(VVAccess.ReadOnly)]
    public float FlightTimeRemaining;
}
