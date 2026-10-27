using System.Numerics;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;

namespace Content.Server._Stalker_EN.NPC.Combat;

public sealed class STFriendlyFireSystem : EntitySystem
{
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;

    public bool IsAllyInLine(EntityUid shooter, EntityUid target, Angle spread)
    {
        if (!TryComp<STHoldFireForAlliesComponent>(shooter, out var hold))
            return false;

        var from = _transform.GetWorldPosition(shooter);
        var line = _transform.GetWorldPosition(target) - from;
        var length = line.Length();
        if (length < 0.01f)
            return false;

        var dir = line / length;
        var halfSpread = (float) spread.Theta / 2f;

        foreach (var ally in _lookup.GetEntitiesInRange<NpcFactionMemberComponent>(_transform.GetMapCoordinates(shooter), length))
        {
            if (ally.Owner == shooter || ally.Owner == target || _mobState.IsDead(ally.Owner) || !_faction.IsEntityFriendly(shooter, ally.Owner))
                continue;

            var offset = _transform.GetWorldPosition(ally.Owner) - from;
            if (offset.Length() < hold.TouchRange)
                return true;

            var along = Vector2.Dot(offset, dir);
            if (along <= 0f || along >= length)
                continue;

            var lateral = (offset - dir * along).Length();
            if (lateral < hold.LineWidth || MathF.Atan2(lateral, along) < halfSpread)
                return true;
        }

        return false;
    }
}
