using Content.Shared.Mobs;
using Content.Shared.NPC.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Brain;

[RegisterComponent]
public sealed partial class STBarkComponent : Component
{
    [DataField]
    public SoundSpecifier? Enemy;

    [DataField]
    public SoundSpecifier? Attack;

    [DataField]
    public SoundSpecifier? Cover;

    [DataField]
    public SoundSpecifier? Grenade;

    [DataField]
    public SoundSpecifier? Backup;

    [DataField]
    public SoundSpecifier? Kill;

    [DataField]
    public SoundSpecifier? Death;

    [DataField]
    public TimeSpan Cooldown = TimeSpan.FromSeconds(9);

    [DataField]
    public TimeSpan SquadGap = TimeSpan.FromSeconds(3.5);

    [DataField]
    public float SquadRange = 18f;

    [DataField]
    public TimeSpan CryMin = TimeSpan.FromSeconds(14);

    [DataField]
    public TimeSpan CryMax = TimeSpan.FromSeconds(26);

    [ViewVariables]
    public TimeSpan Next;

    [ViewVariables]
    public TimeSpan NextCry;

    [ViewVariables]
    public TimeSpan Last;

    [ViewVariables]
    public bool Fighting;
}

public sealed class STBarkSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;

    private readonly HashSet<Entity<STBarkComponent>> _squad = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<STBarkComponent, MobStateChangedEvent>(OnMobState);
    }

    private void OnMobState(Entity<STBarkComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead && args.OldMobState != MobState.Dead && ent.Comp.Death != null)
            _audio.PlayPvs(ent.Comp.Death, ent);
    }

    public void Tick(EntityUid uid, STBrainComponent brain, bool changed, TimeSpan now)
    {
        if (!TryComp<STBarkComponent>(uid, out var comp))
            return;

        var fighting = brain.Behaviour is STBehaviour.Fight or STBehaviour.Finish;
        if (!fighting)
        {
            comp.Fighting = false;
            return;
        }

        if (!comp.Fighting)
        {
            comp.Fighting = true;
            comp.NextCry = now + Between(comp.CryMin, comp.CryMax);
            Say(uid, comp, comp.Enemy, now);
            return;
        }

        if (changed && Line(comp, brain.Doing) is { } line && Say(uid, comp, line, now))
            return;

        if (brain.Behaviour != STBehaviour.Fight || now < comp.NextCry)
            return;

        comp.NextCry = now + Between(comp.CryMin, comp.CryMax);
        Say(uid, comp, comp.Attack, now);
    }

    private static SoundSpecifier? Line(STBarkComponent comp, string doing)
    {
        return doing switch
        {
            "throwing a grenade" => comp.Grenade,
            "moving to cover" or "reloading out of sight" or "taking cover from fire" => comp.Cover,
            "falling back, wounded" or "falling back to its post, squad down" => comp.Backup,
            "flanking" => comp.Attack,
            "finishing a downed target" => comp.Kill,
            _ => null,
        };
    }

    private TimeSpan Between(TimeSpan min, TimeSpan max)
    {
        return min + (max - min) * _random.NextFloat();
    }

    private bool Say(EntityUid uid, STBarkComponent comp, SoundSpecifier? sound, TimeSpan now)
    {
        if (sound == null || now < comp.Next)
            return false;

        _squad.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(uid), comp.SquadRange, _squad);
        foreach (var mate in _squad)
        {
            if (mate.Owner != uid && now - mate.Comp.Last < comp.SquadGap && _faction.IsEntityFriendly(uid, mate.Owner))
                return false;
        }

        comp.Last = now;
        comp.Next = now + comp.Cooldown * _random.NextFloat(0.8f, 1.4f);
        _audio.PlayPvs(sound, uid);
        return true;
    }
}
