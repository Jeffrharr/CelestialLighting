using UnityEngine;
using Verse;

namespace CelestialLighting;

// §26 (issue #140): draws the twilight sweep — ONE textured quad per frame over the map's open sky,
// through the additive pass §24 built and the mask §23b generalised (epic #103).
//
// THE TEXTURE IS REBAKED EVERY FRAME, which no other lane here does, and the reason is that §26's
// field has no slow half to cache. §23b splits its bake at a cadence boundary because its SHAPE is a
// noise walk that moves a few times an hour while its COLOUR moves every frame; §25 bakes its atlas
// once ever because a bounded sheet's silhouette never changes at all. §26's entire content is the
// boundary POSITION, and the boundary position is the thing that moves every frame — there is no
// slower quantity to split off. What makes it affordable instead is a table plus the window: the band
// is one-dimensional, so TwilightSweepField bakes it into a 256-entry lookup and the texel loop is a
// copy — 23 us per bake as measured by Tools/SweepPreview, down from 91 us evaluating the maths per
// texel — and even that is paid only between sunset and §8's fade floor, on well under 1% of a game
// year's frames and on zero frames of a map with no sky.
//
// [StaticConstructorOnStartup] is mandatory, the same as AuroraCurtainOverlay / SnowGlareOverlay /
// CloudUnderlightOverlay: `new Material(...)` and `new Texture2D(...)` must happen on Unity's main
// thread, and the attribute is what guarantees the static initialiser runs there (at startup, after
// ShaderDatabase has loaded) rather than on whichever thread first touches the type.
[StaticConstructorOnStartup]
public static class TwilightSweepOverlay
{
    // Additive, for epic #103's founding reason: SkyColorSet.sky is a MULTIPLY into
    // MatBases.LightOverlay.color whose brightest palette is already (1,1,1), so a multiplicative lane
    // has no headroom to make one part of the map brighter than another — and "one part of the map"
    // is the whole of §26. MoteGlow adds. Same shader as §11a, §24 and §23b.
    //
    // This composites rendered pixels only. SkyTarget.glow, GlowGrid, plant growth, solar output and
    // pawn vision are all untouched — §26 stays inside the mod's colour-only lane, like §11 and §13
    // and unlike §7/§17/§18d, which deliberately write .glow.
    private static readonly Material SweepMat = new Material(ShaderDatabase.MoteGlow);

    // The byte buffer and the texture are allocated once and rewritten in place. A per-frame
    // `new byte[16384]` inside the twilight window would be ~1 MB/s of garbage during exactly the
    // minutes the player is most likely to be watching the screen.
    private static readonly byte[] Pixels =
        new byte[TwilightSweepField.Resolution * TwilightSweepField.Resolution * 4];

    private static readonly Texture2D SweepTex = BuildTexture();

    // WHITE, and the material's colour carries no hue of its own. The gradient lives in the texture
    // because a material colour is one value for the whole quad and could not express a gradient at
    // all — the same conclusion CloudField.WriteUnderlightRgba reached, and it also keeps us from
    // asking MoteGlow to multiply a coloured material through a texture, which nothing in this
    // codebase has ever verified it does the way we would assume.
    private static readonly Color MatColour = Color.white;

    // Draws this frame's sweep over `map`, or nothing at all if there is none.
    public static void Draw(Map map)
    {
        float sweep = TwilightSweep.PositionFor(map);

        // The overwhelmingly common case: the sun is up, or below the fade floor, or this map has no
        // sky, or the feature is off. Returning before touching the texture or the draw call is what
        // makes "§26 costs nothing outside the minutes it can draw" literally true rather than
        // approximately true.
        if (sweep <= 0f)
            return;

        float amplitude = TwilightSweepMath.WindowEnvelope(sweep) * TwilightSweep.AmplitudeScale;
        if (amplitude <= 0f)
            return;

        // The mask before the bake, not after. A fully-roofed map has nothing to draw on, and
        // discovering that after walking 4,096 texels would pay the whole cost for no pixels.
        Mesh mesh = OpenSkyMask.MeshFor(map);
        if (mesh == null)
            return;

        TwilightSweep.AxisFor(map, out float axisU, out float axisV);
        SkyColorTemperature.Rgb hot = TwilightSweep.HotTintFor(map);
        SkyColorTemperature.Rgb cool = TwilightSweep.CoolTintFor(map);

        TwilightSweepField.WriteRgba(
            Pixels, TwilightSweepField.Resolution, TwilightSweepField.Resolution,
            axisU, axisV, sweep, amplitude,
            hot.R, hot.G, hot.B, cool.R, cool.G, cool.B);

        SweepTex.LoadRawTextureData(Pixels);
        SweepTex.Apply(false);

        SweepMat.mainTexture = SweepTex;
        SweepMat.color = MatColour;

        // UVs UNTRANSFORMED, which is the one line that says "this quad IS the map". Both meshes the
        // mask can hand back chart the whole map in [0,1] (CloudSheetDraw records the convention), so
        // an identity transform puts texel (0,0) at one map corner and (1,1) at the other. §23b and
        // §23c set a scale and offset here because they are placing a BOUNDED blob inside that chart;
        // §26 has nothing to place — its field already covers the map.
        SweepMat.mainTextureScale = Vector2.one;
        SweepMat.mainTextureOffset = Vector2.zero;

        // ALTITUDE: VisEffects (33), above LightingOverlay (32) and below FogOfWar — the pair §11a's
        // curtain, §24's glare and §23b's underlight all need. AltitudeLayer.Weather (31), where
        // vanilla's own weather overlays live, would put this pass BELOW the multiply it exists to
        // exceed; §24 measured what that costs (ΔE 19.79, but fading as the sky darkened, i.e.
        // backwards) rather than asserting it.
        //
        // BELOW FogOfWar is right here for the same reason it is right for glare and wrong for the
        // aurora. An aurora is sky, and a player's ignorance of the terrain does not hide the sky;
        // §26 is light landing on GROUND, so unexplored ground having none of it is correct.
        float altitude = AltitudeLayer.VisEffects.AltitudeFor();

        // The two meshes need different origins, and getting this wrong offsets the whole effect by
        // half a map with no error: vanilla's shared plane is centred on map.Center and placed by
        // SkyOverlay.DrawWorldOverlay, while our masked mesh is built in absolute cell coordinates and
        // draws at the origin. Branching on which came back keeps that difference in one visible place
        // — the same shape SnowGlareOverlay and CloudSheetDraw both use.
        if (mesh == MeshPool.wholeMapPlane)
        {
            SkyOverlay.DrawWorldOverlay(map, SweepMat, altitude);
            return;
        }

        Graphics.DrawMesh(mesh, new Vector3(0f, altitude, 0f), Quaternion.identity, SweepMat, 0);
    }

    private static Texture2D BuildTexture()
    {
        Texture2D texture = new Texture2D(
            TwilightSweepField.Resolution, TwilightSweepField.Resolution, TextureFormat.RGBA32, false)
        {
            name = "CelestialLighting_TwilightSweep",
            // CLAMP, and it is load-bearing rather than a default worth copying. §23b's field wraps
            // because it tiles and pans; §26's covers the map exactly once, so a Repeat mode would
            // let bilinear filtering at the map edge sample the opposite edge — which is the anti-solar
            // corner bleeding into the sunward one, i.e. a thin wrong-coloured seam along two sides.
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        return texture;
    }
}
