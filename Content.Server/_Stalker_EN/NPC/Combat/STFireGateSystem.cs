using System.Numerics;
using Content.Server._Stalker_EN.NPC.Brain;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Combat;

public sealed class STFireGateSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly STShotLineSystem _shotLine = default!;
    [Dependency] private readonly STFriendlyFireSystem _friendlyFire = default!;

    private const double SettledSlack = 0.02;

    public bool Allows(EntityUid uid, GunComponent gun, MapCoordinates muzzle, Vector2 aim, float distance, EntityUid? target)
    {
        var line = aim - muzzle.Position;
        var length = line.Length();
        if (length < 0.01f)
            return false;

        var dir = line / length;
        var spread = _shotLine.NextSpread(gun);
        var far = new MapCoordinates(muzzle.Position + dir * distance, muzzle.MapId);

        var allowed = !_friendlyFire.IsAllyInLine(uid, muzzle.Position + dir * MathF.Max(length, distance), spread, target) &&
               !_shotLine.GlassInWay(muzzle, new MapCoordinates(muzzle.Position + dir * MathF.Min(length, distance), muzzle.MapId)) &&
               !_shotLine.WallInSpread(uid, muzzle, far, target, spread) &&
               !(spread.Theta > gun.MinAngleModified.Theta + gun.AngleIncreaseModified.Theta + SettledSlack &&
                 _shotLine.WallInSpread(uid, muzzle, far, target, spread, STShotLineSystem.SpreadWaitRange)) &&
               !_shotLine.GlassInSpread(muzzle, far, spread) &&
               !_shotLine.CoverInSpread(muzzle, far, spread);

        if (TryComp<STBrainComponent>(uid, out var brain))
            brain.DeniedSince = allowed ? null : brain.DeniedSince ?? _timing.CurTime;

        return allowed;
    }
}
