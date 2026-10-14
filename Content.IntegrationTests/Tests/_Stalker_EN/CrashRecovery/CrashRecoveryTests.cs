using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Stalker.StalkerDB;
using Content.Server._Stalker_EN.CrashRecovery;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared._Stalker.StalkerRepository;
using Content.Shared._Stalker_EN.CrashRecovery;
using Content.Shared._Stalker_EN.Loadout;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item;
using Content.Shared.Labels.Components;
using Content.Shared.Labels.EntitySystems;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Paper;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Stacks;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._Stalker_EN.CrashRecovery;

[TestFixture]
[TestOf(typeof(CrashRecoverySystem))]
public sealed class CrashRecoveryTests
{
    private const string Rule = "STCrashRecovery";

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: CrTestPouch
  parent: BaseItem
  components:
  - type: Item
    size: Tiny
  - type: Storage
    grid:
    - 0,0,3,3
  - type: ContainerContainer
    containers:
      storagebase: !type:Container
";

    private sealed class Ctx
    {
        public TestPair Pair = default!;
        public RobustIntegrationTest.ServerIntegrationInstance Server = default!;
        public IEntityManager Ent = default!;
        public CrashRecoverySystem Cr = default!;
        public IServerDbManager Db = default!;
        public EntityCoordinates Coords;
    }

    private sealed class Player
    {
        public ICommonSession Session = default!;
        public EntityUid Mind;
        public EntityUid Mob;
        public EntityUid Stash;
        public string Login => Session.Name;
    }

    private static async Task<Ctx> Setup()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var ctx = new Ctx
        {
            Pair = pair,
            Server = server,
            Ent = server.EntMan,
            Cr = server.System<CrashRecoverySystem>(),
            Db = server.ResolveDependency<IServerDbManager>(),
            Coords = map.GridCoords,
        };
        await server.WaitPost(() => server.System<GameTicker>().StartGameRule(Rule));
        await pair.RunTicksSync(5);
        return ctx;
    }

    private static async Task<Player> AddPlayer(Ctx ctx, string name)
    {
        var p = new Player { Session = await ctx.Server.AddDummySession(name) };
        await ctx.Db.SetLoginItems(p.Login, StalkerDbSystem.DefaultStalkerItems);
        await ctx.Server.WaitPost(() =>
        {
            var minds = ctx.Server.System<SharedMindSystem>();
            p.Mob = ctx.Ent.SpawnEntity("MobHuman", ctx.Coords);
            p.Mind = minds.CreateMind(p.Session.UserId);
            minds.TransferTo(p.Mind, p.Mob);
            p.Stash = ctx.Ent.SpawnEntity("DebugRepository", ctx.Coords);
            ctx.Ent.GetComponent<StalkerRepositoryComponent>(p.Stash).StorageOwner = "PB" + p.Login;
        });
        await ctx.Pair.RunTicksSync(2);
        return p;
    }

    private static async Task Flush(Ctx ctx)
    {
        await ctx.Server.WaitPost(() => ctx.Cr.FlushPendingWrites(TimeSpan.FromSeconds(10)));
        await ctx.Pair.RunTicksSync(2);
    }

    private static async Task Crash(Ctx ctx, params Player[] players)
    {
        await Flush(ctx);
        await ctx.Server.WaitPost(() =>
        {
            var minds = ctx.Server.System<SharedMindSystem>();
            foreach (var p in players)
            {
                var old = p.Mob;
                p.Mob = ctx.Ent.SpawnEntity("MobHuman", ctx.Coords);
                minds.TransferTo(p.Mind, p.Mob);
                ctx.Ent.DeleteEntity(old);
            }
            ctx.Server.System<GameTicker>().StartGameRule(Rule);
        });
        await ctx.Pair.RunTicksSync(10);
        await Flush(ctx);
    }

    private static async Task<string?> Backup(Ctx ctx, Player p)
    {
        await Flush(ctx);
        return await ctx.Db.GetCrashRecovery(p.Login);
    }

    private static async Task<List<EntityUid>> Claim(Ctx ctx, Player p, int times = 1)
    {
        var before = new HashSet<EntityUid>();
        await ctx.Server.WaitPost(() =>
        {
            before.UnionWith(ctx.Ent.GetEntities());
            for (var i = 0; i < times; i++)
                ctx.Ent.EventBus.RaiseLocalEvent(p.Stash, new CrashRecoveryClaimMessage { Actor = p.Mob });
        });
        await ctx.Pair.RunTicksSync(10);
        await Flush(ctx);
        var spawned = new List<EntityUid>();
        await ctx.Server.WaitPost(() =>
        {
            var containers = ctx.Server.System<SharedContainerSystem>();
            foreach (var e in ctx.Ent.GetEntities())
            {
                if (before.Contains(e) || ctx.Ent.Deleted(e) || ctx.Ent.IsQueuedForDeletion(e))
                    continue;
                if (containers.TryGetContainingContainer((e, null, null), out var c) && c.ID == "toggleable-clothing")
                    continue;
                spawned.Add(e);
            }
        });
        return spawned;
    }

    private static Dictionary<string, int> Tally(IEntityManager ent, IEnumerable<EntityUid> uids)
    {
        var result = new Dictionary<string, int>();
        foreach (var uid in uids)
        {
            if (!ent.HasComponent<ItemComponent>(uid) ||
                ent.HasComponent<VirtualItemComponent>(uid) ||
                ent.HasComponent<OrganComponent>(uid) ||
                ent.HasComponent<BodyPartComponent>(uid))
                continue;

            var proto = ent.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID;
            if (proto == null)
                continue;

            var count = ent.TryGetComponent<StackComponent>(uid, out var stack) ? stack.Count : 1;
            result[proto] = result.GetValueOrDefault(proto) + count;

            if (ent.TryGetComponent<BallisticAmmoProviderComponent>(uid, out var ammo))
                result[proto + "#ammo"] = result.GetValueOrDefault(proto + "#ammo") + ammo.Count;
        }
        return result;
    }

    private static List<EntityUid> Carried(IEntityManager ent, EntityUid mob)
    {
        var list = new List<EntityUid>();
        void Walk(EntityUid uid)
        {
            list.Add(uid);
            if (!ent.TryGetComponent<ContainerManagerComponent>(uid, out var man))
                return;
            foreach (var (id, container) in man.Containers)
            {
                if (id is "toggleable-clothing" or "actions" or "program-container")
                    continue;
                foreach (var child in container.ContainedEntities)
                {
                    if (ent.HasComponent<BallisticAmmoProviderComponent>(uid) &&
                        ent.HasComponent<CartridgeAmmoComponent>(child))
                        continue;
                    Walk(child);
                }
            }
        }

        if (ent.System<InventorySystem>().TryGetContainerSlotEnumerator(mob, out var slots))
        {
            while (slots.NextItem(out var item, out _))
                Walk(item);
        }
        foreach (var held in ent.System<SharedHandsSystem>().EnumerateHeld(mob))
            Walk(held);
        return list;
    }

    private static string Describe(Dictionary<string, int> d) =>
        string.Join(", ", d.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));

    private static EntityUid Spawn(Ctx ctx, string proto) => ctx.Ent.SpawnEntity(proto, ctx.Coords);

    private static void Equip(Ctx ctx, Player p, string proto, string slot) =>
        Assert.That(ctx.Server.System<InventorySystem>().TryEquip(p.Mob, Spawn(ctx, proto), slot, force: true), $"equip {proto} in {slot}");

    private static EntityUid Hold(Ctx ctx, Player p, string proto)
    {
        var item = Spawn(ctx, proto);
        Assert.That(ctx.Server.System<SharedHandsSystem>().TryPickupAnyHand(p.Mob, item), $"pick up {proto}");
        return item;
    }

    private static void Put(Ctx ctx, EntityUid container, EntityUid item)
    {
        var containers = ctx.Server.System<SharedContainerSystem>();
        Assert.That(containers.Insert(item, containers.GetContainer(container, "storagebase")), "insert into storage");
    }

    private static void LoadMagazine(Ctx ctx, Player p, EntityUid mag, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var round = Spawn(ctx, "STCartridge545FMJ");
            ctx.Ent.EventBus.RaiseLocalEvent(mag, new InteractUsingEvent(p.Mob, round, mag, ctx.Coords));
        }
        Assert.That(ctx.Ent.GetComponent<BallisticAmmoProviderComponent>(mag).Count, Is.EqualTo(count));
    }

    private static void SetBoxCount(Ctx ctx, EntityUid box, int count)
    {
        var ammo = ctx.Ent.GetComponent<BallisticAmmoProviderComponent>(box);
        ctx.Server.System<SharedGunSystem>().SetBallisticUnspawned((box, ammo), count);
    }

    private static async Task Snapshot(Ctx ctx)
    {
        await ctx.Server.WaitPost(() => ctx.Cr.ForceSnapshot());
        await Flush(ctx);
    }

    [Test]
    public async Task FullKitComesBackExactly()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrFullKit");
        Dictionary<string, int> original = default!;
        float flashlightCharge = 0;
        float bottleVolume = 0;

        await ctx.Server.WaitPost(() =>
        {
            Equip(ctx, p, "ClothingHeadHelmetBasic", "head");
            Equip(ctx, p, "ClothingMaskGas", "mask");
            Equip(ctx, p, "ClothingEyesGlassesSunglasses", "eyes");
            Equip(ctx, p, "ClothingHeadsetGrey", "ears");
            Equip(ctx, p, "ClothingNeckScarfStripedRed", "neck");
            Equip(ctx, p, "ClothingTorsoJaba", "torso");
            Equip(ctx, p, "STClothingLegsJeansCz", "legs");
            Equip(ctx, p, "ClothingOuterCoatDoshdevik", "cloak");
            Equip(ctx, p, "ClothingNeckDogtagBrigand", "dogtag");
            Equip(ctx, p, "ClothingOuterArmorExoskelet", "outerClothing");
            Equip(ctx, p, "ClothingHandsGlovesColorBlack", "gloves");
            Equip(ctx, p, "ClothingShoesBootsJack", "shoes");
            Equip(ctx, p, "ClothingBeltUtility", "belt");
            Equip(ctx, p, "ClothingBackpack", "back");

            var inv = ctx.Server.System<InventorySystem>();
            var roubles = Spawn(ctx, "Roubles");
            ctx.Server.System<SharedStackSystem>().SetCount(roubles, 1337);
            Assert.That(inv.TryEquip(p.Mob, roubles, "pocket1", force: true));
            var paper = Spawn(ctx, "Paper");
            ctx.Server.System<PaperSystem>().SetContent(paper, "crash test note");
            Assert.That(inv.TryEquip(p.Mob, paper, "pocket2", force: true));

            Assert.That(inv.TryGetSlotEntity(p.Mob, "back", out var backpack));

            Put(ctx, backpack!.Value, Spawn(ctx, "MedkitFilled"));
            Put(ctx, backpack.Value, Spawn(ctx, "ArtMedusaActive1"));

            var bottle = Spawn(ctx, "DrinkWaterBottleFull");
            var solutions = ctx.Server.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(bottle, "drink", out var drink, out _));
            solutions.SplitSolution(drink!.Value, 10);
            bottleVolume = drink.Value.Comp.Solution.Volume.Float();
            Put(ctx, backpack.Value, bottle);

            var flashlight = Spawn(ctx, "FlashlightLantern");
            Put(ctx, backpack.Value, flashlight);
            var batteries = ctx.Server.System<PredictedBatterySystem>();
            var cell = Carried(ctx.Ent, p.Mob).First(e => ctx.Ent.HasComponent<PredictedBatteryComponent>(e));
            var battery = ctx.Ent.GetComponent<PredictedBatteryComponent>(cell);
            batteries.SetCharge((cell, battery), battery.MaxCharge / 3);
            flashlightCharge = batteries.GetCharge((cell, battery));
            Put(ctx, backpack.Value, Spawn(ctx, "PowerCellMedium"));

            var spareMag = Spawn(ctx, "545Mag30");
            LoadMagazine(ctx, p, spareMag, 23);
            Put(ctx, backpack.Value, spareMag);
            var box = Spawn(ctx, "545FMJBox");
            SetBoxCount(ctx, box, 13);
            Put(ctx, backpack.Value, box);
            Put(ctx, backpack.Value, Spawn(ctx, "STBoltBagFilled"));

            var crowbar = Spawn(ctx, "Crowbar");
            ctx.Server.System<LabelSystem>().Label(crowbar, "mine");
            Put(ctx, backpack.Value, crowbar);

            var outer = Spawn(ctx, "CrTestPouch");
            Put(ctx, backpack.Value, outer);
            var containers = ctx.Server.System<SharedContainerSystem>();
            var current = outer;
            for (var i = 0; i < 7; i++)
            {
                var next = Spawn(ctx, "CrTestPouch");
                containers.Insert(next, containers.GetContainer(current, "storagebase"));
                current = next;
            }
            containers.Insert(Spawn(ctx, "Pen"), containers.GetContainer(current, "storagebase"));

            var rifle = Hold(ctx, p, "STWeaponRifleAKS74");
            var slots = ctx.Server.System<ItemSlotsSystem>();
            if (slots.TryGetSlot(rifle, "gun_magazine", out var magSlot) && magSlot.Item is { } stock)
                ctx.Ent.DeleteEntity(stock);
            var mag = Spawn(ctx, "545Mag30");
            LoadMagazine(ctx, p, mag, 17);
            Assert.That(slots.TryInsert(rifle, "gun_magazine", mag, null));
            if (ctx.Ent.TryGetComponent<WieldableComponent>(rifle, out var wieldable))
                ctx.Server.System<SharedWieldableSystem>().TryWield(rifle, wieldable, p.Mob);

            original = Tally(ctx.Ent, Carried(ctx.Ent, p.Mob));
            ctx.Cr.ForceSnapshot();
        });

        await Crash(ctx, p);
        var spawned = await Claim(ctx, p);

        await ctx.Server.WaitAssertion(() =>
        {
            Assert.That(Describe(Tally(ctx.Ent, spawned)), Is.EqualTo(Describe(original)));

            var paper = spawned.Single(e => ctx.Ent.HasComponent<PaperComponent>(e));
            Assert.That(ctx.Ent.GetComponent<PaperComponent>(paper).Content, Is.EqualTo("crash test note"));

            var crowbar = spawned.Single(e => ctx.Ent.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "Crowbar");
            Assert.That(ctx.Ent.GetComponent<LabelComponent>(crowbar).CurrentLabel, Is.EqualTo("mine"));

            var batteries = ctx.Server.System<PredictedBatterySystem>();
            var charges = spawned.Where(e => ctx.Ent.HasComponent<PredictedBatteryComponent>(e))
                .Select(e => batteries.GetCharge((e, ctx.Ent.GetComponent<PredictedBatteryComponent>(e)))).ToList();
            Assert.That(charges.Any(c => Math.Abs(c - flashlightCharge) < 1f), $"charges {string.Join(",", charges)} vs {flashlightCharge}");

            var bottle = spawned.Single(e => ctx.Ent.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "DrinkWaterBottleFull");
            Assert.That(ctx.Server.System<SharedSolutionContainerSystem>().TryGetSolution(bottle, "drink", out var drink, out _));
            Assert.That(drink!.Value.Comp.Solution.Volume.Float(), Is.EqualTo(bottleVolume).Within(0.01f));
        });

        Assert.That(await Backup(ctx, p), Is.Null);
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task NakedPlayerHasNothingToClaim()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrNaked");
        await Snapshot(ctx);
        Assert.That(await Backup(ctx, p), Is.Null);
        await Crash(ctx, p);
        Assert.That(Tally(ctx.Ent, await Claim(ctx, p)), Is.Empty);
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClaimWithoutCrashGivesNothing()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrNoCrash");
        await ctx.Server.WaitPost(() => Equip(ctx, p, "ClothingHeadHelmetBasic", "head"));
        await Snapshot(ctx);
        var backup = await Backup(ctx, p);
        Assert.That(backup, Is.Not.Null);

        Assert.That(Tally(ctx.Ent, await Claim(ctx, p, 5)), Is.Empty);
        Assert.That(await Backup(ctx, p), Is.EqualTo(backup));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClaimSpamAndRepeatGiveOneSet()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrSpam");
        await ctx.Server.WaitPost(() => Equip(ctx, p, "ClothingHeadHelmetBasic", "head"));
        await Snapshot(ctx);
        await Crash(ctx, p);

        var first = Tally(ctx.Ent, await Claim(ctx, p, 20));
        Assert.That(Describe(first), Is.EqualTo("ClothingHeadHelmetBasic=1"));

        await Snapshot(ctx);
        await ctx.Pair.RunSeconds(4);
        Assert.That(Tally(ctx.Ent, await Claim(ctx, p, 5)), Is.Empty);
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task PlayersOnlyClaimTheirOwnBackup()
    {
        var ctx = await Setup();
        var a = await AddPlayer(ctx, "CrOwnerA");
        var b = await AddPlayer(ctx, "CrOwnerB");
        await ctx.Server.WaitPost(() => Equip(ctx, a, "ClothingHeadHelmetBasic", "head"));
        await Snapshot(ctx);
        await Crash(ctx, a, b);

        var stolen = new List<EntityUid>();
        var before = new HashSet<EntityUid>();
        await ctx.Server.WaitPost(() =>
        {
            before.UnionWith(ctx.Ent.GetEntities());
            ctx.Ent.EventBus.RaiseLocalEvent(a.Stash, new CrashRecoveryClaimMessage { Actor = b.Mob });
        });
        await ctx.Pair.RunTicksSync(5);
        await ctx.Server.WaitPost(() => stolen.AddRange(ctx.Ent.GetEntities().Where(e => !before.Contains(e))));

        Assert.That(Tally(ctx.Ent, stolen), Is.Empty);
        Assert.That(await Backup(ctx, a), Is.Not.Null);
        Assert.That(Describe(Tally(ctx.Ent, await Claim(ctx, a))), Is.EqualTo("ClothingHeadHelmetBasic=1"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task UnclaimedBackupIsNotOverwritten()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrUnclaimed");
        await ctx.Server.WaitPost(() => Equip(ctx, p, "ClothingHeadHelmetBasic", "head"));
        await Snapshot(ctx);
        await Crash(ctx, p);

        await ctx.Server.WaitPost(() => Hold(ctx, p, "Crowbar"));
        await ctx.Pair.RunSeconds(4);
        await Snapshot(ctx);
        await Crash(ctx, p);

        Assert.That(Describe(Tally(ctx.Ent, await Claim(ctx, p))), Is.EqualTo("ClothingHeadHelmetBasic=1"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task DepositThenCrashDoesNotDuplicate()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrDeposit");
        await ctx.Server.WaitPost(() =>
        {
            Equip(ctx, p, "ClothingHeadHelmetBasic", "head");
            var crowbar = Hold(ctx, p, "Crowbar");
            ctx.Cr.ForceSnapshot();
            ctx.Ent.EventBus.RaiseLocalEvent(p.Stash, new InteractUsingEvent(p.Mob, crowbar, p.Stash, ctx.Coords));
        });
        await Crash(ctx, p);

        Assert.That(Describe(Tally(ctx.Ent, await Claim(ctx, p))), Is.EqualTo("ClothingHeadHelmetBasic=1"));
        Assert.That(await ctx.Db.GetLoginItems("PB" + p.Login), Does.Contain("Crowbar"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task WithdrawThenCrashKeepsTheItem()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrWithdraw");
        await ctx.Server.WaitPost(() =>
        {
            var crowbar = Hold(ctx, p, "Crowbar");
            ctx.Ent.EventBus.RaiseLocalEvent(p.Stash, new InteractUsingEvent(p.Mob, crowbar, p.Stash, ctx.Coords));
        });
        await Flush(ctx);

        await ctx.Server.WaitPost(() =>
        {
            var stash = ctx.Ent.GetComponent<StalkerRepositoryComponent>(p.Stash);
            var info = stash.ContainedItems.First(i => i.ProductEntity == "Crowbar");
            ctx.Ent.EventBus.RaiseLocalEvent(p.Stash, new RepositoryEjectMessage(info, 1)
            {
                Actor = p.Mob,
                Entity = ctx.Ent.GetNetEntity(p.Stash),
            });
        });
        await Crash(ctx, p);

        Assert.That(Describe(Tally(ctx.Ent, await Claim(ctx, p))), Is.EqualTo("Crowbar=1"));
        Assert.That(await ctx.Db.GetLoginItems("PB" + p.Login), Does.Not.Contain("Crowbar"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task LoadoutCompletionSavesImmediately()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrLoadout");
        await ctx.Server.WaitPost(() =>
        {
            Equip(ctx, p, "ClothingHeadHelmetBasic", "head");
            var ev = new LoadoutOperationCompletedEvent(p.Mob, p.Stash);
            ctx.Ent.EventBus.RaiseLocalEvent(p.Stash, ref ev);
        });
        await Crash(ctx, p);
        Assert.That(Describe(Tally(ctx.Ent, await Claim(ctx, p))), Is.EqualTo("ClothingHeadHelmetBasic=1"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task NewLootIsSavedWithinSeconds()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrLoot");
        await ctx.Server.WaitPost(() =>
        {
            Equip(ctx, p, "ClothingBackpack", "back");
            ctx.Server.System<InventorySystem>().TryGetSlotEntity(p.Mob, "back", out var backpack);
            Put(ctx, backpack!.Value, Spawn(ctx, "Crowbar"));
        });
        await ctx.Pair.RunSeconds(4);
        await Crash(ctx, p);
        Assert.That(Describe(Tally(ctx.Ent, await Claim(ctx, p))), Is.EqualTo("ClothingBackpack=1, Crowbar=1"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task LootedCorpseIsNotDuplicated()
    {
        var ctx = await Setup();
        var a = await AddPlayer(ctx, "CrCorpseA");
        var b = await AddPlayer(ctx, "CrCorpseB");
        await ctx.Server.WaitPost(() => Equip(ctx, a, "ClothingHeadHelmetBasic", "head"));
        await Snapshot(ctx);

        await ctx.Server.WaitPost(() =>
        {
            ctx.Server.System<MobStateSystem>().ChangeMobState(a.Mob, MobState.Dead);
            Assert.That(ctx.Server.System<InventorySystem>().TryUnequip(a.Mob, "head", out var helmet, force: true));
            Assert.That(ctx.Server.System<SharedHandsSystem>().TryPickupAnyHand(b.Mob, helmet!.Value));
        });
        await ctx.Pair.RunSeconds(4);
        await Crash(ctx, a, b);

        var total = Tally(ctx.Ent, (await Claim(ctx, a)).Concat(await Claim(ctx, b)));
        Assert.That(Describe(total), Is.EqualTo("ClothingHeadHelmetBasic=1"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task LeavingYourBodyClearsTheBackup()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrGhost");
        await ctx.Server.WaitPost(() => Equip(ctx, p, "ClothingHeadHelmetBasic", "head"));
        await Snapshot(ctx);
        Assert.That(await Backup(ctx, p), Is.Not.Null);

        await ctx.Server.WaitPost(() =>
        {
            var ghost = Spawn(ctx, "MobObserver");
            ctx.Server.System<SharedMindSystem>().TransferTo(p.Mind, ghost);
        });
        await ctx.Pair.RunSeconds(4);
        Assert.That(await Backup(ctx, p), Is.Null);
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task LootedDisconnectedBodyIsNotDuplicated()
    {
        var ctx = await Setup();
        var a = await AddPlayer(ctx, "CrSsdA");
        var b = await AddPlayer(ctx, "CrSsdB");
        await ctx.Server.WaitPost(() => Equip(ctx, a, "ClothingHeadHelmetBasic", "head"));
        await Snapshot(ctx);

        await ctx.Server.WaitPost(() =>
        {
            ctx.Server.PlayerMan.SetAttachedEntity(a.Session, null);
            Assert.That(ctx.Server.System<InventorySystem>().TryUnequip(a.Mob, "head", out var helmet, force: true));
            Assert.That(ctx.Server.System<SharedHandsSystem>().TryPickupAnyHand(b.Mob, helmet!.Value));
        });
        await ctx.Pair.RunSeconds(4);
        await Snapshot(ctx);
        await Crash(ctx, a, b);

        var total = Tally(ctx.Ent, (await Claim(ctx, a)).Concat(await Claim(ctx, b)));
        Assert.That(Describe(total), Is.EqualTo("ClothingHeadHelmetBasic=1"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task HandOffIsNotDuplicated()
    {
        var ctx = await Setup();
        var a = await AddPlayer(ctx, "CrHandA");
        var b = await AddPlayer(ctx, "CrHandB");
        EntityUid crowbar = default;
        await ctx.Server.WaitPost(() => crowbar = Hold(ctx, a, "Crowbar"));
        await Snapshot(ctx);

        await ctx.Server.WaitPost(() =>
        {
            var hands = ctx.Server.System<SharedHandsSystem>();
            Assert.That(hands.TryDrop(a.Mob, crowbar));
            Assert.That(hands.TryPickupAnyHand(b.Mob, crowbar));
        });
        await ctx.Pair.RunSeconds(4);
        await Crash(ctx, a, b);

        var total = Tally(ctx.Ent, (await Claim(ctx, a)).Concat(await Claim(ctx, b)));
        Assert.That(Describe(total), Is.EqualTo("Crowbar=1"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task SellingThenCrashingNeverDoubles([Values(false, true)] bool waitForSave)
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, waitForSave ? "CrSellLate" : "CrSellEarly");
        EntityUid crowbar = default;
        await ctx.Server.WaitPost(() => crowbar = Hold(ctx, p, "Crowbar"));
        await Snapshot(ctx);

        await ctx.Server.WaitPost(() =>
        {
            ctx.Ent.DeleteEntity(crowbar);
            var money = Hold(ctx, p, "Roubles");
            ctx.Server.System<SharedStackSystem>().SetCount(money, 500);
        });
        if (waitForSave)
            await ctx.Pair.RunSeconds(4);
        await Crash(ctx, p);

        var got = Tally(ctx.Ent, await Claim(ctx, p));
        Assert.That(Describe(got), Is.EqualTo(waitForSave ? "Roubles=500" : "Crowbar=1"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task CorruptedBackupIsKeptForAdmins()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrCorrupt");
        ctx.Server.ResolveDependency<ILogManager>().GetSawmill("crash-recovery").Level = LogLevel.Fatal;
        await ctx.Db.SetCrashRecovery(p.Login, "{broken");
        await Crash(ctx, p);

        Assert.That(Tally(ctx.Ent, await Claim(ctx, p)), Is.Empty);
        Assert.That(await Backup(ctx, p), Is.EqualTo("{broken"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task RoundRestartWipesBackups()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrRestart");
        await ctx.Server.WaitPost(() => Equip(ctx, p, "ClothingHeadHelmetBasic", "head"));
        await Snapshot(ctx);
        await Crash(ctx, p);
        Assert.That(await Backup(ctx, p), Is.Not.Null);

        await ctx.Server.WaitPost(() => ctx.Ent.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent()));
        Assert.That(await Backup(ctx, p), Is.Null);
        Assert.That(Tally(ctx.Ent, await Claim(ctx, p)), Is.Empty);
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task LoadoutLoadThenCrashGivesTheLoadoutOnce()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrLoadoutFlow");
        await ctx.Db.SetLoginItems("PB" + p.Login, StalkerDbSystem.DefaultStalkerItems);
        await ctx.Server.WaitPost(() =>
        {
            Equip(ctx, p, "ClothingHeadHelmetBasic", "head");
            Equip(ctx, p, "ClothingShoesBootsJack", "shoes");
            ctx.Ent.EventBus.RaiseLocalEvent(p.Stash, new LoadoutSaveMessage("", true)
            {
                Actor = p.Mob,
                Entity = ctx.Ent.GetNetEntity(p.Stash),
            });
        });
        await ctx.Pair.RunTicksSync(10);
        await Flush(ctx);

        await ctx.Server.WaitPost(() =>
        {
            var inv = ctx.Server.System<InventorySystem>();
            var hands = ctx.Server.System<SharedHandsSystem>();
            foreach (var slot in new[] { "head", "shoes" })
            {
                Assert.That(inv.TryUnequip(p.Mob, slot, out var item, force: true));
                Assert.That(hands.TryPickupAnyHand(p.Mob, item!.Value));
                ctx.Ent.EventBus.RaiseLocalEvent(p.Stash, new InteractUsingEvent(p.Mob, item.Value, p.Stash, ctx.Coords));
            }
            Assert.That(Tally(ctx.Ent, Carried(ctx.Ent, p.Mob)), Is.Empty);
        });
        await ctx.Pair.RunSeconds(4);

        await ctx.Server.WaitPost(() => ctx.Ent.EventBus.RaiseLocalEvent(p.Stash, new LoadoutLoadMessage(0)
        {
            Actor = p.Mob,
            Entity = ctx.Ent.GetNetEntity(p.Stash),
        }));
        await ctx.Pair.RunTicksSync(10);
        await ctx.Server.WaitAssertion(() =>
            Assert.That(Describe(Tally(ctx.Ent, Carried(ctx.Ent, p.Mob))), Is.EqualTo("ClothingHeadHelmetBasic=1, ClothingShoesBootsJack=1")));

        await Crash(ctx, p);

        var claimed = await Claim(ctx, p);
        Assert.That(Describe(Tally(ctx.Ent, claimed)), Is.EqualTo("ClothingHeadHelmetBasic=1, ClothingShoesBootsJack=1"));
        var stash = await ctx.Db.GetLoginItems("PB" + p.Login);
        Assert.That(stash, Does.Not.Contain("ClothingHeadHelmetBasic").And.Not.Contain("ClothingShoesBootsJack"));

        await ctx.Server.WaitAssertion(() =>
        {
            var xforms = ctx.Server.System<SharedTransformSystem>();
            var feet = xforms.GetMapCoordinates(p.Mob);
            foreach (var item in claimed.Where(e => ctx.Ent.HasComponent<ItemComponent>(e)))
                Assert.That(xforms.GetMapCoordinates(item), Is.EqualTo(feet), "claimed items spawn at the claimer's feet");
        });
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClaimThenStashThenCrashGivesNothingBack()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrChain");
        await ctx.Server.WaitPost(() => Hold(ctx, p, "Crowbar"));
        await Snapshot(ctx);
        await Crash(ctx, p);

        var claimed = await Claim(ctx, p);
        await ctx.Server.WaitPost(() =>
        {
            var crowbar = claimed.Single(e => ctx.Ent.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "Crowbar");
            Assert.That(ctx.Server.System<SharedHandsSystem>().TryPickupAnyHand(p.Mob, crowbar));
            ctx.Ent.EventBus.RaiseLocalEvent(p.Stash, new InteractUsingEvent(p.Mob, crowbar, p.Stash, ctx.Coords));
        });
        await Crash(ctx, p);

        Assert.That(Tally(ctx.Ent, await Claim(ctx, p)), Is.Empty);
        Assert.That(await ctx.Db.GetLoginItems("PB" + p.Login), Does.Contain("Crowbar"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task RespawnReplacesTheOldBackup()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrRespawn");
        await ctx.Server.WaitPost(() => Equip(ctx, p, "ClothingHeadHelmetBasic", "head"));
        await Snapshot(ctx);

        await ctx.Server.WaitPost(() =>
        {
            ctx.Server.System<MobStateSystem>().ChangeMobState(p.Mob, MobState.Dead);
            p.Mob = Spawn(ctx, "MobHuman");
            ctx.Server.System<SharedMindSystem>().TransferTo(p.Mind, p.Mob);
            Equip(ctx, p, "ClothingShoesBootsJack", "shoes");
        });
        await ctx.Pair.RunSeconds(4);
        await Crash(ctx, p);

        Assert.That(Describe(Tally(ctx.Ent, await Claim(ctx, p))), Is.EqualTo("ClothingShoesBootsJack=1"));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task SavingFortyKittedPlayersIsCheap()
    {
        var ctx = await Setup();
        var players = new List<Player>();
        for (var i = 0; i < 40; i++)
            players.Add(await AddPlayer(ctx, $"CrLoad{i}"));

        double ms = 0;
        await ctx.Server.WaitPost(() =>
        {
            foreach (var p in players)
            {
                Equip(ctx, p, "ClothingHeadHelmetBasic", "head");
                Equip(ctx, p, "ClothingOuterArmorExoskelet", "outerClothing");
                Equip(ctx, p, "ClothingBackpack", "back");
                ctx.Server.System<InventorySystem>().TryGetSlotEntity(p.Mob, "back", out var backpack);
                foreach (var proto in new[] { "MedkitFilled", "545FMJBox", "PowerCellMedium", "STBoltBagFilled", "Crowbar" })
                    Put(ctx, backpack!.Value, Spawn(ctx, proto));
                Hold(ctx, p, "STWeaponRifleAKS74");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ctx.Cr.ForceSnapshot();
            ms = sw.Elapsed.TotalMilliseconds;
        });
        await Flush(ctx);

        TestContext.Out.WriteLine($"Snapshot of 40 kitted players took {ms:F1} ms");
        Assert.That(ms, Is.LessThan(250));
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task OldStashCellsStackWithNewOnes()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrOldCells");
        await ctx.Server.WaitAssertion(() =>
        {
            var proto = ctx.Server.ProtoMan.Index<EntityPrototype>("PowerCellMedium");
            Assert.That(proto.TryGetComponent<PredictedBatteryComponent>(out var cell, ctx.Ent.ComponentFactory));
            float start = cell!.StartingCharge;
            var charge = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var json = "{\"AllItems\":[" +
                       "{\"ClassType\":\"SolutionItemStalker\",\"PrototypeName\":\"PowerCellMedium\",\"CountVendingMachine\":1,\"Contents\":{}}," +
                       "{\"ClassType\":\"BatteryItemStalker\",\"PrototypeName\":\"PowerCellMedium\",\"CountVendingMachine\":1,\"CurrentCharge\":" + charge + "}" +
                       "]}";

            var stash = ctx.Ent.GetComponent<StalkerRepositoryComponent>(p.Stash);
            stash.LoadedDbJson = json;
            ctx.Server.System<Content.Server._Stalker.Storage.StalkerStorageSystem>().LoadStalkerItemsByEntityUid(p.Stash);

            var cells = stash.ContainedItems.Where(i => i.ProductEntity == "PowerCellMedium").ToList();
            Assert.That(cells, Has.Count.EqualTo(1), "old and new cells should be one stack");
            Assert.That(cells[0].Count, Is.EqualTo(2));
        });
        await ctx.Pair.CleanReturnAsync();
    }

    [Test]
    public async Task RapidSavesLandInOrder()
    {
        var ctx = await Setup();
        var p = await AddPlayer(ctx, "CrOrder");
        await ctx.Server.WaitPost(() =>
        {
            var inv = ctx.Server.System<InventorySystem>();
            for (var i = 0; i < 25; i++)
            {
                Equip(ctx, p, "ClothingHeadHelmetBasic", "head");
                ctx.Cr.ImmediateSnapshot(p.Mob);
                Assert.That(inv.TryUnequip(p.Mob, "head", out var helmet, force: true));
                ctx.Ent.DeleteEntity(helmet!.Value);
                ctx.Cr.ImmediateSnapshot(p.Mob);
            }
            Equip(ctx, p, "ClothingShoesBootsJack", "shoes");
            ctx.Cr.ImmediateSnapshot(p.Mob);
        });
        await Crash(ctx, p);
        Assert.That(Describe(Tally(ctx.Ent, await Claim(ctx, p))), Is.EqualTo("ClothingShoesBootsJack=1"));
        await ctx.Pair.CleanReturnAsync();
    }
}
