using System.Numerics;
using Content.Server._Stalker_EN.NPC.Anomaly;
using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server._Stalker_EN.NPC.Brain;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Pathfinding;
using Content.Server.Stunnable;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.NPC;
using Content.Shared.Popups;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Abilities;

[RegisterComponent]
public sealed partial class STDragComponent : Component
{
    [DataField]
    public TimeSpan Cooldown = TimeSpan.FromSeconds(20);

    [DataField]
    public TimeSpan DragTime = TimeSpan.FromSeconds(7);

    [DataField]
    public TimeSpan StunTime = TimeSpan.FromSeconds(3);

    [DataField]
    public TimeSpan LockTime = TimeSpan.FromSeconds(10);

    [DataField]
    public float GrabRange = 2.5f;

    [DataField]
    public float DragMin = 8f;

    [DataField]
    public float DragMax = 12f;

    [DataField]
    public float FreeDamage = 60f;

    [ViewVariables]
    public EntityUid? Victim;

    [ViewVariables]
    public TimeSpan DragUntil;

    [ViewVariables]
    public TimeSpan NextDrag;

    [ViewVariables]
    public TimeSpan NextThink;

    [ViewVariables]
    public float DamageTaken;

    [ViewVariables]
    public Vector2 StillAt;

    [ViewVariables]
    public TimeSpan StillSince;

    [ViewVariables]
    public int Detours;
}

public sealed class STDragSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly StunSystem _stun = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly STAnomalyAvoidanceSystem _anomalies = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    private const string TargetKey = STAwarenessSystem.TargetKey;
    private const int Directions = 16;
    private static readonly TimeSpan ThinkInterval = TimeSpan.FromSeconds(0.3);
    private static readonly TimeSpan SeenSlack = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StallTime = TimeSpan.FromSeconds(1.2);
    private const float StallStep = 0.3f;
    private const float DetourStep = 5f;
    private const float DetourTurn = 55f;

    private readonly List<EntityUid> _players = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<STDragComponent, DamageChangedEvent>(OnDamaged);
    }

    private void OnDamaged(Entity<STDragComponent> ent, ref DamageChangedEvent args)
    {
        if (ent.Comp.Victim != null && args.DamageIncreased && args.DamageDelta != null)
            ent.Comp.DamageTaken += args.DamageDelta.GetTotal().Float();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<STDragComponent, STBrainComponent, STAwarenessComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var comp, out var brain, out var awareness, out var htn))
        {
            if (now < comp.NextThink || !HasComp<ActiveNPCComponent>(uid))
                continue;

            comp.NextThink = now + ThinkInterval;
            var self = _transform.GetMapCoordinates(uid);

            if (comp.Victim is { } victim)
            {
                var arrived = brain.Override is { } goal && (_transform.ToMapCoordinates(goal).Position - self.Position).Length() <= 1f;
                if (TerminatingOrDeleted(victim) || !_mobState.IsAlive(uid) || _mobState.IsDead(victim) || now >= comp.DragUntil || comp.DamageTaken >= comp.FreeDamage || arrived)
                    Release(uid, comp, brain, now);
                else
                {
                    brain.Doing = "dragging its victim away";
                    if ((self.Position - comp.StillAt).LengthSquared() > StallStep * StallStep)
                    {
                        comp.StillAt = self.Position;
                        comp.StillSince = now;
                    }
                    else if (now - comp.StillSince > StallTime)
                    {
                        comp.StillSince = now;
                        comp.Detours++;
                        var away = self.Position - _transform.GetWorldPosition(victim);
                        var heading = away.LengthSquared() > 0.01f ? away.Normalized() : Vector2.UnitX;
                        var detour = _transform.ToCoordinates(Transform(uid).ParentUid, self.Offset(Angle.FromDegrees(comp.Detours * DetourTurn).RotateVec(heading) * DetourStep));
                        if (_pathfinding.GetPoly(detour) is { } poly && (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) == 0 && !_anomalies.InHazard(uid, detour))
                            brain.Override = detour;
                    }
                }

                continue;
            }

            if (now < comp.NextDrag || awareness.State != STAwarenessState.Engaged)
            {
                brain.Commit = false;
                continue;
            }

            _players.Clear();
            foreach (var (hostile, track) in awareness.Notice)
            {
                if (now - track.LastSeen <= SeenSlack && HasComp<ActorComponent>(hostile) && !TerminatingOrDeleted(hostile) && _mobState.IsAlive(hostile))
                    _players.Add(hostile);
            }

            if (_players.Count < 2)
            {
                brain.Commit = false;
                continue;
            }

            brain.Commit = true;

            var straggler = EntityUid.Invalid;
            var apart = -1f;
            foreach (var player in _players)
            {
                var nearest = float.MaxValue;
                foreach (var other in _players)
                {
                    if (other != player)
                        nearest = MathF.Min(nearest, (_transform.GetWorldPosition(other) - _transform.GetWorldPosition(player)).Length());
                }

                if (nearest <= apart)
                    continue;

                apart = nearest;
                straggler = player;
            }

            brain.TargetLock = straggler;
            brain.TargetLockUntil = now + comp.LockTime;

            if ((_transform.GetWorldPosition(straggler) - self.Position).Length() > comp.GrabRange)
            {
                straggler = EntityUid.Invalid;
                var reach = comp.GrabRange;
                foreach (var player in _players)
                {
                    var gap = (_transform.GetWorldPosition(player) - self.Position).Length();
                    if (gap > reach)
                        continue;

                    reach = gap;
                    straggler = player;
                }
            }

            if (!straggler.IsValid() || !_pulling.TryStartPull(uid, straggler))
                continue;

            brain.Commit = false;
            brain.TargetLock = straggler;
            brain.TargetLockUntil = now + comp.LockTime;

            comp.Victim = straggler;
            comp.StillAt = self.Position;
            comp.StillSince = now;
            comp.Detours = 0;
            comp.DamageTaken = 0f;
            comp.DragUntil = now + comp.DragTime;
            _stun.TryKnockdown(straggler, comp.StunTime, false);
            brain.Override = PickSpot(uid, comp, self, straggler);
            brain.OverrideUntil = comp.DragUntil;
            brain.Doing = "dragging its victim away";
            _popup.PopupEntity(Loc.GetString("st-bloodsucker-drags"), straggler, straggler, PopupType.LargeCaution);
        }
    }

    private void Release(EntityUid uid, STDragComponent comp, STBrainComponent brain, TimeSpan now)
    {
        if (comp.Victim is { } victim && TryComp<PullableComponent>(victim, out var pullable) && pullable.Puller == uid)
            _pulling.TryStopPull(victim, pullable);

        comp.Victim = null;
        comp.NextDrag = now + comp.Cooldown;
        brain.Override = null;
        brain.OverrideUntil = TimeSpan.Zero;
    }

    private EntityCoordinates PickSpot(EntityUid uid, STDragComponent comp, MapCoordinates self, EntityUid victim)
    {
        var parent = Transform(uid).ParentUid;
        EntityCoordinates? best = null;
        var bestScore = float.MinValue;
        var centre = Vector2.Zero;
        var others = 0;
        foreach (var player in _players)
        {
            if (player == victim)
                continue;

            centre += _transform.GetWorldPosition(player);
            others++;
        }

        centre /= Math.Max(1, others);

        foreach (var radius in new[] { comp.DragMax, (comp.DragMin + comp.DragMax) / 2f, comp.DragMin })
        {
            for (var i = 0; i < Directions; i++)
            {
                var end = self.Offset(Angle.FromDegrees(i * 360f / Directions).ToVec() * radius);
                var coords = _transform.ToCoordinates(parent, end);
                if (_pathfinding.GetPoly(coords) is not { } poly ||
                    (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0 ||
                    _anomalies.InHazard(uid, coords))
                {
                    continue;
                }

                var score = (end.Position - centre).Length();
                foreach (var player in _players)
                {
                    if (player == victim)
                        continue;

                    var eye = _transform.GetMapCoordinates(player);
                    if (!_examine.InRangeUnOccluded(eye, end, (end.Position - eye.Position).Length() + 1f, null))
                        score += 20f;
                }

                if (score <= bestScore)
                    continue;

                bestScore = score;
                best = coords;
            }
        }

        return best ?? _transform.ToCoordinates(parent, self.Offset((self.Position - centre).Normalized() * comp.DragMin));
    }
}
