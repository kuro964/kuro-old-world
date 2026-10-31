using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._Stalker_OW.Workshop;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client.UserInterface.Systems.Inventory;

/// <summary>ST:OW: read-only module cells shown in place of artifact slots while module equipment is worn.</summary>
public sealed partial class InventoryUIController
{
    [Dependency] private readonly IPrototypeManager _modulePrototypes = default!;

    private readonly List<SlotButton> _moduleCells = new();
    private bool _showingModules;

    public void RefreshModuleSlots()
    {
        var modules = GetWornModuleDisplay();
        if (_showingModules != (modules != null) || _moduleCells.Count != (modules?.Count ?? 0))
        {
            UpdateInventoryHotbar(_playerInventory);
            return;
        }

        if (modules == null)
            return;

        for (var i = 0; i < _moduleCells.Count; i++)
            UpdateModuleCell(_moduleCells[i], modules[i], i);
    }

    // Gets the first worn item with module slots
    private List<EntProtoId?>? GetWornModuleDisplay()
    {
        if (_playerUid is not { } wearer || !_inventorySystem.TryGetContainerSlotEnumerator(wearer, out var slots))
            return null;

        while (slots.NextItem(out var item))
        {
            if (_entities.TryGetComponent<EquipmentModificationComponent>(item, out var equipment) &&
                equipment.Installed.Count > 0)
                return equipment.Installed;
        }

        return null;
    }

    private Control CreateModuleCell(List<EntProtoId?> modules, int index)
    {
        // Keep module displays separate from real inventory slots so they cannot be used to equip items
        var cell = new Control { MinSize = new Vector2(SlotControl.DefaultButtonSize), MouseFilter = Control.MouseFilterMode.Stop };
        if (index >= modules.Count)
            return cell;

        var slot = new SlotButton { ButtonTexturePath = "FlatSlotBackground", FullButtonTexturePath = "FlatSlotBackground" };
        UpdateModuleCell(slot, modules[index], index);
        _moduleCells.Add(slot);
        cell.AddChild(slot);
        return cell;
    }

    private void UpdateModuleCell(SlotButton slot, EntProtoId? module, int index)
    {
        var name = module is { } id && _modulePrototypes.TryIndex(id, out var prototype)
            ? prototype.Name
            : Loc.GetString("workshop-none");
        slot.ToolTip = Loc.GetString("workshop-inventory-module", ("number", index + 1), ("name", name));
        slot.SetPrototype(module?.Id, false);
    }
}
