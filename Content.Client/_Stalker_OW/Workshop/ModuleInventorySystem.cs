using Content.Client.Examine;
using Content.Client.Inventory;
using Content.Client.UserInterface.Systems.Inventory;
using Content.Shared._Stalker_OW.Workshop;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Shared.Containers;

namespace Content.Client._Stalker_OW.Workshop;

// Updates module displays when equipment or module data changes
public sealed class ModuleInventorySystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IUserInterfaceManager _uiManager = default!;
    [Dependency] private readonly ClientInventorySystem _inventory = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly StatsExamineSystem _stats = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private bool _hotbarPending;
    private readonly HashSet<EntityUid> _workbenchesPending = new();

    public override void Initialize()
    {
        // The shared system rebuilds the modifiers before sending this event
        SubscribeLocalEvent<EquipmentModificationComponent, EquipmentModulesChangedEvent>(OnModulesChanged);
        SubscribeLocalEvent<EquipmentModificationComponent, ComponentShutdown>(OnShutdown);
        _inventory.OnSpriteUpdate += OnEquipmentChanged;
    }

    public override void Shutdown()
    {
        _inventory.OnSpriteUpdate -= OnEquipmentChanged;
        _workbenchesPending.Clear();
        _hotbarPending = false;
        base.Shutdown();
    }

    private void OnModulesChanged(Entity<EquipmentModificationComponent> ent, ref EquipmentModulesChangedEvent args)
    {
        ModulesChanged(ent);
    }

    private void OnShutdown(Entity<EquipmentModificationComponent> ent, ref ComponentShutdown args)
    {
        ModulesChanged(ent);
    }

    private void OnEquipmentChanged(ClientInventorySystem.SlotSpriteUpdate update)
    {
        _hotbarPending = true;
        _stats.RefreshEquippedStats();
    }

    private void ModulesChanged(EntityUid uid)
    {
        _stats.RefreshModuleStats(uid);

        if (TryComp<TransformComponent>(uid, out var transform) && transform.ParentUid == _players.LocalEntity)
            _hotbarPending = true;

        // Wait until state handling and component shutdown finish before refreshing
        if (_containers.TryGetContainingContainer(uid, out var container))
            _workbenchesPending.Add(container.Owner);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_hotbarPending)
        {
            _hotbarPending = false;
            _uiManager.GetUIController<InventoryUIController>().RefreshModuleSlots();
        }

        foreach (var bench in _workbenchesPending)
        {
            if (!TerminatingOrDeleted(bench) &&
                _ui.TryGetOpenUi<WorkshopBoundUserInterface>(bench, WorkshopUiKey.Key, out var bui))
            {
                bui.Refresh();
            }
        }
        _workbenchesPending.Clear();
    }
}
