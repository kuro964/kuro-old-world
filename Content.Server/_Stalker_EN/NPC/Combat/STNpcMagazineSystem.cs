using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Combat;

public sealed class STNpcMagazineSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly PredictedBatterySystem _battery = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<STNpcMagazineComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<STNpcMagazineComponent, AttemptShootEvent>(OnAttemptShoot);
        SubscribeLocalEvent<STNpcMagazineComponent, GunShotEvent>(OnShot);
    }

    private void OnMapInit(Entity<STNpcMagazineComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.Rounds = _random.Next(ent.Comp.Capacity / 2, ent.Comp.Capacity + 1);
        ent.Comp.BurstLeft = _random.Next(ent.Comp.BurstMin, ent.Comp.BurstMax + 1);
        PlanEarlyReload(ent.Comp);
    }

    private void OnAttemptShoot(Entity<STNpcMagazineComponent> ent, ref AttemptShootEvent args)
    {
        var now = _timing.CurTime;
        if (now < ent.Comp.ReloadEnd || now < ent.Comp.PauseUntil)
            args.Cancelled = true;
    }

    private void OnShot(Entity<STNpcMagazineComponent> ent, ref GunShotEvent args)
    {
        var now = _timing.CurTime;
        var fired = Math.Max(1, args.Ammo.Count);
        ent.Comp.Rounds -= fired;
        ent.Comp.LastShot = now;
        ent.Comp.LullChecked = false;

        ent.Comp.BurstLeft -= fired;
        if (ent.Comp.BurstLeft <= 0)
        {
            ent.Comp.PauseUntil = now + _random.Next(ent.Comp.BurstPauseMin, ent.Comp.BurstPauseMax);
            ent.Comp.BurstLeft = _random.Next(ent.Comp.BurstMin, ent.Comp.BurstMax + 1);
        }

        if (TryComp<PredictedBatteryComponent>(ent, out var battery))
            _battery.SetCharge((ent, battery), battery.MaxCharge);

        if (TryComp<BasicEntityAmmoProviderComponent>(ent, out var basic) && basic.Capacity is { } capacity)
            _gun.UpdateBasicEntityAmmoCount(ent, capacity, basic);

        if (ent.Comp.Rounds <= ent.Comp.ReloadAt)
            Reload(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<STNpcMagazineComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            var quietSince = comp.PauseUntil > comp.LastShot ? comp.PauseUntil : comp.LastShot;
            if (comp.LullChecked || now < comp.ReloadEnd || now - quietSince < comp.LullTime)
                continue;

            comp.LullChecked = true;

            if (comp.Rounds < comp.Capacity * comp.EarlyReloadBelow && _random.Prob(comp.EarlyReloadChance))
                Reload((uid, comp));
        }
    }

    private void Reload(Entity<STNpcMagazineComponent> ent)
    {
        var jitter = ent.Comp.ReloadJitter.TotalSeconds;
        ent.Comp.ReloadEnd = _timing.CurTime + ent.Comp.ReloadTime + TimeSpan.FromSeconds(_random.NextDouble(-jitter, jitter));
        ent.Comp.Rounds = ent.Comp.Capacity;
        PlanEarlyReload(ent.Comp);
        _audio.PlayPvs(ent.Comp.ReloadSound, ent);
    }

    private void PlanEarlyReload(STNpcMagazineComponent comp)
    {
        var below = Math.Max(2, (int) (comp.Capacity * comp.EarlyReloadBelow));
        comp.ReloadAt = _random.Prob(comp.EarlyReloadChance) ? _random.Next(1, below) : 0;
    }
}
