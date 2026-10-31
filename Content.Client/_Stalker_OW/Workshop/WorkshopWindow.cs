using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._Stalker_OW.Workshop;
using Content.Shared.Armor;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._Stalker_OW.Workshop;

public sealed class WorkshopWindow : FancyWindow
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    private readonly SharedArmorSystem _armor;

    private static readonly Color ReadyColor = Color.FromHex("#8FCB88");
    private static readonly Color WarningColor = Color.FromHex("#E1A582");
    private static readonly Color DividerColor = Color.FromHex("#51574D");
    private static readonly Color SubduedColor = Color.FromHex("#A3A59F");

    private const int SlotColumnWidth = 220;
    private const int ItemColumnWidth = 260;
    private const int StatColumnWidth = 380;
    private const int StatNameWidth = 176;
    private const int StatValueWidth = 76;

    private readonly BoxContainer _body = Column();
    private readonly BoxContainer _slots = Column();
    private readonly BoxContainer _stats = Column();
    private readonly RichTextLabel _status = new() { HorizontalExpand = true };
    private readonly Label _armorClass = new();
    private readonly RichTextLabel _name = new() { MaxWidth = 240, HorizontalAlignment = HAlignment.Center };
    private readonly RichTextLabel _operation = new() { HorizontalExpand = true };
    private readonly EntityPrototypeView _icon = new()
    {
        MinSize = new Vector2(112), MaxSize = new Vector2(112),
        HorizontalAlignment = HAlignment.Center, Stretch = SpriteView.StretchMode.Fit,
        Scale = new Vector2(3), OverrideDirection = Direction.South,
    };
    private readonly Label _emptyIcon = new()
    {
        Text = "—", HorizontalAlignment = HAlignment.Center, VerticalAlignment = VAlignment.Center,
    };
    private readonly Button _insert;
    private readonly Button _eject;
    private readonly Button _apply;
    private readonly Button _cancel;
    private readonly ProgressBar _progress;
    private readonly List<ModuleRow> _moduleRows = new();

    private WorkshopBoundUserInterfaceState? _state;
    private string? _displayedPrototype;
    private List<WorkshopStatState>? _renderedStats;
    private bool _slotsLocked;

    private sealed record ModuleRow(int Slot, SlotButton Button, RichTextLabel Installed, RichTextLabel Pending)
    {
        public string? Prototype;
    }

    public event Action<BoundUserInterfaceMessage>? ActionRequested;

    public WorkshopWindow()
    {
        IoCManager.InjectDependencies(this);
        _armor = _entities.System<SharedArmorSystem>();

        Title = L("workshop-title");
        SetSize = new Vector2(1000, 540);
        MinSize = new Vector2(960, 480);
        _body.Margin = new Thickness(14);
        _body.HorizontalExpand = _body.VerticalExpand = true;
        ContentsContainer.AddChild(_body);
        SetTitleHelpTooltip(L("workshop-guide"));

        var main = Row();
        main.VerticalExpand = main.HorizontalExpand = true;
        main.AddChild(Section("workshop-slots-header", _slots, SlotColumnWidth));
        main.AddChild(Divider());

        var centre = Column();
        centre.SeparationOverride = 12;
        var preview = new Control
        {
            MinSize = new Vector2(112), MaxSize = new Vector2(112), HorizontalAlignment = HAlignment.Center,
        };
        preview.AddChild(_icon);
        preview.AddChild(_emptyIcon);
        centre.AddChild(preview);
        centre.AddChild(_name);
        centre.AddChild(_operation);

        _apply = ActionButton("workshop-apply", () => Send(WorkshopAction.Apply));
        _progress = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, MinHeight = 3, MaxHeight = 3,
            VerticalAlignment = VAlignment.Bottom, HorizontalExpand = true,
            MouseFilter = MouseFilterMode.Ignore, Visible = false,
            ForegroundStyleBoxOverride = new StyleBoxFlat(ReadyColor),
            BackgroundStyleBoxOverride = new StyleBoxFlat(Color.FromHex("#20251F")),
        };
        _apply.AddChild(_progress);
        centre.AddChild(_apply);
        _insert = ActionButton("workshop-insert", () => Send(WorkshopAction.Insert));
        centre.AddChild(_insert);
        _eject = ActionButton("workshop-eject", () => Send(WorkshopAction.Eject));
        centre.AddChild(_eject);
        _cancel = ActionButton("workshop-cancel", () => Send(WorkshopAction.Cancel));
        centre.AddChild(_cancel);
        main.AddChild(Section("workshop-item-header", centre, ItemColumnWidth));
        main.AddChild(Divider());

        var stats = Section("workshop-stats-header", _stats, StatColumnWidth, true);
        stats.AddChild(_armorClass);
        _armorClass.SetPositionInParent(1);
        var heading = StatRow(L("workshop-stats"), L("workshop-current"), L("workshop-after"), header: true);
        stats.AddChild(heading);
        heading.SetPositionInParent(2);
        var rule = new PanelContainer { MinHeight = 1, MaxHeight = 1, PanelOverride = new StyleBoxFlat(DividerColor) };
        stats.AddChild(rule);
        rule.SetPositionInParent(3);
        Text(stats, L("workshop-stat-help"));
        main.AddChild(stats);

        _body.AddChild(main);
        _body.AddChild(_status);
        Render();
    }

    public void SetState(WorkshopBoundUserInterfaceState state)
    {
        _state = state;
        Render();
    }

    public void Refresh() => Render();

    private void Send(WorkshopAction action, int slot = -1) =>
        ActionRequested?.Invoke(new WorkshopMessage(action, slot));

    private void Render()
    {
        var target = _entities.GetEntity(_state?.Target);
        var hasTarget = target != null && _entities.EntityExists(target);
        _entities.TryGetComponent<EquipmentModificationComponent>(target, out var equipment);
        var changes = _state?.Changes ?? new Dictionary<int, EntProtoId?>();
        var pending = changes.Count > 0;
        var mine = _players.LocalEntity is { } local && _state?.Worker == _entities.GetNetEntity(local);
        var working = _state?.Working ?? false;
        var locked = _state?.Worker != null && (!mine || working);

        // Equipment name and icon
        SetText(_name, hasTarget ? _entities.GetComponent<MetaDataComponent>(target!.Value).EntityName : L("workshop-empty"));
        var prototype = hasTarget ? _entities.GetComponent<MetaDataComponent>(target!.Value).EntityPrototype?.ID : null;
        if (_displayedPrototype != prototype)
        {
            _displayedPrototype = prototype;
            _icon.SetPrototype(prototype);
            _icon.InvalidateMeasure();
        }
        _emptyIcon.Visible = prototype == null;

        // Keep buttons in place and update whether they can be used
        _insert.Disabled = hasTarget || locked;
        _eject.Disabled = !hasTarget || locked;
        _apply.Disabled = !pending || !mine || working;
        _cancel.Disabled = !pending || !mine;
        _progress.Visible = working;
        SetText(_operation, pending ? Loc.GetString("workshop-pending-count", ("count", changes.Count)) : "");
        SetText(_status, _state?.Worker != null && !mine ? L("workshop-busy") : "");

        _slotsLocked = locked;
        RenderSlots(hasTarget, equipment, changes, locked);

        var armorClass = hasTarget && _entities.TryGetComponent<ArmorComponent>(target, out var armor) &&
                         armor.ArmorClass is { } value
            ? value.ToString()
            : "—";
        _armorClass.Text = Loc.GetString("workshop-armor-class", ("value", armorClass));

        var stats = hasTarget ? _armor.GetWorkshopPreview(target!.Value, changes) : new List<WorkshopStatState>();
        if (_renderedStats != null && SameStats(_renderedStats, stats))
            return;

        _renderedStats = stats;
        RenderStats(stats, pending);
    }

    private void RenderSlots(bool hasTarget, EquipmentModificationComponent? equipment,
        Dictionary<int, EntProtoId?> changes, bool locked)
    {
        var count = equipment?.Installed.Count ?? 0;
        if (!hasTarget || count == 0)
        {
            _slots.RemoveAllChildren();
            _moduleRows.Clear();
            if (hasTarget)
            {
                Text(_slots, L("workshop-no-slots"));
                return;
            }

            var row = Row();
            row.AddChild(CreateSlotButton(true, L("workshop-slot-help")));
            var hint = Column();
            hint.HorizontalExpand = true;
            Text(hint, L("workshop-slots-empty"));
            row.AddChild(hint);
            _slots.AddChild(row);
            return;
        }

        if (_moduleRows.Count != count)
        {
            _slots.RemoveAllChildren();
            _moduleRows.Clear();
            for (var i = 0; i < count; i++)
            {
                var slot = i;
                var row = Row();
                var button = CreateSlotButton(false, "");
                button.Pressed += (args, _) =>
                {
                    if (_slotsLocked || args.Function != EngineKeyFunctions.UIClick)
                        return;

                    Send(WorkshopAction.SelectSlot, slot);
                    args.Handle();
                };
                row.AddChild(button);
                var labels = Column();
                labels.MinWidth = labels.MaxWidth = 140;
                labels.VerticalAlignment = VAlignment.Center;
                var installedLabel = Text(labels, "");
                var pendingLabel = Text(labels, "");
                row.AddChild(labels);
                _slots.AddChild(row);
                _moduleRows.Add(new ModuleRow(slot, button, installedLabel, pendingLabel));
            }
        }

        foreach (var row in _moduleRows)
        {
            var installed = equipment!.Installed[row.Slot];
            var staged = changes.TryGetValue(row.Slot, out var change);
            var shown = staged && change != null ? change.Value.Id : installed?.Id;
            if (row.Prototype != shown)
            {
                row.Prototype = shown;
                row.Button.SetPrototype(shown, false);
            }

            row.Button.Highlight = staged;
            row.Button.Modulate = locked ? Color.Gray : Color.White;
            row.Button.ToolTip = Loc.GetString("workshop-slot", ("number", row.Slot + 1)) + "\n" + L("workshop-slot-help");
            SetText(row.Installed, installed is { } id ? PrototypeName(id) : L("workshop-none"));
            row.Pending.Visible = staged;
            if (staged)
            {
                var name = change is { } next ? PrototypeName(next) : L("workshop-remove");
                SetText(row.Pending, Loc.GetString("workshop-pending", ("name", name)), ReadyColor);
            }
        }
    }

    private void RenderStats(List<WorkshopStatState> stats, bool pending)
    {
        _stats.RemoveAllChildren();
        if (stats.Count == 0)
            _stats.AddChild(StatRow("—", "—", "—"));

        foreach (var group in stats.GroupBy(stat => stat.Name))
        {
            var block = Column();
            block.SeparationOverride = 2;
            block.Margin = new Thickness(0, 3, 0, 7);
            foreach (var stat in group.OrderBy(stat => stat.Flat))
            {
                var current = stat.Flat ? stat.Current : WorkshopStatDisplay.ProtectionPercent(stat.Current);
                var proposed = stat.Flat ? stat.Proposed : WorkshopStatDisplay.ProtectionPercent(stat.Proposed);
                var suffix = stat.Flat ? "" : "%";
                var title = stat.Flat ? Loc.GetString("workshop-flat", ("name", stat.Name)) : stat.Name;
                var changed = pending && Math.Abs(proposed - current) > 0.0001f;
                Color? tint = changed ? proposed > current ? ReadyColor : WarningColor : null;
                block.AddChild(StatRow(title, $"{current:0.#}{suffix}", $"{proposed:0.#}{suffix}", tint, subdued: stat.Flat));
            }
            _stats.AddChild(block);
        }
    }

    private static bool SameStats(List<WorkshopStatState> a, List<WorkshopStatState> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            p.First.Name == p.Second.Name && p.First.Flat == p.Second.Flat &&
            p.First.Current.Equals(p.Second.Current) && p.First.Proposed.Equals(p.Second.Proposed));

    private string PrototypeName(EntProtoId id) =>
        _proto.TryIndex(id, out var prototype) ? prototype.Name : id.Id;

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!_progress.Visible || _state == null)
            return;

        var now = _timing.CurTime;
        if (_entities.GetEntity(_state.Worker) is { } worker && _entities.EntityExists(worker))
            now -= _entities.System<MetaDataSystem>().GetPauseTime(worker);

        var duration = (_state.Finishes - _state.Started).TotalSeconds;
        _progress.Value = duration <= 0
            ? 0
            : (float) Math.Clamp((now - _state.Started).TotalSeconds / duration, 0, 1);
    }

    #region Layout helpers

    private static BoxContainer Section(string title, BoxContainer content, float minWidth, bool expand = false)
    {
        var section = Column();
        section.MinWidth = minWidth;
        section.MaxWidth = expand ? float.PositiveInfinity : minWidth;
        section.HorizontalExpand = expand;
        section.VerticalExpand = true;
        var header = new Label { Text = L(title), HorizontalAlignment = HAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        header.AddStyleClass("LabelHeading");
        section.AddChild(header);
        content.HorizontalExpand = true;
        var scroll = new ScrollContainer { VerticalExpand = true, HScrollEnabled = false };
        scroll.AddChild(content);
        section.AddChild(scroll);
        return section;
    }

    private static PanelContainer Divider() => new()
    {
        MinWidth = 2, MaxWidth = 2, VerticalExpand = true, PanelOverride = new StyleBoxFlat(DividerColor),
    };

    private static SlotButton CreateSlotButton(bool disabled, string tooltip)
    {
        var button = new SlotButton
        {
            ButtonTexturePath = "FlatSlotBackground", FullButtonTexturePath = "FlatSlotBackground",
            ToolTip = tooltip, VerticalAlignment = VAlignment.Top,
            Modulate = disabled ? Color.Gray : Color.White,
        };
        button.SetPrototype(null, false);
        return button;
    }

    private static BoxContainer StatRow(string name, string current, string proposed, Color? color = null,
        bool header = false, bool subdued = false)
    {
        var row = Row();
        if (subdued || header)
        {
            var caption = new Label
            {
                Text = name, MinWidth = StatNameWidth, MaxWidth = StatNameWidth,
                FontColorOverride = subdued ? SubduedColor : Color.White,
            };
            caption.AddStyleClass(subdued ? "LabelSubText" : "LabelHeading");
            row.AddChild(caption);
        }
        else
        {
            var label = new RichTextLabel { MinWidth = StatNameWidth, MaxWidth = StatNameWidth };
            SetText(label, name);
            row.AddChild(label);
        }

        foreach (var (value, tint) in new[] { (current, (Color?) null), (proposed, color) })
        {
            var cell = new Label
            {
                Text = value, MinWidth = StatValueWidth, MaxWidth = StatValueWidth, Align = Label.AlignMode.Right,
                FontColorOverride = tint ?? (subdued ? SubduedColor : Color.White),
            };
            if (header)
                cell.AddStyleClass("LabelHeading");
            if (subdued)
                cell.AddStyleClass("LabelSubText");
            row.AddChild(cell);
        }

        return row;
    }

    private static string L(string key) => Loc.GetString(key);

    private static BoxContainer Column() =>
        new() { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 6 };

    private static BoxContainer Row() =>
        new() { Orientation = BoxContainer.LayoutOrientation.Horizontal, SeparationOverride = 8 };

    private static Button ActionButton(string key, Action action)
    {
        var button = new Button { MinHeight = 32, HorizontalExpand = true };
        var label = new RichTextLabel { HorizontalExpand = true, MouseFilter = MouseFilterMode.Ignore };
        SetText(label, L(key));
        button.AddChild(label);
        button.OnPressed += _ => action();
        return button;
    }

    private static RichTextLabel Text(BoxContainer parent, string text, Color? color = null)
    {
        var label = new RichTextLabel { HorizontalExpand = true };
        SetText(label, text, color);
        parent.AddChild(label);
        return label;
    }

    private static void SetText(RichTextLabel label, string text, Color? color = null)
    {
        var message = new FormattedMessage();
        if (color is { } c)
            message.PushColor(c);
        message.AddText(text);
        if (color != null)
            message.Pop();
        label.SetMessage(message);
    }

    #endregion
}
