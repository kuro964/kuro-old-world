using Content.Server.Damage.Systems;
using Content.Shared._Stalker.Stamina;
using Content.Shared.Damage.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Shared.Physics.Components;

namespace Content.Server._Stalker.Stamina;

public sealed class StaminaActiveSystem : EntitySystem
{
    [Dependency] private readonly StaminaSystem _stamina = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _speed = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        // ST:OW begin
        var query = EntityQueryEnumerator<StaminaComponent, StaminaActiveComponent, InputMoverComponent, PhysicsComponent>();

        while (query.MoveNext(out var uid, out var stamina, out var active, out var input, out var phys))
        {
            if (input.HeldMoveButtons.HasFlag(MoveButtons.Walk) && !active.Slowed && phys.LinearVelocity.LengthSquared() > 0f)
            {
                _stamina.TakeStaminaDamage(uid, active.RunStaminaDamage, stamina, visual: false, shouldLog: false);
            }

            if (!active.Slowed && stamina.StaminaDamage >= active.SlowThreshold)
            {
                active.Slowed = true; Dirty(uid, active); _speed.RefreshMovementSpeedModifiers(uid);
            }

            else if (active.Slowed && stamina.StaminaDamage <= active.ReviveStaminaLevel)
            {
                active.Slowed = false;
                Dirty(uid, active);
                _speed.RefreshMovementSpeedModifiers(uid);
            }
        }
    }
    // ST:OW end
}