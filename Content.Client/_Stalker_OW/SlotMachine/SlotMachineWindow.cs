using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._Stalker_OW.SlotMachine;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._Stalker_OW.SlotMachine;

public sealed class SlotMachineWindow : FancyWindow
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    // Seconds each symbol stays on a reel that is still turning
    private const float CycleInterval = 0.07f;

    // How long the lever stays locked after a pull while waiting for the server to answer
    private static readonly TimeSpan PullLock = TimeSpan.FromSeconds(0.75);

    private readonly SpriteSystem _sprite;
    private readonly TextureRect[] _reels = new TextureRect[SlotMachineComponent.ReelCount];
    private readonly StyleBoxFlat[] _reelFrames = new StyleBoxFlat[SlotMachineComponent.ReelCount];
    private readonly int[] _shownSymbols = new int[SlotMachineComponent.ReelCount];
    private readonly List<Texture> _symbols = new();
    private readonly List<Button> _tierButtons = new();
    private readonly Label _balance = new() { HorizontalExpand = true };
    private readonly Label _status = new() { HorizontalAlignment = HAlignment.Center };
    private readonly RichTextLabel _bonus = new() { HorizontalAlignment = HAlignment.Center, Visible = false };
    private readonly Label _tierInfo = new() { HorizontalAlignment = HAlignment.Center, FontColorOverride = SlotMachineColors.Subdued };
    private readonly BoxContainer _tiers = new()
    {
        Orientation = BoxContainer.LayoutOrientation.Horizontal,
        HorizontalAlignment = HAlignment.Center,
        SeparationOverride = 6,
    };
    private readonly SlotMachineLever _lever = new();

    private SlotMachineOddsWindow? _odds;
    private EntityUid _machine;
    private int _selectedTier;
    private TimeSpan _leverLockedUntil;
    private bool _wasSpinning;
    private (bool Spinning, SlotMachineOutcome Outcome, EntProtoId? Prize, SlotMachineRarity Rarity, EntProtoId? Bonus)? _shownStatus;

    public event Action<int>? SpinRequested;

    public SlotMachineWindow()
    {
        IoCManager.InjectDependencies(this);
        _sprite = _entities.System<SpriteSystem>();
        Array.Fill(_shownSymbols, -1);

        Title = Loc.GetString("slot-machine-title");
        MinSize = new Vector2(380, 330);
        SetSize = new Vector2(380, 330);

        var body = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Margin = new Thickness(12),
            SeparationOverride = 10,
            HorizontalExpand = true,
            VerticalExpand = true,
        };
        ContentsContainer.AddChild(body);

        var header = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        var oddsButton = new Button { Text = Loc.GetString("slot-machine-odds-button") };
        oddsButton.OnPressed += _ => ToggleOdds();
        header.AddChild(_balance);
        header.AddChild(oddsButton);
        body.AddChild(header);

        var reels = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalAlignment = HAlignment.Center,
            SeparationOverride = 8,
        };

        for (var i = 0; i < _reels.Length; i++)
        {
            _reels[i] = new TextureRect
            {
                MinSize = new Vector2(72, 72),
                Stretch = TextureRect.StretchMode.KeepAspectCentered,
                Margin = new Thickness(6),
            };

            _reelFrames[i] = new StyleBoxFlat(SlotMachineColors.Panel)
            {
                BorderColor = SlotMachineColors.PanelEdge,
                BorderThickness = new Thickness(2),
            };

            var frame = new PanelContainer { PanelOverride = _reelFrames[i] };
            frame.AddChild(_reels[i]);
            reels.AddChild(frame);
        }

        _lever.ToolTip = Loc.GetString("slot-machine-lever");
        _lever.OnPressed += _ => PullLever();
        reels.AddChild(_lever);
        body.AddChild(reels);

        var result = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 2,
            HorizontalExpand = true,
        };
        result.AddChild(_status);
        result.AddChild(_bonus);
        body.AddChild(result);
        body.AddChild(_tiers);
        body.AddChild(_tierInfo);

        SetBalance(0);
        OnClose += () => _odds?.Close();
    }

    public void SetMachine(EntityUid machine)
    {
        _machine = machine;
        _symbols.Clear();
        _tiers.RemoveAllChildren();
        _tierButtons.Clear();
        Array.Fill(_shownSymbols, -1);
        _shownStatus = null;

        if (!_entities.TryGetComponent(machine, out SlotMachineComponent? comp))
            return;

        _wasSpinning = comp.Spinning;

        foreach (var symbol in comp.Symbols)
        {
            _symbols.Add(_sprite.Frame0(symbol.Icon));
        }

        for (var i = 0; i < comp.Tiers.Count; i++)
        {
            var tier = i;
            var button = new Button
            {
                Text = SlotMachineFormat.Number(comp.Tiers[i].Price),
                ToggleMode = true,
                MinWidth = 80,
            };
            button.OnPressed += _ => SelectTier(tier);
            _tierButtons.Add(button);
            _tiers.AddChild(button);
        }

        SelectTier(Math.Clamp(_selectedTier, 0, Math.Max(0, comp.Tiers.Count - 1)));
    }

    public void SetBalance(int balance)
    {
        _balance.Text = Loc.GetString("slot-machine-balance", ("amount", SlotMachineFormat.Number(balance)));
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        if (!_entities.TryGetComponent(_machine, out SlotMachineComponent? comp))
            return;

        var spinning = comp.Spinning;

        var elapsed = spinning ? MathF.Max(0f, (float) (_timing.CurTime - comp.SpinStart).TotalSeconds) : 0f;

        if (spinning && !_wasSpinning)
            _lever.Pull();

        _wasSpinning = spinning;

        for (var i = 0; i < _reels.Length; i++)
        {
            var turning = spinning && i < comp.ReelStopDelays.Length && elapsed < comp.ReelStopDelays[i];
            var symbol = turning
                ? (int) (elapsed / CycleInterval) + i * 2
                : i < comp.Result.Length ? comp.Result[i] : 0;

            ShowSymbol(i, symbol);
        }

        _lever.Disabled = spinning || _timing.RealTime < _leverLockedUntil;

        foreach (var button in _tierButtons)
        {
            button.Disabled = spinning;
        }

        UpdateStatus(comp);
        UpdateHighlight(comp);
    }

    private void ShowSymbol(int reel, int symbol)
    {
        if (_symbols.Count == 0)
            return;

        symbol = Math.Abs(symbol) % _symbols.Count;
        if (_shownSymbols[reel] == symbol)
            return;

        _shownSymbols[reel] = symbol;
        _reels[reel].Texture = _symbols[symbol];
    }

    private void UpdateStatus(SlotMachineComponent comp)
    {
        var status = (comp.Spinning, comp.LastOutcome, comp.LastPrize, comp.LastRarity, comp.LastBonusPrize);
        if (_shownStatus is { } shown && shown.Equals(status))
            return;

        _shownStatus = status;
        _status.FontColorOverride = null;

        if (!comp.Spinning && _proto.TryIndex(comp.LastBonusPrize, out var bonus))
        {
            _bonus.SetMessage(Loc.GetString("slot-machine-status-bonus", ("prize", bonus.Name)), defaultColor: Color.White);
            _bonus.Visible = true;
        }
        else
        {
            _bonus.Visible = false;
        }

        if (comp.Spinning)
        {
            _status.Text = Loc.GetString("slot-machine-status-spinning");
            return;
        }

        switch (comp.LastOutcome)
        {
            case SlotMachineOutcome.Win:
                var prize = _proto.TryIndex(comp.LastPrize, out var prototype) ? prototype.Name : string.Empty;
                _status.Text = Loc.GetString("slot-machine-status-win", ("prize", prize));
                _status.FontColorOverride = SlotMachineColors.Text(comp.LastRarity);
                break;
            case SlotMachineOutcome.Loss:
                _status.Text = Loc.GetString("slot-machine-status-loss");
                _status.FontColorOverride = SlotMachineColors.Subdued;
                break;
            case SlotMachineOutcome.Refunded:
                _status.Text = Loc.GetString("slot-machine-status-refund");
                break;
            default:
                _status.Text = Loc.GetString("slot-machine-status-idle");
                break;
        }
    }
    
    // Outlines the reels in the colour of the prize's rarity for a jackpot
    // A bonus prize turns the reels rainbow
    private void UpdateHighlight(SlotMachineComponent comp)
    {
        var won = !comp.Spinning && comp.LastOutcome == SlotMachineOutcome.Win;
        var rainbow = !comp.Spinning && comp.LastBonusPrize != null;
        var now = _timing.RealTime;

        if (rainbow)
            _bonus.Modulate = SlotMachineColors.Rainbow(now, 0);

        for (var i = 0; i < _reelFrames.Length; i++)
        {
            if (rainbow)
                _reelFrames[i].BorderColor = SlotMachineColors.Rainbow(now, i + 1);
            else if (won)
                _reelFrames[i].BorderColor = SlotMachineColors.Border(comp.LastRarity);
            else
                _reelFrames[i].BorderColor = SlotMachineColors.PanelEdge;
        }
    }

    private void SelectTier(int tier)
    {
        _selectedTier = tier;

        for (var i = 0; i < _tierButtons.Count; i++)
        {
            _tierButtons[i].Pressed = i == tier;
        }

        if (!_entities.TryGetComponent(_machine, out SlotMachineComponent? comp) || tier < 0 || tier >= comp.Tiers.Count)
        {
            _tierInfo.Text = string.Empty;
            return;
        }

        _tierInfo.Text = Loc.GetString("slot-machine-tier-info",
            ("price", SlotMachineFormat.Number(comp.Tiers[tier].Price)),
            ("chance", SlotMachineFormat.Percent(comp.Tiers[tier].WinChance)));
    }

    private void PullLever()
    {
        _leverLockedUntil = _timing.RealTime + PullLock;
        SpinRequested?.Invoke(_selectedTier);
    }

    private void ToggleOdds()
    {
        if (_odds is { IsOpen: true })
        {
            _odds.Close();
            return;
        }

        if (!_entities.TryGetComponent(_machine, out SlotMachineComponent? comp))
            return;

        _odds = new SlotMachineOddsWindow(comp, _selectedTier);
        _odds.OpenCentered();
    }
}
