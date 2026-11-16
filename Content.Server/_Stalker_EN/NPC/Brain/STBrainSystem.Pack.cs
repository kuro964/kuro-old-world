using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared.NPC;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server._Stalker_EN.NPC.Brain;

public sealed partial class STBrainSystem
{
    private const float BiteReach = 1.75f;
    private const float StandMin = 0.85f;
    private const float StandInside = 0.3f;
    private static readonly float[] SlotAngles = { 100f, -100f, 180f, 50f, -50f, 140f, -140f };
    private static readonly TimeSpan SlotBan = TimeSpan.FromSeconds(1.5);
    private const float SlotCommit = 0.6f;
    private static readonly TimeSpan FanTime = TimeSpan.FromSeconds(4);
    private const float FanInside = 0.6f;
    private const float FanArrive = 1.2f;
    private const int ReachRings = 4;
    private const float SurroundRing = 3.5f;
    private const float ChargeMargin = 0.5f;
    private const float CrowdRange = 0.7f;
    private const float BackOff = 0.6f;
    private const float MinGap = 0.55f;
    private const float BrakeLead = 0.12f;
    private const float StandSlack = 0.15f;
    private const float SlotArrive = 0.3f;

    private const int MoraleMinPack = 3;
    private static readonly TimeSpan MoraleFlee = TimeSpan.FromSeconds(8);
    private const float StalkClose = 3f;
    private const float ChaseRange = 12f;
    private const float OrbitArrive = 1.5f;
    private static readonly TimeSpan PartialHold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FleeTime = TimeSpan.FromSeconds(12);
    private const float FleeDistance = 15f;
    private static readonly float[] FleeRanges = { 15f, 11f, 7f };
    private static readonly float[] FleeAngles = { 0f, 30f, -30f, 60f, -60f, 90f, -90f };
    private const float FlankSpread = 50f;
    private const float FlankTolerance = 20f;
    private const float OrbitSlack = 2f;
    private static readonly Angle OrbitStep = Angle.FromDegrees(50);

    private void Hunt(EntityUid uid, STBrainComponent brain, NPCBlackboard blackboard, EntityUid target, MapCoordinates self, bool mobile, TimeSpan now)
    {
        var to = _transform.GetMapCoordinates(target);
        var distance = (to.Position - self.Position).Length();
        if (to.MapId != self.MapId)
        {
            ClearAim(uid);
            _mover.Stop(uid);
            return;
        }

        if (!brain.Barked)
        {
            brain.Barked = true;
            if (brain.BarkSound != null)
                _audio.PlayPvs(brain.BarkSound, uid);

            if (brain.BarkDelay > TimeSpan.Zero)
            {
                brain.Doing = "roaring";
                brain.BusyUntil = now + brain.BarkDelay;
                _mover.Stop(uid);
                _rotate.TryFaceCoordinates(uid, to.Position);
                return;
            }
        }

        var reach = TryComp<Content.Shared.Weapons.Melee.MeleeWeaponComponent>(uid, out var melee) ? MathF.Max(BiteReach, melee.Range - 0.25f) : BiteReach;
        Boost(uid, brain, brain.RetreatSpeed != 1f && !brain.Biting && now < brain.RestUntil ? brain.RetreatSpeed : 1f);
        GatherAllies(uid, self);
        var mates = 0;
        var biters = 0;
        var biteSide = System.Numerics.Vector2.Zero;
        var rank = 0;
        var crowded = false;
        var anchorId = uid.Id;
        System.Numerics.Vector2? anchor = null;
        var ready = false;
        foreach (var other in _squad)
        {
            if (other.Owner == uid || other.Comp.Kind != STBrainKind.Pack || other.Comp.LastTarget != target || !_faction.IsEntityFriendly(uid, other.Owner))
                continue;

            mates++;
            if (!other.Comp.Biting)
            {
                ready |= (_transform.GetWorldPosition(other.Owner) - to.Position).Length() <= brain.RingRadius + 1.5f;
                continue;
            }

            biters++;
            var side = _transform.GetWorldPosition(other.Owner) - to.Position;
            if (side.LengthSquared() > 0.01f)
            {
                var bearing = System.Numerics.Vector2.Normalize(side);
                biteSide += bearing;
                if (other.Owner.Id < uid.Id)
                {
                    rank++;
                    crowded |= (_transform.GetWorldPosition(other.Owner) - self.Position).LengthSquared() < CrowdRange * CrowdRange;
                }

                if (other.Owner.Id < anchorId)
                {
                    anchorId = other.Owner.Id;
                    anchor = bearing;
                }
            }
        }

        brain.PeakMates = Math.Max(brain.PeakMates, mates);
        if (brain.BreakFraction > 0f &&
            !brain.Broken &&
            brain.PeakMates >= MoraleMinPack &&
            brain.PeakMates + 1 - brain.Losses <= (brain.PeakMates + 1) * brain.BreakFraction &&
            distance > 0.01f)
        {
            brain.Broken = true;
            brain.Biting = false;
            brain.FleeUntil = now + MoraleFlee * _random.NextFloat(0.8f, 1.2f);
            brain.Goal = FleeSpot(uid, self, to, distance);
            brain.GoalSince = now;
        }

        if (now < brain.FleeUntil)
        {
            brain.Doing = brain.Broken ? "fleeing, the pack is broken" : "fleeing, the pack head is dead";
            brain.Biting = false;
            RemComp<NPCMeleeCombatComponent>(uid);
            if (brain.Goal is { } away)
                _mover.MoveTo(uid, _transform.ToCoordinates(Transform(uid).ParentUid, away), 1f);

            return;
        }

        if (now < brain.ShakenUntil)
        {
            brain.Doing = "hesitating, the pack head is dead";
            brain.Biting = false;
            RemComp<NPCMeleeCombatComponent>(uid);
            _mover.Stop(uid);
            _rotate.TryFaceCoordinates(uid, to.Position);
            return;
        }

        if (brain.ShakenUntil != TimeSpan.Zero)
        {
            brain.ShakenUntil = TimeSpan.Zero;
            if (_random.Prob(0.5f) && distance > 0.01f)
            {
                brain.FleeUntil = now + FleeTime;
                brain.Goal = FleeSpot(uid, self, to, distance);
                brain.GoalSince = now;
                return;
            }

            brain.MaxBiters = int.MaxValue;
        }

        var behind = brain.HangBack && mates > 0;
        var stalking = false;
        if (behind)
        {
            brain.Biting = false;
            brain.BiteUntil = TimeSpan.Zero;
        }
        else if (brain.Biting)
        {
            if ((mates >= brain.MaxBiters || brain.Skirmish && !brain.Commit) && distance <= reach && brain.BiteUntil == TimeSpan.Zero)
                brain.BiteUntil = now + brain.BiteTime * _random.NextFloat(0.7f, 1.3f);

            if (brain.BiteUntil != TimeSpan.Zero && now >= brain.BiteUntil && (ready || brain.Skirmish && !brain.Commit) || biters >= brain.MaxBiters && distance > reach)
            {
                brain.StalkUntil = TimeSpan.Zero;
                brain.Biting = false;
                brain.BiteUntil = TimeSpan.Zero;
                brain.RestUntil = now + brain.RestTime;
                brain.Goal = null;
            }
        }
        else if (!mobile || now >= brain.RestUntil && biters < brain.MaxBiters)
        {
            if (mobile && brain.Stalk && !brain.Commit && distance > StalkClose && Watched(target, self, to, brain.StalkCone))
            {
                if (brain.StalkUntil == TimeSpan.Zero)
                    brain.StalkUntil = now + brain.StalkTime;

                stalking = now < brain.StalkUntil;
            }

            if (!stalking)
            {
                brain.Biting = true;
                brain.BiteUntil = TimeSpan.Zero;
                brain.FanUntil = now + FanTime;
            }
        }

        if (brain.Biting && distance > ChaseRange)
        {
            if (brain.Partial != null || now < brain.PartialUntil || _mover.NoPathTo(uid, target))
            {
                CloseIn(uid, brain, blackboard, Transform(target).Coordinates, now);
                return;
            }

            brain.Doing = "running the target down";
            RemComp<NPCMeleeCombatComponent>(uid);
            _mover.MoveTo(uid, new EntityCoordinates(target, System.Numerics.Vector2.Zero), 1f);
            return;
        }

        if (brain.Biting)
        {
            if (_mover.NoPath(uid) && !_mover.NoPathTo(uid, target))
            {
                brain.SlotBanUntil = now + SlotBan;
                brain.SlotShift++;
                _mover.Stop(uid);
            }

            var blocked = distance > reach && (brain.Partial != null || now < brain.PartialUntil || _mover.NoPathTo(uid, target));
            if (blocked && ready && mates >= brain.MaxBiters)
            {
                brain.Biting = false;
                brain.RestUntil = now + brain.RestTime + PartialHold;
                brain.Partial = null;
                brain.PartialUntil = TimeSpan.Zero;
                brain.Goal = null;
            }
            else if (blocked)
            {
                CloseIn(uid, brain, blackboard, Transform(target).Coordinates, now);
                return;
            }
            else
            {
                brain.Partial = null;
                brain.PartialUntil = TimeSpan.Zero;
                brain.Doing = distance <= reach ? "biting" : "going in to bite";
                BiteAt(uid, target, reach, now >= brain.SlotBanUntil && rank > 0 && anchor is { } lead ? Angle.FromDegrees(SlotAngles[(rank - 1 + brain.SlotShift) % SlotAngles.Length]).RotateVec(lead) : null, crowded, now >= brain.SlotBanUntil);
                return;
            }
        }

        RemComp<NPCMeleeCombatComponent>(uid);
        if (stalking)
        {
            Orbit(uid, brain, self, to, _transform.GetWorldRotation(target).ToWorldVec(), now);
            brain.Doing = "stalking, waiting for the target to look away";
            return;
        }

        Orbit(uid, brain, self, to, biters > 0 && biteSide.LengthSquared() > 0.01f ? biteSide : null, now);
        if (behind)
            brain.Doing = "hanging back behind the pack";
    }

    private MapCoordinates FleeSpot(EntityUid uid, MapCoordinates self, MapCoordinates to, float distance)
    {
        var away = (self.Position - to.Position) / distance;
        var parent = Transform(uid).ParentUid;
        foreach (var range in FleeRanges)
        {
            foreach (var angle in FleeAngles)
            {
                var spot = self.Offset(Angle.FromDegrees(angle).RotateVec(away) * range);
                var coords = _transform.ToCoordinates(parent, spot);
                if (_pathfinding.GetPoly(coords) is { } poly &&
                    (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) == 0 &&
                    !_anomalies.InHazard(uid, coords))
                {
                    return spot;
                }
            }
        }

        return self.Offset(away * FleeDistance);
    }

    private bool Watched(EntityUid target, MapCoordinates self, MapCoordinates to, float cone)
    {
        var toSelf = self.Position - to.Position;
        if (toSelf.LengthSquared() < 0.01f)
            return false;

        var facing = _transform.GetWorldRotation(target).ToWorldVec();
        return System.Numerics.Vector2.Dot(System.Numerics.Vector2.Normalize(toSelf), facing) >= MathF.Cos(cone * MathF.PI / 180f);
    }

    private void CloseIn(EntityUid uid, STBrainComponent brain, NPCBlackboard blackboard, EntityCoordinates destination, TimeSpan now)
    {
        brain.Doing = "getting as close as it can";
        if (now < brain.PartialUntil)
            return;

        if (brain.Partial == null)
        {
            RemComp<NPCMeleeCombatComponent>(uid);
            _mover.Stop(uid);
            brain.Partial = _pathfinding.GetPartialPath(uid, Transform(uid).Coordinates, Reachable(uid, destination), 1f, System.Threading.CancellationToken.None, _pathfinding.GetFlags(blackboard));
            return;
        }

        if (!brain.Partial.IsCompleted)
            return;

        var path = brain.Partial.IsCompletedSuccessfully ? brain.Partial.GetAwaiter().GetResult() : null;
        brain.Partial = null;
        brain.PartialUntil = now + PartialHold;
        if (path is not { Result: PathResult.PartialPath or PathResult.Path } || path.Path.Count == 0)
        {
            brain.PathRetries = MaxPathRetries;
            return;
        }

        var closest = path.Path[^1];
        var end = _transform.ToMapCoordinates(new EntityCoordinates(closest.GraphUid, closest.Box.Center)).Position;
        var here = _transform.GetWorldPosition(uid);
        var goal = _transform.ToMapCoordinates(destination).Position;
        if ((end - here).Length() <= ArriveRange || (end - goal).Length() > (here - goal).Length() - 1f)
        {
            brain.PathRetries = MaxPathRetries;
            return;
        }

        _mover.MoveTo(uid, new EntityCoordinates(closest.GraphUid, closest.Box.Center), 0.5f);
        if (TryComp<NPCSteeringComponent>(uid, out var steering))
            steering.CurrentPath = new Queue<PathPoly>(path.Path);
    }

    private EntityCoordinates Reachable(EntityUid uid, EntityCoordinates destination)
    {
        if (_pathfinding.GetPoly(destination) != null)
            return destination;

        var parent = Transform(uid).ParentUid;
        var here = _transform.GetWorldPosition(uid);
        var goal = _transform.ToMapCoordinates(destination);
        var best = destination;
        var bestDistance = float.MaxValue;
        for (var ring = 1; ring <= ReachRings; ring++)
        {
            for (var i = 0; i < 8; i++)
            {
                var spot = goal.Offset(Angle.FromDegrees(i * 45).ToVec() * ring);
                var coords = _transform.ToCoordinates(parent, spot);
                var distance = (spot.Position - here).Length();
                if (distance >= bestDistance || _pathfinding.GetPoly(coords) == null)
                    continue;

                best = coords;
                bestDistance = distance;
            }

            if (bestDistance < float.MaxValue)
                break;
        }

        return best;
    }

    private void Orbit(EntityUid uid, STBrainComponent brain, MapCoordinates self, MapCoordinates to, System.Numerics.Vector2? biteSide, TimeSpan now)
    {
        if (brain.OrbitSign == 0)
            brain.OrbitSign = _random.Prob(0.5f) ? 1 : -1;

        brain.Doing = "circling for an opening";
        var parent = Transform(uid).ParentUid;
        var stale = brain.Goal is not { } goal ||
                    (goal.Position - self.Position).Length() <= OrbitArrive ||
                    MathF.Abs((goal.Position - to.Position).Length() - brain.RingRadius) > OrbitSlack ||
                    _mover.NoPath(uid) ||
                    NoProgress(brain, self, now);

        if (stale)
        {
            if (_mover.NoPath(uid) || NoProgress(brain, self, now))
                brain.OrbitSign = -brain.OrbitSign;

            var bearing = (self.Position - to.Position).ToAngle();
            var turn = OrbitStep;
            if (biteSide is { } front)
            {
                var slot = front.ToAngle() + Angle.FromDegrees(180 + (uid.Id % 3 - 1) * FlankSpread);
                var gap = Angle.ShortestDistance(bearing, slot);
                if (Math.Abs(gap.Degrees) <= FlankTolerance)
                {
                    brain.Goal = null;
                    brain.Doing = "waiting at the flank";
                    _mover.Stop(uid);
                    _rotate.TryFaceCoordinates(uid, to.Position);
                    return;
                }

                brain.OrbitSign = gap.Theta >= 0 ? 1 : -1;
                turn = new Angle(Math.Min(Math.Abs(gap.Theta), OrbitStep.Theta));
            }

            brain.Goal = null;
            foreach (var sign in new[] { brain.OrbitSign, -brain.OrbitSign })
            {
                var next = to.Offset((bearing + turn * sign).ToVec() * brain.RingRadius);
                var coords = _transform.ToCoordinates(parent, next);
                if (_pathfinding.GetPoly(coords) is not { } poly ||
                    (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0 ||
                    _anomalies.InHazard(uid, coords))
                {
                    continue;
                }

                brain.OrbitSign = sign;
                brain.Goal = next;
                brain.GoalSince = now;
                break;
            }
        }

        if (brain.Goal is { } spot)
            _mover.MoveTo(uid, _transform.ToCoordinates(parent, spot), 0.5f);
        else
            _mover.Stop(uid);
    }

    private void BiteAt(EntityUid uid, EntityUid target, float reach, System.Numerics.Vector2? side = null, bool crowded = false, bool shuffle = true)
    {
        EnsureComp<NPCMeleeCombatComponent>(uid).Target = target;
        var stand = MathF.Max(StandMin, reach - StandInside);
        if (TryComp<Content.Shared.Weapons.Melee.MeleeWeaponComponent>(uid, out var weapon))
            stand = MathF.Min(stand, weapon.Range - StandSlack);

        var self = _transform.GetMapCoordinates(uid);
        var to = _transform.GetMapCoordinates(target);
        var offset = self.Position - to.Position;
        var distance = offset.Length();
        var parent = Transform(uid).ParentUid;
        if (distance < MathF.Max(MinGap, stand - BackOff))
        {
            var outward = distance > 0.05f ? offset / distance : side ?? _random.NextAngle().ToVec();
            var spot = _transform.ToCoordinates(parent, to.Offset(outward * (stand - StandSlack)));
            if (_pathfinding.GetPoly(spot) is { } free && (free.Data.Flags & PathfindingBreadcrumbFlag.Space) == 0)
            {
                _mover.MoveTo(uid, spot, SlotArrive);
                return;
            }
        }

        var closing = distance > 0.01f && TryComp<Robust.Shared.Physics.Components.PhysicsComponent>(uid, out var moving)
            ? MathF.Max(0f, -System.Numerics.Vector2.Dot(_physics.GetMapLinearVelocity(uid, moving), offset / distance))
            : 0f;

        if (distance <= stand + StandSlack + closing * BrakeLead && !(crowded && side != null))
        {
            _mover.Stop(uid);
            _rotate.TryFaceCoordinates(uid, to.Position);
            return;
        }

        if (side is { } bearing && (crowded || distance > stand + SlotCommit))
        {
            var ring = TryComp<Content.Shared._Stalker.Jumpscare.JumpscareComponent>(uid, out var leap) && !leap.Away
                ? MathF.Max(SurroundRing, leap.AttackRadius + ChargeMargin)
                : SurroundRing;
            var own = CompOrNull<STBrainComponent>(uid);
            var wide = to.Offset(bearing * MathF.Max(stand, ring));
            var fanning = own != null && _timing.CurTime < own.FanUntil && distance > ring * FanInside;
            if (fanning && (wide.Position - self.Position).Length() <= FanArrive)
            {
                own!.FanUntil = TimeSpan.Zero;
                fanning = false;
            }

            var slot = _transform.ToCoordinates(parent, fanning ? wide : to.Offset(bearing * stand));
            if (_pathfinding.GetPoly(slot) is { } poly && (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) == 0 && !_anomalies.InHazard(uid, slot))
            {
                _mover.MoveTo(uid, slot, SlotArrive);
                return;
            }

            if (own != null)
                own.SlotShift++;
        }

        _mover.MoveTo(uid, new EntityCoordinates(target, System.Numerics.Vector2.Zero), stand);
    }
}
