using System;
using System.Collections.Generic;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Procedural pixel-art surface textures for the 3D world — the ground you walk
/// on and the bark/leaves the trees are built from. They are generated once from
/// the deterministic <see cref="Noise.Hash"/> (never platform RNG), baked to
/// nearest-filtered, tiling <see cref="ImageTexture"/>s, and multiplied over each
/// surface's colour. Because they live in the mesh's UV space, the texels stay
/// welded to the geometry as the camera pans — real pixels on real surfaces, not
/// a screen-space filter.
/// </summary>
public static class PixelTextures
{
    private static readonly Dictionary<string, ImageTexture> Cache = new();

    /// <summary>Ground grain: a soft speckle so grass, sand and rock read as pixels underfoot.</summary>
    public static ImageTexture Ground() => Get("ground", 32, (h, x, y) =>
    {
        // Mostly bright with occasional darker texels and a faint pixel checker.
        float speckle = (h & 0x7) == 0 ? 0.74f : (h & 0x3) == 0 ? 0.86f : 0.96f;
        float checker = ((x + y) & 1) == 0 ? 1.0f : 0.97f;
        return new Color(speckle * checker, speckle * checker, speckle * checker);
    });

    /// <summary>Bark: vertical streaks with knots, multiplied by the trunk colour.</summary>
    public static ImageTexture Bark() => Get("bark", 16, (h, x, y) =>
    {
        // Value driven mostly by the column so streaks run up the trunk.
        float column = 0.7f + (x * 2654435761u % 40) / 100f; // 0.70 .. 1.09-ish
        float knot = (h & 0xF) == 0 ? 0.6f : 1.0f;
        float v = Mathf.Clamp(column * knot, 0.5f, 1.0f);
        return new Color(v, v, v);
    });

    /// <summary>Leaf dapple: clustered lighter/darker texels so canopies look leafy, not smooth.</summary>
    public static ImageTexture Leaf() => Get("leaf", 24, (h, x, y) =>
    {
        float v = (h & 0x7) switch
        {
            0 => 0.72f,
            1 => 0.82f,
            2 or 3 => 1.06f, // a few brighter highlights peeking through
            _ => 0.94f,
        };
        return new Color(v, v, v);
    });

    /// <summary>Berry dapple: darker, denser clusters for forage bushes.</summary>
    public static ImageTexture Berry() => Get("berry", 16, (h, x, y) =>
    {
        float v = (h & 0x3) == 0 ? 0.72f : (h & 0x7) == 0 ? 1.08f : 0.9f;
        return new Color(v, v, v);
    });

    private static ImageTexture Get(string name, int size, Func<uint, int, int, Color> shade)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;

        uint salt = 0u;
        foreach (char c in name) salt = salt * 31u + c;

        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgb8);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                image.SetPixel(x, y, shade(Ashfall.SimCore.Noise.Hash(x, y, salt), x, y));

        var texture = ImageTexture.CreateFromImage(image);
        Cache[name] = texture;
        return texture;
    }
}
