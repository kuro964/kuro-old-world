using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Dictionary;

namespace Content.Shared.VendingMachines
{
    [Prototype]
    public sealed partial class VendingMachineInventoryPrototype : IPrototype
    {
        [ViewVariables]
        [IdDataField]
        public string ID { get; private set; } = default!;

        [DataField("startingInventory", customTypeSerializer:typeof(PrototypeIdDictionarySerializer<uint, EntityPrototype>))]
        public Dictionary<string, uint> StartingInventory { get; private set; } = new();

        [DataField("emaggedInventory", customTypeSerializer:typeof(PrototypeIdDictionarySerializer<uint, EntityPrototype>))]
        public Dictionary<string, uint>? EmaggedInventory { get; private set; }

        [DataField("contrabandInventory", customTypeSerializer:typeof(PrototypeIdDictionarySerializer<uint, EntityPrototype>))]
        public Dictionary<string, uint>? ContrabandInventory { get; private set; }
        
        // ST:OW begin
        [DataField("categoryGroups")]
        public List<VendingCategoryGroup>? CategoryGroups { get; private set; }
        
        public Dictionary<string, uint> GetStartingInventory()
        {
            if (CategoryGroups is not { Count: > 0 })
                return StartingInventory;

            var inventory = new Dictionary<string, uint>();
            foreach (var group in CategoryGroups)
            {
                foreach (var (id, amount) in group.Entries)
                    inventory[id] = amount;
            }

            return inventory;
        }

        [DataDefinition]
        public sealed partial class VendingCategoryGroup
        {
            [DataField("category", required: true)]
            public string Category = "misc";

            [DataField("entries", required: true, customTypeSerializer: typeof(PrototypeIdDictionarySerializer<uint, EntityPrototype>))]
            public Dictionary<string, uint> Entries = new();
        }
        // ST:OW end
    }
}
