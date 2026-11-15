using System.Linq;
using System.Numerics;
using Content.Client._Stalker_OW.Bands;
using Content.Shared._Stalker.Bands;
using Content.Shared._Stalker_EN.FactionRelations;
using Content.Shared._Stalker_OW.PublicName;
using Content.Shared.Roles;
using Content.Shared.Stacks;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Stalker.Bands.UI;

public sealed partial class BandsManagingWindow
{
    // Must match the order of the tabs in BandsManagingWindow.xaml; RelationsTabIndex is 1
    private const int MembersTabIndex = 0;
    private const int PointsTabIndex = 2;
    private const int RewardsTabIndex = 3;
    private const int WarZonesTabIndex = 4;

    public event Action<Guid, string, int>? OnSetTitleRequested;

    private readonly STTitleDrafts _titleDrafts = new();
    private readonly Dictionary<Guid, MemberRow> _memberRows = new();
    private List<BandMemberInfo> _members = new();
    private BandsManagingBoundUserInterfaceState? _lastState;
    private DefaultWindow? _removeConfirm;

    private readonly Dictionary<Label, TimeSpan> _warZoneCooldowns = new();
    private long _lastCountdownSecond = -1;
    private BandsManagingBoundUserInterfaceState? _shownRelations;

    private sealed class MemberRow
    {
        public BandMemberInfo Member = default!;
        public string DisplayName = string.Empty;
        public string RoleName = string.Empty;
        public readonly BoxContainer Root = new()
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(0, 2, 0, 4),
        };
        public readonly Label NameLabel = new() { HorizontalExpand = true, ClipText = true };
        public readonly Label RoleLabel = new() { StyleClasses = { "LabelSecondaryColor" }, Margin = new Thickness(6, 0) };
        public readonly Button RemoveButton = new() { MinWidth = 80, HorizontalAlignment = Control.HAlignment.Right };
        public readonly Label LockedLabel = new() { StyleClasses = { "LabelSecondaryColor" }, Margin = new Thickness(12, 0, 0, 0) };
        public readonly BoxContainer TitleLine = new()
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            Margin = new Thickness(12, 0, 0, 0),
            SeparationOverride = 4,
        };
        public readonly LineEdit TitleEdit = new() { HorizontalExpand = true };
        public readonly Button SaveButton = new() { MinWidth = 80 };
        public readonly Button ClearButton = new() { MinWidth = 80 };
    }

    private void InitializeIgor()
    {
        Tabs.SetTabTitle(MembersTabIndex, Loc.GetString("stow-factions-tab-members"));
        Tabs.SetTabTitle(RelationsTabIndex, Loc.GetString("stow-factions-tab-relations"));
        Tabs.SetTabTitle(PointsTabIndex, Loc.GetString("stow-factions-tab-points"));
        Tabs.SetTabTitle(RewardsTabIndex, Loc.GetString("stow-factions-tab-rewards"));
        Tabs.SetTabTitle(WarZonesTabIndex, Loc.GetString("stow-factions-tab-warzones"));

        MemberSearchLineEdit.OnTextChanged += _ => RefreshMemberRows();
        AffordableOnlyCheckBox.OnToggled += _ =>
        {
            if (_lastState != null)
                UpdateRewards(_lastState);
        };
        AddMemberLineEdit.OnTextEntered += _ =>
        {
            if (!AddMemberButton.Disabled)
                SubmitAddMember();
        };
    }

    private void UpdateIgor(BandsManagingBoundUserInterfaceState state)
    {
        _lastState = state;
        UpdateHeader(state);
        UpdateMembers(state);
        PreserveScroll(PointsScroll, () => UpdatePoints(state));
        PreserveScroll(RewardsScroll, () => UpdateRewards(state));
        PreserveScroll(WarZoneScroll, () => UpdateWarZones(state));
    }

    private static float? ViewerPoints(BandsManagingBoundUserInterfaceState state)
    {
        if (state.BandId == null)
            return null;

        return state.BandPoints.FirstOrDefault(p => p.BandProtoId == state.BandId)?.Points ?? 0f;
    }

    private static string FormatPoints(float points)
    {
        return points.ToString("0.##");
    }

    private static void PreserveScroll(ScrollContainer scroll, Action rebuild)
    {
        var position = scroll.GetScrollValue(true);
        rebuild();
        scroll.SetScrollValue(position);
    }

    #region Header

    private void UpdateHeader(BandsManagingBoundUserInterfaceState state)
    {
        var inBand = state.BandId != null;
        BandNameLabel.Text = state.BandName ?? Loc.GetString("stow-factions-not-in-band");
        HeaderPointsLabel.Text = ViewerPoints(state) is { } points
            ? Loc.GetString("stow-factions-header-points", ("points", FormatPoints(points)))
            : string.Empty;
        BandMemberCountLabel.Text = inBand
            ? Loc.GetString("stow-factions-header-members", ("count", state.Members.Count), ("max", state.MaxMembers))
            : string.Empty;
    }

    #endregion

    #region Points

    private void UpdatePoints(BandsManagingBoundUserInterfaceState state)
    {
        BandPointsList.Children.Clear();

        var ordered = state.BandPoints
            .Where(p => _prototypeManager.TryIndex<STBandPrototype>(p.BandProtoId, out var band) && band.ShowInPoints)
            .OrderByDescending(p => p.BandProtoId == state.BandId)
            .ThenByDescending(p => p.Points)
            .ThenBy(p => p.BandName, STRosterView.NameOrder)
            .ToList();

        NoBandPointsLabel.Visible = ordered.Count == 0;

        foreach (var entry in ordered)
        {
            var own = entry.BandProtoId == state.BandId;
            var row = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Horizontal,
                HorizontalExpand = true,
                Margin = new Thickness(0, 2),
            };

            var name = new Label
            {
                Text = own ? Loc.GetString("stow-factions-points-yours", ("faction", entry.BandName)) : entry.BandName,
                HorizontalExpand = true,
                ClipText = true,
            };
            if (own)
                name.StyleClasses.Add("LabelHeading");

            row.AddChild(name);
            row.AddChild(new Label
            {
                Text = FormatPoints(entry.Points),
                MinWidth = 100,
                Align = Label.AlignMode.Right,
            });

            BandPointsList.AddChild(row);
        }
    }

    #endregion

    #region Rewards

    private void UpdateRewards(BandsManagingBoundUserInterfaceState state)
    {
        var balance = ViewerPoints(state);
        var canRedeem = state.CanManage && balance != null;

        PreserveScroll(RewardsScroll, () =>
        {
            ShopItemList.Children.Clear();
            var shown = 0;

            foreach (var item in state.ShopItems)
            {
                var affordable = balance is { } b && b >= item.Price;
                if (AffordableOnlyCheckBox.Pressed && !affordable)
                    continue;

                ShopItemList.AddChild(CreateRewardRow(item, canRedeem, affordable, balance ?? 0f, state.CanManage));
                shown++;
            }

            NoShopItemsLabel.Visible = shown == 0;
            NoShopItemsLabel.Text = state.ShopItems.Count == 0
                ? Loc.GetString("stow-factions-rewards-none")
                : Loc.GetString("stow-factions-rewards-none-affordable");
        });
    }

    private Control CreateRewardRow(BandShopItem item, bool canRedeem, bool affordable, float balance, bool isLeader)
    {
        var name = item.ProductEntity;
        string? description = null;
        Texture? icon = null;
        var quantity = 1;

        if (_prototypeManager.TryIndex<EntityPrototype>(item.ProductEntity, out var proto))
        {
            name = proto.Name;
            description = string.IsNullOrWhiteSpace(proto.Description) ? null : proto.Description;
            icon = _entityManager.System<SpriteSystem>().GetPrototypeIcon(proto).Default;

            if (proto.TryGetComponent<StackComponent>(out var stack, _entityManager.ComponentFactory))
                quantity = stack.Count;
        }

        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            Margin = new Thickness(0, 2),
            SeparationOverride = 6,
        };

        row.AddChild(new TextureRect
        {
            Texture = icon,
            MinSize = new Vector2(32, 32),
            Stretch = TextureRect.StretchMode.KeepAspectCentered,
            VerticalAlignment = Control.VAlignment.Center,
        });

        var text = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalAlignment = Control.VAlignment.Center,
        };
        text.AddChild(new Label
        {
            Text = quantity > 1 ? Loc.GetString("stow-factions-rewards-quantity", ("item", name), ("count", quantity)) : name,
            ClipText = true,
            ToolTip = description,
        });
        text.AddChild(new Label
        {
            Text = Loc.GetString("stow-factions-rewards-price", ("price", item.Price)),
            StyleClasses = { "LabelSecondaryColor" },
        });
        row.AddChild(text);

        var redeem = new Button
        {
            Text = Loc.GetString("stow-factions-rewards-redeem"),
            MinWidth = 90,
            VerticalAlignment = Control.VAlignment.Center,
            Disabled = !canRedeem || !affordable,
        };

        if (!isLeader)
            redeem.ToolTip = Loc.GetString("stow-factions-leader-required");
        else if (!affordable)
            redeem.ToolTip = Loc.GetString("stow-factions-rewards-short", ("missing", FormatPoints(item.Price - balance)));

        var itemId = item.ProductEntity;
        redeem.OnPressed += _ => OnBuyItemButtonPressed?.Invoke(itemId);
        row.AddChild(redeem);

        return row;
    }

    #endregion

    #region Members

    private void UpdateMembers(BandsManagingBoundUserInterfaceState state)
    {
        _members = state.Members;

        AddMemberLineEdit.Editable = state.CanManage;
        AddMemberButton.Disabled = !state.CanManage;
        AddMemberButton.ToolTip = state.CanManage ? null : Loc.GetString("stow-factions-leader-required");

        var present = _members.Select(m => m.UserId.UserId).ToHashSet();
        foreach (var gone in _memberRows.Keys.Where(id => !present.Contains(id)).ToList())
        {
            _memberRows[gone].Root.Orphan();
            _memberRows.Remove(gone);
        }

        _titleDrafts.RetainOnly(present);

        foreach (var member in _members)
        {
            if (!_memberRows.TryGetValue(member.UserId.UserId, out var row))
            {
                row = CreateMemberRow(member.UserId.UserId);
                _memberRows.Add(member.UserId.UserId, row);
                MembersList.AddChild(row.Root);
            }

            UpdateMemberRow(row, member);
        }

        RefreshMemberRows();
    }

    private void RefreshMemberRows()
    {
        var search = MemberSearchLineEdit.Text;
        var rows = STRosterView.Sort(_members, m => _memberRows[m.UserId.UserId].DisplayName)
            .Select(m => _memberRows[m.UserId.UserId])
            .ToList();

        var shown = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            row.Root.SetPositionInParent(i);
            row.Root.Visible = STRosterView.Matches(row.Member, row.DisplayName, row.RoleName, search);
            if (row.Root.Visible)
                shown++;
        }

        NoMembersLabel.Visible = shown == 0;
        NoMembersLabel.Text = _lastState?.BandId == null
            ? Loc.GetString("stow-factions-not-in-band")
            : _members.Count == 0
                ? Loc.GetString("stow-factions-members-none")
                : Loc.GetString("stow-factions-members-no-match");
    }

    private MemberRow CreateMemberRow(Guid memberId)
    {
        var row = new MemberRow();

        var nameLine = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        nameLine.AddChild(row.NameLabel);
        nameLine.AddChild(row.RoleLabel);
        nameLine.AddChild(row.RemoveButton);
        row.Root.AddChild(nameLine);
        row.Root.AddChild(row.LockedLabel);

        row.TitleEdit.PlaceHolder = Loc.GetString("stow-factions-title-placeholder");
        row.TitleEdit.ToolTip = Loc.GetString("stow-factions-title-hint", ("max", STFactionTitleRules.MaxLength));
        row.TitleEdit.IsValid = text => text.Length <= STFactionTitleRules.MaxLength;
        row.TitleEdit.OnTextChanged += args => _titleDrafts.SetDraft(memberId, args.Text);
        row.TitleEdit.OnTextEntered += _ => SubmitTitle(memberId, row.TitleEdit.Text);

        row.SaveButton.Text = Loc.GetString("stow-factions-title-save-button");
        row.SaveButton.OnPressed += _ => SubmitTitle(memberId, row.TitleEdit.Text);

        row.ClearButton.Text = Loc.GetString("stow-factions-title-clear-button");
        row.ClearButton.OnPressed += _ =>
        {
            _titleDrafts.Discard(memberId);
            row.TitleEdit.Text = string.Empty;
            SubmitTitle(memberId, string.Empty);
        };

        row.TitleLine.AddChild(row.TitleEdit);
        row.TitleLine.AddChild(row.SaveButton);
        row.TitleLine.AddChild(row.ClearButton);
        row.Root.AddChild(row.TitleLine);

        row.RemoveButton.Text = Loc.GetString("stow-factions-members-remove-button");
        row.RemoveButton.OnPressed += _ => ConfirmRemove(row);

        return row;
    }

    private void UpdateMemberRow(MemberRow row, BandMemberInfo member)
    {
        row.Member = member;
        row.DisplayName = string.IsNullOrEmpty(member.Title)
            ? member.CharacterName
            : Loc.GetString("st-public-name-with-rank", ("rank", member.Title), ("name", member.CharacterName));
        row.RoleName = _prototypeManager.TryIndex<JobPrototype>(member.RoleId, out var job)
            ? job.LocalizedName
            : member.RoleId;

        row.NameLabel.Text = row.DisplayName;
        row.Root.ToolTip = Loc.GetString("stow-factions-members-tooltip", ("name", row.DisplayName), ("ckey", member.PlayerName));
        row.RoleLabel.Text = row.RoleName;

        row.RemoveButton.Disabled = !member.CanRemove;
        row.RemoveButton.ToolTip = member.CanRemove ? null
            : Loc.GetString(_lastState?.CanManage == true ? "stow-factions-outranked" : "stow-factions-leader-required");

        row.LockedLabel.Text = Loc.GetString("stow-factions-title-locked");
        row.LockedLabel.Visible = member.TitleLocked;

        row.TitleLine.Visible = member.CanEditTitle && !member.TitleLocked;
        row.ClearButton.Visible = member.Title.Length > 0;

        if (!row.TitleEdit.HasKeyboardFocus() && !_titleDrafts.TryGetDraft(member.UserId.UserId, out _))
            row.TitleEdit.Text = member.Title;
    }

    private void SubmitTitle(Guid memberId, string title)
    {
        OnSetTitleRequested?.Invoke(memberId, title, _titleDrafts.Submit(memberId, title));
    }

    public void OnTitleResult(Guid memberId, int requestId, bool saved, string savedTitle)
    {
        if (!_titleDrafts.OnResult(memberId, requestId, saved))
            return;

        if (_memberRows.TryGetValue(memberId, out var row))
            row.TitleEdit.Text = savedTitle;
    }

    private void ConfirmRemove(MemberRow row)
    {
        _removeConfirm?.Close();

        var memberId = row.Member.UserId.UserId;
        var window = new DefaultWindow
        {
            Title = Loc.GetString("stow-factions-remove-confirm-title"),
            MinWidth = 380,
        };

        var box = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 8,
            Margin = new Thickness(4),
        };

        var message = new FormattedMessage();
        message.AddText(Loc.GetString("stow-factions-remove-confirm-text",
            ("name", row.DisplayName), ("ckey", row.Member.PlayerName)));
        var text = new RichTextLabel { HorizontalExpand = true };
        text.SetMessage(message);
        box.AddChild(text);

        var buttons = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalAlignment = Control.HAlignment.Right,
            SeparationOverride = 6,
        };
        var remove = new Button
        {
            Text = Loc.GetString("stow-factions-remove-confirm-button"),
            StyleClasses = { "ButtonColorRed" },
        };
        var cancel = new Button { Text = Loc.GetString("stow-factions-remove-cancel-button") };

        remove.OnPressed += _ =>
        {
            OnRemoveMemberButtonPressed?.Invoke(memberId);
            window.Close();
        };
        cancel.OnPressed += _ => window.Close();

        buttons.AddChild(cancel);
        buttons.AddChild(remove);
        box.AddChild(buttons);

        window.Contents.AddChild(box);
        window.OnClose += () =>
        {
            if (_removeConfirm == window)
                _removeConfirm = null;
        };

        _removeConfirm = window;
        window.OpenCentered();
    }

    #endregion

    #region War zones

    private void UpdateWarZones(BandsManagingBoundUserInterfaceState state)
    {
        WarZoneList.Children.Clear();
        _warZoneCooldowns.Clear();
        NoWarZonesLabel.Visible = state.WarZones.Count == 0;

        foreach (var zone in state.WarZones.OrderBy(z => z.ZoneId, STRosterView.NameOrder))
        {
            var box = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Vertical,
                HorizontalExpand = true,
                Margin = new Thickness(0, 4),
            };

            box.AddChild(new Label { Text = zone.ZoneId, StyleClasses = { "LabelHeading" } });
            box.AddChild(new Label
            {
                Text = Loc.GetString("stow-factions-warzones-owner",
                    ("owner", zone.Owner.Length > 0 ? zone.Owner : Loc.GetString("stow-factions-warzones-uncontrolled"))),
                StyleClasses = { "LabelSecondaryColor" },
            });

            if (zone.Attacker.Length > 0)
            {
                box.AddChild(new Label
                {
                    Text = Loc.GetString("stow-factions-warzones-contested", ("attacker", zone.Attacker)),
                    StyleClasses = { "LabelSecondaryColor" },
                });
            }

            if (zone.Cooldown > 0)
            {
                var cooldown = new Label { StyleClasses = { "LabelSecondaryColor" } };
                _warZoneCooldowns[cooldown] = _gameTiming.CurTime + TimeSpan.FromSeconds(zone.Cooldown);
                SetWarZoneCooldownText(cooldown, TimeSpan.FromSeconds(zone.Cooldown));
                box.AddChild(cooldown);
            }

            if (WarZoneList.ChildCount > 0)
                WarZoneList.AddChild(new PanelContainer { StyleClasses = { "LowDivider" } });

            WarZoneList.AddChild(box);
        }
    }

    private static void SetWarZoneCooldownText(Label label, TimeSpan remaining)
    {
        label.Text = Loc.GetString("stow-factions-warzones-cooldown",
            ("time", $"{(int) remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"));
    }

    private bool CountdownSecondChanged()
    {
        var second = (long) _gameTiming.CurTime.TotalSeconds;
        if (second == _lastCountdownSecond)
            return false;

        _lastCountdownSecond = second;
        return true;
    }

    private void UpdateWarZoneCountdowns()
    {
        if (_warZoneCooldowns.Count == 0)
            return;

        foreach (var (label, expiry) in _warZoneCooldowns.ToList())
        {
            var remaining = expiry - _gameTiming.CurTime;
            if (remaining <= TimeSpan.Zero)
            {
                label.Visible = false;
                _warZoneCooldowns.Remove(label);
                continue;
            }

            SetWarZoneCooldownText(label, remaining);
        }
    }

    #endregion

    private static bool SameRelations(BandsManagingBoundUserInterfaceState? shown, BandsManagingBoundUserInterfaceState state)
    {
        return shown != null
               && shown.HideRelationsTab == state.HideRelationsTab
               && shown.PlayerFaction == state.PlayerFaction
               && shown.CanManageRelations == state.CanManageRelations
               && shown.AllFactions.SequenceEqual(state.AllFactions)
               && shown.FactionRelations.Select(r => (r.FactionA, r.FactionB, r.Relation))
                   .SequenceEqual(state.FactionRelations.Select(r => (r.FactionA, r.FactionB, r.Relation)))
               && Proposals(shown.IncomingProposals).SequenceEqual(Proposals(state.IncomingProposals))
               && Proposals(shown.OutgoingProposals).SequenceEqual(Proposals(state.OutgoingProposals))
               && shown.CooldownsRemaining.Keys.Order().SequenceEqual(state.CooldownsRemaining.Keys.Order());

        static IEnumerable<(string, string, STFactionRelationType, string?)> Proposals(List<STFactionRelationProposalEntry> list)
        {
            return list.Select(p => (p.InitiatingFaction, p.TargetFaction, p.ProposedRelation, p.CustomMessage));
        }
    }

    private void EnsureVisibleTab()
    {
        if (Tabs.GetTabVisible(Tabs.CurrentTab))
            return;

        for (var i = 0; i < Tabs.ChildCount; i++)
        {
            if (!Tabs.GetTabVisible(i))
                continue;

            Tabs.CurrentTab = i;
            return;
        }
    }

    private void DisposeIgor()
    {
        _removeConfirm?.Close();
        _removeConfirm = null;
    }
}
