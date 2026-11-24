using System.Linq;
using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server._Stalker_EN.NPC.Brain;
using Content.Server.Chat.Systems;
using Content.Server.NPC.Pathfinding;
using Content.Shared._Stalker.Bands;
using Content.Shared._Stalker_EN.NPC.Hive;
using Content.Shared._Stalker_OW.Monolith;
using Content.Shared.Actions;
using Content.Shared.Chat;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.NPC;
using Content.Shared.NPC.Systems;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Hive;

public sealed class STHiveSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly STAwarenessSystem _awareness = default!;
    [Dependency] private readonly STMoverSystem _mover = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly Content.Shared.Examine.ExamineSystemShared _examine = default!;
    [Dependency] private readonly ViewSubscriberSystem _viewSubscriber = default!;

    private static readonly EntProtoId CallAction = "ActionSTHiveCall";
    private static readonly EntProtoId HoldAction = "ActionSTHiveHold";
    private static readonly EntProtoId ReleaseAction = "ActionSTHiveRelease";
    private static readonly TimeSpan ThinkInterval = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan RallyLock = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DirectTime = TimeSpan.FromSeconds(4);
    private const float CallRange = 15f;
    private const int MaxFollowers = 10;
    private const float FollowLeash = 12f;
    private const float StragglerDistance = 30f;
    private static readonly float[] LandingDistances = { 1f, 1.5f, 2f, 2.5f, 0.5f };
    private const int LandingDirections = 8;
    private const float JumpDistance = 8f;
    private const float SlotRadius = 2f;
    private const float SlotRing = 1.2f;
    private const int SlotsPerRing = 5;
    private const float ScatterMin = 4f;
    private const float ScatterMax = 10f;
    private const int ScatterTries = 4;
    private static readonly TimeSpan ScatterCheck = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan ShareInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan ShareLock = TimeSpan.FromSeconds(2);
    private const float ShareRange = 20f;
    private const float SpeakerShare = 0.3f;
    private const int MaxSpeakers = 3;
    private const float SpeakGapMin = 0.3f;
    private const float SpeakGapMax = 0.8f;

    private readonly HashSet<Entity<STHiveCallableComponent>> _nearby = new();
    private readonly List<EntityUid> _lost = new();
    private readonly List<EntityUid> _settled = new();
    private readonly List<(EntityUid Speaker, string Line, TimeSpan At)> _lines = new();
    private readonly HashSet<Entity<Content.Shared.Mobs.Components.MobStateComponent>> _seen = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MonolithHivemindComponent, ComponentStartup>(OnHivemindStartup);
        SubscribeLocalEvent<MonolithHivemindComponent, ComponentShutdown>(OnHivemindShutdown);
        SubscribeLocalEvent<STHiveCallableComponent, PlayerAttachedEvent>(OnCallablePossessed);
        SubscribeLocalEvent<STHiveCallableComponent, PlayerDetachedEvent>(OnCallableReleased);
        SubscribeLocalEvent<STHiveLeaderComponent, ComponentShutdown>(OnLeaderShutdown);
        SubscribeLocalEvent<STHiveLeaderComponent, STHiveCallActionEvent>(OnCall);
        SubscribeLocalEvent<STHiveLeaderComponent, STHiveHoldActionEvent>(OnHold);
        SubscribeLocalEvent<STHiveLeaderComponent, STHiveReleaseActionEvent>(OnRelease);
        SubscribeLocalEvent<STHiveLeaderComponent, DamageChangedEvent>(OnLeaderDamaged);
        SubscribeLocalEvent<STHiveLeaderComponent, MobStateChangedEvent>(OnLeaderMobState);
        SubscribeLocalEvent<BandsComponent, DamageChangedEvent>(OnBandsDamaged);
        SubscribeLocalEvent<STHiveFollowerComponent, MobStateChangedEvent>(OnFollowerMobState);
        SubscribeLocalEvent<STHiveFollowerComponent, ComponentShutdown>(OnFollowerShutdown);
    }

    private void OnHivemindStartup(Entity<MonolithHivemindComponent> ent, ref ComponentStartup args)
    {
        GrantLeader(ent);
    }

    private void OnHivemindShutdown(Entity<MonolithHivemindComponent> ent, ref ComponentShutdown args)
    {
        if (!TerminatingOrDeleted(ent))
            RevokeLeader(ent);
    }

    private void OnCallablePossessed(Entity<STHiveCallableComponent> ent, ref PlayerAttachedEvent args)
    {
        if (HasComp<MonolithHivemindComponent>(ent))
            return;

        RemCompDeferred<STHiveFollowerComponent>(ent);
        GrantLeader(ent);
    }

    private void OnCallableReleased(Entity<STHiveCallableComponent> ent, ref PlayerDetachedEvent args)
    {
        if (!HasComp<MonolithHivemindComponent>(ent) && !TerminatingOrDeleted(ent))
            RevokeLeader(ent);
    }

    private void GrantLeader(EntityUid uid)
    {
        var leader = EnsureComp<STHiveLeaderComponent>(uid);
        EnsureComp<STHiveSightComponent>(uid);
        _actions.AddAction(uid, ref leader.CallAction, CallAction);
        _actions.AddAction(uid, ref leader.HoldAction, HoldAction);
        _actions.AddAction(uid, ref leader.ReleaseAction, ReleaseAction);
    }

    private void RevokeLeader(EntityUid uid)
    {
        RemCompDeferred<STHiveLeaderComponent>(uid);
        RemCompDeferred<STHiveSightComponent>(uid);
    }

    private void OnLeaderShutdown(Entity<STHiveLeaderComponent> ent, ref ComponentShutdown args)
    {
        foreach (var follower in ent.Comp.Followers.ToArray())
            ReturnToPost(follower);

        foreach (var (eye, viewer) in ent.Comp.Watching)
        {
            if (!TerminatingOrDeleted(eye))
                _viewSubscriber.RemoveViewSubscriber(eye, viewer);
        }

        ent.Comp.Watching.Clear();

        if (TerminatingOrDeleted(ent))
            return;

        _actions.RemoveAction(ent.Owner, ent.Comp.CallAction);
        _actions.RemoveAction(ent.Owner, ent.Comp.HoldAction);
        _actions.RemoveAction(ent.Owner, ent.Comp.ReleaseAction);
    }

    private void OnCall(Entity<STHiveLeaderComponent> ent, ref STHiveCallActionEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        if (!_mobState.IsAlive(ent))
            return;

        foreach (var follower in ent.Comp.Followers)
        {
            if (TryComp<STHiveFollowerComponent>(follower, out var comp))
            {
                comp.Hold = false;
                comp.HoldAt = null;
            }
        }

        var here = _transform.GetMapCoordinates(ent);
        _nearby.Clear();
        _lookup.GetEntitiesInRange(here, CallRange, _nearby);
        var joined = 0;
        foreach (var npc in _nearby.OrderBy(n => (_transform.GetWorldPosition(n) - here.Position).LengthSquared()))
        {
            if (ent.Comp.Followers.Count >= MaxFollowers)
                break;

            if (HasComp<STHiveFollowerComponent>(npc) ||
                HasComp<STHiveLeaderComponent>(npc) ||
                HasComp<ActorComponent>(npc) ||
                !HasComp<STBrainComponent>(npc) ||
                !_mobState.IsAlive(npc) ||
                !Mobile(npc))
            {
                continue;
            }

            var follower = AddComp<STHiveFollowerComponent>(npc);
            follower.Leader = ent;
            follower.Slot = FreeSlot(ent.Comp);
            ent.Comp.Followers.Add(npc);
            joined++;
        }

        if (ent.Comp.Followers.Count == 0)
        {
            _popup.PopupEntity(Loc.GetString("st-hive-none"), ent, ent);
            return;
        }

        SyncSight(ent, ent.Comp);
        Speak(ent.Comp, here, "st-hive-call", 4);
        var message = ent.Comp.Followers.Count >= MaxFollowers && joined == 0
            ? Loc.GetString("st-hive-full")
            : Loc.GetString("st-hive-answered", ("count", ent.Comp.Followers.Count));
        _popup.PopupEntity(message, ent, ent);
    }

    private void OnHold(Entity<STHiveLeaderComponent> ent, ref STHiveHoldActionEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        if (ent.Comp.Followers.Count == 0)
        {
            _popup.PopupEntity(Loc.GetString("st-hive-none"), ent, ent);
            return;
        }

        foreach (var follower in ent.Comp.Followers)
        {
            if (!TryComp<STHiveFollowerComponent>(follower, out var comp))
                continue;

            comp.Hold = true;
            comp.HoldAt = Transform(follower).Coordinates;
        }

        Speak(ent.Comp, _transform.GetMapCoordinates(ent), "st-hive-hold", 3);
    }

    private void OnRelease(Entity<STHiveLeaderComponent> ent, ref STHiveReleaseActionEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        if (ent.Comp.Followers.Count == 0)
        {
            _popup.PopupEntity(Loc.GetString("st-hive-none"), ent, ent);
            return;
        }

        Speak(ent.Comp, _transform.GetMapCoordinates(ent), "st-hive-release", 3);
        foreach (var follower in ent.Comp.Followers.ToArray())
            ReturnToPost(follower);
    }

    private void OnLeaderDamaged(Entity<STHiveLeaderComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased ||
            args.Origin is not { } attacker ||
            attacker == ent.Owner ||
            ent.Comp.Followers.Contains(attacker) ||
            TerminatingOrDeleted(attacker))
        {
            return;
        }

        RallyAll(ent.Comp, attacker);
    }

    private void OnBandsDamaged(Entity<BandsComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased ||
            args.Origin is not { } attacker ||
            attacker == ent.Owner ||
            !TryComp<STHiveLeaderComponent>(attacker, out var leader) ||
            leader.Followers.Count == 0 ||
            leader.Followers.Contains(ent))
        {
            return;
        }

        RallyAll(leader, ent);
    }

    private void OnLeaderMobState(Entity<STHiveLeaderComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead || ent.Comp.Followers.Count == 0)
            return;

        Speak(ent.Comp, _transform.GetMapCoordinates(ent), "st-hive-fallen", 3);
        foreach (var follower in ent.Comp.Followers.ToArray())
            Scatter(follower);
    }

    private void OnFollowerMobState(Entity<STHiveFollowerComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Alive)
            RemCompDeferred<STHiveFollowerComponent>(ent);
    }

    private void OnFollowerShutdown(Entity<STHiveFollowerComponent> ent, ref ComponentShutdown args)
    {
        if (TryComp<STHiveLeaderComponent>(ent.Comp.Leader, out var leader))
            leader.Followers.Remove(ent);

        if (TryComp<STBrainComponent>(ent, out var brain))
        {
            brain.Anchor = null;
            brain.Rooted = false;
            brain.AnchorLeash = 0f;
            brain.AnchorDoing = string.Empty;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        _lost.Clear();
        var query = EntityQueryEnumerator<STHiveFollowerComponent, STBrainComponent>();
        while (query.MoveNext(out var uid, out var follower, out var brain))
        {
            if (follower.LifeStage > ComponentLifeStage.Running || now < follower.NextThink)
                continue;

            follower.NextThink = now + ThinkInterval;
            Think(uid, follower, brain, now);
        }

        foreach (var uid in _lost)
            Scatter(uid);

        UpdateScatter(now);
        ShareSight(now);
        SayLines(now);
    }

    private void ShareSight(TimeSpan now)
    {
        var query = EntityQueryEnumerator<STHiveLeaderComponent>();
        while (query.MoveNext(out var leader, out var comp))
        {
            if (now < comp.NextShare)
                continue;

            comp.NextShare = now + ShareInterval;
            SyncSight(leader, comp);
            if (comp.Followers.Count == 0 || !_mobState.IsAlive(leader))
                continue;

            EntityUid? scout = null;
            foreach (var follower in comp.Followers)
            {
                if (_mobState.IsAlive(follower))
                {
                    scout = follower;
                    break;
                }
            }

            if (scout is not { } eye)
                continue;

            var here = _transform.GetMapCoordinates(leader);
            _seen.Clear();
            _lookup.GetEntitiesInRange(here, ShareRange, _seen);
            EntityUid? spotted = null;
            var best = float.MaxValue;
            foreach (var mob in _seen)
            {
                if (mob.Owner == leader ||
                    comp.Followers.Contains(mob.Owner) ||
                    !_mobState.IsAlive(mob.Owner) ||
                    !_awareness.IsHostile(eye, mob.Owner) ||
                    !_examine.InRangeUnOccluded(leader, mob.Owner, ShareRange))
                {
                    continue;
                }

                var gap = (_transform.GetWorldPosition(mob.Owner) - here.Position).LengthSquared();
                if (gap >= best)
                    continue;

                best = gap;
                spotted = mob.Owner;
            }

            if (spotted is not { } enemy)
                continue;

            foreach (var follower in comp.Followers)
            {
                if (!TryComp<STBrainComponent>(follower, out var brain) || !_mobState.IsAlive(follower))
                    continue;

                if (TryComp<STAwarenessComponent>(follower, out var awareness) && awareness.Target == enemy)
                    awareness.TrackUntil = now + awareness.TrackTime;

                if (brain.Behaviour is STBehaviour.Fight or STBehaviour.Finish)
                    continue;

                _awareness.ForceNotice(follower, enemy);
                brain.TargetLock = enemy;
                brain.TargetLockUntil = now + ShareLock;
                brain.NextThink = TimeSpan.Zero;
                brain.NextScan = TimeSpan.Zero;
            }
        }
    }

    private void SyncSight(EntityUid leader, STHiveLeaderComponent comp)
    {
        ICommonSession? session = TryComp<ActorComponent>(leader, out var actor) ? actor.PlayerSession : null;
        foreach (var (eye, viewer) in comp.Watching.ToArray())
        {
            if (viewer == session && comp.Followers.Contains(eye))
                continue;

            if (!TerminatingOrDeleted(eye))
                _viewSubscriber.RemoveViewSubscriber(eye, viewer);

            comp.Watching.Remove(eye);
        }

        if (session != null)
        {
            foreach (var eye in comp.Followers)
            {
                if (comp.Watching.TryAdd(eye, session))
                    _viewSubscriber.AddViewSubscriber(eye, session);
            }
        }

        if (!TryComp<STHiveSightComponent>(leader, out var sight) ||
            sight.Eyes.Count == comp.Followers.Count && sight.Eyes.TrueForAll(comp.Followers.Contains))
        {
            return;
        }

        sight.Eyes.Clear();
        sight.Eyes.AddRange(comp.Followers);
        Dirty(leader, sight);
    }

    private void Think(EntityUid uid, STHiveFollowerComponent follower, STBrainComponent brain, TimeSpan now)
    {
        var leader = follower.Leader;
        if (TerminatingOrDeleted(leader) || !HasComp<STHiveLeaderComponent>(leader) || _mobState.IsDead(leader))
        {
            _lost.Add(uid);
            return;
        }

        var self = _transform.GetMapCoordinates(uid);
        var at = _transform.GetMapCoordinates(leader);
        if (follower.Hold && follower.HoldAt is { } spot)
        {
            if (at.MapId == self.MapId)
                follower.LastLeader = at;

            follower.DownAt = null;
            brain.Rooted = false;
            SetAnchor(brain, spot, 0f, "holding as the Monolith wills");
            return;
        }

        var jumped = follower.LastLeader is { } last &&
                     last.MapId == at.MapId &&
                     (at.Position - last.Position).Length() > JumpDistance;
        if (at.MapId != self.MapId || jumped || (at.Position - self.Position).Length() > StragglerDistance)
        {
            Bring(uid, brain, leader, follower.Slot, now);
            self = _transform.GetMapCoordinates(uid);
        }

        if (at.MapId == self.MapId)
            follower.LastLeader = at;

        if (at.MapId != self.MapId)
        {
            _lost.Add(uid);
            return;
        }

        if (_mobState.IsCritical(leader))
        {
            follower.DownAt ??= Transform(uid).Coordinates;
            brain.Rooted = true;
            SetAnchor(brain, follower.DownAt.Value, 0f, "fighting over its fallen brother");
        }
        else
        {
            follower.DownAt = null;
            brain.Rooted = false;
            if (_mover.NoPath(uid))
                follower.DirectUntil = now + DirectTime;

            var slot = now < follower.DirectUntil ? Transform(leader).Coordinates : SlotSpot(leader, at, follower.Slot);
            SetAnchor(brain, slot, FollowLeash, "following the Monolith's chosen");
        }

        if (TryComp<PullableComponent>(leader, out var pullable) &&
            pullable.Puller is { } puller &&
            puller != uid &&
            !_faction.IsEntityFriendly(uid, puller))
        {
            Rally(uid, brain, puller, now);
        }
    }

    private void RallyAll(STHiveLeaderComponent leader, EntityUid target)
    {
        if (!_mobState.IsAlive(target))
            return;

        var now = _timing.CurTime;
        foreach (var follower in leader.Followers)
        {
            if (TryComp<STBrainComponent>(follower, out var brain) && !_faction.IsEntityFriendly(follower, target))
                Rally(follower, brain, target, now);
        }
    }

    private void Rally(EntityUid uid, STBrainComponent brain, EntityUid target, TimeSpan now)
    {
        if (!_mobState.IsAlive(target))
            return;

        _faction.AggroEntity(uid, target);
        _awareness.ForceNotice(uid, target);
        brain.TargetLock = target;
        brain.TargetLockUntil = now + RallyLock;
        brain.NextThink = TimeSpan.Zero;
        brain.NextScan = TimeSpan.Zero;
    }

    private void Bring(EntityUid uid, STBrainComponent brain, EntityUid leader, int slot, TimeSpan now)
    {
        _transform.SetCoordinates(uid, LandingSpot(leader, slot));
        _mover.Stop(uid);
        brain.Override = null;
        brain.Returning = false;
        brain.FightOrigin = null;
        if (TryComp<STAwarenessComponent>(uid, out var awareness))
            _awareness.Forget(awareness, now);
    }

    private EntityCoordinates LandingSpot(EntityUid leader, int slot)
    {
        var leaderSpot = Transform(leader).Coordinates;
        var firstAngle = Angle.FromDegrees(slot * 137.5);
        foreach (var distance in LandingDistances)
        {
            for (var step = 0; step < LandingDirections; step++)
            {
                var direction = (firstAngle + Angle.FromDegrees(step * 360.0 / LandingDirections)).ToVec();
                var candidate = leaderSpot.Offset(direction * distance);
                if (Standable(candidate))
                    return candidate;
            }
        }

        return leaderSpot;
    }

    private bool Standable(EntityCoordinates spot)
    {
        return _pathfinding.GetPoly(spot) is { } poly &&
               (poly.Data.Flags & (PathfindingBreadcrumbFlag.Invalid | PathfindingBreadcrumbFlag.Space)) == 0 &&
               (poly.Data.CollisionLayer & (int) CollisionGroup.MobMask) == 0;
    }

    private EntityCoordinates SlotSpot(EntityUid leader, MapCoordinates at, int slot)
    {
        var radius = SlotRadius + slot / SlotsPerRing * SlotRing;
        var spot = _transform.ToCoordinates(at.Offset(Angle.FromDegrees(slot * 137.5).ToVec() * radius));
        return Standable(spot) ? spot : Transform(leader).Coordinates;
    }

    private static void SetAnchor(STBrainComponent brain, EntityCoordinates anchor, float leash, string doing)
    {
        brain.Anchor = anchor;
        brain.AnchorLeash = leash;
        brain.AnchorDoing = doing;
    }

    private void ReturnToPost(EntityUid uid)
    {
        if (!TryComp<STBrainComponent>(uid, out var brain) ||
            brain.Home is not { } home ||
            !home.IsValid(EntityManager) ||
            _transform.GetMapId(home) != Transform(uid).MapID)
        {
            Scatter(uid);
            return;
        }

        RemCompDeferred<STHiveFollowerComponent>(uid);
        Send(uid, brain, home);
        var scatter = EnsureComp<STHiveScatterComponent>(uid);
        scatter.Tries = 0;
        scatter.NextCheck = _timing.CurTime + ScatterCheck;
    }

    private void Scatter(EntityUid uid)
    {
        RemCompDeferred<STHiveFollowerComponent>(uid);
        if (!TryComp<STBrainComponent>(uid, out var brain))
            return;

        var scatter = EnsureComp<STHiveScatterComponent>(uid);
        scatter.Tries = 0;
        scatter.NextCheck = _timing.CurTime + ScatterCheck;
        PickScatterSpot(uid, brain);
    }

    private void PickScatterSpot(EntityUid uid, STBrainComponent brain)
    {
        var self = _transform.GetMapCoordinates(uid);
        for (var i = 0; i < 8; i++)
        {
            var spot = _transform.ToCoordinates(self.Offset(_random.NextAngle().ToVec() * _random.NextFloat(ScatterMin, ScatterMax)));
            if (!Standable(spot))
                continue;

            brain.Home = spot;
            Send(uid, brain, spot);
            return;
        }

        brain.Home = Transform(uid).Coordinates;
    }

    private void UpdateScatter(TimeSpan now)
    {
        _settled.Clear();
        var query = EntityQueryEnumerator<STHiveScatterComponent, STBrainComponent>();
        while (query.MoveNext(out var uid, out var scatter, out var brain))
        {
            if (now < scatter.NextCheck)
                continue;

            scatter.NextCheck = now + ScatterCheck;
            if (!_mobState.IsAlive(uid) || !brain.Returning || brain.Override == null)
            {
                _settled.Add(uid);
                continue;
            }

            if (!_mover.NoPath(uid))
                continue;

            if (++scatter.Tries > ScatterTries)
            {
                brain.Home = Transform(uid).Coordinates;
                brain.Override = null;
                brain.Returning = false;
                _settled.Add(uid);
                continue;
            }

            _mover.Stop(uid);
            PickScatterSpot(uid, brain);
        }

        foreach (var uid in _settled)
            RemComp<STHiveScatterComponent>(uid);
    }

    private void Send(EntityUid uid, STBrainComponent brain, EntityCoordinates spot)
    {
        var distance = (_transform.ToMapCoordinates(spot).Position - _transform.GetWorldPosition(uid)).Length();
        var speed = TryComp<MovementSpeedModifierComponent>(uid, out var move) ? MathF.Max(0.5f, move.CurrentSprintSpeed) : 2f;
        brain.Override = spot;
        brain.OverrideUntil = _timing.CurTime + TimeSpan.FromSeconds(Math.Clamp(distance / speed + 3f, 3f, 120f));
        brain.Returning = true;
    }

    private void Speak(STHiveLeaderComponent leader, MapCoordinates near, string prefix, int lines)
    {
        var voices = leader.Followers
            .Where(f => _mobState.IsAlive(f))
            .OrderBy(f => (_transform.GetWorldPosition(f) - near.Position).LengthSquared())
            .ToList();
        if (voices.Count == 0)
            return;

        var count = Math.Clamp((int) MathF.Round(voices.Count * SpeakerShare), 1, MaxSpeakers);
        var picks = Enumerable.Range(1, lines).OrderBy(_ => _random.Next()).ToList();
        var at = _timing.CurTime;
        for (var i = 0; i < count; i++)
        {
            _lines.Add((voices[i], Loc.GetString($"{prefix}-{picks[i % picks.Count]}"), at));
            at += TimeSpan.FromSeconds(_random.NextFloat(SpeakGapMin, SpeakGapMax));
        }
    }

    private void SayLines(TimeSpan now)
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            var (speaker, line, at) = _lines[i];
            if (now < at)
                continue;

            _lines.RemoveAt(i);
            if (!TerminatingOrDeleted(speaker) && _mobState.IsAlive(speaker))
                _chat.TrySendInGameICMessage(speaker, line, InGameICChatType.Speak, false, ignoreActionBlocker: true);
        }
    }

    private bool Mobile(EntityUid uid)
    {
        return TryComp<MovementSpeedModifierComponent>(uid, out var move) && move.BaseSprintSpeed >= 0.5f;
    }

    private int FreeSlot(STHiveLeaderComponent leader)
    {
        for (var slot = 0; ; slot++)
        {
            var taken = false;
            foreach (var follower in leader.Followers)
            {
                if (TryComp<STHiveFollowerComponent>(follower, out var comp) && comp.Slot == slot)
                {
                    taken = true;
                    break;
                }
            }

            if (!taken)
                return slot;
        }
    }
}
