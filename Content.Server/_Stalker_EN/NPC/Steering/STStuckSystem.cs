using System.Numerics;
using Content.Server.Destructible;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.CombatMode;
using Content.Shared.FixedPoint;
using Content.Shared.Movement.Components;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Steering;

public sealed class STStuckSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private readonly SharedCombatModeSystem _combat = default!;
    [Dependency] private readonly DestructibleSystem _destructible = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;

    private static readonly FixedPoint2 ContactSmashDamage = 300;
    private static readonly FixedPoint2 StuckSmashDamage = 1000;
    private const float ContactMargin = 0.3f;
    private const float BlockedDistance = 0.05f;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(0.2);
    private static readonly TimeSpan WedgeTime = TimeSpan.FromSeconds(0.2);
    private const float AheadCos = 0.5f;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<NPCSteeringComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var steering, out var xform))
        {
            var comp = EnsureComp<STStuckComponent>(uid);
            if (now < comp.NextCheck)
                continue;

            var resumed = now - comp.NextCheck > CheckInterval;
            comp.NextCheck = now + CheckInterval;
            var position = _transform.GetWorldPosition(xform);
            var moved = (position - comp.LastPosition).Length();
            comp.LastPosition = position;

            if (resumed ||
                steering.Status != SteeringStatus.Moving ||
                moved > BlockedDistance ||
                HandlingObstacle(steering) ||
                CompOrNull<MovementSpeedModifierComponent>(uid)?.CurrentSprintSpeed < NPCSteeringSystem.MinMoveSpeed)
            {
                comp.BlockedSince = null;
                continue;
            }

            comp.BlockedSince ??= now;

            if ((steering.Flags & PathFlags.Smashing) != 0x0)
            {
                if (!TrySmashBlocker(uid, steering, true))
                    continue;

                steering.LastStuckTime = now;
                steering.LastStuckCoordinates = xform.Coordinates;
                continue;
            }

            if (now - comp.BlockedSince < WedgeTime || !StaticBlockerAhead(uid, steering))
                continue;

            _steering.Unstick(uid, steering);
            comp.BlockedSince = null;
        }
    }

    private static bool HandlingObstacle(NPCSteeringComponent steering)
    {
        return steering.DoAfterId != null || steering.Pathfind ||
               steering.CurrentPath.TryPeek(out var node) && !node.Data.IsFreeSpace;
    }

    private bool StaticBlockerAhead(EntityUid uid, NPCSteeringComponent steering)
    {
        var origin = _transform.GetWorldPosition(uid);
        var heading = Heading(steering) - origin;
        if (heading.LengthSquared() < 0.01f)
            return false;

        heading = Vector2.Normalize(heading);
        var bounds = _lookup.GetWorldAABB(uid);
        var range = MathF.Max(bounds.Width, bounds.Height) / 2f + ContactMargin;

        foreach (var ent in _lookup.GetEntitiesInRange(uid, range, LookupFlags.Static))
        {
            if (!TryComp<PhysicsComponent>(ent, out var body) || !body.Hard || !body.CanCollide)
                continue;

            var offset = _transform.GetWorldPosition(ent) - origin;
            if (offset.LengthSquared() > 0.0001f && Vector2.Dot(Vector2.Normalize(offset), heading) > AheadCos)
                return true;
        }

        return false;
    }

    public bool TrySmashWhenStuck(EntityUid uid, NPCSteeringComponent steering)
    {
        return TrySmashBlocker(uid, steering, false);
    }

    private bool TrySmashBlocker(EntityUid uid, NPCSteeringComponent steering, bool onContact)
    {
        if (!_melee.TryGetWeapon(uid, out var weaponUid, out var weapon))
            return false;

        var origin = _transform.GetWorldPosition(uid);
        var heading = Heading(steering) - origin;
        var bounds = _lookup.GetWorldAABB(uid);
        var range = MathF.Max(bounds.Width, bounds.Height) / 2f + ContactMargin;
        EntityUid? blocker = null;
        var closest = float.MaxValue;
        var maxDamage = onContact ? ContactSmashDamage : StuckSmashDamage;

        foreach (var ent in _lookup.GetEntitiesInRange(uid, range, LookupFlags.Static))
        {
            if (!TryComp<DestructibleComponent>(ent, out var destructible) || _destructible.DestroyedAt(ent, destructible) > maxDamage)
                continue;

            var offset = _transform.GetWorldPosition(ent) - origin;
            if ((onContact && Vector2.Dot(offset, heading) <= 0f) || offset.LengthSquared() >= closest)
                continue;

            closest = offset.LengthSquared();
            blocker = ent;
        }

        if (blocker == null)
            return false;

        if (weapon.NextAttack > _timing.CurTime)
            return true;

        _combat.SetInCombatMode(uid, true);
        _melee.AttemptLightAttack(uid, weaponUid, weapon, blocker.Value);
        _combat.SetInCombatMode(uid, false);
        return true;
    }

    private Vector2 Heading(NPCSteeringComponent steering)
    {
        var target = steering.CurrentPath.TryPeek(out var node) && node.IsValid()
            ? new EntityCoordinates(node.GraphUid, node.Box.Center)
            : steering.Coordinates;

        return target.IsValid(EntityManager) ? _transform.ToMapCoordinates(target).Position : Vector2.Zero;
    }
}
