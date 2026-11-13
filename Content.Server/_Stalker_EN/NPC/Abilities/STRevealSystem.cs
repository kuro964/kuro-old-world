using Content.Shared.Damage.Systems;
using Content.Shared.Stealth;
using Content.Shared.Stealth.Components;

namespace Content.Server._Stalker_EN.NPC.Abilities;

[RegisterComponent]
public sealed partial class STRevealComponent : Component
{
}

public sealed class STRevealSystem : EntitySystem
{
    [Dependency] private readonly SharedStealthSystem _stealth = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<STRevealComponent, DamageChangedEvent>(OnDamaged);
    }

    private void OnDamaged(Entity<STRevealComponent> ent, ref DamageChangedEvent args)
    {
        if (args.DamageIncreased && TryComp<StealthComponent>(ent, out var stealth))
            _stealth.SetVisibility(ent, 1f, stealth);
    }
}
