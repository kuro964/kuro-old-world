using System.Numerics;
using Content.Shared._Stalker.ZoneAnomaly.Components;
using Content.Shared._Stalker.ZoneAnomaly.Effects.Components;
using Content.Shared.Physics;
using Content.Shared.Throwing;
using Content.Shared.Whitelist;
using Robust.Server.GameObjects;
// ST:OW begin
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Utility;
// ST:OW end

namespace Content.Server._Stalker.ZoneAnomaly.Effects.Systems;

public sealed class ZoneAnomalyEffectThrowSystem : EntitySystem
{
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!; // ST:OW

    private const float WallSafetyDistance = 0.75f; // ST:OW

    public override void Initialize()
    {
        SubscribeLocalEvent<ZoneAnomalyEffectThrowComponent, ZoneAnomalyActivateEvent>(OnActivate);
    }

    private void OnActivate(Entity<ZoneAnomalyEffectThrowComponent> effect, ref ZoneAnomalyActivateEvent args)
    {
        if (!TryComp<ZoneAnomalyComponent>(effect, out var anomaly))
            return;

        var anomalyPosition = _transform.GetWorldPosition(effect); // ST:OW

        foreach (var entity in anomaly.InAnomaly)
        {
            if (effect.Comp.Whitelist is { } whitelist && _whitelist.IsWhitelistFail(whitelist, entity))
                continue;

            // ST:OW begin
            var entityTransform = Transform(entity);
            var entityPosition = _transform.GetWorldPosition(entity);
            var offset = entityPosition - anomalyPosition;
            var distance = offset.Length();

            if (distance < effect.Comp.MinDistance || distance <= 0.001f)
                continue;

            var throwDirection = offset / distance;
            var throwDistance = FindThrowDistance(entity, entityTransform.MapID, entityPosition, throwDirection, effect.Comp.Distance);

            if (throwDistance <= 0.1f)
                continue;

            var throwVector = throwDirection * throwDistance;

            _throwing.TryThrow(entity, throwVector, effect.Comp.Force, effect, pushbackRatio: 0f, compensateFriction: true, recoil: false);
            // ST:OW end
        }
    }

    // ST:OW begin
    private float FindThrowDistance(EntityUid entity, MapId mapId, Vector2 start, Vector2 direction, float requestedDistance)
    {
        var ray = new CollisionRay(start, direction, (int)CollisionGroup.MobMask);

        var hit = _physics.IntersectRay(mapId, ray, requestedDistance, entity, returnOnFirstHit: true)
            .FirstOrNull();

        if (hit == null)
            return requestedDistance;

        return MathF.Max(0f, hit.Value.Distance - WallSafetyDistance);
    }
    // ST:OW end
}
