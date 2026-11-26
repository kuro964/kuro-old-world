using Content.Shared._RMC14.Weapons.Ranged.Prediction;
using Content.Shared._Stalker_OW.Projectiles.Components;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Spawners;
using Robust.Shared.Timing;

namespace Content.Client._Stalker_OW.Projectiles;

public sealed class BoltPredictionSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private const float RestingSpeed = 0.1f;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GunComponent, AmmoShotEvent>(OnAmmoShot);
        SubscribeLocalEvent<BoltBallisticsComponent, ProjectileHitEvent>(OnHit);
    }

    private void OnAmmoShot(Entity<GunComponent> gun, ref AmmoShotEvent args)
    {
        foreach (var uid in args.FiredProjectiles)
        {
            if (!IsClientSide(uid) ||
                !TryComp<BoltBallisticsComponent>(uid, out var bolt) ||
                !TryComp<PhysicsComponent>(uid, out var physics))
            {
                continue;
            }

            _physics.SetLinearVelocity(uid, physics.LinearVelocity * Math.Max(0f, bolt.SpeedMultiplier), body: physics);
            EnsureComp<TimedDespawnComponent>(uid).Lifetime = bolt.MaxFlightTime;
            RemComp<EmbeddableProjectileComponent>(uid);
        }
    }

    private void OnHit(Entity<BoltBallisticsComponent> ent, ref ProjectileHitEvent args)
    {
        if (IsClientSide(ent))
            QueueDel(ent);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var ahead = (float) Math.Min(_timing.TickRemainder.TotalSeconds, _timing.TickPeriod.TotalSeconds);
        var flying = EntityQueryEnumerator<PredictedProjectileClientComponent, BoltBallisticsComponent, SpriteComponent, PhysicsComponent>();
        while (flying.MoveNext(out var uid, out _, out _, out var sprite, out var physics))
        {
            var lead = physics.LinearVelocity * ahead;
            _sprite.SetOffset((uid, sprite), (-_transform.GetWorldRotation(uid)).RotateVec(lead));
        }

        var local = _player.LocalEntity;
        if (local == null)
            return;

        var query = EntityQueryEnumerator<BoltBallisticsComponent, PredictedProjectileServerComponent, SpriteComponent, PhysicsComponent>();
        while (query.MoveNext(out var uid, out _, out var predicted, out var sprite, out var physics))
        {
            if (sprite.Visible || predicted.ClientEnt != local)
                continue;

            if (physics.BodyType == BodyType.Static || physics.LinearVelocity.Length() < RestingSpeed)
                _sprite.SetVisible((uid, sprite), true);
        }
    }
}
