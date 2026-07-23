using System.Collections.Generic;
using Ashfall.Proto;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// The Ashfall item &amp; resource icons, ported verbatim from the design system's
/// PixelIcon component. Each glyph is a tiny colour grid; we bake it once into a
/// nearest-filtered <see cref="ImageTexture"/> so the HUD can draw crisp pixel
/// art at any scale without shipping a single hand-authored image file.
/// </summary>
public static class PixelIcons
{
    private sealed record Glyph(Dictionary<char, Color> Palette, string[] Rows);

    private static readonly Dictionary<string, Glyph> Defs = new()
    {
        ["wood"] = new(new() { ['o'] = new("3a2415"), ['W'] = new("8a5a34"), ['l'] = new("a86f42"), ['e'] = new("c9975c") }, new[]
        {
            "            ", "  oooooo    ", " oeWWWWeo   ", " oWllWWlWo  ", " oeWWWWeo   ", "  oooooo    ",
            "   oooooo   ", "  oeWWWWeo  ", "  oWllWWlWo ", "  oeWWWWeo  ", "   oooooo   ", "            ",
        }),
        ["stone"] = new(new() { ['o'] = new("2e2c29"), ['S'] = new("6e6a63"), ['l'] = new("9a958b"), ['d'] = new("47443f") }, new[]
        {
            "            ", "            ", "    oooo    ", "   olllSo   ", "  olSSSSdo  ", " olSSllSSdo ",
            " oSSSSSSSdo ", " oSllSSSSdo ", " odSSSSSddo ", "  oddddddo  ", "   oooooo   ", "            ",
        }),
        ["fiber"] = new(new() { ['G'] = new("4a7c40"), ['l'] = new("6fa85a"), ['d'] = new("2f5228") }, new[]
        {
            "            ", "     l      ", "  l  l   l  ", "  l ll  ll  ", " dl ll  ll  ", " ll llddll  ",
            " ll GlllG l ", "  GGGGGGG   ", "  dGGGGGd   ", "   dGGGd    ", "            ", "            ",
        }),
        ["plank"] = new(new() { ['o'] = new("5e3b22"), ['P'] = new("b07d4a"), ['l'] = new("c9975c"), ['d'] = new("8a5a34") }, new[]
        {
            "            ", "            ", " oooooooooo ", " oPlPPPPlPo ", " odPPPPPPdo ", " oPlPPPPlPo ",
            " oPPPPllPPo ", " odPPPPPPdo ", " oPlPPPPlPo ", " oooooooooo ", "            ", "            ",
        }),
        ["rope"] = new(new() { ['o'] = new("8a6a2e"), ['R'] = new("c9a15a"), ['l'] = new("e0c07a") }, new[]
        {
            "            ", "   oooo     ", "  oRRlRo    ", " oRlooRRo   ", " oRo  olRo  ", " oRo   oRo  ",
            "  oRlooRRo  ", "   oRRlRo   ", "    oooRo   ", "       oRo  ", "        oo  ", "            ",
        }),
        ["axe"] = new(new() { ['o'] = new("2e2c29"), ['M'] = new("9a958b"), ['d'] = new("6e6a63"), ['l'] = new("c4bfb4"), ['H'] = new("8a5a34") }, new[]
        {
            "       ooo  ", "      oMMMo ", "     oMlMMd ", "    oMMMMdo ", "   oMMlMdo  ", "  o oMddo   ",
            "  Ho oo     ", "  oHo       ", "   oHo      ", "    oHo     ", "     oHo    ", "      oo    ",
        }),
        ["pickaxe"] = new(new() { ['o'] = new("2e2c29"), ['M'] = new("9a958b"), ['l'] = new("c4bfb4"), ['d'] = new("6e6a63"), ['H'] = new("8a5a34") }, new[]
        {
            "  o      o  ", " oMdo  odMo ", " oMMdolllMo ", "  oMMlllMo  ", "  o oHHo o  ", "    oHHo    ",
            "    oHHo    ", "    oHHo    ", "    oHHo    ", "    oHHo    ", "    oHHo    ", "     oo     ",
        }),
        ["wall"] = new(new() { ['o'] = new("3a2415"), ['W'] = new("8a5a34"), ['l'] = new("a86f42") }, new[]
        {
            " o o o o o  ", " oWoWoWoWo  ", "oWloWloWloW ", "oWloWloWloW ", "oWWWWWWWWWo ", "oWloWloWloW ",
            "oWloWloWloW ", "oWloWloWloW ", "oWloWloWloW ", "oWloWloWloW ", "oooooooooo  ", "            ",
        }),
        ["campfire"] = new(new() { ['o'] = new("3a2415"), ['W'] = new("8a5a34"), ['l'] = new("a86f42"), ['f'] = new("e0842e"), ['F'] = new("f5c542"), ['y'] = new("f5b24a") }, new[]
        {
            "     f      ", "    fFf     ", "   fFyFf    ", "   fyFyf    ", "  ffyFyff   ", "  fyFFFyf   ",
            "   ffyff    ", "  oWWWWWo   ", " oWlWWWlWo  ", "oWloooooWlo ", "  oo   oo   ", "            ",
        }),
        ["berry"] = new(new() { ['o'] = new("3a1f22"), ['R'] = new("c23b3b"), ['l'] = new("e86a6a"), ['G'] = new("4a7c40"), ['d'] = new("2f5228") }, new[]
        {
            "      Go    ", "     GGdo   ", "    oGo     ", "   oRRo oo  ", "  oRlRooRRo ", "  oRRRoRlRo ",
            "  oRRRoRRRo ", "   oRRoRRo  ", "    ooRRo   ", "     oRo    ", "      o     ", "            ",
        }),
        ["hunger"] = new(new() { ['o'] = new("5a3410"), ['M'] = new("c9822f"), ['l'] = new("e0a24a"), ['B'] = new("e8ddc4") }, new[]
        {
            "            ", "    ooo     ", "   oMloo    ", "  oMMMMo    ", "  oMlMMo    ", "  oMMMMo    ",
            "   oMMo     ", "    oBo     ", "   BooB     ", "  Bo  oB    ", "            ", "            ",
        }),
        ["stamina"] = new(new() { ['o'] = new("1f5a2e"), ['B'] = new("3f9d54"), ['l'] = new("68c47e") }, new[]
        {
            "     ooo    ", "    olBo    ", "   olBo     ", "  olBBo     ", "  oBBBBoo   ", "   oolBBo   ",
            "     olBo   ", "     olBo   ", "    olBo    ", "    oBo     ", "    oo      ", "            ",
        }),
        ["health"] = new(new() { ['o'] = new("5a1f1f"), ['H'] = new("b83b3b"), ['l'] = new("d95a5a") }, new[]
        {
            "            ", "  oo  oo    ", " oHloolHo   ", "oHlHHHHHHo  ", "oHHHHHHHHo  ", "oHHHHHHHHo  ",
            " oHHHHHHo   ", "  oHHHHo    ", "   oHHo     ", "    oo      ", "            ", "            ",
        }),
    };

    private static readonly Dictionary<string, ImageTexture> Cache = new();

    /// <summary>Maps a gameplay item to its icon; falls back to a wood plank glyph.</summary>
    public static string NameOf(ItemId item) => item switch
    {
        ItemId.Wood => "wood",
        ItemId.Stone => "stone",
        ItemId.Fiber => "fiber",
        ItemId.Plank => "plank",
        ItemId.Rope => "rope",
        ItemId.Axe => "axe",
        ItemId.Pickaxe => "pickaxe",
        ItemId.Wall => "wall",
        ItemId.Campfire => "campfire",
        ItemId.Berry => "berry",
        _ => "wood",
    };

    /// <summary>The baked texture for a glyph name; one image per name, cached.</summary>
    public static ImageTexture Texture(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;

        var glyph = Defs.TryGetValue(name, out var found) ? found : Defs["wood"];
        int rows = glyph.Rows.Length;
        int cols = 0;
        foreach (var row in glyph.Rows) cols = Mathf.Max(cols, row.Length);

        var image = Image.CreateEmpty(cols, rows, false, Image.Format.Rgba8);
        image.Fill(new Color(0, 0, 0, 0));
        for (int y = 0; y < rows; y++)
        {
            string row = glyph.Rows[y];
            for (int x = 0; x < row.Length; x++)
            {
                char ch = row[x];
                if (ch == ' ') continue;
                if (glyph.Palette.TryGetValue(ch, out var colour)) image.SetPixel(x, y, colour);
            }
        }

        var texture = ImageTexture.CreateFromImage(image);
        Cache[name] = texture;
        return texture;
    }

    /// <summary>A ready-to-place, nearest-filtered icon of the given pixel size.</summary>
    public static TextureRect Make(string name, int size)
    {
        return new TextureRect
        {
            Texture = Texture(name),
            CustomMinimumSize = new Vector2(size, size),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
    }

    public static TextureRect Make(ItemId item, int size) => Make(NameOf(item), size);
}
