using System.Collections.Generic;
using System.Text;
using Ashfall.Proto;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Touch-first survival overlay. It DISPLAYS server state (meters, inventory)
/// and RAISES intent (move, jump, craft, place) — it never invents authoritative
/// state, and every action is a request the server may still refuse.
///
/// Layout is thumb-first: status reads across the top, a movement stick sits
/// under the left thumb and the action controls under the right. Crafting and
/// building live behind one toggle so they are not always covering the world —
/// tap "Actions", pick from a sheet, tap out.
/// </summary>
public partial class SurvivalHud : Control
{
    /// <summary>Meters are 0..100 display points, matching <see cref="SurvivalRules.MaxPoints"/>.</summary>
    private const int MeterMax = SurvivalRules.MaxPoints;

    /// <summary>Thumb-sized so an action is a comfortable one-handed tap.</summary>
    private const int ActionButtonHeight = 60;

    private const int RoundButtonSize = 72;
    private const int PanelMargin = 16;

    private WorldConnection _connection = null!;

    private ProgressBar _hunger = null!;
    private ProgressBar _stamina = null!;
    private ProgressBar _health = null!;
    private Label _inventoryReadout = null!;

    private VirtualJoystick _joystick = null!;
    private PanelContainer _sheet = null!;
    private Button _toggle = null!;

    private readonly List<(CraftingRules.Recipe Recipe, Button Button)> _craftButtons = new();
    private readonly List<(ItemId Item, Button Button)> _placeButtons = new();
    private IReadOnlyDictionary<ItemId, int> _inventory = new Dictionary<ItemId, int>();

    private VBoxContainer _travelList = null!;
    private IReadOnlyList<WorldConnection.WorldInfo> _worlds = new List<WorldConnection.WorldInfo>();

    /// <summary>The movement stick, so the owner can feed it to the player body.</summary>
    public VirtualJoystick MoveStick => _joystick;

    /// <summary>The jump button was tapped; the owner tells the player to jump.</summary>
    public event System.Action? JumpPressed;

    /// <summary>
    /// The player asked to place <see cref="ItemId"/>. The HUD has no world
    /// knowledge, so the owner resolves the target tile from player position and
    /// facing and sends the request.
    /// </summary>
    public event System.Action<ItemId>? PlaceRequested;

    public override void _Ready()
    {
        // Fill the screen but let touches fall through to the world where the HUD
        // draws nothing; only the controls themselves capture taps.
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        BuildMeters();
        BuildInventory();
        BuildActionSheet();
        BuildTouchControls();
        RefreshActionAvailability();
    }

    /// <summary>Subscribe once the owner has the live connection.</summary>
    public void Bind(WorldConnection connection)
    {
        _connection = connection;
        _connection.StatsUpdated += (hunger, stamina, health, _) =>
            CallDeferred(nameof(ApplyStats), hunger, stamina, health);
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

    private void BuildMeters()
    {
        var meters = new VBoxContainer();
        meters.SetAnchorsPreset(LayoutPreset.TopLeft);
        meters.OffsetLeft = PanelMargin;
        meters.OffsetTop = PanelMargin;
        meters.AddThemeConstantOverride("separation", 6);
        AddChild(meters);

        _hunger = AddMeter(meters, "Hunger", new Color("c9822f"));
        _stamina = AddMeter(meters, "Stamina", new Color("3f9d54"));
        _health = AddMeter(meters, "Health", new Color("b83b3b"));
    }

    private static ProgressBar AddMeter(Container parent, string name, Color fill)
    {
        var label = new Label { Text = name };
        parent.AddChild(label);

        var bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = MeterMax,
            Value = MeterMax,
            ShowPercentage = true,
            CustomMinimumSize = new Vector2(180, 22),
        };

        var fillStyle = new StyleBoxFlat { BgColor = fill };
        bar.AddThemeStyleboxOverride("fill", fillStyle);
        parent.AddChild(bar);
        return bar;
    }

    private void BuildInventory()
    {
        _inventoryReadout = new Label
        {
            Text = "Inventory\n(empty)",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _inventoryReadout.SetAnchorsPreset(LayoutPreset.TopRight);
        _inventoryReadout.OffsetRight = -PanelMargin;
        _inventoryReadout.OffsetLeft = -220;
        _inventoryReadout.OffsetTop = PanelMargin;
        AddChild(_inventoryReadout);
    }

    /// <summary>
    /// The movement stick (left thumb) and the action buttons (right thumb): a
    /// round "Actions" toggle that opens the crafting/building sheet, and a Jump
    /// button. Both thumbs reach their controls without shifting grip.
    /// </summary>
    private void BuildTouchControls()
    {
        _joystick = new VirtualJoystick();
        _joystick.SetAnchorsPreset(LayoutPreset.BottomLeft);
        float diameter = _joystick.Radius * 2;
        _joystick.OffsetLeft = PanelMargin;
        _joystick.OffsetRight = PanelMargin + diameter;
        _joystick.OffsetTop = -(diameter + PanelMargin);
        _joystick.OffsetBottom = -PanelMargin;
        AddChild(_joystick);

        var buttons = new VBoxContainer();
        buttons.SetAnchorsPreset(LayoutPreset.BottomRight);
        buttons.GrowHorizontal = GrowDirection.Begin;
        buttons.GrowVertical = GrowDirection.Begin;
        buttons.OffsetRight = -PanelMargin;
        buttons.OffsetBottom = -PanelMargin;
        buttons.AddThemeConstantOverride("separation", 10);
        buttons.Alignment = BoxContainer.AlignmentMode.End;
        AddChild(buttons);

        var jump = new Button
        {
            Text = "Jump",
            CustomMinimumSize = new Vector2(RoundButtonSize, RoundButtonSize),
        };
        jump.Pressed += () => JumpPressed?.Invoke();
        buttons.AddChild(jump);

        _toggle = new Button
        {
            Text = "Actions",
            ToggleMode = true,
            CustomMinimumSize = new Vector2(RoundButtonSize + 24, RoundButtonSize),
        };
        _toggle.Toggled += OnToggleActions;
        buttons.AddChild(_toggle);
    }

    /// <summary>
    /// The crafting and building menu, hidden until "Actions" is tapped. Two
    /// tabs keep gathering-into-tools and placing-structures distinct, and the
    /// buttons are generated from the shared tables so nothing is hand-duplicated.
    /// </summary>
    private void BuildActionSheet()
    {
        _sheet = new PanelContainer { Visible = false };
        _sheet.SetAnchorsPreset(LayoutPreset.BottomWide);
        _sheet.GrowVertical = GrowDirection.Begin;
        _sheet.OffsetLeft = PanelMargin;
        _sheet.OffsetRight = -PanelMargin;
        // Sit clear of the thumb controls along the very bottom.
        _sheet.OffsetBottom = -(RoundButtonSize + PanelMargin * 2);

        var background = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.10f, 0.12f, 0.94f),
            CornerRadiusTopLeft = 14,
            CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14,
            CornerRadiusBottomRight = 14,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 12,
        };
        _sheet.AddThemeStyleboxOverride("panel", background);
        AddChild(_sheet);

        var tabs = new TabContainer();
        _sheet.AddChild(tabs);

        tabs.AddChild(BuildCraftTab());
        tabs.AddChild(BuildPlaceTab());
        tabs.AddChild(BuildTravelTab());
    }

    /// <summary>
    /// Destinations from the gateway registry. Boarding sends a release request;
    /// the server persists the character and the client sails to the new world.
    /// </summary>
    private ScrollContainer BuildTravelTab()
    {
        var scroll = new ScrollContainer { Name = "Travel" };
        _travelList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _travelList.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_travelList);
        return scroll;
    }

    private void RefreshTravel()
    {
        foreach (var child in _travelList.GetChildren()) child.QueueFree();

        if (_worlds.Count == 0)
        {
            _travelList.AddChild(new Label { Text = "No destinations available." });
            return;
        }

        foreach (var world in _worlds)
        {
            var button = new Button
            {
                Text = $"Sail to {world.Id}",
                CustomMinimumSize = new Vector2(0, ActionButtonHeight),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            string target = world.Id;
            button.Pressed += () => _connection.SendRequestRelease(target);
            _travelList.AddChild(button);
        }
    }

    private ScrollContainer BuildCraftTab()
    {
        var scroll = new ScrollContainer { Name = "Craft" };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);

        foreach (var recipe in CraftingRules.Recipes)
        {
            var button = new Button
            {
                Text = DescribeRecipe(recipe),
                CustomMinimumSize = new Vector2(0, ActionButtonHeight),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            var output = recipe.Output;
            button.Pressed += () => _connection.SendCraft(output);
            list.AddChild(button);
            _craftButtons.Add((recipe, button));
        }
        return scroll;
    }

    private ScrollContainer BuildPlaceTab()
    {
        var scroll = new ScrollContainer { Name = "Build" };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);

        foreach (var item in PlacementRules.Placeables)
        {
            var button = new Button
            {
                Text = $"Place {item} (in front of you)",
                CustomMinimumSize = new Vector2(0, ActionButtonHeight),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            var kind = item;
            button.Pressed += () => PlaceRequested?.Invoke(kind);
            list.AddChild(button);
            _placeButtons.Add((item, button));
        }
        return scroll;
    }

    /// <summary>Open sheet ⇄ walk: hide the stick while menuing so it can't steal taps.</summary>
    private void OnToggleActions(bool open)
    {
        _sheet.Visible = open;
        _joystick.Visible = !open;
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

    private void ApplyStats(int hunger, int stamina, int health)
    {
        _hunger.Value = hunger;
        _stamina.Value = stamina;
        _health.Value = health;
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

        RefreshActionAvailability();
    }

    private void RefreshActionAvailability()
    {
        foreach (var (recipe, button) in _craftButtons)
            button.Disabled = !CraftingRules.CanCraft(_inventory, recipe);

        foreach (var (item, button) in _placeButtons)
            button.Disabled = _inventory.GetValueOrDefault(item) <= 0;
    }
}
