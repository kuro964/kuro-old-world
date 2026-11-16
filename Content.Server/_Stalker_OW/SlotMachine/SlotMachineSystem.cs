using Content.Server._Stalker_EN.Currency;
using Content.Server.Stack;
using Content.Shared._Stalker_OW.SlotMachine;
using Content.Shared.ActionBlocker;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_OW.SlotMachine;

public sealed class SlotMachineSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly STCurrencySystem _currency = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private static readonly TimeSpan BalanceRefreshInterval = TimeSpan.FromSeconds(1);

    // The rarities a win can land on. Every spin price gives each of them a weight.
    private static readonly SlotMachineRarity[] RegularRarities =
    {
        SlotMachineRarity.Common,
        SlotMachineRarity.Uncommon,
        SlotMachineRarity.Rare,
    };

    private TimeSpan _nextBalanceRefresh;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SlotMachineComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<SlotMachineComponent, SlotMachineSpinMessage>(OnSpin);
        SubscribeLocalEvent<SlotMachineComponent, EntityTerminatingEvent>(OnTerminating);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);

        ReportPoolProblems();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var refreshBalances = now >= _nextBalanceRefresh;
        if (refreshBalances)
            _nextBalanceRefresh = now + BalanceRefreshInterval;

        var query = EntityQueryEnumerator<SlotMachineComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Spinning && (now - comp.SpinStart).TotalSeconds >= comp.SpinDuration)
                FinishSpin(uid, comp);

            if (!refreshBalances)
                continue;

            foreach (var actor in _ui.GetActors(uid, SlotMachineUiKey.Key))
            {
                SendBalance(uid, comp, actor);
            }
        }
    }

    private void OnUiOpened(EntityUid uid, SlotMachineComponent comp, BoundUIOpenedEvent args)
    {
        SendBalance(uid, comp, args.Actor);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<SlotMachinePrizePoolPrototype>() || args.WasModified<EntityPrototype>())
            ReportPoolProblems();
    }
    
    private void ReportPoolProblems()
    {
        foreach (var pool in _proto.EnumeratePrototypes<SlotMachinePrizePoolPrototype>())
        {
            foreach (var prize in pool.Prizes)
            {
                string problem;

                if (!_proto.HasIndex<EntityPrototype>(prize.Id))
                    problem = "is not an entity prototype";
                else if (!SlotMachinePrizePoolPrototype.IsUsableWeight(prize.Weight))
                    problem = $"has an unusable weight of {prize.Weight}";
                else
                    continue;

                Log.Error($"Slot machine prize pool {pool.ID}: prize {prize.Id} {problem}.");
            }
        }
    }

    private void OnSpin(EntityUid uid, SlotMachineComponent comp, SlotMachineSpinMessage args)
    {
        var user = args.Actor;

        if (comp.Spinning || !_blocker.CanInteract(user, uid))
            return;

        if (args.Tier < 0 || args.Tier >= comp.Tiers.Count)
            return;

        var tier = comp.Tiers[args.Tier];

        // Refuse before taking money if the spin could not run or a win could not be paid out
        if (GetConfigError(comp, tier) is { } error)
        {
            RefuseSpin(uid, user, error);
            return;
        }

        if (!_proto.TryIndex(comp.PrizePool, out var pool))
        {
            RefuseSpin(uid, user, "its prize pool does not exist");
            return;
        }

        var tiers = pool.GetTiers(_proto, tier.RarityWeights);
        if (tiers.Count == 0)
        {
            RefuseSpin(uid, user, "its prize pool has no valid prize");
            return;
        }

        var bonusPrizes = pool.GetPrizes(_proto, SlotMachineRarity.Mystery);

        if (_currency.CountRoubles(user, comp.Currency) < tier.Price
            || !_currency.TryDeductRoubles(user, tier.Price, comp.Currency))
        {
            _popup.PopupEntity(Loc.GetString("slot-machine-popup-poor"), uid, user);
            SendBalance(uid, comp, user);
            return;
        }

        var win = _random.Prob(tier.WinChance);
        SlotMachinePrize? prize = win ? PickPrize(tiers) : null;

        comp.Spinning = true;
        comp.SpinStart = _timing.CurTime;
        comp.Result = RollSymbols(comp.Symbols.Count, win, comp.NearMissChance);
        comp.LastOutcome = SlotMachineOutcome.None;
        comp.LastPrize = null;
        comp.LastBonusPrize = null;
        comp.PendingPrize = prize?.Id;
        comp.PendingBonusPrize = bonusPrizes.Count > 0 && _random.Prob(comp.BonusChance)
            ? PickWeighted(bonusPrizes).Id
            : (EntProtoId?) null;
        comp.PendingRarity = prize?.Rarity ?? SlotMachineRarity.Common;
        comp.Stake = tier.Price;
        comp.Spinner = user;
        Dirty(uid, comp);
        
        _audio.PlayPvs(comp.SpinSound, uid);

        if (RollRare(comp.RareSpinSound, comp.RareSpinChance))
            _audio.PlayPvs(comp.RareSpinSound, uid);

        SendBalance(uid, comp, user);
    }

    private void RefuseSpin(EntityUid uid, EntityUid user, string reason)
    {
        Log.Error($"Slot machine {ToPrettyString(uid)} is misconfigured: {reason}.");
        _popup.PopupEntity(Loc.GetString("slot-machine-popup-broken"), uid, user);
    }

    private static string? GetConfigError(SlotMachineComponent comp, SlotMachineTier tier)
    {
        if (tier.Price <= 0)
            return "the spin price must be positive";

        if (!IsChance(tier.WinChance))
            return "the win chance must be between 0 and 1";

        if (!IsChance(comp.NearMissChance) || !IsChance(comp.BonusChance) || !IsChance(comp.RareSpinChance) || !IsChance(comp.RareLoseChance))
            return "the near-miss, bonus and rare sound chances must be between 0 and 1";

        var total = 0f;
        foreach (var rarity in RegularRarities)
        {
            if (!tier.RarityWeights.TryGetValue(rarity, out var weight) || !SlotMachinePrizePoolPrototype.IsUsableWeight(weight))
                return "common, uncommon and rare each need a positive weight";

            total += weight;
        }

        if (tier.RarityWeights.Count != RegularRarities.Length)
            return "only common, uncommon and rare can have a weight";

        if (Math.Abs(total - 100f) > 0.001f)
            return "common, uncommon and rare weights must add up to 100";

        if (comp.Symbols.Count < 2)
            return "it needs at least two symbols";

        if (comp.ReelStopDelays.Length != SlotMachineComponent.ReelCount)
            return $"it needs exactly {SlotMachineComponent.ReelCount} reel stop delays";

        var previous = 0f;
        foreach (var delay in comp.ReelStopDelays)
        {
            if (!(delay >= previous) || !float.IsFinite(delay))
                return "reel stop delays must be zero or more and in order";

            previous = delay;
        }

        if (!(comp.SpinDuration >= previous) || !float.IsFinite(comp.SpinDuration))
            return "the spin duration must not be shorter than the last reel stop delay";

        return null;
    }
    
    private static bool IsChance(float chance)
    {
        return chance >= 0f && chance <= 1f;
    }

    // If machine is being destroyed, taken, etc mid-spin, then settle instantly
    private void OnTerminating(EntityUid uid, SlotMachineComponent comp, ref EntityTerminatingEvent args)
    {
        if (comp.Spinning)
            Settle(uid, comp, out _, out _);
    }

    private void FinishSpin(EntityUid uid, SlotMachineComponent comp)
    {
        var spinner = comp.Spinner;
        var prize = comp.PendingPrize;
        var bonusPrize = comp.PendingBonusPrize;

        comp.LastOutcome = Settle(uid, comp, out var item, out var bonusItem);
        comp.LastBonusPrize = bonusItem != null ? bonusPrize : null;

        // One drop sound per payout
        if (item != null || bonusItem != null)
            _audio.PlayPvs(comp.DispenseSound, uid);

        if (bonusItem is { } bonus)
            _popup.PopupEntity(Loc.GetString("slot-machine-popup-bonus", ("prize", Name(bonus))), uid, PopupType.Large);

        if (item is { } won)
        {
            comp.LastPrize = prize;
            comp.LastRarity = comp.PendingRarity;

            _audio.PlayPvs(comp.WinSound, uid);
            _popup.PopupEntity(Loc.GetString("slot-machine-popup-win", ("prize", Name(won))), uid, PopupType.Medium);
        }
        else if (comp.LastOutcome == SlotMachineOutcome.Loss)
        {
            var rare = RollRare(comp.RareLoseSound, comp.RareLoseChance);
            _audio.PlayPvs(rare ? comp.RareLoseSound : comp.LoseSound, uid);
        }

        Dirty(uid, comp);

        if (spinner is { } player && _ui.IsUiOpen(uid, SlotMachineUiKey.Key, player))
            SendBalance(uid, comp, player);
    }
    
    private SlotMachineOutcome Settle(EntityUid uid, SlotMachineComponent comp, out EntityUid? item, out EntityUid? bonusItem)
    {
        item = null;
        bonusItem = null;

        var prize = comp.PendingPrize;
        var bonus = comp.PendingBonusPrize;
        var spinner = comp.Spinner;
        var stake = comp.Stake;

        comp.Spinning = false;
        comp.PendingPrize = null;
        comp.PendingBonusPrize = null;
        comp.Spinner = null;
        comp.Stake = 0;

        var xform = Transform(uid);
        if (xform.MapUid is not { } map || TerminatingOrDeleted(map) || TerminatingOrDeleted(xform.ParentUid))
            return SlotMachineOutcome.None;

        var coordinates = xform.Coordinates.Offset(comp.DispenseOffset);

        if (bonus is { } bonusId)
        {
            if (_proto.HasIndex<EntityPrototype>(bonusId))
            {
                bonusItem = Spawn(bonusId, _transform.ToMapCoordinates(coordinates));
                _adminLog.Add(LogType.Action, LogImpact.Low,
                    $"Slot machine {ToPrettyString(uid):machine} paid bonus {ToPrettyString(bonusItem.Value):item} on a {stake} spin by {ToPrettyString(spinner):player}");
            }
            else
                Log.Error($"Slot machine {ToPrettyString(uid)} could not dispense missing bonus {bonusId}.");
        }

        if (prize == null)
            return SlotMachineOutcome.Loss;

        if (_proto.HasIndex<EntityPrototype>(prize.Value))
        {
            item = Spawn(prize.Value, _transform.ToMapCoordinates(coordinates));

            _adminLog.Add(LogType.Action,
                LogImpact.Low,
                $"Slot machine {ToPrettyString(uid):machine} paid out {ToPrettyString(item.Value):item} on a {stake} spin by {ToPrettyString(spinner):player}");

            return SlotMachineOutcome.Win;
        }

        if (!_proto.HasIndex(comp.Currency))
        {
            Log.Error($"Slot machine {ToPrettyString(uid)} could not dispense missing prize prototype {prize}, and could not refund the {stake} stake of {ToPrettyString(spinner)} because {comp.Currency} does not exist.");
            return SlotMachineOutcome.None;
        }

        Log.Error($"Slot machine {ToPrettyString(uid)} could not dispense missing prize prototype {prize}; refunding {stake}.");

        if (spinner is { } player && !TerminatingOrDeleted(player))
            _currency.AddRoubles(player, stake, comp.Currency);
        else
            _stack.SpawnAtPosition(stake, comp.Currency, coordinates);

        return SlotMachineOutcome.Refunded;
    }
    
    private int[] RollSymbols(int symbols, bool win, float nearMissChance)
    {
        var first = _random.Next(symbols);

        if (win)
            return new[] { first, first, first };

        if (_random.Prob(nearMissChance))
            return new[] { first, first, OtherSymbol(symbols, first) };

        return new[] { first, OtherSymbol(symbols, first), _random.Next(symbols) };
    }
    
    private int OtherSymbol(int symbols, int symbol)
    {
        return (symbol + 1 + _random.Next(symbols - 1)) % symbols;
    }
    
    private SlotMachinePrize PickPrize(List<SlotMachinePrizeTier> tiers)
    {
        var tier = tiers[0];
        var tierRoll = _random.NextDouble();

        foreach (var candidate in tiers)
        {
            tierRoll -= candidate.Chance;
            if (tierRoll >= 0d)
                continue;

            tier = candidate;
            break;
        }

        return PickWeighted(tier.Prizes);
    }
    
    private SlotMachinePrize PickWeighted(List<SlotMachinePrize> prizes)
    {
        var total = 0f;
        foreach (var prize in prizes)
        {
            total += prize.Weight;
        }

        var roll = _random.NextFloat() * total;

        foreach (var prize in prizes)
        {
            roll -= prize.Weight;
            if (roll < 0f)
                return prize;
        }

        return prizes[^1];
    }

    private bool RollRare(SoundSpecifier? sound, float chance)
    {
        return sound != null && _random.Prob(chance);
    }

    private void SendBalance(EntityUid uid, SlotMachineComponent comp, EntityUid actor)
    {
        var balance = _currency.CountRoubles(actor, comp.Currency);
        _ui.ServerSendUiMessage(uid, SlotMachineUiKey.Key, new SlotMachineBalanceMessage(balance), actor);
    }
}
