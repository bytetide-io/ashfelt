using System.Collections.Generic;
using System.Linq;
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

    private const float FadeSeconds = 0.16f;
    private const int HotbarSlot = 52;
    private const int MenuTabWidth = 340;

    private WorldConnection _connection = null!;

    private readonly List<Meter> _meters = new();
    private ColorRect _dayDot = null!;
    private Label _dayText = null!;

    /// <summary>Display size of the carried-items grid — a visual cap only.</summary>
    private const int CarrySlots = 24;
    private const int InvSlot = 52;

    private HFlowContainer _invGrid = null!;
    private Label _carriedCount = null!;
    private PanelContainer _detailRow = null!;
    private TextureRect _detailIcon = null!;
    private Label _detailName = null!;
    private Label _detailDesc = null!;
    private Button _detailAction = null!;
    private ItemId _selected = ItemId.None;

    private HBoxContainer _hotbar = null!;

    private VBoxContainer _gatherPrompt = null!;
    private TextureRect _gatherIcon = null!;

    private Control _hudLayer = null!;
    private VirtualJoystick _joystick = null!;
    private PanelContainer _sheet = null!;
    private Button _toggle = null!;
    private Tween? _slide;
    private readonly List<Button> _tabButtons = new();
    private readonly List<Control> _tabPanels = new();
    private int _activeTab;

    private readonly List<CraftCard> _craftCards = new();
    private readonly List<(ItemId Item, Button Button, Label Owned)> _placeCards = new();
    private IReadOnlyDictionary<ItemId, int> _inventory = new Dictionary<ItemId, int>();

    /// <summary>A crafting row: its recipe, the CRAFT button, and the ingredient
    /// count labels that turn red when the pouch is short.</summary>
    private sealed class CraftCard
    {
        public required CraftingRules.Recipe Recipe;
        public required PanelContainer Panel;
        public required Button Button;
        public required IReadOnlyList<(ItemId Item, int Need, Label Label)> Ingredients;
    }

    private VBoxContainer _travelList = null!;
    private IReadOnlyList<WorldConnection.WorldInfo> _worlds = new List<WorldConnection.WorldInfo>();

    /// <summary>Items surfaced on the hotbar, in priority order, when held.</summary>
    private static readonly ItemId[] HotbarOrder =
        { ItemId.Axe, ItemId.Pickaxe, ItemId.Wall, ItemId.Campfire, ItemId.Berry };

    public VirtualJoystick MoveStick => _joystick;
    public event System.Action? JumpPressed;
    public event System.Action<ItemId>? PlaceRequested;
    public event System.Action? FeedFirePressed;

    private Button _feedButton = null!;

    /// <summary>One survival meter: an icon-labelled striped bar plus a live numeral.</summary>
    private sealed class Meter
    {
        public required StripedBar Fill;
        public required Label Numeral;

        public void Set(int value)
        {
            float fraction = Mathf.Clamp(value / (float)MeterMax, 0f, 1f);
            Fill.Size = new Vector2(fraction * (MeterWidth - 4), MeterHeight - 4);
            Fill.QueueRedraw();
            Numeral.Text = value.ToString();
        }
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Theme = DesignSystem.BuildTheme();

        // Everything that draws over the world lives on one layer so the
        // full-screen menu can hide the whole HUD behind it in a single flip.
        _hudLayer = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _hudLayer.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_hudLayer);

        BuildMeters();
        BuildTopRight();
        BuildActionSheet();
        BuildHotbar();
        BuildGatherPrompt();
        BuildFeedButton();
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
        _hudLayer.AddChild(column);

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

        var name = DesignSystem.Kicker(label, DesignSystem.LabelMuted, 9);
        name.CustomMinimumSize = new Vector2(MeterLabelWidth, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(name);

        var bar = new Control { CustomMinimumSize = new Vector2(MeterWidth, MeterHeight) };
        var bg = new ColorRect { Color = DesignSystem.Edge };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bar.AddChild(bg);
        var fillRect = new StripedBar
        {
            Fill = fill,
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
        _hudLayer.AddChild(column);

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
        _hudLayer.AddChild(_hotbar);
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
        _hudLayer.AddChild(_joystick);

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
        _hudLayer.AddChild(jump);
    }

    // ---- "Tap to gather" prompt (floats over a nearby resource) --------

    private const int ReticleBox = 36;
    private const int PromptWidth = 160;

    private void BuildGatherPrompt()
    {
        _gatherPrompt = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(PromptWidth, 0),
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        _gatherPrompt.AddThemeConstantOverride("separation", DesignSystem.SpaceSm);

        var reticle = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = new Color(DesignSystem.EmberLight, 0.9f) };
        reticle.SetBorderWidthAll(3);
        var box = new PanelContainer
        {
            CustomMinimumSize = new Vector2(ReticleBox, ReticleBox),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddThemeStyleboxOverride("panel", reticle);
        _gatherPrompt.AddChild(box);

        var chip = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        chip.AddThemeStyleboxOverride("panel", DesignSystem.Panel(new Color(DesignSystem.Ink900, 0.82f), 8));
        var chipRow = new HBoxContainer();
        chipRow.AddThemeConstantOverride("separation", 6);
        _gatherIcon = PixelIcons.Make("wood", 16);
        chipRow.AddChild(_gatherIcon);
        var label = DesignSystem.Kicker("TAP TO GATHER", DesignSystem.EmberLight, 9);
        label.VerticalAlignment = VerticalAlignment.Center;
        chipRow.AddChild(label);
        chip.AddChild(chipRow);
        _gatherPrompt.AddChild(chip);

        _hudLayer.AddChild(_gatherPrompt);
    }

    /// <summary>Floats the gather reticle over a resource the world has resolved as
    /// in reach; <paramref name="screen"/> is HUD-space (window) coordinates. Hidden
    /// while the action menu is open so it never fights the sheet.</summary>
    public void ShowGatherPrompt(Vector2 screen, ItemId yield)
    {
        if (_toggle.ButtonPressed) { _gatherPrompt.Visible = false; return; }
        _gatherIcon.Texture = PixelIcons.Texture(PixelIcons.NameOf(yield));
        _gatherPrompt.Position = screen - new Vector2(PromptWidth / 2f, ReticleBox / 2f);
        _gatherPrompt.Visible = true;
    }

    public void HideGatherPrompt() => _gatherPrompt.Visible = false;

    // ---- "Feed fire" button (floats above the hotbar) ------------------

    /// <summary>
    /// A fixed-position button rather than a floating reticle: it sits above the
    /// hotbar so it never competes with the "tap to gather" gesture over the same
    /// patch of screen — standing next to both a tree and a campfire should never
    /// leave "which one did that tap mean?"
    /// </summary>
    private void BuildFeedButton()
    {
        _feedButton = new Button
        {
            Text = "FEED FIRE",
            CustomMinimumSize = new Vector2(0, HotbarSlot - 12),
            Visible = false,
        };
        _feedButton.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _feedButton.GrowHorizontal = GrowDirection.Both;
        _feedButton.GrowVertical = GrowDirection.Begin;
        _feedButton.OffsetBottom = -(HotbarSlot + DesignSystem.Space * 2);
        _feedButton.AddThemeFontSizeOverride("font_size", DesignSystem.LabelSize);
        if (DesignSystem.Display is { } font) _feedButton.AddThemeFontOverride("font", font);
        DesignSystem.StyleButton(_feedButton, DesignSystem.EmberButton(), DesignSystem.EmberButtonPressed(), DesignSystem.OnEmber);
        _feedButton.Pressed += () => FeedFirePressed?.Invoke();
        _hudLayer.AddChild(_feedButton);
    }

    /// <summary>Shown while a warmth structure is in reach; dimmed (but still
    /// tappable, same as any other request the server may refuse) when the player
    /// is not carrying the Wood a feed costs.</summary>
    public void ShowFeedPrompt()
    {
        if (_toggle.ButtonPressed) { _feedButton.Visible = false; return; }
        _feedButton.Visible = true;
        _feedButton.Disabled = _inventory.GetValueOrDefault(ItemId.Wood) <= 0;
    }

    public void HideFeedPrompt() => _feedButton.Visible = false;

    // ---- Slide-out action sheet ---------------------------------------

    /// <summary>
    /// The full-screen action menu from the design's INVENTORY mock: a dimmed
    /// scrim over the world, an ACTIONS header with a ✕ close, the ember segmented
    /// ITEMS/CRAFT/BUILD/TRAVEL strip, and the selected tab's panel below. It
    /// hides the world HUD entirely while open so nothing competes with it.
    /// </summary>
    private void BuildActionSheet()
    {
        _sheet = new PanelContainer { Visible = false, Modulate = new Color(1, 1, 1, 0) };
        _sheet.SetAnchorsPreset(LayoutPreset.FullRect);

        var scrim = new StyleBoxFlat { BgColor = new Color(DesignSystem.Ink900, 0.9f) };
        scrim.ContentMarginLeft = DesignSystem.Space;
        scrim.ContentMarginRight = DesignSystem.Space;
        scrim.ContentMarginTop = DesignSystem.SpaceLg;
        scrim.ContentMarginBottom = DesignSystem.SpaceLg;
        _sheet.AddThemeStyleboxOverride("panel", scrim);
        AddChild(_sheet);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        _sheet.AddChild(column);

        column.AddChild(BuildMenuHeader());
        column.AddChild(BuildTabBar());

        var content = new Control
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        column.AddChild(content);

        AddTabPanel(content, BuildInventoryTab());
        AddTabPanel(content, BuildCraftTab());
        AddTabPanel(content, BuildPlaceTab());
        AddTabPanel(content, BuildTravelTab());

        SelectTab(0);
    }

    /// <summary>The menu header: the ACTIONS title and the ✕ that closes back to the
    /// world, mirroring the design's inventory sheet.</summary>
    private Control BuildMenuHeader()
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", DesignSystem.SpaceSm);

        var title = DesignSystem.Kicker("ACTIONS", DesignSystem.Parchment, 13);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(title);

        var close = new Button { Text = "✕", CustomMinimumSize = new Vector2(36, 36) };
        close.AddThemeFontSizeOverride("font_size", 15);
        DesignSystem.StyleButton(close, DesignSystem.Slot(), DesignSystem.Slot(selected: true), DesignSystem.Muted);
        close.Pressed += () => _toggle.ButtonPressed = false;
        row.AddChild(close);
        return row;
    }

    /// <summary>The design's segmented tab strip — an inset bar of four equal ember
    /// segments — so ITEMS/CRAFT/BUILD/TRAVEL read and switch on one thumb.</summary>
    private Control BuildTabBar()
    {
        var bar = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            CustomMinimumSize = new Vector2(MenuTabWidth, 0),
        };
        var box = new StyleBoxFlat
        {
            BgColor = DesignSystem.Ink900,
            BorderColor = DesignSystem.Edge,
            ContentMarginLeft = 3,
            ContentMarginRight = 3,
            ContentMarginTop = 3,
            ContentMarginBottom = 3,
        };
        box.SetBorderWidthAll(DesignSystem.Outline);
        bar.AddThemeStyleboxOverride("panel", box);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bar.AddChild(row);

        string[] names = { "ITEMS", "CRAFT", "BUILD", "TRAVEL" };
        for (int i = 0; i < names.Length; i++)
        {
            var tab = new Button { Text = names[i], SizeFlagsHorizontal = SizeFlags.ExpandFill };
            if (DesignSystem.Display is { } font) tab.AddThemeFontOverride("font", font);
            tab.AddThemeFontSizeOverride("font_size", 10);
            int index = i;
            tab.Pressed += () => SelectTab(index);
            _tabButtons.Add(tab);
            row.AddChild(tab);
        }
        return bar;
    }

    private void AddTabPanel(Control content, Control panel)
    {
        panel.SetAnchorsPreset(LayoutPreset.FullRect);
        panel.Visible = _tabPanels.Count == 0;
        content.AddChild(panel);
        _tabPanels.Add(panel);
    }

    /// <summary>Lights the chosen segment ember, dims the rest, and reveals only that
    /// tab's panel — the menu never scrolls two lists at once.</summary>
    private void SelectTab(int index)
    {
        for (int i = 0; i < _tabButtons.Count; i++)
        {
            bool active = i == index;
            var tab = _tabButtons[i];
            var fill = active ? DesignSystem.Ember : new Color(0, 0, 0, 0);
            tab.AddThemeStyleboxOverride("normal", TabSegment(fill));
            tab.AddThemeStyleboxOverride("hover", TabSegment(fill));
            tab.AddThemeStyleboxOverride("pressed", TabSegment(DesignSystem.Ember));
            tab.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            var text = active ? DesignSystem.OnEmber : DesignSystem.Muted;
            tab.AddThemeColorOverride("font_color", text);
            tab.AddThemeColorOverride("font_hover_color", text);
            tab.AddThemeColorOverride("font_pressed_color", text);
            _tabPanels[i].Visible = active;
        }
        _activeTab = index;
    }

    private static StyleBoxFlat TabSegment(Color fill) => new()
    {
        BgColor = fill,
        ContentMarginLeft = 4,
        ContentMarginRight = 4,
        ContentMarginTop = 10,
        ContentMarginBottom = 10,
    };

    private Control BuildInventoryTab()
    {
        var column = new VBoxContainer { Name = "ITEMS", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 12);

        // "CARRIED ————— n / cap" section rule.
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        header.AddChild(DesignSystem.Kicker("CARRIED", DesignSystem.LabelMuted, 9));
        var rule = new ColorRect { Color = DesignSystem.Ink600, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rule.CustomMinimumSize = new Vector2(0, 2);
        var ruleWrap = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ruleWrap.AddChild(rule);
        header.AddChild(ruleWrap);
        _carriedCount = DesignSystem.Kicker("0 / " + CarrySlots, DesignSystem.Faint, 9);
        header.AddChild(_carriedCount);
        column.AddChild(header);

        var gridScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _invGrid = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _invGrid.AddThemeConstantOverride("h_separation", DesignSystem.SpaceSm);
        _invGrid.AddThemeConstantOverride("v_separation", DesignSystem.SpaceSm);
        gridScroll.AddChild(_invGrid);
        column.AddChild(gridScroll);

        column.AddChild(BuildDetailRow());
        return column;
    }

    /// <summary>The selected-item strip: icon, name, one line of what it does, and
    /// the contextual action (EAT for food). Hidden until a slot is tapped.</summary>
    private PanelContainer BuildDetailRow()
    {
        _detailRow = new PanelContainer { Visible = false };
        _detailRow.AddThemeStyleboxOverride("panel", DesignSystem.Card(pad: 10));

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);

        var tile = new PanelContainer { CustomMinimumSize = new Vector2(36, 36) };
        tile.AddThemeStyleboxOverride("panel", DesignSystem.Slot());
        var centre = new CenterContainer();
        _detailIcon = PixelIcons.Make("wood", 26);
        centre.AddChild(_detailIcon);
        tile.AddChild(centre);
        row.AddChild(tile);

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _detailName = new Label();
        _detailName.AddThemeColorOverride("font_color", DesignSystem.Parchment);
        _detailName.AddThemeFontSizeOverride("font_size", 14);
        text.AddChild(_detailName);
        _detailDesc = new Label();
        _detailDesc.AddThemeColorOverride("font_color", DesignSystem.LabelMuted);
        _detailDesc.AddThemeFontSizeOverride("font_size", 11);
        text.AddChild(_detailDesc);
        row.AddChild(text);

        _detailAction = new Button
        {
            Text = "EAT",
            CustomMinimumSize = new Vector2(0, 40),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        if (DesignSystem.Display is { } font) _detailAction.AddThemeFontOverride("font", font);
        _detailAction.AddThemeFontSizeOverride("font_size", 9);
        DesignSystem.StyleButton(_detailAction, DesignSystem.GreenButton(), DesignSystem.GreenButtonPressed(), DesignSystem.OnEmber);
        _detailAction.Pressed += () => { if (_selected != ItemId.None) _connection.SendEat(_selected); };
        row.AddChild(_detailAction);

        _detailRow.AddChild(row);
        return _detailRow;
    }

    private ScrollContainer BuildCraftTab()
    {
        var scroll = new ScrollContainer { Name = "CRAFT" };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(list);

        foreach (var recipe in CraftingRules.Recipes)
        {
            list.AddChild(BuildCraftCard(recipe));
        }
        return scroll;
    }

    /// <summary>
    /// One recipe as the design's crafting row: the output icon, its name over an
    /// ingredient list, and an ember CRAFT button. Tapping CRAFT is a request; the
    /// server validates the pouch and applies the deltas.
    /// </summary>
    private PanelContainer BuildCraftCard(CraftingRules.Recipe recipe)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", DesignSystem.Card());

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 9);
        row.AddChild(IconTile(recipe.Output, 34));

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 2);
        var name = new Label { Text = ItemCatalog.Of(recipe.Output).Name };
        name.AddThemeColorOverride("font_color", DesignSystem.Parchment);
        name.AddThemeFontSizeOverride("font_size", 15);
        text.AddChild(name);

        var ingredients = new HBoxContainer();
        ingredients.AddThemeConstantOverride("separation", 8);
        var tracked = new List<(ItemId, int, Label)>();
        foreach (var input in recipe.Inputs)
        {
            var chip = new HBoxContainer();
            chip.AddThemeConstantOverride("separation", 3);
            chip.AddChild(PixelIcons.Make(input.Item, 14));
            var count = DesignSystem.Kicker(input.Amount.ToString(), DesignSystem.LabelMuted, 9);
            count.VerticalAlignment = VerticalAlignment.Center;
            chip.AddChild(count);
            ingredients.AddChild(chip);
            tracked.Add((input.Item, input.Amount, count));
        }
        text.AddChild(ingredients);
        row.AddChild(text);

        var craft = new Button
        {
            Text = "CRAFT",
            CustomMinimumSize = new Vector2(0, 40),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        if (DesignSystem.Display is { } font) craft.AddThemeFontOverride("font", font);
        craft.AddThemeFontSizeOverride("font_size", 9);
        DesignSystem.StyleButton(craft, DesignSystem.EmberButton(), DesignSystem.EmberButtonPressed(), DesignSystem.OnEmber);
        var output = recipe.Output;
        craft.Pressed += () => _connection.SendCraft(output);
        row.AddChild(craft);

        panel.AddChild(row);
        _craftCards.Add(new CraftCard { Recipe = recipe, Panel = panel, Button = craft, Ingredients = tracked });
        return panel;
    }

    private ScrollContainer BuildPlaceTab()
    {
        var scroll = new ScrollContainer { Name = "BUILD" };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(list);

        foreach (var item in PlacementRules.Placeables)
            list.AddChild(BuildPlaceCard(item));
        return scroll;
    }

    /// <summary>A buildable as a card: its icon, name over the count on hand, and a
    /// PLACE button that raises a placement request the world resolves in front.</summary>
    private PanelContainer BuildPlaceCard(ItemId item)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", DesignSystem.Card());

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 9);
        row.AddChild(IconTile(item, 34));

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 2);
        var name = new Label { Text = ItemCatalog.Of(item).Name };
        name.AddThemeColorOverride("font_color", DesignSystem.Parchment);
        name.AddThemeFontSizeOverride("font_size", 15);
        text.AddChild(name);
        var owned = DesignSystem.Kicker("HAVE 0", DesignSystem.LabelMuted, 9);
        text.AddChild(owned);
        row.AddChild(text);

        var place = new Button
        {
            Text = "PLACE",
            CustomMinimumSize = new Vector2(0, 40),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        if (DesignSystem.Display is { } font) place.AddThemeFontOverride("font", font);
        place.AddThemeFontSizeOverride("font_size", 9);
        DesignSystem.StyleButton(place, DesignSystem.EmberButton(), DesignSystem.EmberButtonPressed(), DesignSystem.OnEmber);
        var kind = item;
        place.Pressed += () => PlaceRequested?.Invoke(kind);
        row.AddChild(place);

        panel.AddChild(row);
        _placeCards.Add((item, place, owned));
        return panel;
    }

    /// <summary>A slot-framed, centred icon — the design's inset item tile.</summary>
    private static PanelContainer IconTile(ItemId item, int size)
    {
        var tile = new PanelContainer { CustomMinimumSize = new Vector2(size, size) };
        tile.AddThemeStyleboxOverride("panel", DesignSystem.Slot());
        var centre = new CenterContainer();
        centre.AddChild(PixelIcons.Make(item, size - 10));
        tile.AddChild(centre);
        return tile;
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
        if (open) SelectTab(_activeTab);
        _hudLayer.Visible = !open;

        _slide?.Kill();
        _slide = CreateTween();
        if (open)
        {
            _sheet.Visible = true;
            _slide.TweenProperty(_sheet, "modulate:a", 1f, FadeSeconds);
        }
        else
        {
            _slide.TweenProperty(_sheet, "modulate:a", 0f, FadeSeconds);
            _slide.TweenCallback(Callable.From(() => _sheet.Visible = false));
        }
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
        RefreshGrid();
        RefreshHotbar();
        RefreshActionAvailability();
        RefreshDetail();
    }

    /// <summary>Rebuilds the carried grid: one tappable slot per held stack, then
    /// empty slots up to the visual carry cap.</summary>
    private void RefreshGrid()
    {
        foreach (var child in _invGrid.GetChildren()) child.QueueFree();

        int used = 0;
        foreach (var (item, count) in _inventory)
        {
            if (count <= 0) continue;
            used++;
            _invGrid.AddChild(GridSlot(item, count));
        }

        for (int i = used; i < CarrySlots; i++)
            _invGrid.AddChild(EmptySlot());

        _carriedCount.Text = $"{used} / {CarrySlots}";
    }

    private Button GridSlot(ItemId item, int count)
    {
        bool selected = item == _selected;
        var slot = new Button { CustomMinimumSize = new Vector2(InvSlot, InvSlot) };
        DesignSystem.StyleButton(slot, DesignSystem.Slot(selected), DesignSystem.Slot(selected: true), DesignSystem.Parchment);

        var icon = PixelIcons.Make(item, InvSlot - 20);
        icon.SetAnchorsPreset(LayoutPreset.FullRect);
        icon.OffsetLeft = 10; icon.OffsetTop = 10; icon.OffsetRight = -10; icon.OffsetBottom = -10;
        slot.AddChild(icon);

        var badge = DesignSystem.Kicker(count.ToString(), DesignSystem.Parchment, 10);
        badge.SetAnchorsPreset(LayoutPreset.BottomRight);
        badge.GrowHorizontal = GrowDirection.Begin;
        badge.OffsetRight = -3; badge.OffsetBottom = -1;
        slot.AddChild(badge);

        var kind = item;
        slot.Pressed += () => Select(kind);
        return slot;
    }

    private static PanelContainer EmptySlot()
    {
        var slot = new PanelContainer { CustomMinimumSize = new Vector2(InvSlot, InvSlot) };
        slot.AddThemeStyleboxOverride("panel", DesignSystem.Slot(empty: true));
        return slot;
    }

    private void Select(ItemId item)
    {
        _selected = item;
        RefreshGrid();
        RefreshDetail();
    }

    /// <summary>Fills the detail strip from the selected stack; the action is EAT
    /// for food and hidden otherwise.</summary>
    private void RefreshDetail()
    {
        if (_selected == ItemId.None || _inventory.GetValueOrDefault(_selected) <= 0)
        {
            _detailRow.Visible = false;
            return;
        }

        var def = ItemCatalog.Of(_selected);
        _detailRow.Visible = true;
        _detailIcon.Texture = PixelIcons.Texture(PixelIcons.NameOf(_selected));
        _detailName.Text = def.Name;

        if (ItemCatalog.IsFood(_selected))
        {
            _detailDesc.Text = $"Restores {def.FoodValue} hunger";
            _detailAction.Visible = true;
        }
        else
        {
            _detailDesc.Text = DescribeItem(def.Category);
            _detailAction.Visible = false;
        }
    }

    private static string DescribeItem(ItemCategory category) => category switch
    {
        ItemCategory.Resource => "Raw resource",
        ItemCategory.Material => "Crafting material",
        ItemCategory.Tool => "Speeds gathering",
        ItemCategory.Placeable => "Build in the world",
        _ => "",
    };

    private void RefreshActionAvailability()
    {
        foreach (var card in _craftCards)
        {
            bool canCraft = CraftingRules.CanCraft(_inventory, card.Recipe);
            card.Button.Disabled = !canCraft;
            card.Panel.Modulate = canCraft ? Colors.White : new Color(1, 1, 1, 0.55f);
            foreach (var (item, need, label) in card.Ingredients)
            {
                bool enough = _inventory.GetValueOrDefault(item) >= need;
                label.AddThemeColorOverride("font_color", enough ? DesignSystem.LabelMuted : DesignSystem.Health);
            }
        }

        foreach (var (item, button, owned) in _placeCards)
        {
            int have = _inventory.GetValueOrDefault(item);
            button.Disabled = have <= 0;
            owned.Text = $"HAVE {have}";
        }
    }
}
