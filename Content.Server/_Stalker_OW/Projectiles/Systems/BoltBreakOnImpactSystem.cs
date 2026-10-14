using Content.Shared._Stalker_OW.Projectiles.Components;
using Content.Shared.Projectiles;
using Robust.Shared.Random;
using Content.Server.Chemistry.EntitySystems;

namespace Content.Server._Stalker_OW.Projectiles.Systems;

/// <summary>
/// Bolts that are fired or thrown will roll for break chance after embedding in something
/// </summary>
public sealed class BoltBreakOnImpactSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly BoltBallisticsSystem _ballistics = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BoltBreakOnImpactComponent, EmbedEvent>(OnEmbed,
            after: [typeof(SolutionInjectOnCollideSystem)]);
    }

    private void OnEmbed(Entity<BoltBreakOnImpactComponent> ent, ref EmbedEvent args)
    {
        _ballistics.StopFlight(ent.Owner);
        if (TerminatingOrDeleted(ent.Owner) || !_random.Prob(ent.Comp.BreakChance))
            return;

        if (ent.Comp.BrokenPrototype is { } prototype)
            Spawn(prototype, _transform.GetMapCoordinates(ent));

        QueueDel(ent);
    }
}