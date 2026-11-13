using System.Numerics;
using Content.Server._Stalker_EN.NPC.Awareness;
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
    [Dependency] private readonly STAwarenessSystem _awareness = default!;

    private const float BodyRadius = 0.35f;

    private readonly HashSet<Entity<NpcFactionMemberComponent>> _nearby = new();

    public bool IsAllyInLine(EntityUid shooter, EntityUid target, Angle spread)
    {
        return IsAllyInLine(shooter, _transform.GetWorldPosition(shooter), _transform.GetWorldPosition(target), spread, target);
    }

    public bool IsAllyInLine(EntityUid shooter, Vector2 to, Angle spread, EntityUid? target = null)
    {
        return IsAllyInLine(shooter, _transform.GetWorldPosition(shooter), to, spread, target);
    }

    public bool IsAllyInLine(EntityUid shooter, Vector2 from, Vector2 to, Angle spread, EntityUid? target = null)
    {
        if (!TryComp<STHoldFireForAlliesComponent>(shooter, out var hold))
            return false;

        var line = to - from;
        var length = line.Length();
        if (length < 0.01f)
            return false;

        var dir = line / length;
        var halfSpread = (float) spread.Theta / 2f;

        _nearby.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapId(shooter), from + line / 2f, length / 2f + hold.LineWidth * 2f + hold.TouchRange, _nearby);

        foreach (var other in _nearby)
        {
            if (other.Owner == shooter || other.Owner == target || _mobState.IsIncapacitated(other.Owner) || !Protected(shooter, other.Owner))
                continue;

            var offset = _transform.GetWorldPosition(other.Owner) - from;
            if (offset.Length() < hold.TouchRange)
                return true;

            var along = Vector2.Dot(offset, dir);
            if (along <= 0f || along >= length + hold.LineWidth)
                continue;

            var lateral = (offset - dir * along).Length();
            if (lateral < hold.LineWidth || MathF.Atan2(MathF.Max(0f, lateral - BodyRadius - STShotLineSystem.BulletHalfWidth), along) < halfSpread)
                return true;
        }

        return false;
    }

    private bool Protected(EntityUid shooter, EntityUid other)
    {
        return _faction.IsEntityFriendly(shooter, other) || !_awareness.IsHostile(shooter, other);
    }
}
