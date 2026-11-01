using Content.Shared._Stalker_OW.Workshop;
using Content.Shared._Stalker.PersonalDamage;
using Robust.Shared.Prototypes;

namespace Content.Server._Stalker_OW.Workshop;

// Runtime timers belong to the armor, never to the shared module prototypes
[RegisterComponent]
public sealed partial class ModulePersonalDamageComponent : Component
{
    public readonly EntProtoId?[] Prototypes = new EntProtoId?[EquipmentModificationComponent.MaxSlots];
    // References are read-only; each slot keeps its own elapsed time
    public readonly PersonalDamageComponent?[] Effects = new PersonalDamageComponent?[EquipmentModificationComponent.MaxSlots];
    public readonly float[] Elapsed = new float[EquipmentModificationComponent.MaxSlots];
}
