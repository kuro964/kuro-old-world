using Content.Server.Destructible;
using Content.Shared.Inventory;
using Content.Shared.Damage.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Projectiles;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;

namespace Content.Server.Projectiles;

public sealed class ProjectileSystem : SharedProjectileSystem
{
    [Dependency] private readonly DestructibleSystem _destructibleSystem = default!;
    [Dependency] private readonly InventorySystem _inventory = default!; // Stalker-Changes
    [Dependency] private readonly IPrototypeManager _prototype = default!; // Stalker-Changes

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ProjectileComponent, StartCollideEvent>(OnStartCollide); // ST:OW
    }

    // ST:OW begin - The hit itself (damage tier, penetration, deletion) now lives in SharedProjectileSystem
    // Normal collisions and predicted hits share one implementation
    protected override bool TryGetPenetrationHealth(EntityUid target, out FixedPoint2 damageRequired)
    {
        damageRequired = _destructibleSystem.DestroyedAt(target);
        if (TryComp<DamageableComponent>(target, out var damageableComponent))
        {
            damageRequired -= damageableComponent.TotalDamage;
            damageRequired = FixedPoint2.Max(damageRequired, FixedPoint2.Zero);
        }

        return true;
    }
    // ST:OW end
}
