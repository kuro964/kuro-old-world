using Robust.Shared.GameStates;
using Content.Shared.Movement.Components; // ST:OW
using Content.Shared.Movement.Systems; // ST:OW

namespace Content.Shared._Stalker.Stamina;
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class StaminaActiveComponent : Component
{

    /// <summary>
    /// Float on which our entity will be "stunned"
    /// </summary>
    [DataField("slowThreshold"), ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public float SlowThreshold = 180f;

    /// <summary>
    /// Value to compare with StaminaDamage and set default sprint speed back.
    /// If Stamina damage will be less than this value - default sprint will be set.
    /// </summary>
    [DataField("reviveStaminaLevel"), ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public float ReviveStaminaLevel = 80f;

    /// <summary>
    /// Stamina damage to apply when entity is running
    /// </summary>
    [DataField("runStaminaDamage"), ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public float RunStaminaDamage = 0.2f;

    /// <summary>
    /// If our entity is slowed already.
    /// Nothing will happen if you'll set it manually.
    /// </summary>
    public bool Slowed = false;
}
    // ST:OW begin
    /// <summary>
    /// Slow down effect is applied client and server side
    /// </summary>
    public sealed class StaminaActiveMovementSystem : EntitySystem
        {
            private EntityQuery<MovementSpeedModifierComponent> _movementQuery;

            public override void Initialize()
            {
                base.Initialize();

                _movementQuery = GetEntityQuery<MovementSpeedModifierComponent>();

                SubscribeLocalEvent<StaminaActiveComponent, RefreshMovementSpeedModifiersEvent>(OnRefresh);
            }

            private void OnRefresh(
                EntityUid uid,
                StaminaActiveComponent component,
                RefreshMovementSpeedModifiersEvent args)
            {
                if (!component.Slowed)
                    return;

                if (!_movementQuery.TryGetComponent(uid, out var movement))
                    return;

                if (movement.BaseSprintSpeed <= 0f)
                    return;

                args.ModifySpeed(
                    1f,
                    movement.BaseWalkSpeed / movement.BaseSprintSpeed);
            }
        }
    // ST:OW end