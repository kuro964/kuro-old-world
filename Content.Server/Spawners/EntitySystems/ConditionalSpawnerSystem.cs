using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.Spawners.Components;
using Content.Shared.EntityTable;
using Content.Shared.GameTicking.Components;
using JetBrains.Annotations;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Prototypes; // ST:OW

namespace Content.Server.Spawners.EntitySystems
{
    [UsedImplicitly]
    public sealed class ConditionalSpawnerSystem : EntitySystem
    {
        [Dependency] private readonly IRobustRandom _robustRandom = default!;
        [Dependency] private readonly GameTicker _ticker = default!;
        [Dependency] private readonly EntityTableSystem _entityTable = default!;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<GameRuleStartedEvent>(OnRuleStarted);
            SubscribeLocalEvent<ConditionalSpawnerComponent, MapInitEvent>(OnCondSpawnMapInit);
            SubscribeLocalEvent<RandomSpawnerComponent, MapInitEvent>(OnRandSpawnMapInit);
            SubscribeLocalEvent<EntityTableSpawnerComponent, MapInitEvent>(OnEntityTableSpawnMapInit);
        }

        private void OnCondSpawnMapInit(EntityUid uid, ConditionalSpawnerComponent component, MapInitEvent args)
        {
            TrySpawn(uid, component);
        }

        private void OnRandSpawnMapInit(EntityUid uid, RandomSpawnerComponent component, MapInitEvent args)
        {
            Spawn(uid, component);
            if (component.DeleteSpawnerAfterSpawn)
                QueueDel(uid);
        }

        private void OnEntityTableSpawnMapInit(Entity<EntityTableSpawnerComponent> ent, ref MapInitEvent args)
        {
            Spawn(ent);
            if (ent.Comp.DeleteSpawnerAfterSpawn && !TerminatingOrDeleted(ent) && Exists(ent))
                QueueDel(ent);
        }

        private void OnRuleStarted(ref GameRuleStartedEvent args)
        {
            var query = EntityQueryEnumerator<ConditionalSpawnerComponent>();
            while (query.MoveNext(out var uid, out var spawner))
            {
                RuleStarted(uid, spawner, args);
            }
        }

        public void RuleStarted(EntityUid uid, ConditionalSpawnerComponent component, GameRuleStartedEvent obj)
        {
            if (component.GameRules.Contains(obj.RuleId))
                Spawn(uid, component);
        }

        private void TrySpawn(EntityUid uid, ConditionalSpawnerComponent component)
        {
            if (component.GameRules.Count == 0)
            {
                Spawn(uid, component);
                return;
            }

            foreach (var rule in component.GameRules)
            {
                if (!_ticker.IsGameRuleActive(rule))
                    continue;
                Spawn(uid, component);
                return;
            }
        }

        private void Spawn(EntityUid uid, ConditionalSpawnerComponent component)
        {
            if (component.Chance != 1.0f && !_robustRandom.Prob(component.Chance))
                return;

            if (component.Prototypes.Count == 0)
            {
                Log.Warning($"Prototype list in ConditionalSpawnComponent is empty! Entity: {ToPrettyString(uid)}");
                return;
            }

            if (!Deleted(uid))
                Spawn(_robustRandom.Pick(component.Prototypes), Transform(uid).Coordinates);
        }
        
        // ST:OW begin
        private void Spawn(Entity<EntityTableSpawnerComponent> ent)
        {
            if (Deleted(ent))
                return;

            var spawns = _entityTable.GetSpawns(ent.Comp.Table);

            foreach (var prototype in spawns)
            {
                SpawnWithOffset(ent, prototype, ent.Comp.Offset);
            }
        }

        private void Spawn(EntityUid uid, RandomSpawnerComponent component)
        {
            // Base spawn chance behavior
            if (component.Chance != 1.0f && !_robustRandom.Prob(component.Chance))
                return;

            if (Deleted(uid))
                return;

            // Pick rarity from: Legendary -> Epic -> Rare -> Common
            List<EntProtoId> prototypes;

            if (component.LegendaryPrototypes.Count > 0 &&
                (component.LegendaryChance == 1.0f || _robustRandom.Prob(component.LegendaryChance)))
            {
                prototypes = component.LegendaryPrototypes;
            }
            else if (component.EpicPrototypes.Count > 0 &&
                     (component.EpicChance == 1.0f || _robustRandom.Prob(component.EpicChance)))
            {
                prototypes = component.EpicPrototypes;
            }
            else if (component.RarePrototypes.Count > 0 &&
                     (component.RareChance == 1.0f || _robustRandom.Prob(component.RareChance)))
            {
                prototypes = component.RarePrototypes;
            }
            else
            {
                if (component.Prototypes.Count == 0)
                {
                    Log.Warning($"Prototype list in RandomSpawnerComponent is empty! Entity: {ToPrettyString(uid)}");
                    return;
                }

                prototypes = component.Prototypes;
            }

            SpawnWithOffset(uid, _robustRandom.Pick(prototypes), component.Offset);
        }

        private void SpawnWithOffset(EntityUid uid, EntProtoId prototype, float offset)
        {
            var xOffset = _robustRandom.NextFloat(-offset, offset);
            var yOffset = _robustRandom.NextFloat(-offset, offset);

            var coordinates = Transform(uid).Coordinates.Offset(new Vector2(xOffset, yOffset));
            Spawn(prototype, coordinates);
        }
        // ST:OW end
    }
}


