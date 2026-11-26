using Content.Server.Atmos.Rotting;
using Content.Server.EUI;
using Content.Server.Ghost;
using Content.Server._Stalker_EN.NPC.Brain;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Traits.Assorted;
using Robust.Server.Player;

namespace Content.Server._Stalker_EN.NPC.Hive;

public sealed partial class STHiveSystem
{
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly RottingSystem _rotting = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly EuiManager _eui = default!;

    private static readonly TimeSpan ReviveWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MedicPopupGap = TimeSpan.FromSeconds(10);
    private const float ReviveSeconds = 60f;
    private const float TendSeconds = 15f;
    private const float TendReach = 1.5f;
    private const float ReviveMargin = 5f;

    private bool Savable(EntityUid leader, STHiveLeaderComponent comp, TimeSpan now)
    {
        return comp.DiedAt is { } diedAt &&
               now - diedAt <= ReviveWindow &&
               !_rotting.IsRotten(leader) &&
               !HasComp<UnrevivableComponent>(leader) &&
               _mind.TryGetMind(leader, out _, out _);
    }

    private bool SquadFighting(STHiveLeaderComponent comp)
    {
        foreach (var follower in comp.Followers)
        {
            if (TryComp<STBrainComponent>(follower, out var brain) && brain.Behaviour is STBehaviour.Fight or STBehaviour.Finish)
                return true;
        }

        return false;
    }

    private bool IsMedic(EntityUid follower, EntityUid leader, STHiveLeaderComponent comp)
    {
        if (comp.Medic is not { } medic ||
            !comp.Followers.Contains(medic) ||
            !_mobState.IsAlive(medic) ||
            TryComp<STHiveFollowerComponent>(medic, out var medicFollower) && medicFollower.Hold)
        {
            comp.Medic = PickMedic(leader, comp);
        }

        return comp.Medic == follower;
    }

    private EntityUid? PickMedic(EntityUid leader, STHiveLeaderComponent comp)
    {
        var leaderAt = _transform.GetMapCoordinates(leader);
        EntityUid? best = null;
        var bestDistance = float.MaxValue;
        foreach (var follower in comp.Followers)
        {
            if (!_mobState.IsAlive(follower) ||
                !TryComp<STHiveFollowerComponent>(follower, out var followerComp) ||
                followerComp.Hold)
            {
                continue;
            }

            var at = _transform.GetMapCoordinates(follower);
            if (at.MapId != leaderAt.MapId)
                continue;

            var distance = (at.Position - leaderAt.Position).LengthSquared();
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = follower;
        }

        return best;
    }

    private void Tend(EntityUid medic, EntityUid leader, STHiveLeaderComponent comp, TimeSpan now)
    {
        var medicAt = _transform.GetMapCoordinates(medic);
        var leaderAt = _transform.GetMapCoordinates(leader);
        if (medicAt.MapId != leaderAt.MapId || (medicAt.Position - leaderAt.Position).Length() > TendReach)
            return;

        if (!TryComp<DamageableComponent>(leader, out var damageable) ||
            !_thresholds.TryGetThresholdForState(leader, MobState.Critical, out var critical) ||
            !_thresholds.TryGetThresholdForState(leader, MobState.Dead, out var dead))
        {
            return;
        }

        var tick = (float) ThinkInterval.TotalSeconds;
        if (_mobState.IsCritical(leader))
        {
            var healPerSecond = (dead.Value - critical.Value).Float() / TendSeconds;
            Heal((leader, damageable), healPerSecond * tick, medic);
            AnnounceMedic(medic, leader, comp, now, "st-hive-tending");
            return;
        }

        comp.ReviveProgress += tick;
        AnnounceMedic(medic, leader, comp, now, "st-hive-reviving");
        if (comp.ReviveProgress < ReviveSeconds)
            return;

        comp.ReviveProgress = 0f;
        Revive((leader, damageable), dead.Value, medic);
    }

    private void Revive(Entity<DamageableComponent> leader, FixedPoint2 deadThreshold, EntityUid medic)
    {
        var excess = (leader.Comp.TotalDamage - deadThreshold).Float() + ReviveMargin;
        if (excess > 0f)
            Heal(leader, excess, medic);

        if (leader.Comp.TotalDamage >= deadThreshold)
            return;

        _mobState.ChangeMobState(leader, MobState.Critical, origin: medic);
        _popup.PopupEntity(Loc.GetString("st-hive-revived", ("medic", medic), ("leader", leader.Owner)), leader);

        if (_mind.TryGetMind(leader, out _, out var mind) &&
            mind.CurrentEntity != leader.Owner &&
            _players.TryGetSessionById(mind.UserId, out var session))
        {
            _eui.OpenEui(new ReturnToBodyEui(mind, _mind, _players), session);
        }
    }

    private void Heal(Entity<DamageableComponent> target, float amount, EntityUid medic)
    {
        var total = target.Comp.TotalDamage.Float();
        if (total <= 0f || amount <= 0f)
            return;

        var share = MathF.Min(1f, amount / total);
        _damageable.TryChangeDamage((target, target.Comp), -(target.Comp.Damage * share), ignoreResistances: true, interruptsDoAfters: false, origin: medic);
    }

    private void AnnounceMedic(EntityUid medic, EntityUid leader, STHiveLeaderComponent comp, TimeSpan now, string message)
    {
        if (now < comp.NextMedicPopup)
            return;

        comp.NextMedicPopup = now + MedicPopupGap;
        _popup.PopupEntity(Loc.GetString(message, ("medic", medic), ("leader", leader)), leader);
    }
}
