// §27 phase 2b: the fragment program that turns two lighting models into max(vanilla, ours).
//
// WHY A SHADER AT ALL, given VectorLightOverlay's header argues against one. That argument was about
// FIDELITY — falloff(u) * ramp(v) is separable, so a 2-D texture reproduces it exactly and a shader
// buys nothing. This is not that. The composition needs a THIRD per-vertex quantity, vanilla's own
// delivered glow at that point, and there is nowhere left to put it: MoteGlow ignores vertex colour
// (CloudUnderlightOverlay's header records that finding), UV0.x is the radial coordinate and UV0.y
// is the penumbra ramp coordinate. A channel MoteGlow does not read is a channel we cannot use, so
// reading UV1 is the entire reason this file exists.
//
// WHAT IT COMPUTES, and why it is a subtraction rather than a max. The pass is additive and vanilla's
// flood is left RENDERING UNDERNEATH — Patch_VectorLightSuppress stands down in this mode — so what
// reaches the screen is already vanilla. Adding max(0, ours - vanilla) on top of that lands at
// max(vanilla, ours), whereas adding max(vanilla, ours) outright would land at vanilla + max, which
// is the summing failure epic #145 rejected as option 1 and measured at 6 L* over vanilla.
//
// BOTH TERMS ARE IN VANILLA'S GLOW UNITS, which is what makes the subtraction meaningful rather than
// a units accident. Our falloff is Lerp(1 - d/R, 1/d^2, 0.4) — the same curve, with the same 0.4
// weight, that Verse.Glow.ComputeGlowGridsJob.SetGlowFromDist uses. The only difference is that ours
// runs on straight-line distance and vanilla's on GEODESIC distance, and that difference is the whole
// of §27. So the gradient's alpha and the sampled glow are directly comparable, and _Color.a — the
// strength scalar — is applied AFTER the difference, exactly where it is applied today.
//
// WITH _VanillaWeight AT 0 THIS IS MoteGlow. That is deliberate and load-bearing: it is the control
// arm. Any difference between this shader at weight 0 and the stock MoteGlow path is a difference in
// the shader, not in the composition, and the live A/B has an arm that measures precisely that.
Shader "CelestialLighting/VectorLightMax"
{
    Properties
    {
        _MainTex ("Falloff x penumbra gradient", 2D) = "white" {}
        _Color ("Light colour (rgb) and strength (a)", Color) = (1, 1, 1, 1)
        _VanillaWeight ("How much of vanilla's glow to subtract", Float) = 1
    }

    SubShader
    {
        // Same tags, blend and depth state as the MoteGlow path this replaces. Additive with no depth
        // write, at AltitudeLayer.VisEffects, alongside §11a's aurora and §23b's cloud underlight.
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One One
        Cull Off
        Lighting Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;

                // x: distance from the light as a fraction of its radius. y: how far across a soft
                // shadow edge this vertex sits. Both already spent before this feature existed.
                float2 uv : TEXCOORD0;

                // Vanilla's delivered glow at this vertex, per channel, 0..1 — GlowGrid.VisualGlowAt
                // divided by 255. w is unused and carried only because Unity's mesh API hands UV
                // channels over as float4.
                float4 vanilla : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 vanilla : TEXCOORD1;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float _VanillaWeight;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;

                // Vanilla's glow interpolates linearly across the triangle from three corner samples.
                // That is an approximation of a field that is not linear — the 1/d^2 term is convex,
                // so linear interpolation sits ABOVE the true value between samples and we subtract
                // slightly too much in the middle of a long triangle. Erring dim is the safe error
                // here for the same reason AddPenumbraWedges gives: §27's standing risk is rooms
                // coming out too dark, and this cannot make one come out too bright.
                o.vanilla = v.vanilla.rgb;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // White throughout with the falloff curve in alpha — see BuildGradient. Reading alpha
                // and not rgb is what keeps the curve from being squared.
                fixed4 gradient = tex2D(_MainTex, i.uv);

                // Our own model's glow at this fragment, in vanilla's units, before strength.
                fixed3 ours = _Color.rgb * gradient.a;

                fixed3 vanilla = i.vanilla * _VanillaWeight;

                // The excess our straight-line geometry delivers over what vanilla's geodesic flood
                // already put here. Zero wherever vanilla is already the brighter of the two, which
                // is what leaves vanilla holding the floor instead of a black shadow.
                fixed3 excess = max(0, ours - vanilla);

                return fixed4(excess * _Color.a, 1);
            }
            ENDCG
        }
    }

    // No Fallback on purpose. A fallback would let a machine that cannot compile this quietly render
    // something else; VectorLightShader checks Shader.isSupported and stands the whole feature down
    // to the crossfade instead, which is a defined arm rather than an unknown one.
}
