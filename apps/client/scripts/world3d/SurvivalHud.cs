using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ashfall.Proto;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Touch-first survival overlay, dressed in the Ashfall design system. It
/// DISPLAYS server state (meters, inventory) and RAISES intent (move, jump,
/// craft, place, eat) — it never invents authoritative state, and every action
/// is a request the server may still refuse.
///
/// Layout is thumb-first: striped survival meters read across the top-left, a
/// day/night chip and the menu toggle sit top-right, a quick-use hotbar spans
/// the bottom, the movement stick lives under the left thumb and Jump under the
/// right. Crafting, building and travel live behind the menu so they never
/// cover the world.
/// </summary>
public partial class SurvivalHud : Control
{
    private const int MeterMax = SurvivalRules.MaxPoints;
    private const int MeterWidth = 122;
    private const int MeterHeight = 12;
    private const int MeterLabelWidth = 54;

    private const int SideMenuWidth = 300;
    private const float SlideSeconds = 0.22f;
    private const int HotbarSlot = 52;

    private WorldConnection _connection = null!;

    private readonly List<Meter> _meters = new();
    private ColorRect _dayDot = null!;
    private Label _dayText = null!;

    private Label _inventoryReadout = null!;
    private VBoxContainer _foodList = null!;
    private HBoxContainer _hotbar = null!;

    private VirtualJoystick _joystick = null!;
    private PanelContainer _sheet = null!;
    private Button _toggle = null!;
    private Tween? _slide;

    private readonly List<(CraftingRules.Recipe Recipe, Button Button)> _craftButtons = new();
    private readonly List<(ItemId Item, Button Button)> _placeButtons = new();
    private IReadOnlyDictionary<ItemId, int> _inventory = new Dictionary<ItemId, int>();

    private VBoxContainer _travelList = null!;
    private IReadOnlyList<WorldConnection.WorldInfo> _worlds = new List<WorldConnection.WorldInfo>();

    /// <summary>Items surfaced on the hotbar, in priority order, when held.</summary>
    private static readonly ItemId[] HotbarOrder =
        { ItemId.Axe, ItemId.Pickaxe, ItemId.Wall, ItemId.Campfire, ItemId.Berry };

    public VirtualJoystick MoveStick => _joystick;
    public event System.Action? JumpPressed;
    public event System.Action<ItemId>? PlaceRequested;

    /// <summary>One survival meter: an icon-labelled bar plus a live numeral.</summary>
    private sealed class Meter
    {
        public required ColorRect Fill;
        public required Label Numeral;

        public void Set(int value)
        {
            float fraction = Mathf.Clamp(value / (float)MeterMax, 0f, 1f);
            Fill.Size = new Vector2(fraction * (MeterWidth - 4), MeterHeight - 4);
            Numeral.Text = value.ToString();
        }
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Theme = DesignSystem.BuildTheme();

        BuildMeters();
        BuildTopRight();
        BuildActionSheet();
        BuildHotbar();
        BuildTouchControls();
        RefreshActionAvailability();
    }

    public void Bind(WorldConnection connection)
    {
        _connection = connection;
        _connection.StatsUpdated += (hunger, stamina, health, warmth, timeOfDay) =>
            CallDeferred(nameof(ApplyStats), hunger, stamina, health, warmth, timeOfDay);
        _connection.InventoryUpdated += inventory =>
        {
            _inventory = inventory;
            CallDeferred(nameof(RefreshInventory));
        };
        PopulateTravel();
    }

    private async void PopulateTravel()
    {
        _worlds = await _connection.FetchWorldsAsync();
        CallDeferred(nameof(RefreshTravel));
    }

    // ---- Survival meters (top-left) -----------------------------------

    private void BuildMeters()
    {
        var column = new VBoxContainer();
        column.SetAnchorsPreset(LayoutPreset.TopLeft);
        column.OffsetLeft = DesignSystem.Space;
        column.OffsetTop = DesignSystem.Space;
        column.AddThemeConstantOverride("separation", 5);
        AddChild(column);

        _meters.Add(AddMeter(column, "hunger", "HUNGER", DesignSystem.Hunger));
        _meters.Add(AddMeter(column, "stamina", "STAMINA", DesignSystem.Stamina));
        _meters.Add(AddMeter(column, "health", "HEALTH", DesignSystem.Health));
        // Warmth is post-design but a real stat; it borrows an ember-warm hue.
        _meters.Add(AddMeter(column, "campfire", "WARMTH", DesignSystem.Warmth));
    }

    private static Meter AddMeter(Container parent, string icon, string label, Color fill)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(PixelIcons.Make(icon, 22));

        var name = DesignSystem.Kicker(label, DesignSystem.Muted, 9);
        name.CustomMinimumSize = new Vector2(MeterLabelWidth, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(name);

        var bar = new Control { CustomMinimumSize = new Vector2(MeterWidth, MeterHeight) };
        var bg = new ColorRect { Color = DesignSystem.Edge };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bar.AddChild(bg);
        var fillRect = new ColorRect
        {
            Color = fill,
            Position = new Vector2(2, 2),
            Size = new Vector2(MeterWidth - 4, MeterHeight - 4),
        };
        bar.AddChild(fillRect);
        row.AddChild(bar);

        var numeral = DesignSystem.Kicker("0", DesignSystem.Parchment, DesignSystem.NumeralSize);
        numeral.CustomMinimumSize = new Vector2(26, 0);
        numeral.HorizontalAlignment = HorizontalAlignment.Right;
        numeral.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(numeral);

        parent.AddChild(row);
        return new Meter { Fill = fillRect, Numeral = numeral };
    }

    // ---- Day/night chip + menu toggle (top-right) ---------------------

    private void BuildTopRight()
    {
        var column = new VBoxContainer();
        column.SetAnchorsPreset(LayoutPreset.TopRight);
        column.GrowHorizontal = GrowDirection.Begin;
        column.OffsetRight = -DesignSystem.Space;
        column.OffsetTop = DesignSystem.Space;
        column.Alignment = BoxContainer.AlignmentMode.End;
        column.AddThemeConstantOverride("separation", 10);
        AddChild(column);

        var chip = new PanelContainer();
        chip.AddThemeStyleboxOverride("panel", DesignSystem.Panel(new Color(DesignSystem.Ink900, 0.72f), 8));
        chip.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        var chipRow = new HBoxContainer();
        chipRow.AddThemeConstantOverride("separation", 6);
        _dayDot = new ColorRect { Color = DesignSystem.EmberLight, CustomMinimumSize = new Vector2(10, 10) };
        var dotWrap = new CenterContainer();
        dotWrap.AddChild(_dayDot);
        chipRow.AddChild(dotWrap);
        _dayText = DesignSystem.Kicker("DAY", DesignSystem.Parchment, 9);
        _dayText.VerticalAlignment = VerticalAlignment.Center;
        chipRow.AddChild(_dayText);
        chip.AddChild(chipRow);
        column.AddChild(chip);

        _toggle = new Button
        {
            Text = "☰",
            ToggleMode = true,
            CustomMinimumSize = new Vector2(DesignSystem.RoundButton, DesignSystem.RoundButton),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
        };
        _toggle.AddThemeFontSizeOverride("font_size", 28);
        DesignSystem.StyleButton(_toggle,
            DesignSystem.Round(), DesignSystem.Round(), DesignSystem.Parchment);
        _toggle.Toggled += OnToggleActions;
        column.AddChild(_toggle);
    }

    // ---- Quick-use hotbar (bottom-centre) -----------------------------

    private void BuildHotbar()
    {
        _hotbar = new HBoxContainer();
        _hotbar.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _hotbar.GrowHorizontal = GrowDirection.Both;
        _hotbar.GrowVertical = GrowDirection.Begin;
        _hotbar.OffsetBottom = -DesignSystem.Space;
        _hotbar.AddThemeConstantOverride("separation", DesignSystem.SpaceSm);
        AddChild(_hotbar);
    }

    /// <summary>
    /// The hotbar mirrors what the player is holding: placeables build in front,
    /// food is eaten, tools sit ready. Every slot is one tap of an action the
    /// server already exposes, so nothing new is authoritative here.
    /// </summary>
    private void RefreshHotbar()
    {
        foreach (var child in _hotbar.GetChildren()) child.QueueFree();

        foreach (var item in HotbarOrder)
        {
            int count = _inventory.GetValueOrDefault(item);
            if (count <= 0) continue;
            _hotbar.AddChild(HotbarSlotFor(item, count));
        }
    }

    private Button HotbarSlotFor(ItemId item, int count)
    {
        var slot = new Button { CustomMinimumSize = new Vector2(HotbarSlot, HotbarSlot) };
        DesignSystem.StyleButton(slot, DesignSystem.Slot(), DesignSystem.Slot(selected: true), DesignSystem.Parchment);

        var icon = PixelIcons.Make(item, HotbarSlot - 16);
        icon.SetAnchorsPreset(LayoutPreset.FullRect);
        icon.OffsetLeft = 8;
        icon.OffsetTop = 8;
        icon.OffsetRight = -8;
        icon.OffsetBottom = -8;
        slot.AddChild(icon);

        var badge = DesignSystem.Kicker(count.ToString(), DesignSystem.Parchment, 10);
        badge.SetAnchorsPreset(LayoutPreset.BottomRight);
        badge.GrowHorizontal = GrowDirection.Begin;
        badge.OffsetRight = -3;
        badge.OffsetBottom = -1;
        slot.AddChild(badge);

        if (ItemCatalog.IsFood(item)) slot.Pressed += () => _connection.SendEat(item);
        else if (PlacementRules.Placeables.Contains(item)) slot.Pressed += () => PlaceRequested?.Invoke(item);
        return slot;
    }

    // ---- Touch controls (bottom corners) ------------------------------

    private void BuildTouchControls()
    {
        _joystick = new VirtualJoystick();
        _joystick.SetAnchorsPreset(LayoutPreset.BottomLeft);
        float diameter = _joystick.Radius * 2;
        _joystick.OffsetLeft = DesignSystem.Space;
        _joystick.OffsetRight = DesignSystem.Space + diameter;
        _joystick.OffsetTop = -(diameter + DesignSystem.Space);
        _joystick.OffsetBottom = -DesignSystem.Space;
        AddChild(_joystick);

        var jump = new Button
        {
            Text = "JUMP",
            CustomMinimumSize = new Vector2(DesignSystem.RoundButton, DesignSystem.RoundButton),
        };
        jump.SetAnchorsPreset(LayoutPreset.BottomRight);
        jump.GrowHorizontal = GrowDirection.Begin;
        jump.GrowVertical = GrowDirection.Begin;
        jump.OffsetLeft = -(DesignSystem.RoundButton + DesignSystem.Space);
        jump.OffsetRight = -DesignSystem.Space;
        jump.OffsetTop = -(DesignSystem.RoundButton + DesignSystem.Space);
        jump.OffsetBottom = -DesignSystem.Space;
        jump.AddThemeFontSizeOverride("font_size", DesignSystem.LabelSize);
        if (DesignSystem.Display is { } font) jump.AddThemeFontOverride("font", font);
        DesignSystem.StyleButton(jump, DesignSystem.Round(), DesignSystem.Round(), DesignSystem.Parchment);
        jump.Pressed += () => JumpPressed?.Invoke();
        AddChild(jump);
    }

    // ---- Slide-out action sheet ---------------------------------------

    private void BuildActionSheet()
    {
        _sheet = new PanelContainer();
        _sheet.AnchorLeft = 1;
        _sheet.AnchorRight = 1;
        _sheet.AnchorTop = 0;
        _sheet.AnchorBottom = 1;
        _sheet.OffsetLeft = 0;
        _sheet.OffsetRight = SideMenuWidth;

        var background = DesignSystem.Panel(new Color(DesignSystem.Ink900, 0.97f), 12);
        // Clear the round menu button that sits in the top-right corner.
        background.ContentMarginTop = DesignSystem.RoundButton + DesignSystem.Space * 2;
        _sheet.AddThemeStyleboxOverride("panel", background);
        AddChild(_sheet);

        var tabs = new TabContainer();
        StyleTabs(tabs);
        _sheet.AddChild(tabs);

        tabs.AddChild(BuildInventoryTab());
        tabs.AddChild(BuildCraftTab());
        tabs.AddChild(BuildPlaceTab());
        tabs.AddChild(BuildTravelTab());
    }

    private static void StyleTabs(TabContainer tabs)
    {
        if (DesignSystem.Display is { } font) tabs.AddThemeFontOverride("font", font);
        tabs.AddThemeFontSizeOverride("font_size", 10);
        tabs.AddThemeColorOverride("font_selected_color", DesignSystem.OnEmber);
        tabs.AddThemeColorOverride("font_unselected_color", DesignSystem.Muted);
        tabs.AddThemeStyleboxOverride("tab_selected", Flat(DesignSystem.Ember));
        tabs.AddThemeStyleboxOverride("tab_unselected", Flat(DesignSystem.Ink700));
        tabs.AddThemeStyleboxOverride("tab_hovered", Flat(DesignSystem.Ink600));
        tabs.AddThemeStyleboxOverride("panel", DesignSystem.Panel(new Color(0, 0, 0, 0), DesignSystem.SpaceSm));
    }

    private static StyleBoxFlat Flat(Color colour)
    {
        return new StyleBoxFlat
        {
            BgColor = colour,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };
    }

    private ScrollContainer BuildInventoryTab()
    {
        var scroll = new ScrollContainer { Name = "ITEMS" };
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 8);

        _inventoryReadout = new Label { Text = "Inventory\n(empty)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _inventoryReadout.AddThemeColorOverride("font_color", DesignSystem.Muted);
        column.AddChild(_inventoryReadout);

        _foodList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _foodList.AddThemeConstantOverride("separation", 6);
        column.AddChild(_foodList);

        scroll.AddChild(column);
        return scroll;
    }

    private ScrollContainer BuildCraftTab()
    {
        var scroll = new ScrollContainer { Name = "CRAFT" };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);

        foreach (var recipe in CraftingRules.Recipes)
        {
            var button = MenuButton(DescribeRecipe(recipe));
            var output = recipe.Output;
            button.Pressed += () => _connection.SendCraft(output);
            list.AddChild(button);
            _craftButtons.Add((recipe, button));
        }
        return scroll;
    }

    private ScrollContainer BuildPlaceTab()
    {
        var scroll = new ScrollContainer { Name = "BUILD" };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);

        foreach (var item in PlacementRules.Placeables)
        {
            var button = MenuButton($"Place {item}");
            var kind = item;
            button.Pressed += () => PlaceRequested?.Invoke(kind);
            list.AddChild(button);
            _placeButtons.Add((item, button));
        }
        return scroll;
    }

    private ScrollContainer BuildTravelTab()
    {
        var scroll = new ScrollContainer { Name = "TRAVEL" };
        _travelList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _travelList.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_travelList);
        return scroll;
    }

    /// <summary>A full-width ember action row, the design's primary button form.</summary>
    private static Button MenuButton(string text)
    {
        return new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, DesignSystem.TouchTarget),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
    }

    private void RefreshTravel()
    {
        foreach (var child in _travelList.GetChildren()) child.QueueFree();

        if (_worlds.Count == 0)
        {
            var empty = new Label { Text = "No destinations available." };
            empty.AddThemeColorOverride("font_color", DesignSystem.Muted);
            _travelList.AddChild(empty);
            return;
        }

        foreach (var world in _worlds)
        {
            var button = MenuButton($"Sail to {world.Id}");
            string target = world.Id;
            button.Pressed += () => _connection.SendRequestRelease(target);
            _travelList.AddChild(button);
        }
    }

    private void OnToggleActions(bool open)
    {
        _joystick.Visible = !open;

        float targetLeft = open ? -SideMenuWidth : 0;
        float targetRight = open ? 0 : SideMenuWidth;

        _slide?.Kill();
        _slide = CreateTween();
        _slide.SetParallel(true);
        _slide.SetEase(Tween.EaseType.Out);
        _slide.SetTrans(Tween.TransitionType.Cubic);
        _slide.TweenProperty(_sheet, "offset_left", targetLeft, SlideSeconds);
        _slide.TweenProperty(_sheet, "offset_right", targetRight, SlideSeconds);
    }

    private static string DescribeRecipe(CraftingRules.Recipe recipe)
    {
        var inputs = new StringBuilder();
        for (int i = 0; i < recipe.Inputs.Count; i++)
        {
            if (i > 0) inputs.Append(", ");
            var input = recipe.Inputs[i];
            inputs.Append($"{input.Amount} {input.Item}");
        }
        return $"{recipe.Output}  ({inputs})";
    }

    // ---- Live state ---------------------------------------------------

    private void ApplyStats(int hunger, int stamina, int health, int warmth, float timeOfDay)
    {
        _meters[0].Set(hunger);
        _meters[1].Set(stamina);
        _meters[2].Set(health);
        _meters[3].Set(warmth);
        UpdateDayChip(timeOfDay);
    }

    /// <summary>Sunrise at 0.25, noon 0.5, sunset 0.75 — the same phase the world uses.</summary>
    private void UpdateDayChip(float timeOfDay)
    {
        float daylight = Mathf.Sin((timeOfDay - 0.25f) * Mathf.Tau);
        if (daylight > 0.25f)
        {
            _dayText.Text = "DAY";
            _dayDot.Color = DesignSystem.EmberLight;
        }
        else if (daylight > -0.25f)
        {
            _dayText.Text = timeOfDay < 0.5f ? "DAWN" : "DUSK";
            _dayDot.Color = DesignSystem.Ember;
        }
        else
        {
            _dayText.Text = "NIGHT";
            _dayDot.Color = new Color("6678b0");
        }
    }

    private void RefreshInventory()
    {
        if (_inventory.Count == 0)
        {
            _inventoryReadout.Text = "Inventory\n(empty)";
        }
        else
        {
            var lines = new StringBuilder("Inventory");
            foreach (var entry in _inventory)
                lines.Append($"\n{entry.Key} : {entry.Value}");
            _inventoryReadout.Text = lines.ToString();
        }

        RefreshFood();
        RefreshHotbar();
        RefreshActionAvailability();
    }

    private void RefreshFood()
    {
        foreach (var child in _foodList.GetChildren()) child.QueueFree();

        foreach (var (item, count) in _inventory)
        {
            if (count <= 0 || !ItemCatalog.IsFood(item)) continue;

            var button = MenuButton($"Eat {ItemCatalog.Of(item).Name}  (+{ItemCatalog.Of(item).FoodValue} hunger)");
            var food = item;
            button.Pressed += () => _connection.SendEat(food);
            _foodList.AddChild(button);
        }
    }

    private void RefreshActionAvailability()
    {
        foreach (var (recipe, button) in _craftButtons)
            button.Disabled = !CraftingRules.CanCraft(_inventory, recipe);

        foreach (var (item, button) in _placeButtons)
            button.Disabled = _inventory.GetValueOrDefault(item) <= 0;
    }
}
