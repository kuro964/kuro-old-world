using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Stalker_EN.NPC.Anomaly;
using Content.Server._Stalker_EN.NPC.Doors;
using Content.Server._Stalker_EN.NPC.Steering;
using Content.Server.Administration.Managers;
using Content.Server.DoAfter;
using Content.Server.NPC.Components;
using Content.Server.NPC.Events;
using Content.Server.NPC.Pathfinding;
using Content.Shared.CCVar;
using Content.Shared.Climbing.Systems;
using Content.Shared.CombatMode;
using Content.Shared.Interaction;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.NPC;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.NPC.Events;
using Content.Shared.Physics;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Content.Shared.Prying.Systems;
using Microsoft.Extensions.ObjectPool;
using Prometheus;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCSteeringSystem : SharedNPCSteeringSystem
{
    private static readonly Gauge ActiveSteeringGauge = Metrics.CreateGauge(
        "npc_steering_active_count",
        "Amount of NPCs trying to actively do steering");

    /*
     * We use context steering to determine which way to move.
     * This involves creating an array of possible directions and assigning a value for the desireability of each direction.
     *
     * There's multiple ways to implement this, e.g. you can average all directions, or you can choose the highest direction
     * , or you can remove the danger map entirely and only having an interest map (AKA game endeavour).
     * See http://www.gameaipro.com/GameAIPro2/GameAIPro2_Chapter18_Context_Steering_Behavior-Driven_Steering_at_the_Macro_Scale.pdf
     * (though in their case it was for an F1 game so used context steering across the width of the road).
     */

    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ClimbSystem _climb = default!;
    [Dependency] private readonly DoAfterSystem _doAfter = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly PathfindingSystem _pathfindingSystem = default!;
    [Dependency] private readonly PryingSystem _pryingSystem = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly STAnomalyAvoidanceSystem _anomalies = default!;
    [Dependency] private readonly STChasmAvoidanceSystem _chasms = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedCombatModeSystem _combat = default!;
    [Dependency] private readonly STDoorBasherSystem _doorBasher = default!;
    [Dependency] private readonly STStuckSystem _stuck = default!;

    private EntityQuery<FixturesComponent> _fixturesQuery;
    private EntityQuery<MovementSpeedModifierComponent> _modifierQuery;
    private EntityQuery<NpcFactionMemberComponent> _factionQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    private ObjectPool<HashSet<EntityUid>> _entSetPool =
        new DefaultObjectPool<HashSet<EntityUid>>(new SetPolicy<EntityUid>());

    /// <summary>
    /// Enabled antistuck detection so if an NPC is in the same spot for a while it will re-path.
    /// </summary>
    public bool AntiStuck = true;

    private bool _enabled;

    private bool _pathfinding = true;

    public static readonly Vector2[] Directions = new Vector2[InterestDirections];

    private static readonly TimeSpan BeelineInterval = TimeSpan.FromSeconds(0.5);
    private const int ShortcutNodes = 6;

    public const float MinMoveSpeed = 0.1f;

    private readonly HashSet<ICommonSession> _subscribedSessions = new();

    private object _obstacles = new();

    private int _activeSteeringCount;

    public override void Initialize()
    {
        base.Initialize();

        Log.Level = LogLevel.Info;
        _fixturesQuery = GetEntityQuery<FixturesComponent>();
        _modifierQuery = GetEntityQuery<MovementSpeedModifierComponent>();
        _factionQuery = GetEntityQuery<NpcFactionMemberComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        for (var i = 0; i < InterestDirections; i++)
        {
            Directions[i] = new Angle(InterestRadians * i).ToVec();
        }

        UpdatesBefore.Add(typeof(SharedPhysicsSystem));
        Subs.CVar(_configManager, CCVars.NPCEnabled, SetNPCEnabled, true);
        Subs.CVar(_configManager, CCVars.NPCPathfinding, SetNPCPathfinding, true);

        SubscribeLocalEvent<NPCSteeringComponent, ComponentShutdown>(OnSteeringShutdown);
        SubscribeNetworkEvent<RequestNPCSteeringDebugEvent>(OnDebugRequest);
    }

    private void SetNPCEnabled(bool obj)
    {
        if (!obj)
        {
            foreach (var (comp, mover) in EntityQuery<NPCSteeringComponent, InputMoverComponent>())
            {
                mover.CurTickSprintMovement = Vector2.Zero;
                comp.PathfindToken?.Cancel();
                comp.PathfindToken = null;
            }
        }

        _enabled = obj;
    }

    private void SetNPCPathfinding(bool value)
    {
        _pathfinding = value;

        if (!_pathfinding)
        {
            foreach (var comp in EntityQuery<NPCSteeringComponent>(true))
            {
                comp.PathfindToken?.Cancel();
                comp.PathfindToken = null;
            }
        }
    }

    private void OnDebugRequest(RequestNPCSteeringDebugEvent msg, EntitySessionEventArgs args)
    {
        if (!_admin.IsAdmin(args.SenderSession))
            return;

        if (msg.Enabled)
            _subscribedSessions.Add(args.SenderSession);
        else
            _subscribedSessions.Remove(args.SenderSession);
    }

    private void OnSteeringShutdown(EntityUid uid, NPCSteeringComponent component, ComponentShutdown args)
    {
        // Cancel any active pathfinding jobs as they're irrelevant.
        component.PathfindToken?.Cancel();
        component.PathfindToken = null;
    }

    /// <summary>
    /// Adds the AI to the steering system to move towards a specific target
    /// </summary>
    public NPCSteeringComponent Register(EntityUid uid, EntityCoordinates coordinates, NPCSteeringComponent? component = null)
    {
        if (Resolve(uid, ref component, false))
        {
            if (component.Coordinates.Equals(coordinates))
                return component;

            component.PathfindToken?.Cancel();
            component.PathfindToken = null;
            component.CurrentPath.Clear();
        }
        else
        {
            component = AddComp<NPCSteeringComponent>(uid);
            component.Flags = _pathfindingSystem.GetFlags(uid);
        }

        ResetStuck(component, Transform(uid).Coordinates);
        component.Coordinates = coordinates;
        return component;
    }

    /// <summary>
    /// Attempts to register the entity. Does nothing if the coordinates already registered.
    /// </summary>
    public bool TryRegister(EntityUid uid, EntityCoordinates coordinates, NPCSteeringComponent? component = null)
    {
        if (Resolve(uid, ref component, false) && component.Coordinates.Equals(coordinates))
        {
            return false;
        }

        Register(uid, coordinates, component);
        return true;
    }

    /// <summary>
    /// Stops the steering behavior for the AI and cleans up.
    /// </summary>
    public void Unregister(EntityUid uid, NPCSteeringComponent? component = null)
    {
        if (!Resolve(uid, ref component, false))
            return;

        if (TryComp(uid, out InputMoverComponent? controller))
        {
            controller.CurTickSprintMovement = Vector2.Zero;

            var ev = new SpriteMoveEvent(false);
            RaiseLocalEvent(uid, ref ev);
        }

        component.PathfindToken?.Cancel();
        component.PathfindToken = null;
        RemComp<NPCSteeringComponent>(uid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_enabled)
            return;

        // Not every mob has the modifier component so do it as a separate query.
        var npcs = new (EntityUid, NPCSteeringComponent, InputMoverComponent, TransformComponent)[Count<ActiveNPCComponent>()];

        var query = EntityQueryEnumerator<ActiveNPCComponent, NPCSteeringComponent, InputMoverComponent, TransformComponent>();
        var index = 0;

        while (query.MoveNext(out var uid, out _, out var steering, out var mover, out var xform))
        {
            npcs[index] = (uid, steering, mover, xform);
            index++;
        }

        // Dependency issues across threads.
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 1,
        };
        var curTime = _timing.CurTime;

        _activeSteeringCount = 0;

        Parallel.For(0, index, options, i =>
        {
            var (uid, steering, mover, xform) = npcs[i];
            Steer(uid, steering, mover, xform, frameTime, curTime);
        });

        ActiveSteeringGauge.Set(_activeSteeringCount);

        if (_subscribedSessions.Count > 0)
        {
            var data = new List<NPCSteeringDebugData>(index);

            for (var i = 0; i < index; i++)
            {
                var (uid, steering, mover, _) = npcs[i];

                data.Add(new NPCSteeringDebugData(
                    GetNetEntity(uid),
                    mover.CurTickSprintMovement,
                    steering.Interest,
                    steering.Danger,
                    steering.DangerPoints));
            }

            var filter = Filter.Empty();
            filter.AddPlayers(_subscribedSessions);

            RaiseNetworkEvent(new NPCSteeringDebugEvent(data), filter);
        }
    }

    private void SetDirection(EntityUid uid, InputMoverComponent component, NPCSteeringComponent steering, Vector2 value, bool clear = true)
    {
        if (clear && value.Equals(Vector2.Zero))
        {
            steering.CurrentPath.Clear();
            Array.Clear(steering.Interest);
            Array.Clear(steering.Danger);
        }

        component.CurTickSprintMovement = value;
        component.LastInputTick = _timing.CurTick;
        component.LastInputSubTick = ushort.MaxValue;

        var ev = new SpriteMoveEvent(true);
        RaiseLocalEvent(uid, ref ev);
    }

    /// <summary>
    /// Go through each steerer and combine their vectors
    /// </summary>
    private void Steer(
        EntityUid uid,
        NPCSteeringComponent steering,
        InputMoverComponent mover,
        TransformComponent xform,
        float frameTime,
        TimeSpan curTime)
    {
        if (Deleted(steering.Coordinates.EntityId))
        {
            SetDirection(uid, mover, steering, Vector2.Zero);
            steering.Status = SteeringStatus.NoPath;
            return;
        }

        // No path set from pathfinding or the likes.
        if (steering.Status == SteeringStatus.NoPath)
        {
            SetDirection(uid, mover, steering, Vector2.Zero);
            return;
        }

        // Can't move at all, just noop input.
        if (!mover.CanMove)
        {
            SetDirection(uid, mover, steering, Vector2.Zero);
            steering.Status = SteeringStatus.NoPath;
            return;
        }

        Interlocked.Increment(ref _activeSteeringCount);

        var agentRadius = steering.Radius;
        var worldPos = _transform.GetWorldPosition(xform);
        var (layer, mask) = _physics.GetHardCollision(uid);

        // Use rotation relative to parent to rotate our context vectors by.
        var offsetRot = -_mover.GetParentGridAngle(mover);
        _modifierQuery.TryGetComponent(uid, out var modifier);
        var moveSpeed = GetSprintSpeed(uid, modifier);
        if (moveSpeed < MinMoveSpeed)
        {
            ResetStuck(steering, xform.Coordinates);
            SetDirection(uid, mover, steering, Vector2.Zero, false);
            return;
        }

        var body = _physicsQuery.GetComponent(uid);
        var dangerPoints = steering.DangerPoints;
        dangerPoints.Clear();
        Span<float> interest = stackalloc float[InterestDirections];
        Span<float> danger = stackalloc float[InterestDirections];

        // TODO: This should be fly
        steering.CanSeek = true;

        var ev = new NPCSteeringEvent(steering, xform, worldPos, offsetRot);
        RaiseLocalEvent(uid, ref ev);
        // If seek has arrived at the target node for example then immediately re-steer.
        var forceSteer = true;

        if (steering.CanSeek && !TrySeek(uid, mover, steering, body, xform, offsetRot, moveSpeed, interest, frameTime, ref forceSteer))
        {
            SetDirection(uid, mover, steering, Vector2.Zero);
            return;
        }

        DebugTools.Assert(!float.IsNaN(interest[0]));

        // Don't steer too frequently to avoid twitchiness.
        // This should also implicitly solve tie situations.
        // I think doing this after all the ops above is best?
        // Originally I had it way above but sometimes mobs would overshoot their tile targets.

        if (!forceSteer)
        {
            SetDirection(uid, mover, steering, steering.LastSteerDirection, false);
            return;
        }

        // Avoid static objects like walls
        CollisionAvoidance(uid, offsetRot, worldPos, agentRadius, layer, mask, xform, danger);
        DebugTools.Assert(!float.IsNaN(danger[0]));

        Separation(uid, offsetRot, worldPos, agentRadius, layer, mask, body, xform, danger);

        foreach (var (direction, weight) in steering.ExtraInterest)
        {
            ApplySeek(interest, offsetRot.RotateVec(direction), weight);
        }

        foreach (var (direction, weight) in steering.ExtraDanger)
        {
            ApplyDanger(danger, offsetRot.RotateVec(direction), weight);
        }

        // Blend last and current tick
        Blend(steering, frameTime, interest, danger);

        // Remove the danger map from the interest map.
        var desiredDirection = -1;
        var desiredValue = 0f;

        for (var i = 0; i < InterestDirections; i++)
        {
            var adjustedValue = Math.Clamp(steering.Interest[i] - steering.Danger[i], 0f, 1f);

            if (adjustedValue > desiredValue)
            {
                desiredDirection = i;
                desiredValue = adjustedValue;
            }
        }

        var resultDirection = Vector2.Zero;

        if (desiredDirection != -1)
        {
            var leftIndex = (desiredDirection + InterestDirections - 1) % InterestDirections;
            var rightIndex = (desiredDirection + 1) % InterestDirections;
            var left = Math.Clamp(steering.Interest[leftIndex] - steering.Danger[leftIndex], 0f, 1f);
            var right = Math.Clamp(steering.Interest[rightIndex] - steering.Danger[rightIndex], 0f, 1f);
            var curve = left - 2f * desiredValue + right;
            var offset = curve < -0.0001f ? Math.Clamp(0.5f * (left - right) / curve, -0.5f, 0.5f) : 0f;
            resultDirection = new Angle((desiredDirection + offset) * InterestRadians).ToVec();
        }

        steering.LastSteerDirection = resultDirection;
        DebugTools.Assert(!float.IsNaN(resultDirection.X));
        SetDirection(uid, mover, steering, resultDirection, false);
    }

    private EntityCoordinates GetCoordinates(PathPoly poly)
    {
        if (!poly.IsValid())
            return EntityCoordinates.Invalid;

        return new EntityCoordinates(poly.GraphUid, poly.Box.Center);
    }

    private bool TryBeeline(EntityUid uid, NPCSteeringComponent steering)
    {
        var targetPoly = _pathfindingSystem.GetPoly(steering.Coordinates);
        if (targetPoly == null ||
            steering.ForcePath ||
            !TryComp<PhysicsComponent>(uid, out var physics))
        {
            return false;
        }

        var bodyRadius = BodyRadius(uid, steering);
        var walkMask = (CollisionGroup) physics.CollisionMask;
        var here = _transform.GetMapCoordinates(uid);
        var direct = steering.Coordinates.Position.Equals(Vector2.Zero)
            ? CanBeeline(uid, steering.Coordinates.EntityId, bodyRadius, walkMask)
            : CanWalk(uid, here, _transform.ToMapCoordinates(steering.Coordinates), bodyRadius, walkMask);

        if (!direct ||
            _anomalies.LineHazard(uid, steering.Coordinates) ||
            _chasms.LineCrosses(uid, steering.Coordinates, bodyRadius))
        {
            var ahead = Math.Min(steering.CurrentPath.Count - 1, ShortcutNodes);
            for (var skip = ahead; skip >= 1; skip--)
            {
                var node = GetCoordinates(steering.CurrentPath.ElementAt(skip));
                if (!CanWalk(uid, here, _transform.ToMapCoordinates(node), bodyRadius, walkMask) ||
                    _anomalies.LineHazard(uid, node) ||
                    _chasms.LineCrosses(uid, node, bodyRadius))
                {
                    continue;
                }

                for (var i = 0; i < skip; i++)
                    steering.CurrentPath.Dequeue();

                break;
            }

            return false;
        }

        steering.CurrentPath.Clear();
        steering.CurrentPath.Enqueue(targetPoly);
        return true;
    }

    /// <summary>
    /// Get a new job from the pathfindingsystem
    /// </summary>
    private async void RequestPath(EntityUid uid, NPCSteeringComponent steering, TransformComponent xform, float targetDistance)
    {
        // If we already have a pathfinding request then don't grab another.
        // If we're in range then just beeline them; this can avoid stutter stepping and is an easy way to look nicer.
        if (steering.Pathfind || targetDistance < steering.RepathRange)
            return;

        // Short-circuit with no path.
        // If this still causes issues future sloth adjust the collision mask.
        // Thanks past sloth I already realised.
        if (TryBeeline(uid, steering))
            return;

        steering.ForcePath = false;
        var token = new CancellationTokenSource();
        steering.PathfindToken = token;

        var flags = _pathfindingSystem.GetFlags(uid);

        var result = await _pathfindingSystem.GetPathSafe(
            uid,
            xform.Coordinates,
            steering.Coordinates,
            steering.Range,
            token.Token,
            flags,
            GetAvoidTiles(uid));

        if (token.IsCancellationRequested)
            return;

        steering.PathfindToken = null;

        if (result.Result == PathResult.NoPath)
        {
            steering.CurrentPath.Clear();
            steering.FailedPathCount++;

            if (steering.FailedPathCount >= NPCSteeringComponent.FailedPathLimit)
            {
                steering.Status = SteeringStatus.NoPath;
            }

            return;
        }

        var targetPos = _transform.ToMapCoordinates(steering.Coordinates);
        var ourPos = _transform.GetMapCoordinates(uid, xform: xform);

        PrunePath(uid, ourPos, targetPos.Position - ourPos.Position, result.Path);
        steering.CurrentPath = new Queue<PathPoly>(result.Path);
    }

    private void AvoidNextTile(EntityUid uid, NPCSteeringComponent steering)
    {
        var next = steering.CurrentPath.Count > 1 && steering.CurrentPath.TryPeek(out var node) ? node : PolyAhead(uid, steering);
        if (next == null)
            return;

        var avoid = EnsureComp<STNPCAvoidTilesComponent>(uid);
        avoid.Tiles[(next.GraphUid, next.ChunkOrigin, next.TileIndex)] = _timing.CurTime + avoid.AvoidTime;
    }

    private PathPoly? PolyAhead(EntityUid uid, NPCSteeringComponent steering)
    {
        var from = _transform.GetMapCoordinates(uid);
        var to = _transform.ToMapCoordinates(steering.Coordinates);
        var dir = to.Position - from.Position;
        if (from.MapId != to.MapId || dir.LengthSquared() < 0.01f)
            return null;

        var ahead = from.Offset(Vector2.Normalize(dir) * (BodyRadius(uid, steering) + 0.5f));
        var poly = _pathfindingSystem.GetPoly(_transform.ToCoordinates(Transform(uid).ParentUid, ahead));
        return poly == null || poly.Equals(_pathfindingSystem.GetPoly(steering.Coordinates)) ? null : poly;
    }

    public void Unstick(EntityUid uid, NPCSteeringComponent steering)
    {
        AvoidNextTile(uid, steering);
        steering.ForcePath = true;
        steering.CurrentPath.Clear();
    }

    public float BodyRadius(EntityUid uid, NPCSteeringComponent? steering = null)
    {
        var radius = steering?.Radius ?? 0f;
        if (!TryComp<FixturesComponent>(uid, out var fixtures))
            return radius;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (!fixture.Hard)
                continue;

            var bounds = fixture.Shape.ComputeAABB(new Transform(Vector2.Zero, 0f), 0);
            radius = MathF.Max(radius, MathF.Max(bounds.Width, bounds.Height) / 2f);
        }

        return radius;
    }

    private HashSet<(EntityUid Graph, Vector2i Chunk, byte Tile)>? GetAvoidTiles(EntityUid uid)
    {
        if (!TryComp<STNPCAvoidTilesComponent>(uid, out var avoid))
            return null;

        var now = _timing.CurTime;

        foreach (var (tile, until) in avoid.Tiles)
        {
            if (until <= now)
                avoid.Tiles.Remove(tile);
        }

        if (avoid.Tiles.Count == 0)
            return null;

        return new HashSet<(EntityUid Graph, Vector2i Chunk, byte Tile)>(avoid.Tiles.Keys);
    }

    public bool CanBeeline(EntityUid uid, EntityUid target, float radius, CollisionGroup mask)
    {
        if (!_interaction.InRangeUnobstructed(uid, target, range: 30f, mask))
            return false;

        return SidesClear(_transform.GetMapCoordinates(uid), _transform.GetMapCoordinates(target), radius, mask, ent => ent == uid || ent == target);
    }

    public bool CanWalk(EntityUid uid, MapCoordinates from, MapCoordinates to, float radius, CollisionGroup mask)
    {
        SharedInteractionSystem.Ignored ignore = ent => ent == uid;
        return _interaction.InRangeUnobstructed(from, to, 0f, mask, ignore) && SidesClear(from, to, radius, mask, ignore);
    }

    private bool SidesClear(MapCoordinates from, MapCoordinates to, float radius, CollisionGroup mask, SharedInteractionSystem.Ignored ignore)
    {
        var dir = to.Position - from.Position;
        if (dir.LengthSquared() < 0.0001f)
            return true;

        var side = new Vector2(-dir.Y, dir.X).Normalized() * radius;
        return _interaction.InRangeUnobstructed(from.Offset(side), to.Offset(side), 0f, mask, ignore) &&
               _interaction.InRangeUnobstructed(from.Offset(-side), to.Offset(-side), 0f, mask, ignore);
    }

    // TODO: Move these to movercontroller

    private float GetSprintSpeed(EntityUid uid, MovementSpeedModifierComponent? modifier = null)
    {
        if (!Resolve(uid, ref modifier, false))
        {
            return MovementSpeedModifierComponent.DefaultBaseSprintSpeed;
        }

        return modifier.CurrentSprintSpeed;
    }
}
