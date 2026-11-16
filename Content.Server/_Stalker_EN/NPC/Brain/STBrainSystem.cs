using System.Numerics;
using Content.Server._Stalker_EN.NPC.Anomaly;
using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server._Stalker_EN.NPC.Combat;
using Content.Server._Stalker_EN.NPC.Steering;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared._Stalker_EN.Cover;
using Content.Shared.ActionBlocker;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.NPC;
using Content.Shared.NPC.Systems;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Brain;

public sealed partial class STBrainSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NPCUtilitySystem _utility = default!;
    [Dependency] private readonly STBarkSystem _bark = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _speedModifier = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly Content.Shared.Trigger.Systems.TriggerSystem _trigger = default!;
    [Dependency] private readonly Content.Shared.Throwing.ThrowingSystem _throwing = default!;
    [Dependency] private readonly Content.Shared.Popups.SharedPopupSystem _popup = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly STMoverSystem _mover = default!;
    [Dependency] private readonly STAwarenessSystem _awareness = default!;
    [Dependency] private readonly STFireGateSystem _fireGate = default!;
    [Dependency] private readonly STShotLineSystem _shotLine = default!;
    [Dependency] private readonly STFriendlyFireSystem _friendlyFire = default!;
    [Dependency] private readonly STLowCoverSystem _lowCover = default!;
    [Dependency] private readonly STAnomalyAvoidanceSystem _anomalies = default!;
    [Dependency] private readonly STChasmAvoidanceSystem _chasms = default!;

    private const string TargetKey = STAwarenessSystem.TargetKey;
    private const string MinRangeKey = "STPreferredRangeMin";
    private const string MaxRangeKey = "STPreferredRangeMax";
    private const string UseCoverKey = "STUseCover";
    private const string RushKey = "STRush";
    private const float MinSpeed = NPCSteeringSystem.MinMoveSpeed;
    private const float FinishRange = 2f;
    private const float ArriveRange = 1.5f;
    private const float SearchRange = 5f;
    private const float LookRange = 2.5f;
    private const int MaxPathRetries = 4;
    private const int RecentKept = 8;
    private static readonly TimeSpan KickTime = TimeSpan.FromSeconds(2);
    private const float EatRange = 1.5f;
    private static readonly TimeSpan AttackMemory = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TargetDwell = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SeenRecent = TimeSpan.FromSeconds(1);
    private const float AttackerThreat = 0.6f;
    private const float DamageThreat = 0.004f;
    private const float DamageThreatMax = 0.3f;
    private const float SeenThreat = 0.25f;
    private const float CloseThreat = 0.35f;
    private const float CloseThreatRange = 4f;
    private const float CrowdThreat = 0.15f;
    private const float StickThreat = 0.3f;
    private const int GunnerShare = 2;
    private const int PackShare = 3;
    private readonly HashSet<Entity<STBrainComponent>> _rivals = new();
    private readonly List<EntityUid> _expiredAttackers = new();
    private const float NearestSlack = 1.5f;
    private static readonly TimeSpan GiveUpStill = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan LeashGrace = TimeSpan.FromSeconds(4);
    private const string FollowKey = "FollowTarget";
    private const float FollowRange = 5f;
    private const float FollowClose = 2f;
    private static readonly TimeSpan ThinkInterval = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan CalmInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan SeenSlack = TimeSpan.FromSeconds(0.35);
    private static readonly TimeSpan SearchStep = TimeSpan.FromSeconds(2.5);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<STBrainComponent, DamageChangedEvent>(OnDamaged);
        SubscribeLocalEvent<STBrainComponent, MobStateChangedEvent>(OnMobState);
        SubscribeLocalEvent<STBrainComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<STBrainComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
    }

    private void OnDamaged(Entity<STBrainComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased)
            return;

        ent.Comp.NextThink = TimeSpan.Zero;
        if (args.Origin is { } attacker && attacker != ent.Owner && !TerminatingOrDeleted(attacker))
        {
            ent.Comp.HitFrom = _transform.GetMapCoordinates(attacker);
            ent.Comp.HitAt = _timing.CurTime;
            var dealt = args.DamageDelta?.GetTotal().Float() ?? 0f;
            var before = ent.Comp.Attackers.TryGetValue(attacker, out var seen) && _timing.CurTime - seen.At < AttackMemory ? seen.Damage : 0f;
            ent.Comp.Attackers[attacker] = (_timing.CurTime, before + dealt);
            ent.Comp.NextScan = TimeSpan.Zero;
        }

        if (ent.Comp.LeashSince != null)
            ent.Comp.LeashSince = _timing.CurTime;

        if (ent.Comp.Returning)
        {
            ent.Comp.Returning = false;
            ent.Comp.Override = null;
            ent.Comp.LeashSince = _timing.CurTime;
        }
    }

    private void OnRefreshSpeed(Entity<STBrainComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (ent.Comp.SpeedBoost != 1f)
            args.ModifySpeed(ent.Comp.SpeedBoost, ent.Comp.SpeedBoost);
    }

    private void Boost(EntityUid uid, STBrainComponent brain, float boost)
    {
        if (brain.SpeedBoost == boost)
            return;

        brain.SpeedBoost = boost;
        _speedModifier.RefreshMovementSpeedModifiers(uid);
    }

    private void OnMobState(Entity<STBrainComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Alive && ent.Comp.Behaviour != STBehaviour.None)
            Release(ent, ent.Comp);

        if (args.NewMobState == MobState.Dead && HasComp<Content.Server._Stalker.Pack.STPackHeadComponent>(ent))
            ShakePack(ent);

        if (args.NewMobState == MobState.Dead && ent.Comp.BreakFraction > 0f)
        {
            _squad.Clear();
            _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(ent), SquadRange, _squad);
            foreach (var mate in _squad)
            {
                if (mate.Owner != ent.Owner && mate.Comp.Kind == STBrainKind.Pack && _faction.IsEntityFriendly(ent.Owner, mate.Owner))
                    mate.Comp.Losses++;
            }
        }
    }

    private void ShakePack(EntityUid head)
    {
        var now = _timing.CurTime;
        _squad.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(head), SquadRange, _squad);
        foreach (var mate in _squad)
        {
            if (mate.Owner == head || mate.Comp.Kind != STBrainKind.Pack || !_faction.IsEntityFriendly(head, mate.Owner))
                continue;

            mate.Comp.ShakenUntil = now + TimeSpan.FromSeconds(_random.NextFloat(3f, 5f));
            mate.Comp.NextThink = TimeSpan.Zero;
        }
    }

    public void Wake(EntityUid uid)
    {
        if (!TryComp<STBrainComponent>(uid, out var brain))
            return;

        brain.NextThink = TimeSpan.Zero;
        brain.NextScan = TimeSpan.Zero;
    }

    public void Disable(EntityUid uid)
    {
        if (TryComp<STBrainComponent>(uid, out var brain) && brain.Behaviour != STBehaviour.None)
            Release(uid, brain);
    }

    private void OnShutdown(Entity<STBrainComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Behaviour != STBehaviour.None)
            Release(ent, ent.Comp);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<STBrainComponent, STAwarenessComponent, HTNComponent, ActiveNPCComponent>();
        while (query.MoveNext(out var uid, out var brain, out var awareness, out var htn, out _))
        {
            if (!brain.Roots.Contains(htn.RootTask.Task))
                continue;

            if (!htn.Enabled)
                continue;

            if (brain.SuppressAim is { } aim)
                Suppress(uid, brain, aim, frameTime, now);

            var changed = (byte) awareness.State != brain.LastState || awareness.Target != brain.LastTarget;
            if (!changed && now < brain.NextThink)
                continue;

            brain.NextThink = now + (brain.Kind != STBrainKind.Rusher && brain.Behaviour is STBehaviour.Fight or STBehaviour.Finish ? ThinkInterval : CalmInterval) * _random.NextFloat(0.9f, 1.1f);
            brain.LastState = (byte) awareness.State;
            brain.LastTarget = awareness.Target;
            Think(uid, brain, awareness, htn.Blackboard, now);
            _bark.Tick(uid, brain, brain.Doing != brain.LastDoing, now);
            if (brain.Doing == brain.LastDoing)
                continue;

            brain.LastDoing = brain.Doing;
            brain.Recent.Add((now, brain.Doing));
            if (brain.Recent.Count > RecentKept)
                brain.Recent.RemoveAt(0);
        }
    }

    private void Think(EntityUid uid, STBrainComponent brain, STAwarenessComponent awareness, NPCBlackboard blackboard, TimeSpan now)
    {
        if (!_mobState.IsAlive(uid))
        {
            Release(uid, brain);
            return;
        }

        if (now < brain.BusyUntil)
        {
            _mover.Stop(uid);
            return;
        }

        if (brain.Override is { } forced && now < brain.OverrideUntil)
        {
            ClearAim(uid);
            brain.SuppressAim = null;
            if (!brain.Returning || !_transform.InRange(Transform(uid).Coordinates, forced, ArriveRange))
            {
                _mover.MoveTo(uid, forced, 0.5f);
                return;
            }
        }

        brain.Override = null;
        brain.Returning = false;
        if (brain.Kind == STBrainKind.Auto || brain.AutoKind)
        {
            var charging = brain.ChargeWhenShielded && TryComp<Content.Shared._Stalker.Psyonics.Actions.Shield.PsyonicsActionShieldComponent>(uid, out var shield) && shield.IsActive;
            var kind = !charging && _gun.TryGetGun(uid, out _, out _) ? STBrainKind.Gunner : STBrainKind.Pack;
            if (brain.AutoKind && kind != brain.Kind)
            {
                brain.TargetQuery = string.Empty;
                ClearAim(uid);
            }

            brain.AutoKind = true;
            brain.Kind = kind;
        }

        if (brain.TargetQuery.Length == 0)
        {
            brain.TargetQuery = brain.Kind == STBrainKind.Pack ? "STMeleeAliveTargets" : "STMonolithNearbyGunTargets";
            brain.DownedQuery = string.Empty;
        }

        if (brain.DownedQuery.Length == 0)
            brain.DownedQuery = brain.Kind == STBrainKind.Pack ? "STMeleeCritTargets" : "STMonolithNearbyCritTargets";

        Scan(uid, brain, blackboard, now);

        var hasTarget = blackboard.TryGetValue<EntityUid>(TargetKey, out var target, EntityManager) &&
                        target.IsValid() &&
                        !TerminatingOrDeleted(target);
        if (hasTarget && _mobState.IsDead(target))
        {
            blackboard.Remove<EntityUid>(TargetKey);
            hasTarget = false;
        }

        var next = hasTarget && _mobState.IsAlive(target) ? STBehaviour.Fight
            : hasTarget ? STBehaviour.Finish
            : awareness.State == STAwarenessState.Lost ? STBehaviour.Investigate
            : awareness.State == STAwarenessState.Searching ? STBehaviour.Search
            : STBehaviour.Idle;

        if (next != brain.Behaviour)
        {
            brain.Behaviour = next;
            brain.Goal = null;
            brain.Hiding = false;
            brain.InCover = false;
            brain.PathRetries = 0;
            brain.Pushing = false;
            brain.Biting = false;
            brain.FlankSpot = null;
            brain.FlankDone = false;
            brain.Closing = false;
            brain.PeakMates = 0;
            brain.Broken = false;
            Boost(uid, brain, 1f);
            brain.Losses = 0;
            brain.Partial = null;
            brain.PartialUntil = TimeSpan.Zero;
            brain.ThrowAt = null;
            brain.BiteUntil = TimeSpan.Zero;
            brain.Barked = brain.Barked && next is STBehaviour.Fight or STBehaviour.Finish;
            brain.SuppressAim = null;
            brain.NextWander = TimeSpan.Zero;
        }

        var self = _transform.GetMapCoordinates(uid);
        if (next == STBehaviour.Idle)
        {
            brain.FightOrigin = null;
            brain.LeashSince = null;
        }
        else
        {
            brain.FightOrigin ??= self;
            if (Leashed(uid, brain, awareness, blackboard, self, now))
                return;
        }

        brain.Home ??= Transform(uid).Coordinates;
        if ((self.Position - brain.StillAt).LengthSquared() > 0.09f)
        {
            brain.StillAt = self.Position;
            brain.StillSince = now;
        }

        if (_mover.Moving(uid) &&
            now - brain.StillSince > KickTime &&
            now - brain.LastKick > KickTime &&
            TryComp<Robust.Shared.Physics.Components.PhysicsComponent>(uid, out var stuckBody) &&
            stuckBody.LinearVelocity.LengthSquared() < 0.01f)
        {
            brain.LastKick = now;
            _mover.Stop(uid);
            _physics.WakeBody(uid, body: stuckBody);
        }

        var mobile = _actionBlocker.CanMove(uid) &&
                     !(TryComp<MovementSpeedModifierComponent>(uid, out var speed) && speed.CurrentSprintSpeed < MinSpeed);

        switch (next)
        {
            case STBehaviour.Fight when brain.Kind == STBrainKind.Pack:
                Hunt(uid, brain, blackboard, target, self, mobile, now);
                break;
            case STBehaviour.Fight:
                Fight(uid, brain, awareness, blackboard, target, self, mobile, now);
                break;
            case STBehaviour.Finish when brain.Kind == STBrainKind.Pack:
                brain.Doing = "finishing a downed target";
                BiteAt(uid, target, BiteReach);
                break;
            case STBehaviour.Finish:
                Finish(uid, brain, blackboard, target, mobile);
                break;
            case STBehaviour.Investigate:
                Investigate(uid, brain, awareness, blackboard, self, mobile, now);
                break;
            case STBehaviour.Search:
                Search(uid, brain, awareness, blackboard, self, mobile, now);
                break;
            default:
                Idle(uid, brain, awareness, blackboard, self, mobile, now);
                break;
        }
    }

    private void Scan(EntityUid uid, STBrainComponent brain, NPCBlackboard blackboard, TimeSpan now)
    {
        if (now < brain.NextScan)
            return;

        brain.NextScan = now + ScanInterval;
        if (brain.TargetLock is { } locked && now < brain.TargetLockUntil && !TerminatingOrDeleted(locked) && _mobState.IsAlive(locked))
        {
            blackboard.SetValue(TargetKey, locked);
            return;
        }

        brain.TargetLock = null;
        var scores = _utility.GetEntities(blackboard, brain.TargetQuery, false).Entities;
        var has = blackboard.TryGetValue<EntityUid>(TargetKey, out var current, EntityManager) && current.IsValid();
        var best = EntityUid.Invalid;
        var bestScore = 0f;
        foreach (var (ent, score) in scores)
        {
            if (score <= bestScore)
                continue;

            best = ent;
            bestScore = score;
        }

        if (brain.ChaseNearest && has && _mobState.IsAlive(current))
        {
            var here = _transform.GetWorldPosition(uid);
            var nearest = current;
            var nearestGap = (_transform.GetWorldPosition(current) - here).Length() - NearestSlack;
            foreach (var (ent, score) in scores)
            {
                if (score <= 0f || ent == current)
                    continue;

                var gap = (_transform.GetWorldPosition(ent) - here).Length();
                if (gap >= nearestGap)
                    continue;

                nearest = ent;
                nearestGap = gap;
            }

            if (nearest != current)
            {
                blackboard.SetValue(TargetKey, nearest);
                return;
            }
        }

        if (has && _mobState.IsAlive(current))
        {
            if (!_awareness.IsHostile(uid, current))
            {
                blackboard.Remove<EntityUid>(TargetKey);
                return;
            }

            var pick = MostThreatening(uid, brain, scores, current, now);
            if (pick != current)
            {
                blackboard.SetValue(TargetKey, pick);
                brain.TargetSince = now;
            }

            return;
        }

        if (best.IsValid())
        {
            blackboard.SetValue(TargetKey, SquadTarget(uid, brain, scores) ?? best);
            brain.TargetSince = now;
            return;
        }

        var downed = _utility.GetEntities(blackboard, brain.DownedQuery).GetHighest();
        if (downed.IsValid())
            blackboard.SetValue(TargetKey, downed);
        else if (has)
            blackboard.Remove<EntityUid>(TargetKey);
    }

    private EntityUid MostThreatening(EntityUid uid, STBrainComponent brain, Dictionary<EntityUid, float> scores, EntityUid current, TimeSpan now)
    {
        _expiredAttackers.Clear();
        foreach (var (attacker, hit) in brain.Attackers)
        {
            if (now - hit.At >= AttackMemory || TerminatingOrDeleted(attacker))
                _expiredAttackers.Add(attacker);
        }

        foreach (var attacker in _expiredAttackers)
            brain.Attackers.Remove(attacker);

        if (scores.Count < 2 && brain.Attackers.Count == 0)
            return current;

        var here = _transform.GetMapCoordinates(uid);
        _rivals.Clear();
        _lookup.GetEntitiesInRange(here, SquadRange, _rivals);
        TryComp<STAwarenessComponent>(uid, out var awareness);

        float Threat(EntityUid ent, float utility)
        {
            var threat = utility;
            if (brain.Attackers.TryGetValue(ent, out var hit))
            {
                var fresh = 1f - (float) ((now - hit.At).TotalSeconds / AttackMemory.TotalSeconds);
                threat += AttackerThreat * fresh + MathF.Min(DamageThreatMax, hit.Damage * DamageThreat);
            }

            var seen = awareness != null && awareness.Notice.TryGetValue(ent, out var track) && now - track.LastSeen <= SeenRecent;
            threat += seen ? SeenThreat : -SeenThreat;
            if ((_transform.GetWorldPosition(ent) - here.Position).Length() <= CloseThreatRange)
                threat += CloseThreat;

            var sharing = 0;
            foreach (var rival in _rivals)
            {
                if (rival.Owner != uid && rival.Comp.LastTarget == ent && rival.Comp.Behaviour == STBehaviour.Fight && _faction.IsEntityFriendly(uid, rival.Owner))
                    sharing++;
            }

            var room = brain.Kind == STBrainKind.Pack ? Math.Min(brain.MaxBiters, PackShare) : GunnerShare;
            threat -= CrowdThreat * Math.Max(0, sharing - room + 1);
            return threat;
        }

        scores.TryGetValue(current, out var currentUtility);
        var best = current;
        var bestThreat = Threat(current, currentUtility) + StickThreat;
        foreach (var (ent, utility) in scores)
        {
            if (ent == current || utility <= 0f || !_mobState.IsAlive(ent))
                continue;

            var threat = Threat(ent, utility);
            if (threat <= bestThreat)
                continue;

            best = ent;
            bestThreat = threat;
        }

        if (best != current && now - brain.TargetSince < TargetDwell && !brain.Attackers.ContainsKey(best))
            return current;

        return best;
    }

    private EntityUid? SquadTarget(EntityUid uid, STBrainComponent brain, Dictionary<EntityUid, float> scores)
    {
        if (brain.Kind != STBrainKind.Gunner)
            return null;

        _squad.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(uid), SquadRange, _squad);
        foreach (var mate in _squad)
        {
            if (mate.Owner != uid &&
                mate.Comp.Behaviour == STBehaviour.Fight &&
                mate.Comp.LastTarget is { } shared &&
                scores.TryGetValue(shared, out var score) &&
                score > 0f &&
                _faction.IsEntityFriendly(uid, mate.Owner))
            {
                return shared;
            }
        }

        return null;
    }

    private void Finish(EntityUid uid, STBrainComponent brain, NPCBlackboard blackboard, EntityUid target, bool mobile)
    {
        brain.Doing = "finishing a downed target";
        brain.SuppressAim = null;
        AimAt(uid, blackboard, target);
        if (mobile)
            _mover.MoveTo(uid, new EntityCoordinates(target, Vector2.Zero), FinishRange);
        else
            _mover.Stop(uid);
    }

    private void Investigate(EntityUid uid, STBrainComponent brain, STAwarenessComponent awareness, NPCBlackboard blackboard, MapCoordinates self, bool mobile, TimeSpan now)
    {
        ClearAim(uid);
        brain.SuppressAim = null;
        if (!blackboard.TryGetValue<EntityCoordinates>(STAwarenessSystem.LastKnownKey, out var lastKnown, EntityManager))
        {
            _mover.Stop(uid);
            return;
        }

        var spot = _transform.ToMapCoordinates(lastKnown);
        var distance = (spot.Position - self.Position).Length();
        if (!mobile)
        {
            brain.Doing = "watching where the target was";
            if (brain.Suppression > 1f)
            {
                brain.Doing = "suppressing where the target was";
                brain.SuppressAim = spot;
            }

            _rotate.TryFaceCoordinates(uid, spot.Position);
            return;
        }

        if (Noticing(uid, brain, awareness, now))
        {
            awareness.LastPursuing = now;
            return;
        }

        if (brain.Kind == STBrainKind.Gunner && TakeCover(uid, brain, self, now))
        {
            awareness.LastPursuing = now;
            return;
        }

        if (brain.PathRetries > 0 && now - brain.StillSince > GiveUpStill)
            brain.PathRetries = MaxPathRetries;

        if (brain.PathRetries < MaxPathRetries && (brain.Partial != null || now < brain.PartialUntil || _mover.NoPath(uid)))
        {
            if (brain.Partial == null && now >= brain.PartialUntil)
                brain.PathRetries++;

            awareness.LastPursuing = now;
            CloseIn(uid, brain, blackboard, lastKnown, now);
            brain.Doing = "getting as close as it can to the last known spot";
            return;
        }

        if (spot.MapId != self.MapId ||
            distance <= ArriveRange ||
            distance <= LookRange && _examine.InRangeUnOccluded(self, spot, LookRange, null) ||
            brain.PathRetries >= MaxPathRetries ||
            _mover.NoPath(uid))
        {
            brain.Doing = "reached the last known spot";
            _mover.Stop(uid);
            blackboard.SetValue(STAwarenessSystem.ArrivedKey, true);
            return;
        }

        brain.Doing = "going to the last known spot";
        awareness.LastPursuing = now;
        _mover.MoveTo(uid, lastKnown, 1f);
    }

    private void Search(EntityUid uid, STBrainComponent brain, STAwarenessComponent awareness, NPCBlackboard blackboard, MapCoordinates self, bool mobile, TimeSpan now)
    {
        ClearAim(uid);
        brain.SuppressAim = null;
        if (!mobile || Noticing(uid, brain, awareness, now))
            return;

        brain.Doing = "searching";
        if (now < brain.NextWander && !_mover.NoPath(uid))
            return;

        var first = brain.NextWander == TimeSpan.Zero;
        brain.NextWander = now + SearchStep;
        var hasSpot = blackboard.TryGetValue<EntityCoordinates>(STAwarenessSystem.LastKnownKey, out var lastKnown, EntityManager);
        var centre = hasSpot ? _transform.ToMapCoordinates(lastKnown) : self;
        if (first && hasSpot && centre.MapId == self.MapId)
            _mover.MoveTo(uid, lastKnown, 0.5f);
        else if (TryRandomSpot(uid, centre.MapId == self.MapId ? centre : self, SearchRange, out var spot))
            _mover.MoveTo(uid, spot, 0.5f);
    }

    private bool Noticing(EntityUid uid, STBrainComponent brain, STAwarenessComponent awareness, TimeSpan now)
    {
        foreach (var (hostile, track) in awareness.Notice)
        {
            if (track.Value >= 1f || now - track.LastSeen > awareness.UpdateInterval * 2 || TerminatingOrDeleted(hostile))
                continue;

            brain.Doing = "looking at something";
            _mover.Stop(uid);
            _rotate.TryFaceCoordinates(uid, _transform.GetWorldPosition(hostile));
            return true;
        }

        return false;
    }

    private void Idle(EntityUid uid, STBrainComponent brain, STAwarenessComponent awareness, NPCBlackboard blackboard, MapCoordinates self, bool mobile, TimeSpan now)
    {
        ClearAim(uid);
        brain.SuppressAim = null;
        brain.Home ??= Transform(uid).Coordinates;

        if (brain.NextWander == TimeSpan.Zero)
            brain.NextWander = now + TimeSpan.FromSeconds(_random.NextFloat(4f, 9f));

        if (mobile && Noticing(uid, brain, awareness, now))
        {
            brain.NextWander = now + TimeSpan.FromSeconds(awareness.SuspicionPause);
            return;
        }

        brain.Doing = "idle";
        if (!mobile)
            return;

        if (blackboard.TryGetValue<EntityCoordinates>(FollowKey, out var leader, EntityManager) && leader.IsValid(EntityManager))
        {
            var lead = _transform.ToMapCoordinates(leader);
            if (lead.MapId == self.MapId && (lead.Position - self.Position).Length() > FollowRange)
            {
                brain.Doing = "following the pack";
                _mover.MoveTo(uid, leader, FollowClose);
                return;
            }
        }

        if (brain.Food is { } food)
        {
            if (TerminatingOrDeleted(food) || Transform(food).MapID != self.MapId)
            {
                brain.Food = null;
            }
            else
            {
                brain.Doing = "going to eat";
                if ((_transform.GetWorldPosition(food) - self.Position).Length() > EatRange)
                {
                    _mover.MoveTo(uid, new EntityCoordinates(food, Vector2.Zero), 1f);
                    return;
                }

                _mover.Stop(uid);
                _interaction.AltInteract(uid, food);
                brain.Food = null;
                brain.NextWander = now + TimeSpan.FromSeconds(_random.NextFloat(4f, 9f));
                return;
            }
        }

        if (brain.WanderRange <= 0f || now < brain.NextWander)
            return;

        if (brain.EatsFood && _utility.GetEntities(blackboard, "NearbyFood").GetHighest() is { Valid: true } meal)
        {
            brain.Food = meal;
            return;
        }

        brain.NextWander = now + TimeSpan.FromSeconds(_random.NextFloat(4f, 9f));
        var home = _transform.ToMapCoordinates(brain.Home.Value);
        if (TryRandomSpot(uid, home.MapId == self.MapId ? home : self, brain.WanderRange, out var spot))
            _mover.MoveTo(uid, spot, 0.5f);
    }

    private bool TryRandomSpot(EntityUid uid, MapCoordinates centre, float range, out EntityCoordinates spot)
    {
        var parent = Transform(uid).ParentUid;
        for (var i = 0; i < 6; i++)
        {
            var end = centre.Offset(_random.NextAngle().ToVec() * _random.NextFloat(1f, range));
            spot = _transform.ToCoordinates(parent, end);
            if (_pathfinding.GetPoly(spot) is { } poly &&
                (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) == 0 &&
                !_anomalies.InHazard(uid, spot))
            {
                return true;
            }
        }

        spot = default;
        return false;
    }

    private void AimAt(EntityUid uid, NPCBlackboard blackboard, EntityUid target)
    {
        if (!TryComp<NPCRangedCombatComponent>(uid, out var ranged))
        {
            ranged = AddComp<NPCRangedCombatComponent>(uid);
            if (blackboard.TryGetValue<float>(NPCBlackboard.RotateSpeed, out var rotSpeed, EntityManager))
                ranged.RotationSpeed = new Angle(rotSpeed);

            if (blackboard.TryGetValue<SoundSpecifier>("SoundTargetInLOS", out var losSound, EntityManager))
                ranged.SoundTargetInLOS = losSound;

            if (blackboard.TryGetValue<float>("ShootDelay", out var shootDelay, EntityManager))
                ranged.ShootDelay = shootDelay;
        }

        ranged.Target = target;
        if (ranged.Status is not (CombatStatus.Normal or CombatStatus.NotInSight))
            ranged.Status = CombatStatus.Normal;
    }

    private void ClearAim(EntityUid uid)
    {
        RemComp<NPCRangedCombatComponent>(uid);
        RemComp<NPCMeleeCombatComponent>(uid);
    }

    private bool Leashed(EntityUid uid, STBrainComponent brain, STAwarenessComponent awareness, NPCBlackboard blackboard, MapCoordinates self, TimeSpan now)
    {
        if (brain.Leash <= 0f || brain.FightOrigin is not { } origin)
            return false;

        var away = (self.Position - origin.Position).Length();
        if (origin.MapId != self.MapId || away <= brain.Leash)
        {
            brain.LeashSince = null;
            return false;
        }

        brain.LeashSince ??= now;
        if (now - brain.LeashSince < LeashGrace)
            return false;

        var speed = TryComp<MovementSpeedModifierComponent>(uid, out var move) ? MathF.Max(0.5f, move.CurrentSprintSpeed) : 2f;
        blackboard.Remove<EntityUid>(TargetKey);
        _awareness.Forget(awareness, now);
        RemComp<NPCMeleeCombatComponent>(uid);
        ClearAim(uid);
        brain.Biting = false;
        brain.SuppressAim = null;
        brain.Goal = null;
        brain.LeashSince = null;
        brain.Returning = true;
        brain.Override = _transform.ToCoordinates(Transform(uid).ParentUid, origin);
        brain.OverrideUntil = now + TimeSpan.FromSeconds(Math.Clamp(away / speed + 3f, 4f, 30f));
        brain.Doing = "breaking off, too far from where the fight began";
        _mover.MoveTo(uid, brain.Override.Value, 0.5f);
        return true;
    }

    private void Release(EntityUid uid, STBrainComponent brain)
    {
        brain.Behaviour = STBehaviour.None;
        brain.Goal = null;
        brain.Pushing = false;
        brain.Biting = false;
        brain.SuppressAim = null;
        ClearAim(uid);
        _mover.Stop(uid);
    }

    private void Suppress(EntityUid uid, STBrainComponent brain, MapCoordinates aim, float frameTime, TimeSpan now)
    {
        var muzzle = _transform.GetMapCoordinates(uid);
        var offset = aim.Position - muzzle.Position;
        if (muzzle.MapId != aim.MapId || offset.LengthSquared() < 0.01f)
            return;

        if (brain.FireRange > 0f && offset.LengthSquared() > brain.FireRange * brain.FireRange)
            return;

        _rotate.TryRotateTo(uid, offset.ToWorldAngle(), frameTime, SuppressTolerance);

        if (now >= brain.NextSuppress)
        {
            var keen = MathF.Max(0.1f, brain.Suppression);
            brain.SuppressUntil = now + SuppressTime * keen;
            brain.NextSuppress = brain.SuppressUntil + _random.Next(SuppressGapMin, SuppressGapMax) / keen;
        }

        if (now >= brain.SuppressUntil ||
            !_gun.TryGetGun(uid, out var gunUid, out var gun) ||
            !_gun.CanShoot(gun) ||
            _shotLine.Check(uid, muzzle, aim) == STShotLine.Blocked ||
            _shotLine.WallInSpread(uid, muzzle, aim, null, _shotLine.NextSpread(gun), STShotLineSystem.SpreadWaitRange) ||
            !_fireGate.Allows(uid, gun, muzzle, aim.Position, offset.Length(), null))
        {
            return;
        }

        _gun.AttemptShoot(uid, gunUid, gun, _transform.ToCoordinates(aim));
    }
}
