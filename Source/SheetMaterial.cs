using UnityEngine;

namespace CelestialLighting;

// One aurora sheet's Material, plus a record of what was last written to it, so a value that has not
// changed is not written again.
//
// ================================================================================================
// WHY THIS IS WORTH A CLASS
//
// UnityEngine.Material's convenience properties are not field accesses. Decompiled from the shipped
// UnityEngine.CoreModule, every one of `color`, `mainTexture`, `mainTextureScale` and
// `mainTextureOffset` — getter AND setter — does this:
//
//     int id = GetFirstPropertyNameIdByAttribute(ShaderPropertyFlags.MainColor);
//     if (id >= 0) return GetColor(id);
//     return GetColor("_Color");
//
// That is a native call to scan the shader's property attributes, then a second native call to do
// the actual get or set, with a managed-to-native string marshal on the fallback path. Measured in
// place (issue #60), each access costs roughly 1.5 microseconds under Mono.
//
// §11a paid that eighteen times per frame writing sheet state and six more times reading `color.a`
// back to decide whether to draw — about 38 microseconds a frame, on a fixed per-frame budget of
// ~90. Of those eighteen writes only six carried a value that had actually changed: alpha moves
// every frame, while a display's UV scale is fixed for its whole life and the bounded field pins its
// offset to zero permanently.
//
// ================================================================================================
// WHY ELISION RATHER THAN Shader.PropertyToID
//
// The faster-looking fix is to cache the property IDs and call SetColor(int, Color) directly. It was
// not taken, because reproducing Unity's own resolution rule means calling
// GetFirstPropertyNameIdByAttribute — which is not public — and guessing "_Color" / "_MainTex"
// instead would be a guess about a shader we do not own (ShaderDatabase.MoteGlow). Guess wrong and
// the sheet silently stops tinting, in the dark, during a rare event: the exact failure that is
// hardest to notice and hardest to attribute.
//
// Skipping a redundant write cannot be wrong in that way. The material ends up holding precisely the
// values it held before — this only declines to send one that is already there.
//
// The comparisons are FIELD-BY-FIELD rather than `==`, deliberately. UnityEngine's Color and Vector2
// equality operators are approximate (they compare squared magnitude against ~1e-10), so `==` would
// treat a small-but-real alpha change as no change. Exact comparison can only ever elide a write
// that would have stored a bit-identical value.
public sealed class SheetMaterial
{
    public readonly Material Mat;

    // What was last written. Guarded by the `have*` flags rather than seeded with a sentinel, because
    // a fresh Material holds its SHADER DEFAULTS, not anything we chose — so until we have written a
    // property once, we know nothing about it and must not elide.
    private Color colour;
    private Vector2 scale;
    private Vector2 offset;

    private bool haveColour;
    private bool haveScale;
    private bool haveOffset;

    public SheetMaterial(Material mat)
    {
        Mat = mat;
    }

    // The alpha this sheet is currently drawing at, from our own record rather than from a
    // Material.color read. Only meaningful once a colour has been written; before that the sheet has
    // never been placed and must not be drawn, which is what the false default gives.
    public float Alpha => haveColour ? colour.a : 0f;

    public void SetColour(Color value)
    {
        if (haveColour && colour.r == value.r && colour.g == value.g
            && colour.b == value.b && colour.a == value.a)
            return;

        colour = value;
        haveColour = true;
        Mat.color = value;
    }

    public void SetScale(Vector2 value)
    {
        if (haveScale && scale.x == value.x && scale.y == value.y)
            return;

        scale = value;
        haveScale = true;
        Mat.mainTextureScale = value;
    }

    public void SetOffset(Vector2 value)
    {
        if (haveOffset && offset.x == value.x && offset.y == value.y)
            return;

        offset = value;
        haveOffset = true;
        Mat.mainTextureOffset = value;
    }

    // Not elided, and not cached. It is called eight times per completed refresh sweep — roughly once
    // every hundred frames — so there is nothing to win, and the two textures swap roles on every
    // upload, which is exactly the pattern where a stale cache would pin the wrong field on screen.
    public void SetTexture(Texture texture)
    {
        Mat.mainTexture = texture;
    }
}
