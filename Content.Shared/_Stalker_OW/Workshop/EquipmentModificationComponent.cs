using Content.Shared.Damage;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Stalker_OW.Workshop;

// Stores installed modules as prototype IDs so they stay with the equipment when saved
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true)]
public sealed partial class EquipmentModificationComponent : Component
{
    public const int MaxSlots = 5;

    // Number of module slots
    [DataField]
    public int Slots;

    // Blocks artifact slots while worn
    [DataField]
    public bool BlocksArtifacts;

    // Each entry is a module slot with null meaning empty
    [DataField, AutoNetworkedField]
    public List<EntProtoId?> Installed = new();

    // Modules that no longer fit are stored w/o applying stats
    // The workbench returns them as items when the equipment is inserted
    [DataField, AutoNetworkedField]
    public List<EntProtoId> Stored = new();

    // Combined module modifiers
    [ViewVariables]
    public DamageModifierSet Cached = new();

    public int SlotCount => Math.Clamp(Slots, 0, MaxSlots);
}

// Installing consumes the module and removing it spawns a new copy
[RegisterComponent, NetworkedComponent]
public sealed partial class EquipmentModuleComponent : Component
{
    [DataField]
    public float InstallTime = 2f;

    // Only one module of this prototype can be installed on the equipment
    [DataField]
    public bool Exclusive;
}

// Reads the armor effects from the module prototype
[RegisterComponent, NetworkedComponent]
public sealed partial class ArmorModuleEffectComponent : Component
{
    // Coefficients multiply and flat reductions add to the armor's protection
    [DataField(required: true)]
    public DamageModifierSet Modifiers = new();
}
