using System.Linq;
using System.Numerics;
using Content.Shared._Stalker_EN.Cover;
using Content.Shared.Damage.Components;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Combat;

public enum STShotLine : byte
{
    Clear,
    Cover,
    Blocked,
}

public sealed class STShotLineSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly STLowCoverSystem _cover = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;

    public const float BulletHalfWidth = 0.12f;
    private const float BodyHalfWidth = 0.35f;
    private const float MaxCorridorSpread = 0.35f;
    private const float MaxWallSpread = 1.2f;
    private const float CorridorRange = 4f;
    private const float AimShift = 0.24f;
    private const float NearWallRange = 2f;
    public const float SpreadWaitRange = 4f;
    private static readonly float[] SpreadLanes = { -1f, -0.5f, 0.5f, 1f };
    private static readonly float[] AimShifts = { 0f, AimShift / 2f, -AimShift / 2f, AimShift, -AimShift };

    private EntityQuery<MobStateComponent> _mobQuery;
    private EntityQuery<STLowCoverComponent> _coverQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<RequireProjectileTargetComponent> _passQuery;

    public override void Initialize()
    {
        base.Initialize();

        _mobQuery = GetEntityQuery<MobStateComponent>();
        _coverQuery = GetEntityQuery<STLowCoverComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _passQuery = GetEntityQuery<RequireProjectileTargetComponent>();
    }

    public bool GlassInWay(MapCoordinates from, MapCoordinates to)
    {
        var line = to.Position - from.Position;
        var length = line.Length();
        if (from.MapId != to.MapId || length <= BodyHalfWidth + 0.05f)
            return false;

        var dir = line / length;
        var normal = new Vector2(-dir.Y, dir.X);
        var front = to.Position - dir * BodyHalfWidth;
        var state = (Bodies: _physicsQuery, Pass: _passQuery);

        bool Lane(float lane)
        {
            var start = from.Position + normal * (BulletHalfWidth * lane);
            var edge = front + normal * ((BulletHalfWidth + BodyHalfWidth) * lane) - start;
            var edgeLength = edge.Length();
            var ray = new CollisionRay(start, edge / edgeLength, (int) CollisionGroup.BulletImpassable);
            return _physics.IntersectRayWithPredicate(from.MapId, ray, state,
                static (ent, s) => !s.Bodies.TryComp(ent, out var body) ||
                                   (body.CollisionLayer & (int) CollisionGroup.Opaque) != 0 ||
                                   s.Pass.TryComp(ent, out var pass) && pass.Active,
                edgeLength).Any();
        }

        return Lane(0f) || Lane(-0.5f) && Lane(-1f) || Lane(0.5f) && Lane(1f);
    }

    public bool Unobstructed(EntityUid shooter, MapCoordinates from, MapCoordinates to, EntityUid? target)
    {
        return _interaction.InRangeUnobstructed(from, to, 0f, predicate: ent => Ignored(ent, shooter, target, from, to, true));
    }

    public bool GlassInSpread(MapCoordinates from, MapCoordinates to, Angle spread)
    {
        var line = to.Position - from.Position;
        var length = line.Length();
        if (from.MapId != to.MapId || length <= BodyHalfWidth + 0.05f)
            return false;

        var dir = line / length;
        var normal = new Vector2(-dir.Y, dir.X);
        var tan = MathF.Tan(MathF.Min((float) spread.Theta, MaxCorridorSpread) / 2f);
        var state = (Bodies: _physicsQuery, Pass: _passQuery);
        foreach (var lane in SpreadLanes)
        {
            var start = from.Position + normal * (BulletHalfWidth * lane);
            var segment = to.Position - dir * BodyHalfWidth + normal * ((BulletHalfWidth + length * tan) * lane) - start;
            var segmentLength = segment.Length();
            var ray = new CollisionRay(start, segment / segmentLength, (int) CollisionGroup.BulletImpassable);
            if (_physics.IntersectRayWithPredicate(from.MapId, ray, state,
                    static (ent, s) => !s.Bodies.TryComp(ent, out var body) ||
                                       body.BodyType != BodyType.Static ||
                                       (body.CollisionLayer & (int) CollisionGroup.Opaque) != 0 ||
                                       s.Pass.TryComp(ent, out var pass) && pass.Active,
                    segmentLength).Any())
            {
                return true;
            }
        }

        return false;
    }

    public STShotLine Check(EntityUid shooter, EntityUid target)
    {
        return Check(shooter, _transform.GetMapCoordinates(shooter), _transform.GetMapCoordinates(target), target);
    }

    public STShotLine Check(EntityUid shooter, MapCoordinates from, MapCoordinates to, EntityUid? target = null)
    {
        if (from.MapId != to.MapId)
            return STShotLine.Blocked;

        var line = to.Position - from.Position;
        var length = line.Length();
        if (length < 0.01f)
            return STShotLine.Clear;

        var dir = line / length;
        var ray = new CollisionRay(from.Position, dir, (int) CollisionGroup.BulletImpassable);
        var hasGun = _gun.TryGetGun(shooter, out _, out var gun);
        if (Hits(ray, length, shooter, target, from, to, true) ||
            Clipped(shooter, from, to, target) ||
            GlassInWay(from, to) ||
            hasGun && WallInSpread(shooter, from, to, target, gun!.MinAngleModified) ||
            hasGun && CoverInSpread(from, to, gun!.MinAngleModified))
        {
            return STShotLine.Blocked;
        }

        return Hits(ray, length, shooter, target, from, to, false) ? STShotLine.Cover : STShotLine.Clear;
    }

    public Angle NextSpread(GunComponent gun)
    {
        var shotTime = gun.NextFire > _timing.CurTime ? gun.NextFire : _timing.CurTime;
        var decayed = gun.CurrentAngle.Theta - gun.AngleDecayModified.Theta * (shotTime - gun.LastFire).TotalSeconds;
        return new Angle(Math.Clamp(decayed + gun.AngleIncreaseModified.Theta, gun.MinAngleModified.Theta, gun.MaxAngleModified.Theta));
    }

    public bool CoverInSpread(MapCoordinates from, MapCoordinates to, Angle spread)
    {
        var line = to.Position - from.Position;
        var length = line.Length();
        if (from.MapId != to.MapId || length < 0.01f)
            return false;

        var dir = line / length;
        var tan = MathF.Tan(MathF.Min((float) spread.Theta, MaxCorridorSpread) / 2f);
        var normal = new Vector2(-dir.Y, dir.X);
        var reach = MathF.Min(length, CorridorRange);
        var state = (System: this, From: from, To: to);

        foreach (var lane in SpreadLanes)
        {
            var start = from.Position + normal * (BulletHalfWidth * lane);
            var segment = from.Position + dir * reach + normal * ((BulletHalfWidth + reach * tan) * lane) - start;
            var segmentLength = segment.Length();
            var ray = new CollisionRay(start, segment / segmentLength, (int) CollisionGroup.BulletImpassable);
            if (_physics.IntersectRayWithPredicate(from.MapId, ray, state, static (ent, s) => !s.System.MidCover(ent, s.From, s.To), segmentLength).Any())
                return true;
        }

        return false;
    }

    private bool MidCover(EntityUid ent, MapCoordinates from, MapCoordinates to)
    {
        return _coverQuery.TryComp(ent, out var cover) &&
               !_cover.IsBehind((ent, cover), from) &&
               !_cover.IsBehind((ent, cover), to, cover.Range);
    }

    public bool IsClear(EntityUid shooter, MapCoordinates from, MapCoordinates to, EntityUid? target = null)
    {
        return Check(shooter, from, to, target) == STShotLine.Clear;
    }

    public bool Clipped(EntityUid shooter, MapCoordinates from, MapCoordinates to, EntityUid? target)
    {
        var aim = to.Position;
        return !TryAim(shooter, from, ref aim, target);
    }

    public bool TryAim(EntityUid shooter, MapCoordinates from, ref Vector2 aim, EntityUid? target)
    {
        var line = aim - from.Position;
        var length = line.Length();
        if (length - BodyHalfWidth <= 0.05f)
            return true;

        var normal = new Vector2(-line.Y, line.X) / length;
        foreach (var shift in AimShifts)
        {
            var shifted = aim + normal * shift;
            if (EdgesHit(shooter, from, shifted, target))
                continue;

            aim = shifted;
            return true;
        }

        return false;
    }

    private bool EdgesHit(EntityUid shooter, MapCoordinates from, Vector2 aim, EntityUid? target)
    {
        var line = aim - from.Position;
        var length = line.Length();
        var reach = length - BodyHalfWidth;
        var dir = line / length;
        var edge = new Vector2(-dir.Y, dir.X) * BulletHalfWidth;
        var to = new MapCoordinates(aim, from.MapId);
        var left = FirstHit(new CollisionRay(from.Position + edge, dir, (int) CollisionGroup.BulletImpassable), reach, shooter, target, from, to);
        var right = FirstHit(new CollisionRay(from.Position - edge, dir, (int) CollisionGroup.BulletImpassable), reach, shooter, target, from, to);
        return left != null && right != null || MathF.Min(left ?? float.MaxValue, right ?? float.MaxValue) <= NearWallRange;
    }

    private float? FirstHit(CollisionRay ray, float length, EntityUid shooter, EntityUid? target, MapCoordinates from, MapCoordinates to)
    {
        var state = (System: this, shooter, target, from, to);
        float? first = null;
        foreach (var hit in _physics.IntersectRayWithPredicate(from.MapId, ray, state,
                     static (ent, s) => s.System.Ignored(ent, s.shooter, s.target, s.from, s.to, true), length, false))
        {
            if (first == null || hit.Distance < first)
                first = hit.Distance;
        }

        return first;
    }

    public bool WallInSpread(EntityUid shooter, MapCoordinates from, MapCoordinates to, EntityUid? target, Angle spread, float range = NearWallRange)
    {
        var line = to.Position - from.Position;
        var length = line.Length();
        var reach = MathF.Min(length - BodyHalfWidth, range);
        if (from.MapId != to.MapId || reach <= 0.05f)
            return false;

        var dir = line / length;
        var normal = new Vector2(-dir.Y, dir.X);
        var tan = MathF.Tan(MathF.Min((float) spread.Theta, MaxWallSpread) / 2f);
        foreach (var lane in SpreadLanes)
        {
            var start = from.Position + normal * (BulletHalfWidth * lane);
            var segment = from.Position + dir * reach + normal * ((BulletHalfWidth + reach * tan) * lane) - start;
            var segmentLength = segment.Length();
            if (Hits(new CollisionRay(start, segment / segmentLength, (int) CollisionGroup.BulletImpassable), segmentLength, shooter, target, from, to, true))
                return true;
        }

        return false;
    }

    private bool Hits(CollisionRay ray, float length, EntityUid shooter, EntityUid? target, MapCoordinates from, MapCoordinates to, bool passTargetCover)
    {
        var state = (System: this, shooter, target, from, to, passTargetCover);
        return _physics.IntersectRayWithPredicate(from.MapId, ray, state,
            static (ent, s) => s.System.Ignored(ent, s.shooter, s.target, s.from, s.to, s.passTargetCover), length).Any();
    }

    private bool Ignored(EntityUid ent, EntityUid shooter, EntityUid? target, MapCoordinates from, MapCoordinates to, bool passTargetCover)
    {
        if (ent == shooter || ent == target || _mobQuery.HasComp(ent) || _passQuery.TryComp(ent, out var pass) && pass.Active)
            return true;

        if (!_coverQuery.TryComp(ent, out var cover))
            return false;

        var low = new Entity<STLowCoverComponent>(ent, cover);
        return _cover.IsBehind(low, from) || _cover.IsBehind(low, to, passTargetCover ? cover.Range : 0);
    }
}
