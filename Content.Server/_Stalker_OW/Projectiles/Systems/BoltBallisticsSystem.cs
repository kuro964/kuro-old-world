using System;
using System.Numerics;
using Content.Shared._Stalker_OW.Projectiles.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._Stalker_OW.Projectiles.Systems;

/// <summary>
/// Manages flight duration, tracks active flight, and handles "recycling"
/// </summary>
public sealed class BoltBallisticsSystem : EntitySystem
{
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    
    private readonly HashSet<EntityUid> _activeBolts = new();
    private readonly List<EntityUid> _finishedBolts = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GunComponent, AmmoShotEvent>(OnAmmoShot);
        SubscribeLocalEvent<BoltBallisticsComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnAmmoShot(Entity<GunComponent> gun, ref AmmoShotEvent args)
    {
        foreach (var uid in args.FiredProjectiles)
        {
            if (!TryComp<BoltBallisticsComponent>(uid, out var bolt) ||
                !TryComp<PhysicsComponent>(uid, out var physics) ||
                !_activeBolts.Add(uid))
            {
                continue;
            }

            bolt.FlightTimeRemaining = Math.Max(0f, bolt.MaxFlightTime);

            if (physics.BodyType == BodyType.Dynamic)
            {
                _physics.SetLinearVelocity(
                    uid,
                    physics.LinearVelocity * Math.Max(0f, bolt.SpeedMultiplier),
                    body: physics);
            }
        }
    }

    private void OnShutdown(
        Entity<BoltBallisticsComponent> ent,
        ref ComponentShutdown args)
    {
        StopFlight(ent.Owner);
    }

    public void StopFlight(EntityUid uid)
    {
        _activeBolts.Remove(uid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_activeBolts.Count == 0)
            return;

        _finishedBolts.Clear();

        foreach (var uid in _activeBolts)
        {
            if (!TryComp<BoltBallisticsComponent>(uid, out var bolt) ||
                !TryComp<PhysicsComponent>(uid, out var physics) ||
                !TryComp<ProjectileComponent>(uid, out var projectile) ||
                projectile.Weapon == null ||
                projectile.ProjectileSpent)
            {
                _finishedBolts.Add(uid);
                continue;
            }

            if (MetaData(uid).EntityPaused)
                continue;

            bolt.FlightTimeRemaining -= frameTime;

            if (bolt.FlightTimeRemaining > 0f)
                continue;

            if (physics.BodyType == BodyType.Dynamic)
                _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);

            projectile.Weapon = null;
            projectile.Shooter = null;
            projectile.ProjectileSpent = false;
            projectile.PenetrationAmount = FixedPoint2.Zero;
            Dirty(uid, projectile);

            _finishedBolts.Add(uid);
        }

        foreach (var uid in _finishedBolts)
            _activeBolts.Remove(uid);
    }
}