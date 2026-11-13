using System.Numerics;
using Content.Server._Stalker_EN.NPC.Steering;
using Content.Server.Damage.Systems;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Stunnable;
using Content.Shared.ActionBlocker;
using Content.Shared.Examine;
using Content.Shared._Stalker.Jumpscare;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.Physics;
using Content.Shared.Throwing;
using Robust.Server.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using YamlDotNet.Core.Tokens;

namespace Content.Server._Stalker.Jumpscare;

public sealed class JumpscareSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly StunSystem _stunSystem = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly TransformSystem _xform = default!;
    [Dependency] private readonly StaminaSystem _stamina = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly MovementModStatusSystem _movementMod = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly Robust.Shared.Audio.Systems.SharedAudioSystem _audio = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly STChasmAvoidanceSystem _chasms = default!;

    private static readonly TimeSpan TellRetry = TimeSpan.FromSeconds(1.5);
    private const float LungeOvershoot = 2f;
    private const float LandShort = 1.2f;
    private const float LandClose = 0.3f;
    private static readonly TimeSpan LandWait = TimeSpan.FromSeconds(1);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<JumpscareComponent>();

        while (query.MoveNext(out var uid, out var comp))
        {
            // ST:OW begin
            if (!TryComp<MobStateComponent>(uid, out var state) || !_mobState.IsAlive(uid, state) || !_actionBlocker.CanMove(uid))
            {
                CancelLunge(comp);
                continue;
            }
            // ST:OW end
            if (comp.MovingToJumpTarget)
            {
                MoveTowardsTarget(uid, comp, frameTime);
                continue;
            }

            if (comp.NextTimeUpdate > _timing.CurTime)
                continue;
            comp.NextTimeUpdate = _timing.CurTime + TimeSpan.FromSeconds(comp.UpdateCooldown);

            if (comp.OnCoolDown && _timing.CurTime <= comp.EndTime)
                continue;

            if (CheckHumanTarget(uid, comp) is not { } humanTarget)
            {
                if (!comp.OnCoolDown)
                {
                    comp.OnCoolDown = true;
                    comp.EndTime = _timing.CurTime + TellRetry;
                }

                continue;
            }

            if (_timing.CurTime > comp.EndTime)
            {
                comp.StartTime = _timing.CurTime;
                comp.EndTime = comp.StartTime + TimeSpan.FromSeconds(comp.ReloadTime + _random.NextFloat(-comp.RandomiseReloadTime, comp.RandomiseReloadTime));

                if (TryComp<MobStateComponent>(uid, out var entityMobState) && _mobState.IsAlive(uid, entityMobState))
                {
                    _movementMod.TryUpdateMovementSpeedModDuration(uid, "JumpscareSlowdownStatusEffect", TimeSpan.FromSeconds(0.4f), 0f, 0f);
                    if (comp.WindupSound != null)
                        _audio.PlayPvs(comp.WindupSound, uid);

                    comp.PreparingStartTime = _timing.CurTime;
                    comp.PreparingEndTime = comp.PreparingStartTime + comp.PreparingReloadTime;
                    comp.OnCoolDown = false;
                }
            }

            if (_timing.CurTime > comp.PreparingEndTime && !comp.OnCoolDown)
            {
                var startPos = _xform.GetWorldPosition(uid);
                var targetPos = _xform.GetWorldPosition(humanTarget);
                var lunge = targetPos - startPos;
                comp.JumpTarget = comp.Away ? startPos + AwayFrom(startPos, targetPos) * comp.JumpDistance
                    : lunge.Length() > LandShort ? targetPos - Vector2.Normalize(lunge) * LandShort : startPos;
                comp.LungeTarget = humanTarget;
                comp.CurrentStep = 0;
                comp.NextStepTime = _timing.CurTime;
                comp.MovingToJumpTarget = true;
                comp.OnCoolDown = true;
            }
        }
    }

    // ST:OW begin
    private static void CancelLunge(JumpscareComponent comp)
    {
        comp.MovingToJumpTarget = false;
        comp.OnCoolDown = true;
        comp.CurrentStep = 0;
        comp.NextStepTime = null;
    }
    // ST:OW end
    
    private void MoveTowardsTarget(EntityUid uid, JumpscareComponent comp, float frameTime)
    {
        if (_timing.CurTime < comp.NextStepTime)
            return;

        var currentPos = _xform.GetWorldPosition(uid);
        var targetPos = comp.JumpTarget;
        var direction = (targetPos - currentPos).Normalized();
        var distanceRemaining = (targetPos - currentPos).Length();

        if (distanceRemaining < 0.1f || comp.CurrentStep >= comp.TotalSteps)
        {
            if (distanceRemaining >= LandClose && HasComp<Content.Shared.Throwing.ThrownItemComponent>(uid) && _timing.CurTime < comp.NextStepTime + LandWait)
                return;

            comp.MovingToJumpTarget = false;
            Land(uid, comp, currentPos);
            return;
        }

        var stepDistance = MathF.Min(comp.JumpDistance, distanceRemaining);

        _throwing.TryThrow(uid, direction * stepDistance, comp.JumpPower, user: uid, pushbackRatio: 0);
        _movementMod.TryUpdateMovementSpeedModDuration(uid, "JumpscareSlowdownStatusEffect", comp.SlowdownTime, 0.5f, 0.5f);

        comp.CurrentStep = comp.TotalSteps;
        comp.NextStepTime = _timing.CurTime + TimeSpan.FromSeconds(comp.StepInterval);
    }

    private void Land(EntityUid uid, JumpscareComponent comp, Vector2 position)
    {
        if (comp.RecoveryTime > TimeSpan.Zero)
            _movementMod.TryUpdateMovementSpeedModDuration(uid, "JumpscareSlowdownStatusEffect", comp.RecoveryTime, 0.3f, 0.3f);

        if (comp.Away || comp.HitRange <= 0f || comp.LungeTarget is not { } victim || TerminatingOrDeleted(victim))
            return;

        var offset = _xform.GetWorldPosition(victim) - position;
        if (offset.Length() > comp.HitRange)
            return;

        _damage.TryChangeDamage(victim, comp.ChargeDamage, origin: uid);
        if (comp.HitKnockdown > TimeSpan.Zero)
            _stunSystem.TryKnockdown(victim, comp.HitKnockdown, false);

        if (comp.HitThrow > 0f && offset.LengthSquared() > 0.0001f)
            _throwing.TryThrow(victim, Vector2.Normalize(offset) * comp.HitThrow, 8f, uid, recoil: false);
    }

    private EntityUid? CheckHumanTarget(EntityUid uid, JumpscareComponent component)
    {
        if (TryComp<HTNComponent>(uid, out var htn))
            return CheckHtnTarget(uid, component, htn);

        var closestDistance = float.MaxValue;
        EntityUid? target = null;

        var entities = new HashSet<Entity<MobStateComponent>>();
        var xform = Transform(uid);
        var mapCoords = _xform.ToMapCoordinates(xform.Coordinates);
        _lookup.GetEntitiesInRange(mapCoords, component.AttackRadius, entities, LookupFlags.Dynamic);

        foreach (var entity in entities)
        {
            if (!HasComp<HumanoidAppearanceComponent>(entity))
                continue;

            if (!_mobState.IsAlive(entity, entity.Comp))
                continue;

            var dist = (_xform.GetWorldPosition(uid) - _xform.GetWorldPosition(entity)).Length();
            if (dist >= closestDistance)
                continue;

            target = entity;
            closestDistance = dist;
        }

        return target;
    }

    private EntityUid? CheckHtnTarget(EntityUid uid, JumpscareComponent component, HTNComponent htn)
    {
        if (!htn.Blackboard.TryGetValue<EntityUid>("Target", out var target, EntityManager) ||
            TerminatingOrDeleted(target) ||
            !HasComp<HumanoidAppearanceComponent>(target) ||
            !TryComp<MobStateComponent>(target, out var state) ||
            !_mobState.IsAlive(target, state))
        {
            return null;
        }

        var dist = (_xform.GetWorldPosition(uid) - _xform.GetWorldPosition(target)).Length();

        if (dist > component.AttackRadius || !_examine.InRangeUnOccluded(uid, target, component.AttackRadius) || !LungeClear(uid, target, component))
            return null;

        return target;
    }

    private bool LungeClear(EntityUid uid, EntityUid target, JumpscareComponent component)
    {
        if (!TryComp<PhysicsComponent>(uid, out var physics))
            return true;

        var bounds = _lookup.GetWorldAABB(uid);
        var radius = MathF.Min(bounds.Width, bounds.Height) / 2f;
        var from = _xform.GetMapCoordinates(uid);
        var targetPos = _xform.GetWorldPosition(target);
        if (!component.Away)
        {
            var toward = targetPos - from.Position;
            var landing = toward.LengthSquared() > 0.0001f ? from.Offset(toward + Vector2.Normalize(toward) * LungeOvershoot) : from;
            return _steering.CanBeeline(uid, target, radius, (CollisionGroup) physics.CollisionMask) &&
                   !_chasms.LineCrosses(uid, _xform.ToCoordinates(landing), radius);
        }

        var direction = AwayFrom(from.Position, targetPos);
        return _steering.CanWalk(uid, from, from.Offset(direction * component.JumpDistance), radius, (CollisionGroup) _physics.GetHardCollision(uid).Mask) &&
               !_chasms.LineCrosses(uid, _xform.ToCoordinates(from.Offset(direction * (component.JumpDistance + LungeOvershoot))), radius);
    }

    private static Vector2 AwayFrom(Vector2 self, Vector2 threat)
    {
        var away = self - threat;
        return away.LengthSquared() > 0.0001f ? Vector2.Normalize(away) : Vector2.UnitX;
    }
}
