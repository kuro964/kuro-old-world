using Content.Shared.Armor;
using Content.Shared.Clothing.Components;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Inventory;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Reflect;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Examine;

/// <summary>
/// System for comparing armor stats between examined items and currently equipped items.
/// Provides a "Compare Stats" verb for items with ArmorComponent and ClothingComponent.
/// </summary>
public sealed class StatsExamineSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;
    [Dependency] private readonly SharedArmorSystem _armor = default!; // ST:OW

    // ST:OW begin
    private StatsExamineWindow? _window;
    private Action? _refreshModules;
    private bool _refreshPending;
    private EntityUid? _examinedTarget;
    private EntityUid? _comparisonUser;
    private SlotFlags? _comparisonSlots;
    // ST:OW end

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ClothingComponent, GetVerbsEvent<ExamineVerb>>(OnClothingStatsVerb);
    }

    // ST:OW begin
    public void RefreshModuleStats(EntityUid uid)
    {
        if (_refreshModules == null)
            return;
        if (uid == _examinedTarget || _comparisonUser is { } user && _comparisonSlots is { } slots &&
            GetEquippedArmor(user, slots) == uid)
            _refreshPending = true;
    }

    public void RefreshEquippedStats() => _refreshPending = _refreshModules != null;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_refreshPending)
            return;
        _refreshPending = false;
        _refreshModules?.Invoke();
    }

    private void ClearComparison()
    {
        _window = null;
        _refreshPending = false;
        _refreshModules = null;
        _examinedTarget = null;
        _comparisonUser = null;
        _comparisonSlots = null;
    }
    // ST:OW end

    /// <summary>
    /// Adds a "Compare Stats" verb to items with ArmorComponent and ClothingComponent.
    /// </summary>
    private void OnClothingStatsVerb(EntityUid uid, ClothingComponent component, GetVerbsEvent<ExamineVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        // Only add the compare verb if the item has armor component
        if (!TryComp<ArmorComponent>(uid, out var armor))
            return;

        if (armor.Hidden || armor.HiddenExamine)
            return;

        // Create a separate verb for stat comparison
        var verb = new ExamineVerb
        {
            ClientExclusive = true,
            Act = () => OpenStatsWindow(args.User, args.Target), // ST:OW
            Text = "Compare Stats",
            Message = "Compare armor stats with currently equipped item",
            Category = VerbCategory.Examine,
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/dot.svg.192dpi.png")),
            Priority = -1 // Show after the standard armor examine verb
        };

        args.Verbs.Add(verb);
    }

    /// <summary>
    /// Opens the stats comparison window for a spawned entity.
    /// Compares the examined item's stats with the equipped item in the same slot.
    /// </summary>
    private void OpenStatsWindow(EntityUid user, EntityUid target) // ST:OW
    {
        _window?.Close();
        _window = new StatsExamineWindow();
        _window.OpenCentered();

        // ST:OW begin
        _window.OnClose += ClearComparison;
        _examinedTarget = target;
        _comparisonUser = user;
        _refreshModules = () => RefreshEntityStats(user, target);
        _refreshModules();
    }

    private void RefreshEntityStats(EntityUid user, EntityUid target)
    {
        if (_window == null)
            return;
        if (Deleted(user) || Deleted(target) ||
            !TryComp<ArmorComponent>(target, out var examinedArmor) ||
            !TryComp<ClothingComponent>(target, out var examinedClothing))
        {
            _window.Close();
            return;
        }
        _comparisonSlots = examinedClothing.Slots;
        // ST:OW end
        // Get the examined item's modifiers
        var examinedModifiers = _armor.GetWorkshopProtection(target); // ST:OW
        var examinedArmorClass = examinedArmor.ArmorClass;

        // Get reflect chance if available
        float? examinedReflectProb = null;
        if (TryComp<ReflectComponent>(target, out var reflect))
        {
            examinedReflectProb = reflect.ReflectProb;
        }

        // Get the equipped item in the same slot
        TryGetEquippedArmorStats(user, examinedClothing.Slots, out var equippedModifiers, out var equippedArmorClass, out var equippedReflectProb);

        _window.UpdateStats(examinedModifiers, examinedArmorClass, examinedReflectProb, equippedModifiers, equippedArmorClass, equippedReflectProb);
    }

    /// <summary>
    /// Opens the stats comparison window for a prototype (shop item).
    /// Compares the prototype's stats with the equipped item in the same slot.
    /// </summary>
    public void OpenStatsWindowFromPrototype(EntityUid user, string prototypeId)
    {
        if (!_prototypeManager.TryIndex<EntityPrototype>(prototypeId, out var prototype)) // ST:OW
            return;

        _window?.Close();
        _window = new StatsExamineWindow();
        _window.OpenCentered();

        // Get components from prototype
        DamageModifierSet? examinedModifiers = null;
        int? examinedArmorClass = null;
        float? examinedReflectProb = null;
        SlotFlags? slotFlags = null;

        if (prototype.TryGetComponent<ArmorComponent>(out var armor, _componentFactory))
        {
            examinedModifiers = armor.Modifiers ?? armor.BaseModifiers;
            examinedArmorClass = armor.ArmorClass;
        }

        if (prototype.TryGetComponent<ReflectComponent>(out var reflect, _componentFactory))
        {
            examinedReflectProb = reflect.ReflectProb;
        }

        if (prototype.TryGetComponent<ClothingComponent>(out var clothing, _componentFactory))
        {
            slotFlags = clothing.Slots;
        }

        // ST:OW begin
        _window.OnClose += ClearComparison;
        _comparisonUser = user;
        _comparisonSlots = slotFlags;
        _refreshModules = () => RefreshPrototypeStats(user, slotFlags, examinedModifiers, examinedArmorClass, examinedReflectProb);
        _refreshModules();
    }

    private void RefreshPrototypeStats(EntityUid user, SlotFlags? slotFlags, DamageModifierSet? examinedModifiers,
        int? examinedArmorClass, float? examinedReflectProb)
    {
        if (_window == null)
            return;
        if (Deleted(user))
        {
            _window.Close();
            return;
        }
        // ST:OW end
        // Get the equipped item in the same slot
        DamageModifierSet? equippedModifiers = null;
        int? equippedArmorClass = null;
        float? equippedReflectProb = null;

        if (slotFlags.HasValue)
        {
            TryGetEquippedArmorStats(user, slotFlags.Value, out equippedModifiers, out equippedArmorClass, out equippedReflectProb);
        }

        _window.UpdateStats(examinedModifiers ?? new DamageModifierSet(), examinedArmorClass, examinedReflectProb, equippedModifiers, equippedArmorClass, equippedReflectProb);
    }

    /// <summary>
    /// Gets the stats of the equipped item in the specified slot.
    /// Uses exact flag matching first, then falls back to partial matching.
    /// </summary>
    private void TryGetEquippedArmorStats(
        EntityUid user,
        SlotFlags slotFlags,
        out DamageModifierSet? modifiers,
        out int? armorClass,
        out float? reflectProb)
    {
        modifiers = null;
        armorClass = null;
        reflectProb = null;

        // ST:OW begin
        if (GetEquippedArmor(user, slotFlags) is not { } equippedUid ||
            !TryComp<ArmorComponent>(equippedUid, out var armor))
            return;

        modifiers = _armor.GetWorkshopProtection(equippedUid);
        armorClass = armor.ArmorClass;
        if (TryComp<ReflectComponent>(equippedUid, out var reflect))
            reflectProb = reflect.ReflectProb;
    }

    private EntityUid? GetEquippedArmor(EntityUid user, SlotFlags slotFlags)
    {
        if (Deleted(user) || !TryComp<InventoryComponent>(user, out var inventory))
            return null;

        foreach (var slot in inventory.Slots)
        {
            if (slot.SlotFlags == slotFlags && _inventory.TryGetSlotEntity(user, slot.Name, out var uid) &&
                HasComp<ArmorComponent>(uid))
                return uid;
        }
        foreach (var slot in inventory.Slots)
        {
            if ((slot.SlotFlags & slotFlags) == slotFlags && _inventory.TryGetSlotEntity(user, slot.Name, out var uid) &&
                HasComp<ArmorComponent>(uid))
                return uid;
        }
        return null;
    }
    // ST:OW end

    public override void Shutdown()
    {
        base.Shutdown();
        _window?.Close();
    }
}
