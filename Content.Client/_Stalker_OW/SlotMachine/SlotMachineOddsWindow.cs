using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._Stalker_OW.SlotMachine;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Stalker_OW.SlotMachine;

public sealed class SlotMachineOddsWindow : FancyWindow
{
    [Dependency] private readonly IPrototypeManager _proto = default!;

    private const int Columns = 2;
    private const int AllCategoriesId = 0;

    private readonly LineEdit _search = new() { HorizontalExpand = true };
    private readonly OptionButton _categoryFilter = new();
    private readonly OptionButton _stakeFilter = new();
    private readonly Label _noResults = new() { FontColorOverride = SlotMachineColors.Subdued, Visible = false };
    private readonly List<Section> _sections = new();

    private readonly BoxContainer _sectionList = new()
    {
        Orientation = BoxContainer.LayoutOrientation.Vertical,
        SeparationOverride = 4,
        HorizontalExpand = true,
    };

    public SlotMachineOddsWindow(SlotMachineComponent machine, int stake)
    {
        IoCManager.InjectDependencies(this);

        Title = Loc.GetString("slot-machine-odds-title");
        MinSize = new Vector2(400, 420);
        SetSize = new Vector2(460, 560);

        var body = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Margin = new Thickness(12),
            SeparationOverride = 4,
            HorizontalExpand = true,
            VerticalExpand = true,
        };
        ContentsContainer.AddChild(body);

        body.AddChild(Heading("slot-machine-odds-tiers"));
        foreach (var tier in machine.Tiers)
        {
            var price = new Label { Text = FormatPrice(tier), HorizontalExpand = true };
            body.AddChild(Row(price, new Label { Text = FormatChance(tier.WinChance) }));
        }

        body.AddChild(new Control { MinHeight = 6 });

        var prizesHeading = Heading("slot-machine-odds-prizes");
        prizesHeading.HorizontalExpand = true;
        prizesHeading.VerticalAlignment = VAlignment.Center;
        body.AddChild(Row(prizesHeading, _stakeFilter));
        body.AddChild(Row(_search, _categoryFilter));

        var list = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 4,
            HorizontalExpand = true,
        };
        list.AddChild(_sectionList);
        list.AddChild(_noResults);

        var scroll = new ScrollContainer { HScrollEnabled = false, HorizontalExpand = true, VerticalExpand = true };
        scroll.AddChild(list);
        body.AddChild(scroll);
        body.AddChild(Heading("slot-machine-odds-rounded"));

        _noResults.Text = Loc.GetString("slot-machine-odds-none");
        _search.PlaceHolder = Loc.GetString("slot-machine-odds-search");
        _search.OnTextChanged += _ => Refresh();

        _categoryFilter.AddItem(Loc.GetString("slot-machine-category-all"), AllCategoriesId);
        _categoryFilter.OnItemSelected += args =>
        {
            _categoryFilter.SelectId(args.Id);
            Refresh();
        };

        if (machine.Tiers.Count == 0)
            return;

        if (!_proto.TryIndex(machine.PrizePool, out var pool))
            return;

        BuildSections(pool);

        for (var i = 0; i < machine.Tiers.Count; i++)
        {
            _stakeFilter.AddItem(FormatPrice(machine.Tiers[i]), i);
        }

        _stakeFilter.OnItemSelected += args =>
        {
            _stakeFilter.SelectId(args.Id);
            ShowStake(pool, machine.Tiers[args.Id]);
        };

        stake = Math.Clamp(stake, 0, machine.Tiers.Count - 1);
        _stakeFilter.SelectId(stake);
        ShowStake(pool, machine.Tiers[stake]);
    }

    private void BuildSections(SlotMachinePrizePoolPrototype pool)
    {
        var categories = new SortedSet<SlotMachinePrizeCategory>();

        foreach (var rarity in Enum.GetValues<SlotMachineRarity>())
        {
            var prizes = pool.GetPrizes(_proto, rarity);
            if (prizes.Count == 0)
                continue;

            var section = new Section(rarity);
            _sections.Add(section);
            _sectionList.AddChild(BuildSection(section));

            if (rarity == SlotMachineRarity.Mystery)
            {
                BuildMystery(section);
                continue;
            }

            BuildItems(section, prizes);

            foreach (var prize in prizes)
            {
                categories.Add(prize.Category);
            }
        }

        foreach (var category in categories)
        {
            _categoryFilter.AddItem(CategoryName(category), FilterId(category));
        }
    }

    private Control BuildSection(Section section)
    {
        var rarity = section.Rarity;

        var title = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 6,
            Margin = new Thickness(6, 2),
        };
        title.AddChild(section.Arrow);

        var name = RarityText(RarityName(rarity), rarity);
        name.HorizontalExpand = true;
        title.AddChild(name);

        if (rarity == SlotMachineRarity.Mystery)
        {
            title.AddChild(new SlotMachineRainbowLabel(Loc.GetString("slot-machine-odds-chance-hidden")));
        }
        else
        {
            section.Share.FontColorOverride = SlotMachineColors.Text(rarity);
            title.AddChild(section.Share);
        }

        var header = new ContainerButton { HorizontalExpand = true };
        header.AddStyleClass(ContainerButton.StyleClassButton);
        header.AddChild(title);
        header.OnPressed += _ =>
        {
            section.Open = !section.Open;
            Refresh();
        };

        section.Root.AddChild(header);
        section.Root.AddChild(section.Body);
        return section.Root;
    }

    private void BuildItems(Section section, List<SlotMachinePrize> prizes)
    {
        var color = SlotMachineColors.Text(section.Rarity);
        var totalWeight = prizes.Sum(p => p.Weight);

        foreach (var categoryPrizes in prizes.GroupBy(p => p.Category).OrderBy(g => g.Key))
        {
            var group = new Group(categoryPrizes.Key);
            var grid = new GridContainer { Columns = Columns, HorizontalExpand = true };

            group.Root.AddChild(new Label { Text = CategoryName(categoryPrizes.Key), FontColorOverride = SlotMachineColors.Subdued });
            group.Root.AddChild(grid);

            foreach (var prize in categoryPrizes)
            {
                var name = _proto.Index(prize.Id).Name;

                var cell = new BoxContainer
                {
                    Orientation = BoxContainer.LayoutOrientation.Horizontal,
                    SeparationOverride = 4,
                    HorizontalExpand = true,
                    MouseFilter = MouseFilterMode.Pass,
                };

                var view = new EntityPrototypeView
                {
                    MinSize = new Vector2(32),
                    MaxSize = new Vector2(32),
                    Stretch = SpriteView.StretchMode.Fit,
                    OverrideDirection = Direction.South,
                };
                view.SetPrototype(prize.Id);
                cell.AddChild(view);

                cell.AddChild(new Label
                {
                    Text = name,
                    FontColorOverride = color,
                    HorizontalExpand = true,
                    ClipText = true,
                    VerticalAlignment = VAlignment.Center,
                });

                grid.AddChild(cell);
                group.Items.Add(new Item(cell, name, prize.Weight / totalWeight));
            }

            section.Groups.Add(group);
            section.Body.AddChild(group.Root);
        }
    }

    private static void BuildMystery(Section section)
    {
        section.Body.AddChild(new Label { Text = Loc.GetString("slot-machine-odds-mystery-title") });

        var text = new RichTextLabel { HorizontalExpand = true };
        text.SetMessage(FormattedMessage.FromMarkupPermissive(
            Loc.GetString("slot-machine-odds-mystery-text")));
        section.Body.AddChild(text);

        var chance = Row(
            new Label { Text = Loc.GetString("slot-machine-odds-mystery-chance") },
            new SlotMachineRainbowLabel(Loc.GetString("slot-machine-odds-chance-hidden")));
        chance.HorizontalExpand = false;
        chance.Margin = new Thickness(0, 6, 0, 0);
        section.Body.AddChild(chance);
    }

    private void ShowStake(SlotMachinePrizePoolPrototype pool, SlotMachineTier stake)
    {
        var tiers = pool.GetTiers(_proto, stake.RarityWeights);

        foreach (var section in _sections)
        {
            if (section.Rarity == SlotMachineRarity.Mystery)
                continue;

            var tier = tiers.Find(t => t.Rarity == section.Rarity);
            section.Rollable = tier != null;

            if (tier == null)
                continue;

            section.Share.Text = Loc.GetString("slot-machine-odds-share", ("chance", SlotMachineFormat.Percent(tier.Chance)));

            foreach (var group in section.Groups)
            {
                foreach (var item in group.Items)
                {
                    item.Cell.ToolTip = Loc.GetString("slot-machine-odds-item-tooltip",
                        ("name", item.Name),
                        ("chance", SlotMachineFormat.Percent(tier.Chance * item.Share)));
                }
            }
        }

        Refresh();
    }

    private void Refresh()
    {
        var search = _search.Text.Trim();
        var category = _categoryFilter.SelectedId;
        var filtering = search.Length > 0 || category != AllCategoriesId;
        var anyResults = false;

        foreach (var section in _sections)
        {
            if (section.Rarity == SlotMachineRarity.Mystery)
            {
                SetOpen(section, section.Open);
                continue;
            }

            var matches = 0;

            foreach (var group in section.Groups)
            {
                var groupMatches = 0;

                foreach (var item in group.Items)
                {
                    var visible = (category == AllCategoriesId || category == FilterId(group.Category))
                                  && (search.Length == 0 || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

                    item.Cell.Visible = visible;
                    if (visible)
                        groupMatches++;
                }

                group.Root.Visible = groupMatches > 0;
                matches += groupMatches;
            }

            var shown = section.Rollable && (!filtering || matches > 0);
            anyResults |= shown;
            section.Root.Visible = shown;

            SetOpen(section, section.Open || filtering);
        }

        _noResults.Visible = filtering && !anyResults;
    }

    private static void SetOpen(Section section, bool open)
    {
        section.Body.Visible = open;
        section.Arrow.Text = open ? "-" : "+";
    }

    private static BoxContainer Row(params Control[] children)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 6,
            HorizontalExpand = true,
        };

        foreach (var child in children)
        {
            row.AddChild(child);
        }

        return row;
    }

    private static Label Heading(string key) => new()
    {
        Text = Loc.GetString(key),
        FontColorOverride = SlotMachineColors.Subdued,
    };

    private static Control RarityText(string text, SlotMachineRarity rarity)
    {
        if (rarity == SlotMachineRarity.Mystery)
            return new SlotMachineRainbowLabel(text);

        return new Label { Text = text, FontColorOverride = SlotMachineColors.Text(rarity) };
    }

    private static string FormatPrice(SlotMachineTier stake)
    {
        return Loc.GetString("slot-machine-odds-price", ("price", SlotMachineFormat.Number(stake.Price)));
    }

    private static string FormatChance(double chance)
    {
        return Loc.GetString("slot-machine-odds-chance", ("chance", SlotMachineFormat.Percent(chance)));
    }

    private static int FilterId(SlotMachinePrizeCategory category) => (int) category + 1;

    private static string CategoryName(SlotMachinePrizeCategory category) => category switch
    {
        SlotMachinePrizeCategory.Headwear => Loc.GetString("slot-machine-category-headwear"),
        SlotMachinePrizeCategory.Glasses => Loc.GetString("slot-machine-category-glasses"),
        SlotMachinePrizeCategory.Masks => Loc.GetString("slot-machine-category-masks"),
        SlotMachinePrizeCategory.Scarves => Loc.GetString("slot-machine-category-scarves"),
        SlotMachinePrizeCategory.Gloves => Loc.GetString("slot-machine-category-gloves"),
        SlotMachinePrizeCategory.Cloaks => Loc.GetString("slot-machine-category-cloaks"),
        _ => Loc.GetString("slot-machine-category-other"),
    };

    private static string RarityName(SlotMachineRarity rarity) => rarity switch
    {
        SlotMachineRarity.Mystery => Loc.GetString("slot-machine-rarity-mystery"),
        SlotMachineRarity.Rare => Loc.GetString("slot-machine-rarity-rare"),
        SlotMachineRarity.Uncommon => Loc.GetString("slot-machine-rarity-uncommon"),
        _ => Loc.GetString("slot-machine-rarity-common"),
    };

    private sealed class Section(SlotMachineRarity rarity)
    {
        public readonly SlotMachineRarity Rarity = rarity;
        public readonly List<Group> Groups = new();
        public readonly Label Arrow = new() { MinWidth = 12 };
        public readonly Label Share = new();
        public readonly BoxContainer Root = new()
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
        };

        public readonly BoxContainer Body = new()
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 2,
            Margin = new Thickness(8, 4, 0, 6),
            HorizontalExpand = true,
            Visible = false,
        };

        public bool Open;
        public bool Rollable = true;
    }

    private sealed class Group(SlotMachinePrizeCategory category)
    {
        public readonly SlotMachinePrizeCategory Category = category;
        public readonly List<Item> Items = new();

        public readonly BoxContainer Root = new()
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
        };
    }

    private sealed class Item(Control cell, string name, float share)
    {
        public readonly Control Cell = cell;
        public readonly string Name = name;
        public readonly float Share = share;
    }
}
