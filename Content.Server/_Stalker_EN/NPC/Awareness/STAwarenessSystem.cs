using Content.Server._Stalker.NPCs;
using Content.Server._Stalker_EN.NPC.Combat;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators;
using System.Numerics;
using Content.Shared._Stalker_EN.NPC.Awareness;
using Content.Shared.ActionBlocker;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using System.Linq;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Awareness;

public sealed class STAwarenessSystem : EntitySystem
{
    [Dependency] private readonly Robust.Shared.Audio.Systems.SharedAudioSystem _audio = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly Content.Server._Stalker_EN.NPC.Brain.STBrainSystem _brain = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;

    public const string TargetKey = "Target";
    public const string LastKnownKey = "STLastKnownCoordinates";
    public const string ArrivedKey = "STReachedLastKnown";
    public const string LastSeenKey = "STLastSeenCoordinates";
    private const string SightRangeKey = "AggroVisionRadius";
    private const float KeepSlack = 0.5f;
    private const int MeterStep = 8;
    private const int NoticeRays = 8;

    private readonly List<(EntityUid Target, float Distance, bool Player)> _candidates = new();
    private readonly HashSet<EntityUid> _seen = new();
    private readonly List<EntityUid> _expired = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<STAwarenessComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<STAwarenessComponent, MobStateChangedEvent>(OnMobStateChanged);
    }

    private void OnShutdown(Entity<STAwarenessComponent> ent, ref ComponentShutdown args)
    {
        ClearMeters(ent);
    }

    private void OnMobStateChanged(Entity<STAwarenessComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Alive)
            return;

        ent.Comp.Notice.Clear();
        ClearMeters(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<STAwarenessComponent, HTNComponent, ActiveNPCComponent>();

        while (query.MoveNext(out var uid, out var comp, out var htn, out _))
        {
            if (now < comp.NextUpdate)
                continue;

            comp.NextUpdate = now + comp.UpdateInterval;

            if (!comp.ManagedRoots.Contains(htn.RootTask.Task))
                continue;

            UpdateAwareness(uid, comp, htn, now);
        }
    }

    private void UpdateAwareness(EntityUid uid, STAwarenessComponent comp, HTNComponent htn, TimeSpan now)
    {
        var blackboard = htn.Blackboard;
        UpdateNotice(uid, comp, htn, now);

        var hasTarget = blackboard.TryGetValue<EntityUid>(TargetKey, out var target, EntityManager) &&
                        target.IsValid() &&
                        !TerminatingOrDeleted(target);

        if (hasTarget && !_mobState.IsAlive(target))
        {
            if (comp.Target == target || comp.State == STAwarenessState.Unaware || !RememberedHostile(uid, comp))
            {
                comp.Target = target;
                var finishing = !_mobState.IsDead(target) && CanSee(uid, target, blackboard, KeepSlack);
                SetState(comp, finishing ? STAwarenessState.Engaged : STAwarenessState.Unaware, now);
                return;
            }

            if (!_mobState.IsDead(target))
                return;

            blackboard.Remove<EntityUid>(TargetKey);
            hasTarget = false;
        }

        if (hasTarget)
        {
            var engaging = comp.State != STAwarenessState.Engaged || comp.Target != target;
            if (comp.Target != target)
            {
                comp.Target = target;
                comp.LastSeen = now;
                comp.TrackUntil = now + comp.TrackTime;
                blackboard.SetValue(LastKnownKey, Transform(target).Coordinates);
                blackboard.Remove<EntityCoordinates>(LastSeenKey);
            }

            SetState(comp, STAwarenessState.Engaged, now);
            if (engaging)
                Spotted(uid, target);

            if (CanSee(uid, target, blackboard, KeepSlack))
            {
                comp.LastSeen = now;
                comp.TrackUntil = now + comp.TrackTime;
                blackboard.SetValue(LastKnownKey, Transform(target).Coordinates);
                blackboard.SetValue(LastSeenKey, Transform(target).Coordinates);
                return;
            }

            if (now < comp.TrackUntil && InTrackRange(uid, target, blackboard, comp))
            {
                blackboard.SetValue(LastKnownKey, Transform(target).Coordinates);
                return;
            }

            blackboard.Remove<EntityUid>(TargetKey);
            EnterLost(comp, blackboard, now);
            return;
        }

        if ((comp.State is STAwarenessState.Lost or STAwarenessState.Searching) && !RememberedHostile(uid, comp))
        {
            GiveUp(comp, now);
            return;
        }

        switch (comp.State)
        {
            case STAwarenessState.Engaged:
                if (RememberedHostile(uid, comp) && CanSee(uid, comp.Target!.Value, blackboard, KeepSlack))
                {
                    comp.LastSeen = now;
                    blackboard.SetValue(LastKnownKey, Transform(comp.Target.Value).Coordinates);
                    blackboard.SetValue(LastSeenKey, Transform(comp.Target.Value).Coordinates);
                    blackboard.SetValue(TargetKey, comp.Target.Value);
                    break;
                }

                if (RememberedHostile(uid, comp) &&
                    now - comp.LastSeen <= comp.MemoryTime &&
                    blackboard.ContainsKey(LastKnownKey))
                {
                    EnterLost(comp, blackboard, now);
                }
                else
                {
                    comp.Target = null;
                    SetState(comp, STAwarenessState.Unaware, now);
                }

                break;
            case STAwarenessState.Lost:
                if (Reacquire(uid, comp, htn, now))
                    break;

                if (now < comp.HoldUntil || CompOrNull<NPCSteeringComponent>(uid)?.DoAfterId != null)
                    comp.StateStart += comp.UpdateInterval;

                var arrived = blackboard.TryGetValue<bool>(ArrivedKey, out var reached, EntityManager) && reached;
                var stalled = now - comp.LastPursuing > comp.PursueStallTime;

                if (arrived || stalled || now - comp.StateStart > comp.PursueTime)
                {
                    blackboard.SetValue(ArrivedKey, false);
                    SetState(comp, STAwarenessState.Searching, now);
                }

                break;
            case STAwarenessState.Searching:
                if (Reacquire(uid, comp, htn, now))
                    break;

                if (now - comp.StateStart > comp.SearchTime)
                    GiveUp(comp, now);

                break;
        }
    }

    public bool CanAlert(EntityUid uid, STAwarenessComponent comp)
    {
        return comp.State is not (STAwarenessState.Engaged or STAwarenessState.Lost) &&
               HasComp<ActiveNPCComponent>(uid) &&
               TryComp<HTNComponent>(uid, out var htn) &&
               comp.ManagedRoots.Contains(htn.RootTask.Task) &&
               !HasLiveTarget(htn.Blackboard);
    }

    private bool HasLiveTarget(NPCBlackboard blackboard)
    {
        return blackboard.TryGetValue<EntityUid>(TargetKey, out var target, EntityManager) &&
               target.IsValid() &&
               !TerminatingOrDeleted(target) &&
               !_mobState.IsDead(target);
    }

    private bool Reacquire(EntityUid uid, STAwarenessComponent comp, HTNComponent htn, TimeSpan now)
    {
        if (!RememberedHostile(uid, comp) ||
            now - comp.LastSeen > comp.MemoryTime && !NoticeFull(comp, comp.Target!.Value) ||
            !CanSee(uid, comp.Target!.Value, htn.Blackboard))
        {
            return false;
        }

        var coordinates = Transform(comp.Target.Value).Coordinates;
        comp.LastSeen = now;
        htn.Blackboard.SetValue(LastKnownKey, coordinates);
        htn.Blackboard.SetValue(LastSeenKey, coordinates);
        htn.Blackboard.SetValue(TargetKey, comp.Target.Value);
        SetState(comp, STAwarenessState.Engaged, now);
        Spotted(uid, comp.Target.Value);
        return true;
    }

    public bool Gates(EntityUid uid)
    {
        return TryComp<STAwarenessComponent>(uid, out var comp) &&
               TryComp<HTNComponent>(uid, out var htn) &&
               comp.ManagedRoots.Contains(htn.RootTask.Task);
    }

    public bool Noticed(EntityUid uid, EntityUid target)
    {
        return TryComp<STAwarenessComponent>(uid, out var comp) &&
               (comp.Target == target && comp.State == STAwarenessState.Engaged || NoticeFull(comp, target));
    }

    public void ForceNotice(EntityUid uid, EntityUid target)
    {
        if (!TryComp<STAwarenessComponent>(uid, out var comp))
            return;

        if (!comp.Notice.TryGetValue(target, out var track))
            comp.Notice[target] = track = new STNoticeTrack();

        var now = _timing.CurTime;
        track.Value = 1f;
        track.LastSeen = now;
        track.NoticedAt ??= now;
    }

    private static bool NoticeFull(STAwarenessComponent comp, EntityUid target)
    {
        return comp.Notice.TryGetValue(target, out var track) && track.Value >= 1f;
    }

    private void UpdateNotice(EntityUid uid, STAwarenessComponent comp, HTNComponent htn, TimeSpan now)
    {
        var dt = Math.Clamp((float) (now - comp.LastNoticeUpdate).TotalSeconds, 0f, 0.5f);
        comp.LastNoticeUpdate = now;
        var blackboard = htn.Blackboard;
        var radius = blackboard.GetValueOrDefault<float>(blackboard.GetVisionRadiusKey(EntityManager), EntityManager);
        var xform = Transform(uid);
        var self = _transform.GetWorldPosition(xform);
        var facing = _transform.GetWorldRotation(xform).ToWorldVec();
        var frontCos = MathF.Cos(MathHelper.DegreesToRadians(comp.NoticeFrontArc / 2f));
        var noticed = false;

        _seen.Clear();
        _candidates.Clear();
        foreach (var hostile in _faction.GetNearbyHostiles(uid, radius))
        {
            if (!_mobState.IsDead(hostile))
                _candidates.Add((hostile, (_transform.GetWorldPosition(hostile) - self).Length(), HasComp<ActorComponent>(hostile)));
        }

        _candidates.Sort((a, b) => a.Player != b.Player ? b.Player.CompareTo(a.Player) : a.Distance.CompareTo(b.Distance));
        for (var i = 0; i < _candidates.Count && i < NoticeRays && _seen.Count < comp.NoticeCandidates; i++)
        {
            var (target, distance, _) = _candidates[i];
            if (!_examine.InRangeUnOccluded(uid, target, radius))
                continue;

            _seen.Add(target);
            if (!comp.Notice.TryGetValue(target, out var track))
                comp.Notice[target] = track = new STNoticeTrack();

            track.LastSeen = now;
            if (comp.State == STAwarenessState.Engaged && comp.Target == target)
            {
                track.Value = 1f;
                track.NoticedAt ??= now;
                continue;
            }

            if (track.Value >= 1f)
                continue;

            if (!comp.NoticeMeter)
            {
                track.Value = 1f;
                track.NoticedAt = now;
                noticed = true;
                continue;
            }

            var rate = 1f / (NoticeTime(comp, distance, radius) * comp.SightFactor);
            if (distance > comp.NoticeNearRange && distance > 0.01f &&
                Vector2.Dot((_transform.GetWorldPosition(target) - self) / distance, facing) < frontCos)
            {
                rate *= comp.NoticePeripheralFactor;
            }

            if (comp.Target == target && comp.State is STAwarenessState.Lost or STAwarenessState.Searching)
                rate *= comp.NoticeAlertedFactor;

            track.Value = MathF.Min(1f, track.Value + rate * dt);
            if (!track.Looked && track.Value >= comp.SuspicionFraction && comp.State == STAwarenessState.Unaware)
            {
                track.Looked = true;
                _rotate.TryFaceCoordinates(uid, _transform.GetWorldPosition(target), xform);
            }

            if (track.Value >= 1f)
            {
                track.NoticedAt = now;
                noticed = true;
                if (comp.NoticeSound != null && HasComp<ActorComponent>(target))
                    _audio.PlayEntity(comp.NoticeSound, target, uid);
            }
        }

        _expired.Clear();
        foreach (var (target, track) in comp.Notice)
        {
            if (TerminatingOrDeleted(target))
            {
                _expired.Add(target);
                continue;
            }

            if (_seen.Contains(target) ||
                now - track.LastSeen < comp.NoticeDrainDelay ||
                comp.State == STAwarenessState.Engaged && comp.Target == target)
            {
                continue;
            }

            track.Value -= dt / comp.NoticeTimeFar;
            track.NoticedAt = null;
            if (track.Value <= 0f)
                _expired.Add(target);
        }

        foreach (var target in _expired)
            comp.Notice.Remove(target);

        if (comp.NoticeMeter)
            SyncMeters(uid, comp, now);

        if (noticed && comp.State != STAwarenessState.Engaged)
            _brain.Wake(uid);
    }

    private static float NoticeTime(STAwarenessComponent comp, float distance, float radius)
    {
        if (distance <= comp.NoticeNearRange || radius <= comp.NoticeNearRange)
            return comp.NoticeTimeNear;

        var t = Math.Clamp((distance - comp.NoticeNearRange) / (radius - comp.NoticeNearRange), 0f, 1f);
        return comp.NoticeTimeNear + (comp.NoticeTimeFar - comp.NoticeTimeNear) * t;
    }

    private void SyncMeters(EntityUid uid, STAwarenessComponent comp, TimeSpan now)
    {
        var net = GetNetEntity(uid);
        _expired.Clear();
        foreach (var player in comp.MeterShown)
        {
            if (!comp.Notice.ContainsKey(player))
                _expired.Add(player);
        }

        foreach (var player in _expired)
        {
            comp.MeterShown.Remove(player);
            SetMeter(player, net, null);
        }

        foreach (var (target, track) in comp.Notice)
        {
            if (!HasComp<ActorComponent>(target))
                continue;

            byte? value = track.NoticedAt is { } at
                ? now - at < comp.NoticeFlash ? STNoticeMeterComponent.Noticed : null
                : (byte) Math.Clamp(MathF.Round(track.Value * STNoticeMeterComponent.Full), 1f, STNoticeMeterComponent.Full);

            if (value == null)
                comp.MeterShown.Remove(target);
            else
                comp.MeterShown.Add(target);

            SetMeter(target, net, value);
        }
    }

    private void ClearMeters(Entity<STAwarenessComponent> ent)
    {
        var net = GetNetEntity(ent);
        foreach (var player in ent.Comp.MeterShown)
            SetMeter(player, net, null);

        ent.Comp.MeterShown.Clear();
    }

    private void SetMeter(EntityUid player, NetEntity npc, byte? value)
    {
        if (TerminatingOrDeleted(player))
            return;

        if (value is not { } shown)
        {
            if (TryComp<STNoticeMeterComponent>(player, out var existing) && existing.Meters.Remove(npc))
                Dirty(player, existing);

            return;
        }

        var meter = EnsureComp<STNoticeMeterComponent>(player);
        if (meter.Meters.TryGetValue(npc, out var old) &&
            old != STNoticeMeterComponent.Noticed &&
            shown != STNoticeMeterComponent.Noticed &&
            Math.Abs(old - shown) < MeterStep &&
            shown != STNoticeMeterComponent.Full)
        {
            return;
        }

        if (meter.Meters.TryGetValue(npc, out old) && old == shown)
            return;

        meter.Meters[npc] = shown;
        Dirty(player, meter);
    }

    private void Spotted(EntityUid uid, EntityUid target)
    {
        var spotted = new STTargetSpottedEvent(target, Transform(target).Coordinates);
        RaiseLocalEvent(uid, ref spotted);
    }

    public bool IsHostile(EntityUid uid, EntityUid other)
    {
        if (!TryComp<NpcFactionMemberComponent>(uid, out var mine))
            return false;

        if (TryComp<FactionExceptionComponent>(uid, out var exception))
        {
            if (_faction.IsIgnored((uid, exception), other))
                return false;

            if (_faction.GetHostiles((uid, exception)).Contains(other))
                return true;
        }

        if (!TryComp<NpcFactionMemberComponent>(other, out var theirs))
            return false;

        var hostileFactions = mine.HostileFactions;
        return hostileFactions.Overlaps(theirs.Factions) && !_faction.IsEntityFriendly((uid, mine), (other, theirs));
    }

    public bool Hunting(STAwarenessComponent comp, EntityUid source)
    {
        return comp.State is STAwarenessState.Lost or STAwarenessState.Searching ||
               comp.State == STAwarenessState.Engaged && comp.Target == source;
    }

    public bool TryUpdateHeard(EntityUid uid, EntityUid source, EntityCoordinates where)
    {
        if (!TryComp<STAwarenessComponent>(uid, out var comp) ||
            !Hunting(comp, source) ||
            comp.Target is { } current && current != source && RememberedHostile(uid, comp) ||
            !_mobState.IsAlive(source) ||
            !IsHostile(uid, source) ||
            !TryComp<HTNComponent>(uid, out var htn))
        {
            return false;
        }

        var now = _timing.CurTime;
        var blackboard = htn.Blackboard;
        if (comp.State == STAwarenessState.Engaged)
        {
            if (CanSee(uid, source, blackboard, KeepSlack))
                return false;

            comp.TrackUntil = now + comp.TrackTime;
            blackboard.SetValue(LastKnownKey, where);
            return true;
        }

        comp.Target = source;
        blackboard.SetValue(LastKnownKey, where);
        if (comp.State == STAwarenessState.Searching)
            EnterLost(comp, blackboard, now);

        comp.StateStart = now;
        comp.LastPursuing = now;
        blackboard.SetValue(ArrivedKey, false);
        _brain.Wake(uid);
        return true;
    }

    public bool TryAlert(EntityUid uid, EntityUid source, EntityCoordinates where)
    {
        if (!TryComp<STAwarenessComponent>(uid, out var comp) ||
            !CanAlert(uid, comp) ||
            !_mobState.IsAlive(source) ||
            !IsHostile(uid, source) ||
            !TryComp<HTNComponent>(uid, out var htn))
        {
            return false;
        }

        var now = _timing.CurTime;
        comp.Target = source;
        htn.Blackboard.Remove<EntityCoordinates>(LastSeenKey);
        htn.Blackboard.SetValue(LastKnownKey, where);
        EnterLost(comp, htn.Blackboard, now);
        _brain.Wake(uid);
        return true;
    }

    private bool InTrackRange(EntityUid uid, EntityUid target, NPCBlackboard blackboard, STAwarenessComponent comp)
    {
        var range = blackboard.GetValueOrDefault<float>(SightRangeKey, EntityManager) + comp.TrackSlack;
        return Transform(uid).Coordinates.TryDistance(EntityManager, _transform, Transform(target).Coordinates, out var distance) && distance <= range;
    }

    private bool CanSee(EntityUid uid, EntityUid target, NPCBlackboard blackboard, float slack = 0f)
    {
        var range = blackboard.GetValueOrDefault<float>(SightRangeKey, EntityManager) + slack;
        return _examine.InRangeUnOccluded(uid, target, range);
    }

    private bool RememberedAlive(STAwarenessComponent comp)
    {
        return comp.Target is { } remembered && !TerminatingOrDeleted(remembered) && _mobState.IsAlive(remembered);
    }

    private bool RememberedHostile(EntityUid uid, STAwarenessComponent comp)
    {
        return RememberedAlive(comp) && IsHostile(uid, comp.Target!.Value);
    }

    private static void GiveUp(STAwarenessComponent comp, TimeSpan now)
    {
        comp.Target = null;
        SetState(comp, STAwarenessState.Unaware, now);
    }

    private void EnterLost(STAwarenessComponent comp, NPCBlackboard blackboard, TimeSpan now)
    {
        blackboard.SetValue(ArrivedKey, false);
        comp.LastPursuing = now;
        SetState(comp, STAwarenessState.Lost, now);
    }

    private static void SetState(STAwarenessComponent comp, STAwarenessState state, TimeSpan now)
    {
        if (comp.State == state)
            return;

        comp.State = state;
        comp.StateStart = now;
    }

}

[ByRefEvent]
public readonly record struct STTargetSpottedEvent(EntityUid Target, EntityCoordinates Where);
