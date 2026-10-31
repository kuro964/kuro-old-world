using Content.Shared.Inventory;
using Content.Shared.Inventory.ArtifactSlots;
using Robust.Client.Player;

namespace Content.Client.Inventory.ArtifactSlots;

/// <summary>
/// Handles client-side UI updates for artifact slots.
/// Listens for server state changes and updates the <c>Blocked</c> state of artifact slot buttons
/// based on how many artifact slots are currently active from equipped armor.
/// </summary>
public sealed class ClientArtifactSlotSystem : EntitySystem
{
    [Dependency] private readonly ClientInventorySystem _clientInventory = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly SharedArtifactSlotSystem _artifactSlots = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<ArtifactSlotsComponent, AfterAutoHandleStateEvent>(OnArtifactSlotsStateChanged);
        SubscribeLocalEvent<ArtifactSlotsComponent, ComponentShutdown>(OnArtifactSlotsShutdown);

        _clientInventory.OnLinkInventorySlots += OnLinkInventorySlots;
    }

    public override void Shutdown()
    {
        _clientInventory.OnLinkInventorySlots -= OnLinkInventorySlots;
        base.Shutdown();
    }

    private void OnArtifactSlotsStateChanged(EntityUid uid, ArtifactSlotsComponent component, ref AfterAutoHandleStateEvent args)
    {
        UpdateArtifactSlotBlocked(uid);
    }

    private void OnArtifactSlotsShutdown(EntityUid uid, ArtifactSlotsComponent component, ComponentShutdown args)
    {
        UpdateArtifactSlotBlocked(uid, shuttingDown: true); // ST:OW
    }

    private void OnLinkInventorySlots(EntityUid uid, InventorySlotsComponent slots)
    {
        UpdateArtifactSlotBlocked(uid);
    }

    /// <summary>
    /// Updates the blocked state of all artifact slots on the given entity
    /// based on the current <see cref="ArtifactSlotsComponent.ActiveCount"/>.
    /// </summary>
    private void UpdateArtifactSlotBlocked(EntityUid uid, bool shuttingDown = false) // ST:OW
    {
        if (_playerManager.LocalEntity != uid)
            return;

        if (!TryComp<InventoryComponent>(uid, out var inv))
            return;

        if (!TryComp<InventorySlotsComponent>(uid, out var slots))
            return;

        var activeCount = 0;
        if (!shuttingDown && TryComp<ArtifactSlotsComponent>(uid, out var artifactSlots)) // ST:OW
        {
            activeCount = artifactSlots.ActiveCount;
        }

        var sortedSlots = _artifactSlots.GetArtifactSlotsSorted(inv);

        for (var i = 0; i < sortedSlots.Count; i++)
        {
            var slotName = sortedSlots[i];
            var blocked = i >= activeCount;
            _clientInventory.UpdateSlot(uid, slots, slotName, blocked: blocked);
        }
    }
}
