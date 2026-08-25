using Godot;

namespace Ashfall.Client;

/// <summary>
/// The one place world-space surfaces are shaded, so terrain, foliage, structures
/// and characters all read as the same banded pixel-art material. Toon diffuse
/// with specular off gives the flat cel look; nearest filtering keeps every texel
/// crisp under the low-resolution render.
/// </summary>
public static class WorldMaterials
{
    public static StandardMaterial3D Flat(
        Color? albedo = null, Texture2D? texture = null, Vector3? uvScale = null)
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = albedo ?? Colors.White,
            Roughness = 1.0f,
            DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Toon,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        };
        if (texture is not null) material.AlbedoTexture = texture;
        if (uvScale is { } scale) material.Uv1Scale = scale;
        return material;
    }
}
