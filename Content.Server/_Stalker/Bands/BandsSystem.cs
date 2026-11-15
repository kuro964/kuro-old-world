using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Mind;
using Content.Shared._Stalker.Bands;
using Content.Shared.Roles;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Content.Shared.Actions;
using Content.Shared.Mobs.Systems;
using Content.Shared.StatusIcon.Components;
using Content.Server.Players.JobWhitelist;
using Content.Shared._Stalker.Bands.Components;
using Content.Server._Stalker.WarZone;
using Content.Server._Stalker_EN.FactionRelations; // stalker-en-changes
using Content.Shared._Stalker_EN.FactionRelations; // stalker-en-changes
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.Player;
using Content.Shared._Stalker_EN.Portraits;
using Content.Shared.IdentityManagement; // ST:OW

namespace Content.Server._Stalker.Bands
{
    public sealed partial class BandsSystem : SharedBandsSystem
    {
        [Dependency] private readonly IServerDbManager _dbManager = default!;
        [Dependency] private readonly MindSystem _mindSystem = default!;
        [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
        [Dependency] private readonly SharedActionsSystem _actions = default!;
        [Dependency] private readonly MobStateSystem _mobState = default!;
        [Dependency] private readonly JobWhitelistManager _jobWhitelistManager = default!;
        [Dependency] private readonly WarZoneSystem _warZoneSystem = default!;
        [Dependency] private readonly SharedHandsSystem _hands = default!;
        [Dependency] private readonly IEntityManager _entityManager = default!;
        [Dependency] private readonly ISharedPlayerManager _player = default!;
        [Dependency] private readonly STFactionRelationsCartridgeSystem _factionRelations = default!; // stalker-en-changes
        [Dependency] private readonly IdentitySystem _identity = default!; // ST:OW

        private Dictionary<EntityUid, List<BandShopItem>> _loadedShopItems = new();


        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<BandsManagingComponent, BoundUIOpenedEvent>(HandleBoundUIOpen);
            SubscribeLocalEvent<BandsManagingComponent, ComponentStartup>(SubscribeUpdateUiState);
            SubscribeLocalEvent<BandsManagingComponent, BandsManagingAddMemberMessage>(OnAddMember);
            SubscribeLocalEvent<BandsManagingComponent, BandsManagingRemoveMemberMessage>(OnRemoveMember);

            SubscribeLocalEvent<BandsComponent, ComponentInit>(OnInit);

            SubscribeLocalEvent<BandsManagingComponent, BandsManagingBuyItemMessage>(OnBuyItem);
            // stalker-en-changes start
            SubscribeLocalEvent<BandsManagingComponent, BandsManagingProposeRelationMessage>(OnProposeRelation);
            SubscribeLocalEvent<BandsManagingComponent, BandsManagingRespondProposalMessage>(OnRespondProposal);
            SubscribeLocalEvent<BandsManagingComponent, BandsManagingCancelProposalMessage>(OnCancelProposal);
            // stalker-en-changes end

            SubscribeLocalEvent<BandsComponent, ChangeBandEvent>(OnChange);

            InitializeMembers(); // ST:OW
        }

        private void SubscribeUpdateUiState<T>(Entity<BandsManagingComponent> ent, ref T ev) where T : notnull
        {
            // Call UpdateUiState, passing null for actor as ComponentStartup doesn't have one readily available.
            // UpdateUiState will attempt to find a session from the UI itself.
            UpdateUiState(ent);
        }

        private void HandleBoundUIOpen(Entity<BandsManagingComponent> ent, ref BoundUIOpenedEvent args)
        {
            UpdateUiState(ent, args.Actor);
        }

        private bool TryGetSession(EntityUid uid, [NotNullWhen(true)] out ICommonSession? session)
        {
            if (!_mindSystem.TryGetMind(uid, out _, out var mindComp) || !_player.TryGetSessionById(mindComp.UserId, out var currentSession))
            {
                session = null;
                return false;
            }

            session = currentSession;

            return true;
        }

        private void LoadShopItems(Entity<BandsManagingComponent> ent)
        {
            var (uid, component) = ent;
            if (_prototypeManager.TryIndex(component.ShopListingsProto, out var shopProto))
            {
                _loadedShopItems[uid] = shopProto.Items;
            }
            else
            {
                Logger.ErrorS("bands", $"Failed to load BandShopListingsPrototype with ID {component.ShopListingsProto} for entity {ToPrettyString(uid)}");
                _loadedShopItems[uid] = new List<BandShopItem>(); // Ensure the key exists even if loading fails
            }
        }

        // --- Methods from SharedBandsSystem ---

        private void OnInit(EntityUid uid, BandsComponent component, ComponentInit args)
        {
            EnsureComp<StatusIconComponent>(uid);

            _identity.QueueIdentityUpdate(uid);

            // ST:OW begin
            // only entities with an alternative patch and permission to change it receive the patch-swapping action
            if (component is not { AltBand: not null, CanChange: true })
                return;

            _actions.AddAction(
                uid,
                ref component.ActionChangeEntity,
                component.ActionChange,
                uid);

            // only factions configured to start disguised should automatically equip their alternative patch
            if (!component.StartDisguised)
                return;

            (component.BandStatusIcon, component.AltBand) =
                (component.AltBand, component.BandStatusIcon);

            component.IsDisguised = true;

            if (TryComp<CharacterPortraitComponent>(uid, out var portraitComp))
            {
                portraitComp.IsDisguised = true;
                Dirty(uid, portraitComp);
            }

            Dirty(uid, component);
            // ST:OW end
        }

        private void OnChange(Entity<BandsComponent> entity, ref ChangeBandEvent args)
        {
            if (args.Handled)
                return;

            var comp = entity.Comp;
            if (comp.AltBand == null || !comp.CanChange)
                return;
            // stalker-en-start
            // Swap the band status icon and alt band name
            (comp.BandStatusIcon, comp.AltBand) = (comp.AltBand, comp.BandStatusIcon);
            comp.IsDisguised = !comp.IsDisguised;

            // Also update CharacterPortraitComponent for portrait disguise
            if (TryComp<CharacterPortraitComponent>(entity, out var portraitComp))
            {
                portraitComp.IsDisguised = comp.IsDisguised;
                Dirty(entity, portraitComp);
            }
            // stalker-en-end
            Dirty(entity);
            _identity.QueueIdentityUpdate(entity); // ST:OW
            args.Handled = true;
        }

        // stalker-en-changes start
        private void OnProposeRelation(EntityUid uid, BandsManagingComponent component, BandsManagingProposeRelationMessage msg)
        {
            try
            {
                var playerFaction = ResolvePlayerFaction(component, msg.Actor); // ST:OW
                if (playerFaction == null)
                {
                    Popup(msg.Actor, "stow-factions-leader-required"); // ST:OW
                    return;
                }

                var relationType = (STFactionRelationType) msg.ProposedRelation;
                if (!Enum.IsDefined(relationType))
                    return;

                _factionRelations.TryChangeRelation(
                    msg.Actor,
                    playerFaction,
                    msg.TargetFaction,
                    relationType,
                    msg.CustomMessage,
                    msg.Broadcast);

                UpdateUiState((uid, component), msg.Actor);
            }
            catch (Exception e)
            {
                Logger.ErrorS("bands", $"Error in OnProposeRelation: {e}");
                Popup(msg.Actor, "stow-factions-failed"); // ST:OW
            }
        }

        private void OnRespondProposal(EntityUid uid, BandsManagingComponent component, BandsManagingRespondProposalMessage msg)
        {
            try
            {
                var playerFaction = ResolvePlayerFaction(component, msg.Actor); // ST:OW
                if (playerFaction == null)
                {
                    Popup(msg.Actor, "stow-factions-leader-required"); // ST:OW
                    return;
                }

                if (msg.Accept)
                    _factionRelations.AcceptProposal(msg.Actor, playerFaction, msg.InitiatingFaction);
                else
                    _factionRelations.RejectProposal(playerFaction, msg.InitiatingFaction);

                UpdateUiState((uid, component), msg.Actor);
            }
            catch (Exception e)
            {
                Logger.ErrorS("bands", $"Error in OnRespondProposal: {e}");
                Popup(msg.Actor, "stow-factions-failed"); // ST:OW
            }
        }

        private void OnCancelProposal(EntityUid uid, BandsManagingComponent component, BandsManagingCancelProposalMessage msg)
        {
            try
            {
                var playerFaction = ResolvePlayerFaction(component, msg.Actor); // ST:OW
                if (playerFaction == null)
                {
                    Popup(msg.Actor, "stow-factions-leader-required"); // ST:OW
                    return;
                }

                _factionRelations.CancelProposal(playerFaction, msg.TargetFaction);
                UpdateUiState((uid, component), msg.Actor);
            }
            catch (Exception e)
            {
                Logger.ErrorS("bands", $"Error in OnCancelProposal: {e}");
                Popup(msg.Actor, "stow-factions-failed"); // ST:OW
            }
        }

        // stalker-en-changes end

        // ST:OW begin
        private void OnBuyItem(EntityUid uid, BandsManagingComponent component, BandsManagingBuyItemMessage msg)
        {
            TryRedeem(uid, msg.Actor, msg.ItemId);
        }
        // ST:OW end
    }
}
