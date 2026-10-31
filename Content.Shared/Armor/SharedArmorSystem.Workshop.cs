using System.Linq;
using Content.Shared._Stalker_OW.Workshop;
using Content.Shared.Damage;
using Robust.Shared.Prototypes;

namespace Content.Shared.Armor;

public abstract partial class SharedArmorSystem
{
    private DamageModifierSet WithEquipmentModules(EntityUid uid, DamageModifierSet set)
    {
        var modules = _equipmentModules.GetArmorModifiers(uid);
        return modules.Coefficients.Count == 0 && modules.FlatReduction.Count == 0
            ? set
            : CombineModuleModifiers(set, modules);
    }

    // Add flat reductions and multiply coefficients before damage is applied
    // Copy the dictionaries so shared armor and module data are not changed
    private static DamageModifierSet CombineModuleModifiers(DamageModifierSet armor, DamageModifierSet modules)
    {
        var result = new DamageModifierSet
        {
            Coefficients = new Dictionary<string, float>(armor.Coefficients),
            FlatReduction = new Dictionary<string, float>(armor.FlatReduction),
        };
        foreach (var (type, coefficient) in modules.Coefficients)
            result.Coefficients[type] = result.Coefficients.GetValueOrDefault(type, 1f) * coefficient;
        foreach (var (type, flat) in modules.FlatReduction)
            result.FlatReduction[type] = result.FlatReduction.GetValueOrDefault(type) + flat;

        return result;
    }

    // Gets protection at equal armor and attack class for examine and previews
    // Changes contains pending module replacements or removals
    public DamageModifierSet GetWorkshopProtection(EntityUid uid, IReadOnlyDictionary<int, EntProtoId?>? changes = null)
    {
        if (!TryComp<ArmorComponent>(uid, out var armor))
            return new DamageModifierSet();

        return CombineModuleModifiers(armor.Modifiers ?? armor.BaseModifiers,
            _equipmentModules.GetArmorModifiers(uid, changes));
    }

    public List<WorkshopStatState> GetWorkshopPreview(EntityUid uid, IReadOnlyDictionary<int, EntProtoId?>? changes = null)
    {
        var current = GetWorkshopProtection(uid);
        var proposed = GetWorkshopProtection(uid, changes);
        var result = new List<WorkshopStatState>();
        var types = current.Coefficients.Keys
            .Union(proposed.Coefficients.Keys)
            .Union(current.FlatReduction.Keys)
            .Union(proposed.FlatReduction.Keys)
            .ToList();
        types.Sort(string.CompareOrdinal);

        foreach (var type in types)
        {
            var name = Loc.GetString("armor-damage-type-" + type.ToLowerInvariant());
            result.Add(new WorkshopStatState
            {
                Name = name,
                Current = current.Coefficients.GetValueOrDefault(type, 1f),
                Proposed = proposed.Coefficients.GetValueOrDefault(type, 1f),
            });

            var currentFlat = current.FlatReduction.GetValueOrDefault(type);
            var proposedFlat = proposed.FlatReduction.GetValueOrDefault(type);
            if (currentFlat != 0 || proposedFlat != 0)
                result.Add(new WorkshopStatState { Name = name, Current = currentFlat, Proposed = proposedFlat, Flat = true });
        }

        return result;
    }
}