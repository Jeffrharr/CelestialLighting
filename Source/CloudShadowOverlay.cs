using UnityEngine;
using Verse;

namespace CelestialLighting;

// §23c (DESIGN.md §23c): draws daylight cloud shadows — the same field §23b adds light through,
// subtracted instead of added, over the map's open sky.
//
// ONE TEXTURE, TWO LANES, AND THE SHADER IS THE ONLY DIFFERENCE. CloudFieldTexture bakes alpha = the
// field's residual above its own mean; §23b draws it through MoteGlow with a WHITE material and adds
// the baked colour, and this draws the identical texture through ShaderDatabase.Transparent with a
// BLACK one. Alpha blending toward `tex.rgb * mat.rgb` with a black material colour is black whatever
// the texture's RGB says, so the baked sunset gradient is simply ignored here rather than needing a
// bake of its own. That is not a saving so much as a guarantee: the light a deck adds and the light it
// blocks cannot end up in different places, because they are the same pixels.
//
// The two lanes are also mutually exclusive in time — §23b needs the sun below the horizon, this needs
// it above — so on any given frame at most one of them is drawing.
//
// [StaticConstructorOnStartup] for the usual reason: `new Material(...)` must be on Unity's main
// thread. See EaveShadeOverlay's header, which records what happens without it.
[StaticConstructorOnStartup]
public static class CloudShadowOverlay
{
    // ShaderDatabase.Transparent is ordinary alpha blending, and blending BLACK at alpha `a` is
    // `scene * (1 - a)` — a multiply, which is exactly what a shadow is. Same shader and same
    // reasoning as §15b's eave shade; deliberately NOT MoteGlow, which can only add.
    //
    // MatBases.SunShadow is emphatically not reusable here either, for the reason EaveShadeOverlay
    // records: its shader displaces every vertex by the shadow vector scaled by that vertex's alpha.
    private static readonly Material ShadowMat = new Material(ShaderDatabase.Transparent);

    private static float _lastAlpha = float.NaN;

    // Draws this frame's cloud shadows over `map`, or nothing at all if there are none.
    public static void Draw(Map map)
    {
        float alpha = CloudLayers.ShadowAlphaFor(map);

        // The common case on most frames of most saves: no cloud, no sun, or the feature off.
        if (alpha <= 0f)
            return;

        Mesh mesh = OpenSkyMask.MeshFor(map);
        if (mesh == null)
            return;

        ShadowMat.mainTexture = CloudFieldTexture.For(map);
        CloudFieldTexture.ApplyTiling(ShadowMat, map);

        if (alpha != _lastAlpha)
        {
            // Black, so the blend is a pure multiply. The alpha is the whole signal.
            ShadowMat.color = new Color(0f, 0f, 0f, alpha);
            _lastAlpha = alpha;
        }

        // ALTITUDE: VisEffects, the same as §23b and §24, and for a reason that is worth stating
        // because a shadow's natural home looks like it should be lower. Vanilla's own sun shadows
        // draw far below the lighting overlay, as part of the map mesh — but they are cast BY things
        // ON the map, so they belong under the light. A cloud shadow is cast by something ABOVE
        // everything on the map, so it has to darken pawns, buildings and roofs alike, which means
        // drawing after them. Below FogOfWar for the same reason §23b is: this is a change to the
        // light landing on ground, and unexplored ground has none of it either way.
        if (mesh == MeshPool.wholeMapPlane)
        {
            SkyOverlay.DrawWorldOverlay(map, ShadowMat, AltitudeLayer.VisEffects.AltitudeFor());
            return;
        }

        Vector3 position = new Vector3(0f, AltitudeLayer.VisEffects.AltitudeFor(), 0f);
        Graphics.DrawMesh(mesh, position, Quaternion.identity, ShadowMat, 0);
    }
}
