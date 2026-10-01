using Content.Server.Fluids.EntitySystems;
using Content.Shared._Stalker.ZoneAnomaly.Components;
using Content.Shared._Stalker.ZoneAnomaly.Effects.Components;
using Content.Shared.Chemistry.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._Stalker.ZoneAnomaly.Effects.Systems;

public sealed class ZoneAnomalyEffectFoamSystem : EntitySystem
{
    [Dependency] private readonly SmokeSystem _smoke = default!;

    private readonly EntProtoId _foamPrototypeId = "ZoneAnomalyFoam"; // ST:OW

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ZoneAnomalyEffectFoamComponent, ZoneAnomalyActivateEvent>(OnActivate);
    }

    private void OnActivate(Entity<ZoneAnomalyEffectFoamComponent> effect, ref ZoneAnomalyActivateEvent args)
    {
        var solution = new Solution();
        solution.AddReagent(effect.Comp.Reagent, effect.Comp.ReagentAmount);

        var foam = Spawn(_foamPrototypeId, Transform(effect).MapPosition);
        _smoke.StartSmoke(foam, solution, effect.Comp.Duration, effect.Comp.Range);
    }
}
