using Content.Shared.Clothing.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Inventory;
using Content.Shared.Silicons.Borgs;
using Content.Shared.Verbs;
using Robust.Shared.Utility;
using System.Linq;
using Robust.Shared.Containers; // stalker-changes
using Content.Shared.Tag; // stalker-changes
using Content.Shared._Stalker_EN.Clothing;
using Content.Shared._Stalker_EN.Clothing.Components; // stalker-changes
using Content.Shared._Stalker_OW.Workshop; // ST:OW

namespace Content.Shared.Armor;

/// <summary>
///     This handles logic relating to <see cref="ArmorComponent" />
/// </summary>
public abstract partial class SharedArmorSystem : EntitySystem
{
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!; // stalker-changes
    [Dependency] private readonly InventorySystem _inventory = default!; // stalker-changes
    [Dependency] private readonly TagSystem _tag = default!; // stalker-changes
    [Dependency] private readonly SharedEquipmentModificationSystem _equipmentModules = default!; // ST:OW

    /// <inheritdoc />
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ArmorComponent, MapInitEvent>(OnArmorMapInit); // stalker-changes
        SubscribeLocalEvent<ArmorComponent, InventoryRelayedEvent<CoefficientQueryEvent>>(OnCoefficientQuery);
        SubscribeLocalEvent<ArmorComponent, InventoryRelayedEvent<DamageModifyEvent>>(OnDamageModify);
        SubscribeLocalEvent<ArmorComponent, BorgModuleRelayedEvent<DamageModifyEvent>>(OnBorgDamageModify);
        SubscribeLocalEvent<ArmorComponent, GetVerbsEvent<ExamineVerb>>(OnArmorVerbExamine);
        SubscribeLocalEvent<ArmorComponent, VisorToggledEvent>(OnVisorToggled);
    }

    /// <summary>
    /// Get the total Damage reduction value of all equipment caught by the relay.
    /// </summary>
    /// <param name="ent">The item that's being relayed to</param>
    /// <param name="args">The event, contains the running count of armor percentage as a coefficient</param>
    private void OnCoefficientQuery(Entity<ArmorComponent> ent, ref InventoryRelayedEvent<CoefficientQueryEvent> args)
    {
        if (TryComp<MaskComponent>(ent, out var mask) && mask.IsToggled)
            return;


        if (!IsArtifactAllowed(ent.Owner)) // Stalker-changes
            return;

        if (ent.Comp.Modifiers == null) // Stalker-changes
            return;

        foreach (var armorCoefficient in ent.Comp.Modifiers.Coefficients)
        {
            args.Args.DamageModifiers.Coefficients[armorCoefficient.Key] = args.Args.DamageModifiers.Coefficients.TryGetValue(armorCoefficient.Key, out var coefficient) ? coefficient * armorCoefficient.Value : armorCoefficient.Value;
        }

        // ST:OW begin
        foreach (var (type, multiplier) in _equipmentModules.GetArmorModifiers(ent.Owner).Coefficients)
        {
            var existing = args.Args.DamageModifiers.Coefficients.GetValueOrDefault(type, 1f);
            args.Args.DamageModifiers.Coefficients[type] = existing * multiplier;
        }
        // ST:OW end
    }

    private void OnDamageModify(EntityUid uid, ArmorComponent component, InventoryRelayedEvent<DamageModifyEvent> args)
    {
        if (TryComp<MaskComponent>(uid, out var mask) && mask.IsToggled)
            return;


        if (!IsArtifactAllowed(uid)) // Stalker-changes
            return;

        // stalker-changes-start
        if (component.ArmorClass < args.Args.DamageTier)
        {
            if (component.Modifiers == null)
                return;

            var armorTier = component.ArmorClass;
            var damageTier = args.Args.DamageTier;
            var diff = damageTier - armorTier;

            var modifiedModifiers = new DamageModifierSet
            {
                Coefficients = new Dictionary<string, float>(component.Modifiers.Coefficients),
                FlatReduction = new Dictionary<string, float>(component.Modifiers.FlatReduction),
            };


            // Each tier below the projectile will remove 20% of the armor coefficient effectiveness, additively
            // 25% becomes 15% with a 2 tier difference, et cetera
            foreach (var key in modifiedModifiers.Coefficients.Keys.ToList())
            {
                var reducedCoefficient = (1f - modifiedModifiers.Coefficients[key]) / 5f;
                for (var i = 0; i < diff; i++)
                {
                    modifiedModifiers.Coefficients[key] += reducedCoefficient;
                    if (modifiedModifiers.Coefficients[key] >= 1f)
                        modifiedModifiers.Coefficients[key] = 1f;
                }
            }

            // It also reduces flat reduction by 1 per tier difference
            foreach (var key in modifiedModifiers.FlatReduction.Keys.ToList())
            {
                for (var i = 0; i < diff; i++)
                {
                    modifiedModifiers.FlatReduction[key] -= 1;
                    if (modifiedModifiers.FlatReduction[key] <= 0)
                        modifiedModifiers.FlatReduction[key] = 0;
                }
            }

            args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage, WithEquipmentModules(uid, modifiedModifiers)); // ST:OW

            return;
        }

        if (component.ArmorClass > args.Args.DamageTier)
        {
            if (component.Modifiers == null)
                return;

            var armorTier = component.ArmorClass;
            var damageTier = args.Args.DamageTier;
            var diff = armorTier - damageTier;

            var modifiedModifiers = new DamageModifierSet
            {
                Coefficients = new Dictionary<string, float>(component.Modifiers.Coefficients),
                FlatReduction = new Dictionary<string, float>(component.Modifiers.FlatReduction),
            };

            // Each tier stronger the armor is to the projectile adds 50% of its own protection on top
            foreach (var key in modifiedModifiers.Coefficients.Keys.ToList())
            {
                var addedCoefficient =
                    modifiedModifiers.Coefficients[key] + (1 - modifiedModifiers.Coefficients[key]) / 2;
                for (var i = 0; i < diff; i++)
                {
                    modifiedModifiers.Coefficients[key] *= addedCoefficient;
                }
            }

            // 1 flat per too
            foreach (var key in modifiedModifiers.FlatReduction.Keys.ToList())
            {
                for (var i = 0; i < diff; i++)
                {
                    modifiedModifiers.FlatReduction[key] += 1;
                }
            }

            args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage, WithEquipmentModules(uid, modifiedModifiers)); // ST:OW

            return;
        }

        if (component.Modifiers == null)
            return;

        args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage, WithEquipmentModules(uid, component.Modifiers)); // ST:OW
        // stalker-changes-end
    }

    private void OnBorgDamageModify(EntityUid uid, ArmorComponent component,
        ref BorgModuleRelayedEvent<DamageModifyEvent> args)
    {
        if (TryComp<MaskComponent>(uid, out var mask) && mask.IsToggled)
            return;


        if (!IsArtifactAllowed(uid)) // Stalker-changes
            return;

        // stalker-changes-start
        if (component.ArmorClass < args.Args.DamageTier)
        {
            if (component.Modifiers == null)
                return;

            var armorTier = component.ArmorClass;
            var damageTier = args.Args.DamageTier;
            var diff = damageTier - armorTier;

            var modifiedModifiers = new DamageModifierSet
            {
                Coefficients = new Dictionary<string, float>(component.Modifiers.Coefficients),
                FlatReduction = new Dictionary<string, float>(component.Modifiers.FlatReduction),
            };

            foreach (var key in modifiedModifiers.Coefficients.Keys.ToList())
            {
                var reducedCoefficient = (1f - modifiedModifiers.Coefficients[key]) / 5f;
                for (var i = 0; i < diff; i++)
                {
                    modifiedModifiers.Coefficients[key] += reducedCoefficient;
                    if (modifiedModifiers.Coefficients[key] >= 1f)
                        modifiedModifiers.Coefficients[key] = 1f;
                }
            }

            foreach (var key in modifiedModifiers.FlatReduction.Keys.ToList())
            {
                for (var i = 0; i < diff; i++)
                {
                    modifiedModifiers.FlatReduction[key] -= 1;
                    if (modifiedModifiers.FlatReduction[key] <= 0)
                        modifiedModifiers.FlatReduction[key] = 0;
                }
            }

            args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage, WithEquipmentModules(uid, modifiedModifiers)); // ST:OW

            return;
        }

        if (component.ArmorClass > args.Args.DamageTier)
        {
            if (component.Modifiers == null)
                return;

            var armorTier = component.ArmorClass;
            var damageTier = args.Args.DamageTier;
            var diff = armorTier - damageTier;

            var modifiedModifiers = new DamageModifierSet
            {
                Coefficients = new Dictionary<string, float>(component.Modifiers.Coefficients),
                FlatReduction = component.Modifiers.FlatReduction,
            };

            foreach (var key in modifiedModifiers.Coefficients.Keys.ToList())
            {
                for (var i = 0; i < diff; i++)
                {
                    modifiedModifiers.Coefficients[key] *= 0.6f;
                }
            }

            args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage, WithEquipmentModules(uid, modifiedModifiers)); // ST:OW

            return;
        }

        if (component.Modifiers == null)
            return;

        args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage, WithEquipmentModules(uid, component.Modifiers)); // ST:OW
        // stalker-changes-end
    }

    private void OnArmorVerbExamine(EntityUid uid, ArmorComponent component, GetVerbsEvent<ExamineVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess || !component.ShowArmorOnExamine || component.Hidden || component.HiddenExamine) // Stalker-Changes
            return;

        // ST:OW begin
        var displayed = HasComp<EquipmentModificationComponent>(uid)
            ? GetWorkshopProtection(uid) : component.Modifiers ?? component.BaseModifiers;
        var examineMarkup = GetArmorExamine(displayed, component);
        // ST:OW end
        var ev = new ArmorExamineEvent(examineMarkup);
        RaiseLocalEvent(uid, ref ev);

        _examine.AddDetailedExamineVerb(args, component, examineMarkup,
            Loc.GetString("armor-examinable-verb-text"), "/Textures/Interface/VerbIcons/dot.svg.192dpi.png",
            Loc.GetString("armor-examinable-verb-message"));
    }

    private FormattedMessage GetArmorExamine(DamageModifierSet armorModifiers, ArmorComponent comp)  // Stalker-Changes
    {
        var msg = new FormattedMessage();
        msg.AddMarkupOrThrow(Loc.GetString("armor-examine"));

        msg.PushNewline(); // Stalker-Changes
        msg.AddMarkup(Loc.GetString("armor-class-value", ("value", comp.ArmorClass ?? 0))); // Stalker-Changes

        foreach (var coefficientArmor in armorModifiers.Coefficients)
        {
            msg.PushNewline();

            var armorType = Loc.GetString("armor-damage-type-" + coefficientArmor.Key.ToLower());
            msg.AddMarkupOrThrow(Loc.GetString("armor-coefficient-value",
                ("type", armorType),
                ("value", WorkshopStatDisplay.ProtectionPercent(coefficientArmor.Value)) // ST:OW
            ));
        }

        foreach (var flatArmor in armorModifiers.FlatReduction)
        {
            msg.PushNewline();

            var armorType = Loc.GetString("armor-damage-type-" + flatArmor.Key.ToLower());
            msg.AddMarkupOrThrow(Loc.GetString("armor-reduction-value",
                ("type", armorType),
                ("value", flatArmor.Value)
            ));
        }

        return msg;
    }
  // stalker-changes-start
    private bool IsArtifactAllowed(EntityUid uid)
    {

        if (!TryComp<TagComponent>(uid, out var tagComp) || !_tag.HasTag(tagComp, "STArtifact"))
            return true;


        if (!TryComp<TransformComponent>(uid, out var xform) || !TryComp<MetaDataComponent>(uid, out var meta))
            return false;

        if (!_inventory.TryGetContainingSlot((uid, xform, meta), out var slotDef) || slotDef == null)
            return false;

        var name = slotDef.Name;
        return name == "artifact1" || name == "artifact2" || name == "artifact3" || name == "artifact4" || name == "artifact5";
    }

    private void OnVisorToggled(Entity<ArmorComponent> ent, ref VisorToggledEvent args)
    {
        if (!TryComp<HelmetVisorComponent>(ent, out var visor) || visor.VisorUpModifiers == null)
            return;

        ent.Comp.Modifiers = args.IsUp ? visor.VisorUpModifiers : visor.DefaultModifiers;
        Dirty(ent);
    }
}
  // stalker-changes-end
