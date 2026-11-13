using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server._Stalker_EN.NPC.Brain;
using Content.Shared.Mobs;
using Content.Shared.Popups;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Abilities;

[RegisterComponent]
public sealed partial class STIllusionsComponent : Component
{
    [DataField]
    public EntProtoId Proto = "MutantPsidogCopy";

    [DataField]
    public TimeSpan Interval = TimeSpan.FromSeconds(4);

    [DataField]
    public int MaxAlive = 4;

    [DataField]
    public int PerSpawn = 2;

    [ViewVariables]
    public TimeSpan NextSpawn;

    [ViewVariables]
    public readonly List<EntityUid> Copies = new();
}

public sealed class STIllusionsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly STAwarenessSystem _awareness = default!;
    [Dependency] private readonly STBrainSystem _brain = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<STIllusionsComponent, MobStateChangedEvent>(OnMobState);
        SubscribeLocalEvent<STIllusionsComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnMobState(Entity<STIllusionsComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Alive)
            Dispel(ent.Comp);
    }

    private void OnShutdown(Entity<STIllusionsComponent> ent, ref ComponentShutdown args)
    {
        Dispel(ent.Comp);
    }

    private void Dispel(STIllusionsComponent comp)
    {
        foreach (var copy in comp.Copies)
        {
            if (!TerminatingOrDeleted(copy))
                QueueDel(copy);
        }

        comp.Copies.Clear();
    }

    public override void Update(float frameTime)
    {
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<STIllusionsComponent, STBrainComponent, STAwarenessComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var brain, out var awareness, out var xform))
        {
            if (now < comp.NextSpawn || brain.Behaviour != STBehaviour.Fight || awareness.Target is not { } target)
                continue;

            comp.NextSpawn = now + comp.Interval;
            comp.Copies.RemoveAll(copy => TerminatingOrDeleted(copy));
            if (comp.Copies.Count >= comp.MaxAlive)
                continue;

            for (var i = 0; i < comp.PerSpawn && comp.Copies.Count < comp.MaxAlive; i++)
            {
                var spawned = SpawnAtPosition(comp.Proto, xform.Coordinates);
                comp.Copies.Add(spawned);
                _awareness.ForceNotice(spawned, target);
                _brain.Wake(spawned);
            }

            _popup.PopupEntity(Loc.GetString("st-psidog-splits"), uid, PopupType.MediumCaution);
        }
    }
}
