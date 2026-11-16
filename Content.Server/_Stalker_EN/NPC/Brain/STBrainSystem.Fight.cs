using System.Numerics;
using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server._Stalker_EN.NPC.Combat;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared._Stalker_EN.Cover;
using Content.Shared.NPC;
using Content.Shared.Physics;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server._Stalker_EN.NPC.Brain;

public sealed partial class STBrainSystem
{
    private const float StayBonus = 0.25f;
    private const float GoalBonus = 0.3f;
    private const float RestlessAfter = 2f;
    private const float RestlessRate = 0.08f;
    private const float RestlessMax = 0.3f;
    private const float TravelCost = 0.06f;
    private const float CoverBonus = 0.9f;
    private const float AllySpacing = 1.5f;
    private const float AllyNearPenalty = 0.5f;
    private const float AllyLinePenalty = 1f;
    private const float LineClear = 1f;
    private const float LineCover = 0.6f;
    private const float RangeFalloff = 0.25f;
    private const float CloseFalloff = 0.5f;
    private const float NearSpot = 0.75f;
    private const float RushLead = 0.75f;
    private const float ApproachSlack = 1f;
    private const float SpotRange = 0.3f;
    private const float StepMargin = 0.5f;
    private const float PushRange = 1.5f;
    private const float CoverSearch = 8f;
    private const float CoverFacing = -0.5f;
    private const float SpotHalfSize = 0.3f;
    private const float HideMargin = 0.35f;
    private const float SquadRange = 20f;
    private const float BadGoalRange = 1f;
    private const float ProgressStep = 0.3f;
    private static readonly float[] RetreatSteps = { 5f, 3f };
    private static readonly float[] RetreatAngles = { 0f, 35f, -35f, 70f, -70f };
    private const float CoverHoldRange = 2.5f;
    private const float FlankRange = 7f;
    private const float FlankShift = 4f;
    private const float FlankMinRange = 4f;
    private static readonly float[] FlankAngles = { 75f, -75f, 105f, -105f, 50f, -50f };
    private static readonly float[] FlankScales = { 1f, 0.7f };
    private const float TargetCoverRange = 2f;
    private const float CloseEnough = 0.5f;
    private const float FarPenalty = 3f;
    private static readonly TimeSpan PushRetry = TimeSpan.FromSeconds(4);
    private const float StalePenalty = 1f;
    private static readonly TimeSpan CoverBan = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StalemateTime = TimeSpan.FromSeconds(10);
    private const float PostRange = 4f;
    private const float WallClearance = 3.5f;
    private const float WoundedRange = 8f;
    private const float GrenadeMin = 5f;
    private const float GrenadeMax = 16f;
    private const float GrenadeSplash = 2.5f;
    private const float GrenadeSafe = 5f;
    private const float GrenadeSpeed = 8f;
    private static readonly TimeSpan GrenadeAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GrenadeEngaged = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GrenadeDanger = TimeSpan.FromSeconds(5);
    private const float GrenadeClear = 7f;
    private static readonly TimeSpan PushDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GrenadeCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan GrenadeWindup = TimeSpan.FromSeconds(0.8);
    private static readonly TimeSpan SprayTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CoverTime = TimeSpan.FromSeconds(4);
    private const int MaxLineTests = 8;
    private const int RingDirections = 12;
    private const int HideDirections = 16;
    private static readonly float[] RingRadii = { 1.5f, 3f, 5f };
    private static readonly float[] HideRadii = { 1f, 1.5f, 2f, 2.5f, 3f, 4f, 5f };
    private static readonly TimeSpan BadGoalTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DeniedTime = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan StuckTime = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SuppressTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SuppressGapMin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SuppressGapMax = TimeSpan.FromSeconds(2);
    private static readonly Angle SuppressTolerance = Angle.FromDegrees(10);

    private readonly List<Candidate> _candidates = new();
    private readonly List<Vector2> _allies = new();
    private readonly HashSet<Entity<STLowCoverComponent>> _covers = new();
    private readonly HashSet<EntityUid> _occupants = new();
    private readonly HashSet<Entity<STLowCoverComponent>> _targetCovers = new();
    private readonly HashSet<Entity<STBrainComponent>> _squad = new();

    private record struct Candidate(MapCoordinates Spot, float Score, bool Here, bool Clear = false);

    private void Fight(EntityUid uid, STBrainComponent brain, STAwarenessComponent awareness, NPCBlackboard blackboard, EntityUid target, MapCoordinates self, bool mobile, TimeSpan now)
    {
        var to = _transform.GetMapCoordinates(target);
        var distance = (to.Position - self.Position).Length();
        if (to.MapId != self.MapId || distance < 0.01f)
        {
            _mover.Stop(uid);
            return;
        }

        if (brain.FireRange > 0f && distance > brain.FireRange)
            ClearAim(uid);
        else
            AimAt(uid, blackboard, target);

        GatherAllies(uid, self);

        var seen = awareness.Target == target
            ? now - awareness.LastSeen <= SeenSlack
            : _examine.InRangeUnOccluded(uid, target, distance + 0.5f);
        MapCoordinates? lastSeen = blackboard.TryGetValue<EntityCoordinates>(STAwarenessSystem.LastSeenKey, out var seenAt, EntityManager)
            ? _transform.ToMapCoordinates(seenAt)
            : null;

        if (seen)
        {
            ShareSight(uid, target, now);
            brain.FlankSpot = null;
            brain.FlankDone = false;
        }

        if (brain.ThrowAt is { } throwAt)
        {
            brain.SuppressAim = null;
            brain.Doing = "throwing a grenade";
            _mover.Stop(uid);
            if (now >= throwAt)
            {
                brain.ThrowAt = null;
                Throw(uid, brain);
            }

            return;
        }

        if (brain.Grenades && !seen && TryGrenade(uid, brain, awareness, self, to, distance, lastSeen, now))
            return;

        if (mobile && AvoidBlast(uid, brain, self, seen, lastSeen, now))
            return;

        if (!mobile)
        {
            _mover.Stop(uid);
            brain.SuppressAim = seen ? null : lastSeen;
            brain.Doing = seen ? "shooting from its post" : "covering where the target was";
            return;
        }

        var min = blackboard.TryGetValue<float>(MinRangeKey, out var minRange, EntityManager) ? minRange : brain.RangeMin;
        var max = blackboard.TryGetValue<float>(MaxRangeKey, out var maxRange, EntityManager) ? maxRange : brain.RangeMax;
        var useCover = !blackboard.TryGetValue<bool>(UseCoverKey, out var cover, EntityManager) || cover;

        if (brain.Kind == STBrainKind.Rusher || blackboard.TryGetValue<bool>(RushKey, out var rush, EntityManager) && rush)
        {
            if (!seen && now - awareness.LastSeen <= SprayTime)
            {
                brain.Doing = "spraying where the target was";
                brain.SuppressAim = lastSeen;
                _mover.Stop(uid);
                return;
            }

            brain.SuppressAim = null;
            brain.Doing = "walking in";
            _mover.MoveTo(uid, new EntityCoordinates(target, Vector2.Zero), MathF.Max(max, StepMargin));
            return;
        }

        if (brain.Goal != null && (_mover.NoPath(uid) || NoProgress(brain, self, now)))
        {
            if (brain.Doing == "moving to cover")
                brain.CoverBanUntil = now + CoverBan;

            if (brain.Hiding)
                brain.HideBanUntil = now + CoverBan;

            brain.BadGoal = brain.Goal;
            brain.BadGoalUntil = now + BadGoalTime;
            brain.Goal = null;
            brain.Hiding = false;
        }

        var parent = Transform(uid).ParentUid;
        var radius = _steering.BodyRadius(uid, CompOrNull<NPCSteeringComponent>(uid));
        var mask = (CollisionGroup) _physics.GetHardCollision(uid).Mask;

        var mates = 0;
        foreach (var other in _squad)
        {
            if (other.Owner != uid && other.Comp.Behaviour == STBehaviour.Fight && _faction.IsEntityFriendly(uid, other.Owner))
                mates++;
        }

        brain.PeakMates = Math.Max(brain.PeakMates, mates);
        if (brain.PeakMates >= 2 && mates == 0 && brain.Home is { } post)
        {
            var home = _transform.ToMapCoordinates(post);
            if (home.MapId == self.MapId && (home.Position - self.Position).Length() > PostRange)
            {
                brain.Goal = null;
                brain.Hiding = false;
                brain.SuppressAim = null;
                brain.Doing = "falling back to its post, squad down";
                _mover.MoveTo(uid, post, 1f);
                return;
            }

            brain.Goal = null;
            brain.Hiding = false;
            brain.SuppressAim = seen ? null : lastSeen;
            brain.Doing = "holding its post, squad down";
            _mover.Stop(uid);
            return;
        }

        var wounded = Wounded(uid, brain);
        if (wounded)
        {
            min += WoundedRange;
            max += WoundedRange;
        }

        var reloading = TryComp<STNpcMagazineComponent>(uid, out var magazine) && now < magazine.ReloadEnd;
        if ((reloading || wounded) && (seen || brain.Hiding) && now >= brain.HideBanUntil && (wounded || !brain.InCover))
        {
            if (brain.Hiding && brain.Goal is { } hide && Hidden(to, hide) ||
                TryHide(uid, brain, self, to, parent, radius, mask, now, out hide))
            {
                if (!brain.Hiding)
                    brain.GoalSince = now;

                brain.Hiding = true;
                brain.Goal = hide;
                brain.SuppressAim = null;
                brain.Doing = reloading ? "reloading out of sight" : "falling back, wounded";
                _mover.MoveTo(uid, _transform.ToCoordinates(parent, hide), SpotRange);
                return;
            }
        }

        if (brain.Hiding)
        {
            brain.Hiding = false;
            brain.Goal = null;
        }

        if (wounded && seen && distance < min && !(distance >= CoverHoldRange && brain.InCover))
        {
            var away = (self.Position - to.Position) / distance;
            foreach (var step in RetreatSteps)
            {
                foreach (var angle in RetreatAngles)
                {
                    var spot = self.Offset(Angle.FromDegrees(angle).RotateVec(away) * step);
                    if (Bad(spot, brain.BadGoalUntil > now ? brain.BadGoal : null) || !Walkable(uid, self, spot, parent, radius, mask, 0f))
                        continue;

                    if (brain.Goal is not { } old || (old.Position - spot.Position).LengthSquared() > SpotRange * SpotRange)
                        brain.GoalSince = now;

                    brain.Goal = spot;
                    brain.InCover = false;
                    brain.SuppressAim = null;
                    brain.Doing = "falling back, wounded";
                    _mover.MoveTo(uid, _transform.ToCoordinates(parent, spot), SpotRange);
                    return;
                }
            }
        }

        brain.Closing = distance > max + ApproachSlack || brain.Closing && distance > max - CloseEnough;
        if (brain.Closing && (brain.Partial != null || now < brain.PartialUntil || _mover.NoPath(uid)))
        {
            brain.Goal = null;
            brain.SuppressAim = null;
            CloseIn(uid, brain, blackboard, Transform(target).Coordinates, now);
            return;
        }

        if (brain.Closing)
        {
            brain.Goal = null;
            brain.SuppressAim = null;
            brain.Doing = "closing to firing range";
            _mover.MoveTo(uid, new EntityCoordinates(target, Vector2.Zero), max - 1f);
            return;
        }

        if (!seen && !wounded && now - awareness.LastSeen >= PushDelay && (brain.Pushing || !AllyPushing(uid, target)))
        {
            brain.Pushing = true;
            if (Flank(uid, brain, target, self, to, distance, parent, now))
                return;
        }

        var closing = TryComp<PhysicsComponent>(target, out var body)
            ? MathF.Max(0f, -Vector2.Dot(_physics.GetMapLinearVelocity(target, body), (to.Position - self.Position) / distance))
            : 0f;

        if (PickSpot(uid, brain, target, self, to, min, max, useCover, closing * RushLead, parent, radius, mask, now) is { } pick)
        {
            brain.Pushing = false;
            brain.SuppressAim = null;
            if (pick.Here && seen && brain.DeniedSince is { } denied && now - denied > DeniedTime)
            {
                brain.DeniedSince = null;
                brain.StalemateSince = null;
                brain.StaleUntil = now + CoverBan;
            }
            else if (pick.Here && seen && (!pick.Clear || brain.InCover && TargetCovered(to, self)))
            {
                brain.StalemateSince ??= now;
                if (now - brain.StalemateSince > StalemateTime)
                {
                    brain.StalemateSince = null;
                    brain.StaleUntil = now + CoverBan;
                }
            }
            else
            {
                brain.StalemateSince = null;
            }

            if (pick.Here)
            {
                brain.Goal = null;
                brain.InCover = Covered(self, to);

                brain.Doing = brain.InCover ? "shooting from cover" : seen ? "holding and shooting" : "holding a firing line";
                _mover.Stop(uid);
            }
            else
            {
                if (brain.Goal is not { } old || (old.Position - pick.Spot.Position).LengthSquared() > SpotRange * SpotRange)
                    brain.GoalSince = now;

                brain.Goal = pick.Spot;
                brain.InCover = false;
                brain.Doing = Covered(pick.Spot, to) ? "moving to cover" : wounded ? "falling back, wounded" : "moving to a better firing spot";
                _mover.MoveTo(uid, _transform.ToCoordinates(parent, pick.Spot), SpotRange);
            }

            return;
        }

        brain.Goal = null;
        brain.InCover = false;
        if (!seen && now - awareness.LastSeen < PushDelay)
        {
            brain.Doing = "covering where the target went";
            brain.SuppressAim = lastSeen;
            _mover.Stop(uid);
            return;
        }

        if (seen && _mover.NoPath(uid))
        {
            brain.Pushing = false;
            brain.SuppressAim = null;
            brain.Doing = "watching, no line of fire";
            if (now >= brain.RetryPush)
            {
                brain.RetryPush = now + PushRetry;
                _mover.Stop(uid);
            }

            return;
        }

        if (seen || !wounded && (brain.Pushing || !AllyPushing(uid, target)))
        {
            brain.Pushing = !seen;
            brain.SuppressAim = null;
            brain.Doing = seen ? "closing in for a shot" : "pushing to regain sight";
            _mover.MoveTo(uid, new EntityCoordinates(target, Vector2.Zero), Math.Clamp(distance - StepMargin, StepMargin, PushRange));
            return;
        }

        brain.Doing = "suppressing where the target was";
        brain.SuppressAim = lastSeen;
        _mover.Stop(uid);
    }

    private bool Flank(EntityUid uid, STBrainComponent brain, EntityUid target, MapCoordinates self, MapCoordinates to, float distance, EntityUid parent, TimeSpan now)
    {
        if (brain.FlankSpot is { } spot)
        {
            if ((to.Position - brain.FlankFor.Position).Length() > FlankShift || _mover.NoPath(uid) || NoProgress(brain, self, now))
            {
                brain.FlankSpot = null;
                brain.Goal = null;
            }
            else if ((spot.Position - self.Position).Length() <= ArriveRange)
            {
                brain.FlankSpot = null;
                brain.FlankDone = true;
                brain.Goal = null;
                return false;
            }
            else
            {
                brain.SuppressAim = null;
                brain.Doing = "flanking";
                _mover.MoveTo(uid, _transform.ToCoordinates(parent, spot), 1f);
                return true;
            }
        }

        if (brain.FlankDone || distance < FlankMinRange)
            return false;

        var back = (self.Position - to.Position) / distance;
        MapCoordinates? best = null;
        var bestTravel = float.MaxValue;
        for (var i = 0; i < FlankAngles.Length; i++)
        {
            foreach (var scale in FlankScales)
            {
                var point = to.Offset(Angle.FromDegrees(FlankAngles[i]).RotateVec(back) * FlankRange * scale);
                var travel = (point.Position - self.Position).Length();
                var coords = _transform.ToCoordinates(parent, point);
                if (travel >= bestTravel ||
                    _pathfinding.GetPoly(coords) is not { } poly ||
                    (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0 ||
                    _anomalies.InHazard(uid, coords) ||
                    !_examine.InRangeUnOccluded(point, to, FlankRange + 1f, null) ||
                    !_shotLine.Unobstructed(uid, point, to, target))
                {
                    continue;
                }

                best = point;
                bestTravel = travel;
            }

            if (i % 2 == 1 && best != null)
                break;
        }

        if (best is not { } pick)
        {
            brain.FlankDone = true;
            return false;
        }

        brain.FlankSpot = pick;
        brain.FlankFor = to;
        brain.Goal = pick;
        brain.GoalSince = now;
        brain.SuppressAim = null;
        brain.Doing = "flanking";
        _mover.MoveTo(uid, _transform.ToCoordinates(parent, pick), 1f);
        return true;
    }

    private bool TargetCovered(MapCoordinates to, MapCoordinates self)
    {
        _targetCovers.Clear();
        _lookup.GetEntitiesInRange(to, TargetCoverRange, _targetCovers);
        foreach (var cover in _targetCovers)
        {
            var bag = _transform.GetWorldPosition(cover);
            var toSelf = self.Position - bag;
            var toTarget = to.Position - bag;
            if (toSelf.LengthSquared() < 0.01f || toTarget.LengthSquared() < 0.01f)
                continue;

            if (Vector2.Dot(Vector2.Normalize(toSelf), Vector2.Normalize(toTarget)) <= CoverFacing && _lowCover.IsBehind(cover, to))
                return true;
        }

        return false;
    }

    private bool Wounded(EntityUid uid, STBrainComponent brain)
    {
        return _thresholds.TryGetThresholdForState(uid, Content.Shared.Mobs.MobState.Critical, out var crit) &&
               TryComp<Content.Shared.Damage.Components.DamageableComponent>(uid, out var damage) &&
               damage.TotalDamage >= crit.Value * brain.WoundedFraction;
    }

    private void ShareSight(EntityUid uid, EntityUid target, TimeSpan now)
    {
        foreach (var other in _squad)
        {
            if (other.Owner == uid ||
                other.Comp.LastTarget != target ||
                !TryComp<STAwarenessComponent>(other.Owner, out var mate) ||
                !_faction.IsEntityFriendly(uid, other.Owner))
            {
                continue;
            }

            if (mate.TrackUntil < now + mate.TrackTime)
                mate.TrackUntil = now + mate.TrackTime;
        }
    }

    private bool TryGrenade(EntityUid uid, STBrainComponent brain, STAwarenessComponent awareness, MapCoordinates self, MapCoordinates to, float distance, MapCoordinates? lastSeen, TimeSpan now)
    {
        if (now - awareness.LastSeen < GrenadeAfter ||
            now - awareness.StateStart < GrenadeEngaged ||
            now - brain.GrenadeAt < GrenadeCooldown ||
            distance < GrenadeMin ||
            distance > GrenadeMax)
        {
            return false;
        }

        foreach (var other in _squad)
        {
            if (other.Owner != uid && now - other.Comp.GrenadeAt < GrenadeCooldown && _faction.IsEntityFriendly(uid, other.Owner))
                return false;
        }

        var spot = to;
        if (!_interaction.InRangeUnobstructed(self, to, 0f, predicate: ent => ent == uid || HasComp<Content.Shared.Mobs.Components.MobStateComponent>(ent)))
        {
            if (lastSeen is not { } corner ||
                (corner.Position - to.Position).Length() > GrenadeSplash ||
                (corner.Position - self.Position).Length() < GrenadeMin ||
                !_interaction.InRangeUnobstructed(self, corner, 0f, predicate: ent => ent == uid || HasComp<Content.Shared.Mobs.Components.MobStateComponent>(ent)))
            {
                return false;
            }

            spot = corner;
        }

        foreach (var ally in _allies)
        {
            if ((ally - spot.Position).LengthSquared() < GrenadeSafe * GrenadeSafe)
                return false;
        }

        brain.GrenadeAt = now;
        brain.ThrowAt = now + GrenadeWindup;
        brain.ThrowSpot = spot;
        brain.SuppressAim = null;
        brain.Doing = "throwing a grenade";
        _mover.Stop(uid);
        _rotate.TryFaceCoordinates(uid, spot.Position);
        _popup.PopupEntity(Loc.GetString("st-npc-grenade-warning"), uid, Content.Shared.Popups.PopupType.LargeCaution);
        return true;
    }

    private void Throw(EntityUid uid, STBrainComponent brain)
    {
        var grenade = Spawn(brain.GrenadeProto, Transform(uid).Coordinates);
        _trigger.ActivateTimerTrigger(grenade, uid);
        _throwing.TryThrow(grenade, _transform.ToCoordinates(Transform(uid).ParentUid, brain.ThrowSpot), GrenadeSpeed, uid, compensateFriction: true, recoil: false);
        var until = _timing.CurTime + GrenadeDanger;
        brain.DangerSpot = brain.ThrowSpot;
        brain.DangerUntil = until;
        brain.DangerSource = grenade;
        foreach (var other in _squad)
        {
            if (!_faction.IsEntityFriendly(uid, other.Owner))
                continue;

            other.Comp.DangerSpot = brain.ThrowSpot;
            other.Comp.DangerUntil = until;
            other.Comp.DangerSource = grenade;
            other.Comp.NextThink = TimeSpan.Zero;
        }
    }

    private bool AvoidBlast(EntityUid uid, STBrainComponent brain, MapCoordinates self, bool seen, MapCoordinates? lastSeen, TimeSpan now)
    {
        if (now >= brain.DangerUntil || brain.DangerSpot is not { } danger)
            return false;

        if (brain.DangerSource is { } source && !TerminatingOrDeleted(source))
            danger = _transform.GetMapCoordinates(source);

        if (danger.MapId != self.MapId)
            return false;

        var offset = self.Position - danger.Position;
        var gap = offset.Length();
        if (gap >= GrenadeClear)
        {
            brain.Goal = null;
            brain.FlankSpot = null;
            brain.SuppressAim = seen ? null : lastSeen;
            brain.Doing = "waiting for the blast";
            _mover.Stop(uid);
            return true;
        }

        var away = gap > 0.01f ? offset / gap : _random.NextAngle().ToVec();
        var parent = Transform(uid).ParentUid;
        foreach (var step in RetreatSteps)
        {
            foreach (var angle in RetreatAngles)
            {
                var spot = _transform.ToCoordinates(parent, self.Offset(Angle.FromDegrees(angle).RotateVec(away) * step));
                if (_pathfinding.GetPoly(spot) is not { } poly ||
                    (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0 ||
                    _anomalies.InHazard(uid, spot))
                {
                    continue;
                }

                brain.Goal = null;
                brain.FlankSpot = null;
                brain.SuppressAim = null;
                brain.Doing = "getting clear of the grenade";
                _mover.MoveTo(uid, spot, SpotRange);
                return true;
            }
        }

        return false;
    }

    private bool TakeCover(EntityUid uid, STBrainComponent brain, MapCoordinates self, TimeSpan now)
    {
        if (brain.HitFrom is not { } from || now - brain.HitAt > CoverTime || from.MapId != self.MapId)
            return false;

        var distance = (from.Position - self.Position).Length();
        var parent = Transform(uid).ParentUid;
        if (brain.Hiding && brain.Goal is { } hide && Hidden(from, hide) ||
            _examine.InRangeUnOccluded(from, self, distance + 1f, null) &&
            TryHide(uid, brain, self, from, parent, _steering.BodyRadius(uid, CompOrNull<NPCSteeringComponent>(uid)), (CollisionGroup) _physics.GetHardCollision(uid).Mask, now, out hide))
        {
            brain.Hiding = true;
            brain.Goal = hide;
            brain.Doing = "taking cover from fire";
            _mover.MoveTo(uid, _transform.ToCoordinates(parent, hide), SpotRange);
            return true;
        }

        return false;
    }

    private bool NoProgress(STBrainComponent brain, MapCoordinates self, TimeSpan now)
    {
        if (brain.Goal is not { } goal)
            return false;

        var distance = (goal.Position - self.Position).Length();
        if (brain.GoalBestAt < brain.GoalSince || distance < brain.GoalBest - ProgressStep)
        {
            brain.GoalBest = distance;
            brain.GoalBestAt = now;
            return false;
        }

        return now - brain.GoalBestAt > StuckTime;
    }

    private Candidate? PickSpot(EntityUid uid, STBrainComponent brain, EntityUid target, MapCoordinates self, MapCoordinates to, float min, float max, bool useCover, float lead, EntityUid parent, float radius, CollisionGroup mask, TimeSpan now)
    {
        _candidates.Clear();
        _covers.Clear();
        var stale = now < brain.StaleUntil;
        useCover = useCover && !stale && now >= brain.CoverBanUntil;
        if (useCover)
            _lookup.GetEntitiesInRange(self, CoverSearch, _covers);

        var badGoal = now < brain.BadGoalUntil ? brain.BadGoal : null;
        var still = (float) (now - brain.StillSince).TotalSeconds;
        var restless = MathF.Min(RestlessMax, MathF.Max(0f, still - RestlessAfter) * RestlessRate);
        _candidates.Add(new Candidate(self, Cheap(self, to, min, max, lead, true) + StayBonus - restless - (stale ? StalePenalty : 0f), true));

        if (brain.Goal is { } goal && goal.MapId == self.MapId && (goal.Position - self.Position).LengthSquared() > NearSpot * NearSpot)
            _candidates.Add(new Candidate(goal, Cheap(goal, to, min, max, lead, false) + GoalBonus - TravelCost * (goal.Position - self.Position).Length(), false));

        foreach (var ring in RingRadii)
        {
            for (var i = 0; i < RingDirections; i++)
            {
                var end = self.Offset(Angle.FromDegrees(i * 360f / RingDirections).ToVec() * ring);
                if (Bad(end, badGoal) || !Walkable(uid, self, end, parent, radius, mask))
                    continue;

                _candidates.Add(new Candidate(end, Cheap(end, to, min, max, lead, false) - TravelCost * ring, false));
            }
        }

        if (useCover && Transform(uid).GridUid is { } grid && TryComp<MapGridComponent>(grid, out var gridComp))
        {
            var hard = _physics.GetHardCollision(uid).Mask;
            foreach (var cover in _covers)
            {
                var bag = _transform.GetMapCoordinates(cover);
                var away = bag.Position - to.Position;
                var range = away.Length();
                if (bag.MapId != to.MapId || range < 0.01f)
                    continue;

                var spot = _map.GridTileToLocal(grid, gridComp, _map.TileIndicesFor(grid, gridComp, bag.Offset(away / range)));
                var spotMap = _transform.ToMapCoordinates(spot);
                if (Bad(spotMap, badGoal) ||
                    (spotMap.Position - self.Position).LengthSquared() < NearSpot * NearSpot ||
                    !_lowCover.IsBehind(cover, spotMap) ||
                    _anomalies.InHazard(uid, spot) ||
                    _pathfinding.GetPoly(spot) is not { } poly ||
                    (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0 ||
                    !SpotFree(uid, spotMap, hard))
                {
                    continue;
                }

                _candidates.Add(new Candidate(spotMap, Cheap(spotMap, to, min, max, lead, false) - TravelCost * (spotMap.Position - self.Position).Length(), false));
            }
        }

        GunComponent? gun = null;
        if (_gun.TryGetGun(uid, out _, out var held))
            gun = held;

        var crowded = gun != null && _friendlyFire.IsAllyInLine(uid, self.Position, to.Position, _shotLine.NextSpread(gun), target);
        if (crowded)
        {
            for (var i = 0; i < _candidates.Count; i++)
            {
                var candidate = _candidates[i];
                if (_friendlyFire.IsAllyInLine(uid, candidate.Spot.Position, to.Position, _shotLine.NextSpread(gun!), target))
                    _candidates[i] = candidate with { Score = candidate.Score - AllyLinePenalty };
            }
        }

        _candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        Candidate? best = null;
        var tested = 0;
        foreach (var candidate in _candidates)
        {
            if (best is { } leader && candidate.Score + LineClear <= leader.Score || tested++ >= MaxLineTests)
                break;

            var line = _shotLine.Unobstructed(uid, candidate.Spot, to, target) ? _shotLine.Check(uid, candidate.Spot, to, target) : STShotLine.Blocked;
            if (line == STShotLine.Blocked)
                continue;

            if (line == STShotLine.Clear && gun != null &&
                (_shotLine.WallInSpread(uid, candidate.Spot, to, target, _shotLine.NextSpread(gun), WallClearance) ||
                 _shotLine.GlassInSpread(candidate.Spot, to, _shotLine.NextSpread(gun))))
            {
                line = STShotLine.Cover;
            }

            var total = candidate.Score + (line == STShotLine.Clear ? LineClear : LineCover);
            if (!crowded && gun != null && _friendlyFire.IsAllyInLine(uid, candidate.Spot.Position, to.Position, _shotLine.NextSpread(gun), target))
                total -= AllyLinePenalty;

            if (best == null || total > best.Value.Score)
                best = candidate with { Score = total, Clear = line == STShotLine.Clear };
        }

        return best;
    }

    private float Cheap(MapCoordinates spot, MapCoordinates to, float min, float max, float lead, bool here)
    {
        var distance = (to.Position - spot.Position).Length() - lead;
        var covered = Covered(spot, to);
        var score = distance < min
            ? covered && distance >= CoverHoldRange ? 1f : MathF.Max(-1.5f, 1f - CloseFalloff * (min - distance))
            : distance > (here ? max + ApproachSlack : max - CloseEnough) ? -FarPenalty
            : MathF.Max(-1.5f, 1f - RangeFalloff * MathF.Max(0f, distance - max));

        if (covered)
            score += CoverBonus;

        if (!here)
        {
            foreach (var ally in _allies)
            {
                if ((ally - spot.Position).LengthSquared() < AllySpacing * AllySpacing)
                {
                    score -= AllyNearPenalty;
                    break;
                }
            }
        }

        return score;
    }

    private bool Covered(MapCoordinates spot, MapCoordinates to)
    {
        foreach (var cover in _covers)
        {
            var bag = _transform.GetWorldPosition(cover);
            var toTarget = to.Position - bag;
            var toSpot = spot.Position - bag;
            if (toTarget.LengthSquared() < 0.01f || toSpot.LengthSquared() < 0.01f)
                continue;

            if (Vector2.Dot(Vector2.Normalize(toTarget), Vector2.Normalize(toSpot)) <= CoverFacing && _lowCover.IsBehind(cover, spot))
                return true;
        }

        return false;
    }

    private static bool Bad(MapCoordinates spot, MapCoordinates? badGoal)
    {
        return badGoal is { } bad && (bad.Position - spot.Position).LengthSquared() < BadGoalRange * BadGoalRange;
    }

    private bool Walkable(EntityUid uid, MapCoordinates from, MapCoordinates end, EntityUid parent, float radius, CollisionGroup mask, float margin = StepMargin)
    {
        var line = end.Position - from.Position;
        var length = line.Length();
        if (length < 0.01f)
            return false;

        var beyond = from.Offset(line * ((length + margin) / length));
        return _pathfinding.GetPoly(_transform.ToCoordinates(parent, end)) is { } poly &&
               (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) == 0 &&
               !_anomalies.Crosses(uid, from, end, radius) &&
               !_chasms.LineCrosses(uid, _transform.ToCoordinates(parent, beyond), radius) &&
               _steering.CanWalk(uid, from, beyond, radius, mask);
    }

    private bool SpotFree(EntityUid uid, MapCoordinates spot, int mask)
    {
        _occupants.Clear();
        _lookup.GetEntitiesIntersecting(spot.MapId, Box2.CenteredAround(spot.Position, new Vector2(SpotHalfSize * 2f)), _occupants, LookupFlags.Dynamic | LookupFlags.Static);
        foreach (var ent in _occupants)
        {
            if (ent != uid &&
                TryComp<PhysicsComponent>(ent, out var body) &&
                body.Hard &&
                body.CanCollide &&
                (body.CollisionLayer & mask) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryHide(EntityUid uid, STBrainComponent brain, MapCoordinates self, MapCoordinates to, EntityUid parent, float radius, CollisionGroup mask, TimeSpan now, out MapCoordinates hide)
    {
        var badGoal = now < brain.BadGoalUntil ? brain.BadGoal : null;
        foreach (var distance in HideRadii)
        {
            for (var i = 0; i < HideDirections; i++)
            {
                hide = self.Offset(Angle.FromDegrees(i * 360f / HideDirections).ToVec() * distance);
                if (!Bad(hide, badGoal) && Hidden(to, hide) && Walkable(uid, self, hide, parent, radius, mask, 0f))
                    return true;
            }
        }

        foreach (var distance in HideRadii)
        {
            for (var i = 0; i < HideDirections; i++)
            {
                hide = self.Offset(Angle.FromDegrees(i * 360f / HideDirections).ToVec() * distance);
                var coords = _transform.ToCoordinates(parent, hide);
                if (!Bad(hide, badGoal) &&
                    Hidden(to, hide) &&
                    _pathfinding.GetPoly(coords) is { } poly &&
                    (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) == 0 &&
                    !_anomalies.InHazard(uid, coords))
                {
                    return true;
                }
            }
        }

        hide = default;
        return false;
    }

    private bool Hidden(MapCoordinates viewer, MapCoordinates spot)
    {
        var line = spot.Position - viewer.Position;
        var length = line.Length();
        if (length < 0.01f)
            return false;

        var side = new Vector2(-line.Y, line.X) / length * HideMargin;
        return !_examine.InRangeUnOccluded(viewer, spot, length + 1f, null) &&
               !_examine.InRangeUnOccluded(viewer, spot.Offset(side), length + 1f, null) &&
               !_examine.InRangeUnOccluded(viewer, spot.Offset(-side), length + 1f, null);
    }

    private void GatherAllies(EntityUid uid, MapCoordinates self)
    {
        _allies.Clear();
        _squad.Clear();
        _lookup.GetEntitiesInRange(self, SquadRange, _squad);
        foreach (var other in _squad)
        {
            if (other.Owner == uid || other.Comp.Behaviour == STBehaviour.None || !_faction.IsEntityFriendly(uid, other.Owner))
                continue;

            _allies.Add(_transform.GetWorldPosition(other.Owner));
            if (other.Comp.Goal is { } goal)
                _allies.Add(goal.Position);
        }
    }

    private bool AllyPushing(EntityUid uid, EntityUid target)
    {
        foreach (var other in _squad)
        {
            if (other.Owner != uid && other.Comp.Pushing && other.Comp.LastTarget == target && _faction.IsEntityFriendly(uid, other.Owner))
                return true;
        }

        return false;
    }
}
