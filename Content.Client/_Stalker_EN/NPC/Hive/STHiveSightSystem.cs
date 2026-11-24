using System.Numerics;
using Content.Shared._Stalker_EN.NPC.Hive;
using Content.Shared.Examine;
using Content.Shared.Mobs.Components;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client._Stalker_EN.NPC.Hive;

public sealed class STHiveSightSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(0.2);
    private const float OwnSight = 30f;
    public const int MaxLifts = 10;

    public readonly HashSet<EntityUid> Seen = new();
    public readonly HashSet<EntityUid> Behind = new();
    public Color Tint = Color.White;

    private readonly HashSet<Entity<MobStateComponent>> _near = new();
    private readonly List<MapCoordinates> _lifts = new();
    private float _lift;
    private STHiveSightOverlay _overlay = default!;
    private TimeSpan _next;

    public override void Initialize()
    {
        base.Initialize();
        _overlay = new STHiveSightOverlay();
        _overlays.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlays.RemoveOverlay(_overlay);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        _lifts.Clear();
        if (_player.LocalEntity is not { } me || !TryComp<STHiveSightComponent>(me, out var sight))
        {
            Seen.Clear();
            Behind.Clear();
            return;
        }

        _lift = sight.Lift;
        foreach (var eye in sight.Eyes)
        {
            if (!TerminatingOrDeleted(eye) && _lifts.Count < MaxLifts)
                _lifts.Add(_transform.GetMapCoordinates(eye));
        }

        if (_timing.RealTime < _next)
            return;

        _next = _timing.RealTime + Interval;
        Seen.Clear();
        Behind.Clear();
        Tint = sight.Tint;
        foreach (var eye in sight.Eyes)
        {
            if (TerminatingOrDeleted(eye))
                continue;

            Seen.Add(eye);
            _near.Clear();
            _lookup.GetEntitiesInRange(Transform(eye).Coordinates, sight.Range, _near);
            foreach (var mob in _near)
            {
                if (mob.Owner != me && !Seen.Contains(mob.Owner) && _examine.InRangeUnOccluded(eye, mob.Owner, sight.Range))
                    Seen.Add(mob.Owner);
            }
        }

        foreach (var uid in Seen)
        {
            if (!_examine.InRangeUnOccluded(me, uid, OwnSight))
                Behind.Add(uid);
        }
    }

    public bool Lifted(EntityUid uid, MapId map, Vector2 position)
    {
        if (Seen.Contains(uid))
            return true;

        foreach (var lift in _lifts)
        {
            if (lift.MapId == map && (lift.Position - position).LengthSquared() <= _lift * _lift)
                return true;
        }

        return false;
    }

    public int LiftPoints(IClydeViewport viewport, MapId map, Vector2[] into, out float radius)
    {
        radius = 0f;
        var count = 0;
        foreach (var lift in _lifts)
        {
            if (lift.MapId != map || count >= into.Length)
                continue;

            var local = viewport.WorldToLocal(lift.Position);
            radius = (viewport.WorldToLocal(lift.Position + new Vector2(_lift, 0f)) - local).Length();
            local.Y = viewport.Size.Y - local.Y;
            into[count++] = local;
        }

        return count;
    }
}
