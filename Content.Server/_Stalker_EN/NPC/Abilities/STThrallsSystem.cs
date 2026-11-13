using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server._Stalker_EN.NPC.Brain;
using Content.Server.Stunnable;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Content.Shared.Popups;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Abilities;

[RegisterComponent]
public sealed partial class STThrallComponent : Component
{
    [ViewVariables]
    public TimeSpan Until;

    [ViewVariables]
    public float Speed = 1f;
}

[RegisterComponent]
public sealed partial class STThrallMasterComponent : Component
{
    [DataField]
    public HashSet<string> Groups = new();

    [DataField]
    public float Radius = 25f;

    [DataField]
    public TimeSpan Slump = TimeSpan.FromSeconds(4);

    [DataField]
    public TimeSpan Interval = TimeSpan.FromSeconds(2);

    [DataField]
    public float Speed = 1.4f;

    [DataField]
    public LocId? LinkPopup = "st-thrall-linked";

    [ViewVariables]
    public TimeSpan Next;
}

public sealed class STThrallsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly STAwarenessSystem _awareness = default!;
    [Dependency] private readonly STBrainSystem _brain = default!;
    [Dependency] private readonly StunSystem _stun = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly Content.Shared.Movement.Systems.MovementSpeedModifierSystem _speed = default!;

    private readonly HashSet<Entity<STAlertGroupComponent>> _thralls = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<STThrallMasterComponent, MobStateChangedEvent>(OnMobState);
        SubscribeLocalEvent<STThrallComponent, Content.Shared.Movement.Systems.RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
    }

    private void OnRefreshSpeed(Entity<STThrallComponent> ent, ref Content.Shared.Movement.Systems.RefreshMovementSpeedModifiersEvent args)
    {
        args.ModifySpeed(ent.Comp.Speed, ent.Comp.Speed);
    }

    private void Free(EntityUid thrall)
    {
        if (!RemComp<STThrallComponent>(thrall))
            return;

        _speed.RefreshMovementSpeedModifiers(thrall);
    }

    private void Gather(EntityUid master, STThrallMasterComponent comp)
    {
        _thralls.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(master), comp.Radius, _thralls);
        _thralls.RemoveWhere(thrall => thrall.Owner == master ||
                                       !comp.Groups.Contains(thrall.Comp.Group) ||
                                       !_mobState.IsAlive(thrall.Owner) ||
                                       !_faction.IsEntityFriendly(master, thrall.Owner));
    }

    private void OnMobState(Entity<STThrallMasterComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;

        Gather(ent, ent.Comp);
        foreach (var thrall in _thralls)
        {
            Free(thrall.Owner);
            _stun.TryUpdateParalyzeDuration(thrall.Owner, ent.Comp.Slump);
            _popup.PopupEntity(Loc.GetString("st-thrall-slumps"), thrall.Owner, PopupType.Medium);
        }
    }

    public override void Update(float frameTime)
    {
        var now = _timing.CurTime;
        var linked = EntityQueryEnumerator<STThrallComponent>();
        while (linked.MoveNext(out var thrall, out var link))
        {
            if (now >= link.Until)
                Free(thrall);
        }

        var query = EntityQueryEnumerator<STThrallMasterComponent, STBrainComponent, STAwarenessComponent>();
        while (query.MoveNext(out var uid, out var comp, out var brain, out var awareness))
        {
            if (now < comp.Next)
                continue;

            comp.Next = now + comp.Interval;
            if (brain.Behaviour != STBehaviour.Fight ||
                awareness.Target is not { } target ||
                now - awareness.LastSeen > comp.Interval ||
                !_mobState.IsAlive(uid))
            {
                continue;
            }

            Gather(uid, comp);
            foreach (var thrall in _thralls)
            {
                if (!_awareness.IsHostile(thrall.Owner, target))
                    continue;

                _awareness.ForceNotice(thrall.Owner, target);
                _brain.Wake(thrall.Owner);
                if (!TryComp<STThrallComponent>(thrall.Owner, out var bond))
                {
                    bond = AddComp<STThrallComponent>(thrall.Owner);
                    bond.Speed = comp.Speed;
                    _speed.RefreshMovementSpeedModifiers(thrall.Owner);
                    if (comp.LinkPopup is { } popup)
                        _popup.PopupEntity(Loc.GetString(popup), thrall.Owner, PopupType.MediumCaution);
                }

                bond.Until = now + comp.Interval + comp.Interval;
            }
        }
    }
}
