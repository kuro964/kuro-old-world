using System.Numerics;
using Content.Server._Stalker_EN.NPC.Pathfinding;
using Content.Server._Stalker_EN.NPC.Steering;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.NPC;
using Content.Shared._Stalker.ZoneAnomaly.Components;
using Content.Shared._Stalker.ZoneAnomaly.Effects.Components;
using Content.Shared._Stalker.ZoneAnomaly.Triggers;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Whitelist;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Stalker_EN.NPC.Anomaly;

public sealed class STAnomalyAvoidanceSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;

    private const float PathMargin = 0.4f;
    private const float FieldCost = 4f;
    private const float RefreshReach = 4f;
    private const float LineStep = 0.5f;
    private const int EscapeReach = 8;
    private const string TargetKey = "Target";
    private const float EscapeArrive = 0.4f;
    private const float MinSpeed = 0.1f;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan EscapeInterval = TimeSpan.FromSeconds(0.5);
    private TimeSpan _nextEscape;

    private sealed record Hazard(EntityUid Grid, float Radius, List<Vector2i> Tiles);

    private readonly Dictionary<EntityUid, Hazard> _hazards = new();
    private readonly Dictionary<EntityUid, Dictionary<Vector2i, int>> _tiles = new();
    private readonly HashSet<Entity<ZoneAnomalyComponent>> _found = new();
    private Func<PathPoly, PathPoly, float> _pathCost = default!;

    public override void Initialize()
    {
        base.Initialize();
        _pathCost = PathCost;
        SubscribeLocalEvent<ZoneAnomalyComponent, MapInitEvent>(OnAnomalyMapInit);
        SubscribeLocalEvent<ZoneAnomalyComponent, MoveEvent>(OnAnomalyMove);
        SubscribeLocalEvent<ZoneAnomalyComponent, ComponentShutdown>(OnAnomalyShutdown);
        SubscribeLocalEvent<STAnomalyAvoidanceComponent, STSteeringTermsEvent>(OnSteeringTerms);
        SubscribeLocalEvent<STAnomalyAvoidanceComponent, STPathCostEvent>(OnPathCost);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextEscape)
            return;

        _nextEscape = now + EscapeInterval;
        var query = EntityQueryEnumerator<STAnomalyAvoidanceComponent, ActiveNPCComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out _, out var xform))
        {
            var steering = CompOrNull<NPCSteeringComponent>(uid);
            if (comp.Escape is { } escape)
            {
                if (steering == null || !steering.Coordinates.Equals(escape))
                {
                    comp.Escape = null;
                }
                else if (steering.Status != SteeringStatus.Moving || !InHazard(uid, xform.Coordinates))
                {
                    comp.Escape = null;
                    _steering.Unregister(uid, steering);
                }

                continue;
            }

            if (comp.Weight <= 0f ||
                xform.GridUid is not { } grid ||
                !_tiles.TryGetValue(grid, out var tiles) ||
                TryComp<InputMoverComponent>(uid, out var mover) && !mover.CanMove ||
                TryComp<MovementSpeedModifierComponent>(uid, out var speed) && speed.CurrentSprintSpeed < MinSpeed)
            {
                continue;
            }

            if (steering != null &&
                (TryComp<HTNComponent>(uid, out var htn) && htn.Blackboard.ContainsKey(TargetKey) ||
                 !steering.Coordinates.IsValid(EntityManager) ||
                 _transform.GetMapId(steering.Coordinates) != xform.MapID ||
                 !tiles.ContainsKey(_transform.WithEntityId(steering.Coordinates, grid).Position.Floored())))
            {
                continue;
            }

            var tile = _transform.WithEntityId(xform.Coordinates, grid).Position.Floored();
            if (!tiles.ContainsKey(tile) || FindExit(grid, tiles, tile) is not { } exit)
                continue;

            comp.Escape = exit;
            _steering.Register(uid, exit).Range = EscapeArrive;
        }
    }

    private EntityCoordinates? FindExit(EntityUid grid, Dictionary<Vector2i, int> tiles, Vector2i from)
    {
        for (var ring = 1; ring <= EscapeReach; ring++)
        {
            EntityCoordinates? best = null;
            var bestDistance = float.MaxValue;
            for (var x = -ring; x <= ring; x++)
            {
                for (var y = -ring; y <= ring; y++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(y)) != ring)
                        continue;

                    var tile = from + new Vector2i(x, y);
                    var coords = new EntityCoordinates(grid, tile.X + 0.5f, tile.Y + 0.5f);
                    var distance = x * x + y * y;
                    if (distance >= bestDistance ||
                        tiles.ContainsKey(tile) ||
                        _pathfinding.GetPoly(coords) is not { } poly ||
                        !poly.Data.IsFreeSpace)
                    {
                        continue;
                    }

                    best = coords;
                    bestDistance = distance;
                }
            }

            if (best != null)
                return best;
        }

        return null;
    }

    public bool InHazard(EntityUid uid, EntityCoordinates coordinates)
    {
        return TryComp<STAnomalyAvoidanceComponent>(uid, out var comp) &&
               comp.Weight > 0f &&
               _transform.GetGrid(coordinates) is { } grid &&
               _tiles.TryGetValue(grid, out var tiles) &&
               tiles.ContainsKey(_transform.WithEntityId(coordinates, grid).Position.Floored());
    }

    public bool Crosses(EntityUid uid, MapCoordinates from, MapCoordinates to, float radius)
    {
        if (!TryComp<STAnomalyAvoidanceComponent>(uid, out var comp) || comp.Weight <= 0f)
            return false;

        var segment = to.Position - from.Position;
        var lengthSquared = segment.LengthSquared();
        foreach (var (center, hazardRadius) in GetNearby((uid, comp), from, _timing.CurTime))
        {
            var t = lengthSquared < 0.0001f ? 0f : Math.Clamp(Vector2.Dot(center - from.Position, segment) / lengthSquared, 0f, 1f);
            if ((from.Position + segment * t - center).Length() < hazardRadius + radius)
                return true;
        }

        return false;
    }

    public bool LineHazard(EntityUid uid, EntityCoordinates target)
    {
        if (_tiles.Count == 0 ||
            !TryComp<STAnomalyAvoidanceComponent>(uid, out var comp) ||
            comp.Weight <= 0f ||
            comp.IgnoreWhileChasing && IsChaseTarget(uid, target.EntityId))
        {
            return false;
        }

        var xform = Transform(uid);
        if (xform.GridUid is not { } grid ||
            !_tiles.TryGetValue(grid, out var tiles) ||
            _transform.GetMapId(target) != xform.MapID)
        {
            return false;
        }

        var from = _transform.WithEntityId(xform.Coordinates, grid).Position;
        var line = _transform.WithEntityId(target, grid).Position - from;
        var steps = (int) MathF.Ceiling(line.Length() / LineStep);
        for (var i = 1; i <= steps; i++)
        {
            if (tiles.ContainsKey((from + line * (i / (float) steps)).Floored()))
                return true;
        }

        return false;
    }

    private void OnSteeringTerms(Entity<STAnomalyAvoidanceComponent> ent, ref STSteeringTermsEvent args)
    {
        var comp = ent.Comp;
        var steering = args.Steering;
        if (comp.Weight <= 0f || comp.IgnoreWhileChasing && IsChaseTarget(ent, steering.Coordinates.EntityId))
            return;

        var self = _transform.GetMapCoordinates(args.Transform);
        var nearby = GetNearby(ent, self, args.CurTime);
        if (nearby.Count == 0)
            return;

        var body = _steering.BodyRadius(ent, steering);
        foreach (var (center, radius) in nearby)
        {
            var offset = center - self.Position;
            var edge = offset.Length() - radius - body;
            if (edge < comp.Range)
                steering.ExtraDanger.Add((offset, comp.Weight * (1f - MathF.Max(edge, 0f) / comp.Range)));
        }
    }

    private void OnPathCost(Entity<STAnomalyAvoidanceComponent> ent, ref STPathCostEvent args)
    {
        if (ent.Comp.Weight <= 0f ||
            _tiles.Count == 0 ||
            ent.Comp.IgnoreWhileChasing && args.Destination is { } destination && IsChaseTarget(ent, destination.EntityId))
        {
            return;
        }

        args.Add(_pathCost);
    }

    private float PathCost(PathPoly from, PathPoly to)
    {
        return !IsHazardTile(to) ? 0f : IsHazardTile(from) ? FieldCost : float.PositiveInfinity;
    }

    private List<(Vector2 Center, float Radius)> GetNearby(Entity<STAnomalyAvoidanceComponent> ent, MapCoordinates self, TimeSpan now)
    {
        var comp = ent.Comp;
        if (now < comp.NextRefresh && self.MapId == comp.NearbyMap)
            return comp.Nearby;

        comp.NextRefresh = now + RefreshInterval * _random.NextFloat(0.8f, 1.2f);
        comp.NearbyMap = self.MapId;
        comp.Nearby.Clear();
        if (_hazards.Count == 0)
            return comp.Nearby;

        _found.Clear();
        _lookup.GetEntitiesInRange(self, comp.Range + RefreshReach, _found, LookupFlags.Static | LookupFlags.Sensors | LookupFlags.Approximate);
        foreach (var anomaly in _found)
        {
            if (_hazards.TryGetValue(anomaly, out var hazard))
                comp.Nearby.Add((_transform.GetWorldPosition(anomaly), hazard.Radius));
        }

        return comp.Nearby;
    }

    private bool IsChaseTarget(EntityUid uid, EntityUid target)
    {
        return HasComp<MobStateComponent>(target) && !_faction.IsEntityFriendly(uid, target);
    }

    private bool IsHazardTile(PathPoly poly)
    {
        return _tiles.TryGetValue(poly.GraphUid, out var tiles) && tiles.ContainsKey(poly.Box.Center.Floored());
    }

    private void OnAnomalyMapInit(Entity<ZoneAnomalyComponent> ent, ref MapInitEvent args)
    {
        Index(ent);
    }

    private void OnAnomalyMove(Entity<ZoneAnomalyComponent> ent, ref MoveEvent args)
    {
        if (MetaData(ent).EntityLifeStage == EntityLifeStage.MapInitialized)
            Index(ent);
    }

    private void OnAnomalyShutdown(Entity<ZoneAnomalyComponent> ent, ref ComponentShutdown args)
    {
        Unindex(ent);
    }

    private void Index(Entity<ZoneAnomalyComponent> ent)
    {
        Unindex(ent);
        if (Transform(ent).GridUid is not { } grid || !IsHazard(ent))
            return;

        var radius = TriggerRadius(ent);
        if (TryComp<ZoneAnomalyEffectGravityWellComponent>(ent, out var well) && well.Mode == ZoneAnomalyEffectGravityWellMode.Attract)
            radius = MathF.Max(radius, well.Distance);
        var reach = radius + PathMargin;
        var center = _transform.WithEntityId(Transform(ent).Coordinates, grid).Position;
        var min = (center - new Vector2(reach)).Floored();
        var max = (center + new Vector2(reach)).Floored();
        var counts = _tiles.GetOrNew(grid);
        var tiles = new List<Vector2i>();

        for (var x = min.X; x <= max.X; x++)
        {
            for (var y = min.Y; y <= max.Y; y++)
            {
                if ((new Vector2(x + 0.5f, y + 0.5f) - center).Length() > reach)
                    continue;

                var tile = new Vector2i(x, y);
                counts[tile] = counts.GetValueOrDefault(tile) + 1;
                tiles.Add(tile);
            }
        }

        _hazards[ent] = new Hazard(grid, radius, tiles);
    }

    private void Unindex(EntityUid uid)
    {
        if (!_hazards.Remove(uid, out var hazard) || !_tiles.TryGetValue(hazard.Grid, out var counts))
            return;

        foreach (var tile in hazard.Tiles)
        {
            var left = counts[tile] - 1;
            if (left > 0)
                counts[tile] = left;
            else
                counts.Remove(tile);
        }

        if (counts.Count == 0)
            _tiles.Remove(hazard.Grid);
    }

    private bool IsHazard(Entity<ZoneAnomalyComponent> ent)
    {
        if (!TryComp<PhysicsComponent>(ent, out var body) || body.BodyType != BodyType.Static || HasFilter(ent.Comp.CollisionBlacklist))
            return false;

        var triggered = false;
        foreach (var trigger in EntityManager.GetComponents<ZoneAnomalyTriggerCollideComponent>(ent))
        {
            if (trigger.Whitelist != null || trigger.Blacklist != null)
                return false;

            triggered = true;
        }

        return triggered;
    }

    private static bool HasFilter(EntityWhitelist list)
    {
        return list.Components is { Length: > 0 } || list.Tags is { Count: > 0 } || list.Sizes is { Count: > 0 };
    }

    private float TriggerRadius(EntityUid uid)
    {
        if (!TryComp<FixturesComponent>(uid, out var fixtures))
            return 0f;

        var radius = 0f;
        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (fixture.Hard)
                continue;

            var bounds = fixture.Shape.ComputeAABB(new Transform(Vector2.Zero, 0f), 0);
            radius = MathF.Max(radius, MathF.Max(bounds.Width, bounds.Height) / 2f);
        }

        return radius;
    }
}
