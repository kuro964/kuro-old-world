using System.Numerics;
using Content.Server.Cargo.Systems;
using Content.Server.Power.Components;
using Content.Server.Vocalization.Systems;
using Content.Shared.Cargo;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Destructible;
using Content.Shared.Emp;
using Content.Shared.Power;
using Content.Shared.Throwing;
using Content.Shared.UserInterface;
using Content.Shared.VendingMachines;
using Content.Shared.Wall;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
// ST:OW begin
using Content.Shared.Clothing.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Interaction;
using Content.Shared.Weapons.Ranged.Components;
// ST:OW end

namespace Content.Server.VendingMachines
{
    public sealed class VendingMachineSystem : SharedVendingMachineSystem
    {
        [Dependency] private readonly IRobustRandom _random = default!;
        [Dependency] private readonly PricingSystem _pricing = default!;
        [Dependency] private readonly ThrowingSystem _throwingSystem = default!;
        [Dependency] private readonly InventorySystem _inventory = default!; // ST:OW
        [Dependency] private readonly SharedHandsSystem _hands = default!; // ST:OW
        [Dependency] private readonly Weapons.Ranged.Systems.GunSystem _gun = default!; // ST:OW
        [Dependency] private readonly SharedInteractionSystem _interaction = default!; // ST:OW

        private const float WallVendEjectDistanceFromWall = 1f;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<VendingMachineComponent, PowerChangedEvent>(OnPowerChanged);
            SubscribeLocalEvent<VendingMachineComponent, BreakageEventArgs>(OnBreak);
            SubscribeLocalEvent<VendingMachineComponent, DamageChangedEvent>(OnDamageChanged);
            SubscribeLocalEvent<VendingMachineComponent, PriceCalculationEvent>(OnVendingPrice);
            SubscribeLocalEvent<VendingMachineComponent, TryVocalizeEvent>(OnTryVocalize);

            SubscribeLocalEvent<VendingMachineComponent, ActivatableUIOpenAttemptEvent>(OnActivatableUIOpenAttempt);
            SubscribeLocalEvent<VendingMachineComponent, VendingMachineSelfDispenseEvent>(OnSelfDispense);

            SubscribeLocalEvent<VendingMachineRestockComponent, PriceCalculationEvent>(OnPriceCalculation);
        }

        private void OnVendingPrice(EntityUid uid, VendingMachineComponent component, ref PriceCalculationEvent args)
        {
            var price = 0.0;

            foreach (var entry in component.Inventory.Values)
            {
                if (!PrototypeManager.TryIndex<EntityPrototype>(entry.ID, out var proto))
                {
                    Log.Error($"Unable to find entity prototype {entry.ID} on {ToPrettyString(uid)} vending.");
                    continue;
                }

                price += entry.Amount * _pricing.GetEstimatedPrice(proto);
            }

            args.Price += price;
        }

        protected override void OnMapInit(EntityUid uid, VendingMachineComponent component, MapInitEvent args)
        {
            base.OnMapInit(uid, component, args);

            if (HasComp<ApcPowerReceiverComponent>(uid))
            {
                TryUpdateVisualState((uid, component));
            }
        }

        private void OnActivatableUIOpenAttempt(EntityUid uid, VendingMachineComponent component, ActivatableUIOpenAttemptEvent args)
        {
            if (component.Broken)
                args.Cancel();
        }

        private void OnPowerChanged(EntityUid uid, VendingMachineComponent component, ref PowerChangedEvent args)
        {
            TryUpdateVisualState((uid, component));
        }

        private void OnBreak(EntityUid uid, VendingMachineComponent vendComponent, BreakageEventArgs eventArgs)
        {
            vendComponent.Broken = true;
            Dirty(uid, vendComponent);
            TryUpdateVisualState((uid, vendComponent));
        }

        private void OnDamageChanged(EntityUid uid, VendingMachineComponent component, DamageChangedEvent args)
        {
            if (!args.DamageIncreased && component.Broken)
            {
                component.Broken = false;
                Dirty(uid, component);
                TryUpdateVisualState((uid, component));
                return;
            }

            if (component.Broken || component.DispenseOnHitCoolingDown ||
                component.DispenseOnHitChance == null || args.DamageDelta == null)
                return;

            if (args.DamageIncreased && args.DamageDelta.GetTotal() >= component.DispenseOnHitThreshold &&
                _random.Prob(component.DispenseOnHitChance.Value))
            {
                if (component.DispenseOnHitCooldown != null)
                {
                    component.DispenseOnHitEnd = Timing.CurTime + component.DispenseOnHitCooldown.Value;
                }

                EjectRandom(uid, throwItem: true, forceEject: true, component);
            }
        }

        private void OnSelfDispense(EntityUid uid, VendingMachineComponent component, VendingMachineSelfDispenseEvent args)
        {
            if (args.Handled)
                return;

            args.Handled = true;
            EjectRandom(uid, throwItem: true, forceEject: false, component);
        }

        /// <summary>
        /// Sets the <see cref="VendingMachineComponent.CanShoot"/> property of the vending machine.
        /// </summary>
        public void SetShooting(EntityUid uid, bool canShoot, VendingMachineComponent? component = null)
        {
            if (!Resolve(uid, ref component))
                return;

            component.CanShoot = canShoot;
        }

        /// <summary>
        /// Sets the <see cref="VendingMachineComponent.Contraband"/> property of the vending machine.
        /// </summary>
        public void SetContraband(EntityUid uid, bool contraband, VendingMachineComponent? component = null)
        {
            if (!Resolve(uid, ref component))
                return;

            component.Contraband = contraband;
            Dirty(uid, component);
        }

        /// <summary>
        /// Ejects a random item from the available stock. Will do nothing if the vending machine is empty.
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="throwItem">Whether to throw the item in a random direction after dispensing it.</param>
        /// <param name="forceEject">Whether to skip the regular ejection checks and immediately dispense the item without animation.</param>
        /// <param name="vendComponent"></param>
        public void EjectRandom(EntityUid uid, bool throwItem, bool forceEject = false, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            var availableItems = GetAvailableInventory(uid, vendComponent);
            if (availableItems.Count <= 0)
                return;

            var item = _random.Pick(availableItems);

            if (forceEject)
            // ST:OW begin
            {
                var entry = GetEntry(uid, item.ID, item.Type, vendComponent);
                if (entry == null || entry.Amount == 0)
                    return;
                
                entry.Amount--;
                Dirty(uid, vendComponent);
                DispenseItem(uid, item.ID, throwItem, null, vendComponent);
                UpdateUI((uid, vendComponent));
            }
            // ST:OW end
            else
            {
                TryEjectVendorItem(uid, item.Type, item.ID, throwItem, user: null, vendComponent: vendComponent);
            }
        }

        protected override void EjectItem(EntityUid uid, VendingMachineComponent? vendComponent = null,
            bool forceEject = false)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            // No need to update the visual state because we never changed it during a forced eject
            if (!forceEject)
                TryUpdateVisualState((uid, vendComponent));

            if (string.IsNullOrEmpty(vendComponent.NextItemToEject))
            {
                vendComponent.ThrowNextItem = false;
                // If the item disappeared then it does not belong to the player
                vendComponent.NextBuyer = null; // ST:OW

                return;
            }

            // ST:OW begin
            var itemId = vendComponent.NextItemToEject;
            var throwItem = vendComponent.ThrowNextItem;
            var buyer = vendComponent.NextBuyer;
            vendComponent.NextItemToEject = null;
            vendComponent.ThrowNextItem = false;
            vendComponent.NextBuyer = null;

            DispenseItem(uid, itemId, throwItem, buyer, vendComponent);
        }
        // ST:OW end

        private void DispenseItem(EntityUid uid, string itemId, bool throwItem, EntityUid? buyer,
            VendingMachineComponent vendComponent)
        {
            // Default spawn coordinates
            var xform = Transform(uid);
            var spawnCoordinates = xform.Coordinates;

            // Items dispensed are not inside any wall tiles
            if (TryComp<WallMountComponent>(uid, out var wallMountComponent))
            {
                var offset = (wallMountComponent.Direction + xform.LocalRotation - Math.PI / 2).ToVec() *
                             WallVendEjectDistanceFromWall;
                spawnCoordinates = spawnCoordinates.Offset(offset);
            }

            var ent = Spawn(itemId, spawnCoordinates); // ST:OW
            TryPrefillMonolithMagazine(uid, ent, vendComponent); // ST:OW
            
            if (throwItem)
            {
                var range = vendComponent.NonLimitedEjectRange;
                var direction = new Vector2(
                    _random.NextFloat(-range, range), 
                    _random.NextFloat(-range, range));

                _throwingSystem.TryThrow(ent, direction, vendComponent.NonLimitedEjectForce);
            }
            else if (vendComponent.AutoEquipDispensed &&
                     buyer is { } user && // ST:OW
                     !Deleted(user) &&
                     _interaction.InRangeUnobstructed(user, uid)) // ST:OW
            {
                TryAutoEquipOrHand(ent, user); // ST:OW
            }
        }

        // Magazines are filled! (For Monolith at least)
        private void TryPrefillMonolithMagazine(EntityUid vendingUid, EntityUid item, VendingMachineComponent vendComponent)
        {
            if (!vendComponent.PrefillMagazines)
                return;

            if (MetaData(vendingUid).EntityPrototype?.ID != "VendingMachineBoxesMonolith")
                return;

            var itemProto = MetaData(item).EntityPrototype?.ID;
            if (itemProto == null)
                return;

            // This ammo is specific to Monolith because they ONLY use T3 ammo
            // Do not enable this for other factions
            EntProtoId? tier3Ammo = itemProto switch
            {
                "BaseAPSMag"      => "STCartridge918PBM",
                "VityazMag"       => "STCartridge919PBM",
                "545Mag30"        => "STCartridge545PC",
                "556Mag30"        => "Cartridge556M855",
                "739Mag30"        => "STCartridge739FMJ",
                "754Mag10"        => "STCartridge754FMJ",
                "BaseTommyGunMag" => "Cartridge45ACPAP",
                "TommyGunDrum2"   => "Cartridge45ACPAP",
                _ => null
            };

            if (tier3Ammo == null)
                return;

            _gun.TryFillBallisticMagazine(item, tier3Ammo.Value);
        }

        // Auto-equip items
        private static readonly string[] PrioritySlots = 
        {
            "ears",          // Headsets
            "mask",          // Gas masks
            "head",          // Helmets & hats
            "cloak",         // Cloaks
            "neck",          // Scarves
            "outerClothing", // Suits
            "back",          // Backpacks
            "belt",          // Belt
            "gloves",        // Guess.
        };

        private bool TryAutoEquipOrHand(EntityUid item, EntityUid user)
        {
            if (Deleted(item) || Deleted(user))
                return false;
            
            // Guns go straight to hands even w/ ClothingComponent
            if (!HasComp<GunComponent>(item) && HasComp<ClothingComponent>(item))
            {
                foreach (var slot in PrioritySlots)
                {
                    if (_inventory.TryEquip(user, item, slot, silent: true, force: false))
                        return true;
                }
            }

            // If equip & pickup fail then leave the item at the machine
            return _hands.TryPickupAnyHand(user, item, animateUser: false, animate: false);
        }
        // ST:OW end
        
        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            var disabled = EntityQueryEnumerator<EmpDisabledComponent, VendingMachineComponent>();
            while (disabled.MoveNext(out var uid, out _, out var comp))
            {
                if (comp.NextEmpEject < Timing.CurTime)
                {
                    EjectRandom(uid, true, false, comp);
                    comp.NextEmpEject += (5 * comp.EjectDelay);
                }
            }
        }

        private void OnPriceCalculation(EntityUid uid, VendingMachineRestockComponent component, ref PriceCalculationEvent args)
        {
            var highestPrice = 0.0;

            // Find the most expensive inventory and use that as the highest price.
            foreach (var vendingInventory in component.CanRestock)
            {
                double total = 0;

                if (PrototypeManager.TryIndex(vendingInventory, out VendingMachineInventoryPrototype? inventoryPrototype))
                {
                    foreach (var (item, amount) in inventoryPrototype.GetStartingInventory())
                    {
                        if (PrototypeManager.TryIndex(item, out EntityPrototype? entity))
                            total += _pricing.GetEstimatedPrice(entity) * amount;
                    }
                }

                highestPrice = Math.Max(highestPrice, total);
            }

            args.Price += highestPrice;
        }

        private void OnTryVocalize(Entity<VendingMachineComponent> ent, ref TryVocalizeEvent args)
        {
            args.Cancelled |= ent.Comp.Broken;
        }
    }
}
