using Content.Server._DZ.FarGunshot;
using Content.Server.NPC.Systems;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Mobs.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Awareness;

public readonly record struct STHeardPulse(MapCoordinates Origin, float Range, float WallRange, TimeSpan Time, bool Explosion, List<(EntityUid Listener, string Result)> Listeners);

public sealed class STHearingSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly STAwarenessSystem _awareness = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    private const float GunshotRange = 25f;
    private const float MinRange = 3f;
    private const float ExplosionReference = 50f;
    private const float ExplosionMaxRange = 45f;
    private const float WallFactor = 0.5f;
    private const float ExplosionWallFactor = 0.75f;
    private const int MaxListeners = 8;
    private const int MaxRays = 16;
    private const float GuessOffset = 2f;
    private const float HeardError = 0.1f;
    private const float MaxHeardError = 3f;
    private const float MaxHearingFactor = 2f;
    private static readonly TimeSpan PulseInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan QuietPulseInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ChainBlock = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ListenCooldown = TimeSpan.FromSeconds(20);

    private readonly Dictionary<EntityUid, TimeSpan> _nextPulse = new();
    private readonly Dictionary<EntityUid, TimeSpan> _heardAt = new();
    private readonly HashSet<Entity<STAwarenessComponent>> _listeners = new();
    private readonly List<(EntityUid Uid, float Distance)> _candidates = new();

    public readonly List<STHeardPulse> RecentPulses = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GunComponent, GunShotEvent>(OnGunShot);
        SubscribeLocalEvent<STAwarenessComponent, DamageChangedEvent>(OnDamaged, before: new[] { typeof(MobThresholdSystem) }, after: new[] { typeof(NPCRetaliationSystem) });
        SubscribeLocalEvent<STExplosionHeardEvent>(OnExplosion);
    }

    private void OnGunShot(Entity<GunComponent> gun, ref GunShotEvent args)
    {
        var shooter = args.User;
        var now = _timing.CurTime;
        if (_nextPulse.TryGetValue(shooter, out var next) && now < next ||
            _heardAt.TryGetValue(shooter, out var heard) && now - heard < ChainBlock)
        {
            return;
        }

        Prune(_nextPulse, now, TimeSpan.Zero);
        Prune(_heardAt, now, ListenCooldown);

        var volume = gun.Comp.SoundGunshotModified?.Params.Volume ?? 0f;
        var loudness = MathF.Pow(10f, volume / 20f);
        if (TryComp<FarGunshotComponent>(gun, out var far))
            loudness = MathF.Min(loudness, far.SilencerDecrease ?? far.BaseSilencerDecrease);

        var range = MathF.Max(MinRange, GunshotRange * loudness);
        var alerted = Pulse(shooter, _transform.GetMapCoordinates(shooter), range, Transform(shooter).Coordinates, WallFactor, false);
        _nextPulse[shooter] = now + (alerted > 0 ? PulseInterval : QuietPulseInterval);
    }

    private void OnExplosion(ref STExplosionHeardEvent args)
    {
        if (args.Source is not { } source || TerminatingOrDeleted(source))
            return;

        var range = Math.Clamp(GunshotRange * MathF.Sqrt(args.Intensity / ExplosionReference), GunshotRange, ExplosionMaxRange);
        Pulse(source, args.Epicenter, range, _transform.ToCoordinates(args.Epicenter), ExplosionWallFactor, true);
    }

    private int Pulse(EntityUid source, MapCoordinates origin, float range, EntityCoordinates where, float wallFactor, bool explosion)
    {
        _listeners.Clear();
        _lookup.GetEntitiesInRange(origin, range * MaxHearingFactor, _listeners);

        var now = _timing.CurTime;
        var results = new List<(EntityUid Listener, string Result)>();
        _candidates.Clear();
        foreach (var listener in _listeners)
        {
            if (listener.Owner == source)
                continue;

            var hunting = _awareness.Hunting(listener.Comp, source);
            var reason = !hunting && _heardAt.TryGetValue(listener.Owner, out var heard) && now - heard < ListenCooldown ? "heard one recently"
                : !hunting && !_awareness.CanAlert(listener.Owner, listener.Comp) ? "busy"
                : !_awareness.IsHostile(listener.Owner, source) ? "not hostile"
                : null;

            if (reason != null)
            {
                results.Add((listener.Owner, reason));
                continue;
            }

            var heardFrom = (_transform.GetWorldPosition(listener.Owner) - origin.Position).Length() / MathF.Min(MathF.Max(listener.Comp.HearingFactor, 0.1f), MaxHearingFactor);
            if (heardFrom > range)
                continue;

            _candidates.Add((listener.Owner, heardFrom));
        }

        _candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var alerted = 0;
        var rays = 0;
        foreach (var (listener, distance) in _candidates)
        {
            if (distance > range * wallFactor)
            {
                if (rays++ >= MaxRays)
                    break;

                if (!_examine.InRangeUnOccluded(listener, origin, range))
                {
                    results.Add((listener, "behind a wall"));
                    continue;
                }
            }

            var guess = where.Offset(_random.NextVector2(MathF.Min(distance * HeardError, MaxHeardError)));
            if (_awareness.TryUpdateHeard(listener, source, guess))
            {
                results.Add((listener, "heard, new last known"));
                continue;
            }

            if (!_awareness.TryAlert(listener, source, guess))
            {
                results.Add((listener, "busy"));
                continue;
            }

            results.Add((listener, "heard"));
            _heardAt[listener] = now;
            if (++alerted >= MaxListeners)
                break;
        }

        RecentPulses.Add(new STHeardPulse(origin, range, range * wallFactor, now, explosion, results));
        if (RecentPulses.Count > 16)
            RecentPulses.RemoveAt(0);

        return alerted;
    }

    private static void Prune(Dictionary<EntityUid, TimeSpan> times, TimeSpan now, TimeSpan keep)
    {
        if (times.Count <= 256)
            return;

        foreach (var (uid, time) in times)
        {
            if (now - time > keep)
                times.Remove(uid);
        }
    }

    private void OnDamaged(Entity<STAwarenessComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased ||
            args.Origin is not { } attacker ||
            attacker == ent.Owner ||
            !_mobState.IsAlive(ent) ||
            !_awareness.IsHostile(ent.Owner, attacker))
            return;

        var where = Transform(attacker).Coordinates;
        if (_examine.InRangeUnOccluded(ent.Owner, attacker, ExamineSystemShared.MaxRaycastRange))
            _awareness.ForceNotice(ent.Owner, attacker);
        else
            where = where.Offset(_random.NextVector2(GuessOffset));

        if (_awareness.TryUpdateHeard(ent.Owner, attacker, where) || !_awareness.TryAlert(ent.Owner, attacker, where))
            return;

        var spotted = new STTargetSpottedEvent(attacker, where);
        RaiseLocalEvent(ent, ref spotted);
    }
}
