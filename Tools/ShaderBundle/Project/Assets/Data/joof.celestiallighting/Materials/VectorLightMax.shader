// §27 phase 6: the fragment program that composes max(vanilla, ours) at the visibility polygon's
// own resolution.
//
// WHY A SHADER AT ALL, and why the reason issue #151 gave is not it. #151 justified this file as the
// only way to get vanilla's delivered glow into a fragment program, since MoteGlow ignores vertex
// colour and both of UV0's channels are already spent. That is not a reason: §27 phase 5 computes
// exactly the same max in C#, inside the mask, where vanilla's per-emitter glow is already in hand.
// What phase 5 cannot do is DELIVER it. Its output goes to the lighting overlay's mesh, one vertex
// per cell corner plus one per centre, so a beam whose neck is one cell wide comes out a soft
// ellipse. A shadow BOUNDARY survives cell resolution — a long straight edge blurred by half a cell
// still reads straight — and a one-cell APERTURE does not. The fan is the only surface in §27 finer
// than a cell, so the composition has to happen here.
//
// WHAT CHANGED FROM #151, AND WHY ITS VERSION MEASURED AS A NO-OP. #151 sampled vanilla's glow once
// per mesh VERTEX and carried it in UV1, then let the hardware interpolate it across the triangle.
// The fan's triangles are long radial slivers running from the lamp to the polygon rim, and vanilla
// is near its maximum at the apex and near zero at the rim — so linear interpolation tells every
// fragment along a doorway beam that vanilla is far brighter than it is, and max(0, ours - vanilla)
// collapses to zero in exactly the region the beam occupies. #151's own comment names the effect and
// calls it "slightly too much in the middle of a long triangle"; through an aperture it is not
// slight, it is total. Measured: with the subtraction off the fan draws a crisp wedge through the
// gap, and with it on the frame is indistinguishable from vanilla.
//
// So vanilla arrives as a TEXTURE instead, one texel per cell over the emitter's own square, sampled
// per fragment through UV1 as a coordinate rather than as a value. That is not an approximation of
// vanilla's field — it IS vanilla's field, at exactly the resolution vanilla stores it, bilinearly
// filtered the same way vanilla's own lighting overlay filters it. The composition is per fragment,
// its geometry is the polygon, and neither input is sampled more coarsely than it exists.
//
// WHAT THE OUTPUT MEANS DEPENDS ON THE BLEND, AND THAT IS THE SURFACE LIFT IN ONE SENTENCE. An
// additive pass adds a fixed amount of light to a pixel regardless of what that pixel is, so it
// cannot reveal the surface underneath: the ground beyond a doorway is drawn dark by the lighting
// overlay's multiply, and adding a smooth wedge on top of it produces a smooth wedge, not a lit
// floor. Reported from play as "the beam does not light the other room up — no features are lit,
// just the additional glow", and it is a property of the compositing rather than of the level.
//
// Light on a SURFACE is albedo * illuminance, so the beam has to scale what is already there
// instead of adding beside it. Under Blend DstColor One the frame becomes dst * (1 + output), and
// dst is already albedo * ambient — so the beam's contribution is albedo * ambient * output, which
// carries the ground's own texture with it. §11a's aurora and §23b's underlight are EMITTING MEDIA
// in the sky and are correctly additive; §27 inherited their compositing idiom and it is the wrong
// one for light landing on a floor.
//
// THE CEILING IS TWO TIMES, and it is the blend's rather than a choice. A UNORM render target
// clamps the fragment output to [0, 1] before blending, so dst * (1 + output) can never exceed
// 2 * dst. That bounds the brightest thing this pass can do to one stop over the ambient it lands
// on, which is why the surface lift needs no separate guard against washing a room out.
//
// BOTH TERMS ARE IN VANILLA'S GLOW UNITS, which is what makes the subtraction meaningful rather than
// a units accident. Our falloff is Lerp(1 - d/R, 1/d^2, 0.4) — the same curve, with the same 0.4
// weight, that Verse.Glow.ComputeGlowGridsJob.SetGlowFromDist uses. The only difference is that ours
// runs on straight-line distance and vanilla's on GEODESIC distance, and that difference is the
// whole of §27. _Color.a — the strength scalar — is applied AFTER the difference, exactly where it
// is applied on the stock path.
//
// WITH _VanillaWeight AT 0 THIS IS MoteGlow. That is deliberate and load-bearing: it is the control
// arm. Any difference between this shader at weight 0 and the stock MoteGlow path is a difference in
// the shader, not in the composition, and the live scenario has an arm that measures precisely that.
// #151 records what that arm is worth — its first run measured a masked dE of 5.58 caused entirely
// by the bundle declaring "Queue"="Transparent" (3000) against MoteGlow's 3151, which put the
// additive pass under the lighting overlay's multiply. VectorLightShader.NewMaterial copies
// MoteGlow's queue at runtime rather than trusting the tag below.
Shader "CelestialLighting/VectorLightMax"
{
    Properties
    {
        _MainTex ("Falloff x penumbra gradient", 2D) = "white" {}
        _VanillaTex ("Vanilla's delivered glow over this emitter's square", 2D) = "black" {}
        _Color ("Light colour (rgb) and strength (a)", Color) = (1, 1, 1, 1)
        _VanillaWeight ("How much of vanilla's glow to subtract", Float) = 1
        _SkyAmbient ("Sky-only ambient the beam lands on; 0 selects the additive pass", Float) = 0

        // WHICH BLEND THE SAME FRAGMENT OUTPUT IS FED INTO, and the only thing the surface lift
        // changes. At One/One the pass ADDS its output to the frame; at DstColor/One it MULTIPLIES
        // the frame by (1 + output). The fragment program below is identical under both — the same
        // number is "how much light to add" in one and "how much brighter to make what is here" in
        // the other — which is what makes the off arm reproduce the additive pass exactly rather
        // than approximately. Set on the MATERIAL by VectorLightShader.NewMaterial; blend state
        // cannot come from a MaterialPropertyBlock, which is why the material cache is keyed on it.
        [HideInInspector] _SrcBlend ("", Float) = 1
        [HideInInspector] _DstBlend ("", Float) = 1
    }

    SubShader
    {
        // Same tags and depth state as the MoteGlow path this replaces: no depth write, at
        // AltitudeLayer.VisEffects, alongside §11a's aurora and §23b's cloud underlight. The blend
        // is the one thing that is no longer fixed — see _SrcBlend above.
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend [_SrcBlend] [_DstBlend]
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

                // Where this vertex sits in the emitter's own square, in [0, 1] on both axes — a
                // COORDINATE, not a value, which is the entire correction over #151. zw unused and
                // carried only because Unity's mesh API hands UV channels over as float4.
                float4 vanillaUv : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 vanillaUv : TEXCOORD1;
            };

            sampler2D _MainTex;
            sampler2D _VanillaTex;
            fixed4 _Color;
            float _VanillaWeight;

            // The ambient this beam is landing on, in vanilla's glow units, from the sky alone.
            // ZERO ON THE ADDITIVE PATH, which is what selects between the two compositions in the
            // fragment program: a divisor of nothing is meaningless, so zero means "do not divide"
            // and the pass is the additive one it has always been.
            float _SkyAmbient;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;

                // A texture coordinate interpolates exactly, because position across a triangle IS
                // linear. That is the whole difference: #151 interpolated the VALUE of a field that
                // is not linear, and this interpolates the place to look it up.
                o.vanillaUv = v.vanillaUv.xy;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // White throughout with the falloff curve in alpha — see BuildGradient. Reading alpha
                // and not rgb is what keeps the curve from being squared.
                fixed4 gradient = tex2D(_MainTex, i.uv);

                // Our own model's glow at this fragment, in vanilla's units, before strength.
                fixed3 ours = _Color.rgb * gradient.a;

                // Vanilla's, at this fragment, from its own grid. Bilinear, which is the same
                // filtering vanilla's lighting overlay applies to the same numbers.
                fixed3 vanilla = tex2D(_VanillaTex, i.vanillaUv).rgb * _VanillaWeight;

                // The excess our straight-line geometry delivers over what vanilla's geodesic flood
                // already put here. Zero wherever vanilla is already the brighter of the two, which
                // is what leaves vanilla holding the floor instead of a black shadow — and is why
                // this composition needs no strength knob to stop it over-brightening a lit room.
                fixed3 excess = max(0, ours - vanilla);

                // THE SURFACE LIFT'S DIVISOR IS WHAT KEEPS IT SELF-LIMITING, and leaving vanilla out
                // of it is a real defect rather than a simplification. A cell already rendering at
                // (ambient + vanilla) should end up at (ambient + ours), so the factor to multiply
                // the frame by is
                //
                //     (ambient + ours) / (ambient + vanilla)  =  1 + excess / (ambient + vanilla)
                //
                // Dividing by the sky ambient ALONE was the first cut, and it over-lifted exactly
                // where phase 6 is supposed to contribute nothing: the dim corner of a room vanilla
                // had already lit, where the two models' residual disagreement is small but the
                // divisor was small too. Measured at +2.82 L* against the additive pass's +1.23,
                // which is the same failure the flat beam was rejected for. Beyond an open door
                // vanilla is exactly zero, so the divisor is the sky ambient there and the beam is
                // undiminished — the term costs nothing where the feature actually lives.
                //
                // Per channel, because vanilla's glow is. A warm lamp beside a warm wall competes
                // with it in red and not in blue, and averaging the three first would lose that.
                if (_SkyAmbient > 0)
                    return fixed4(_Color.a * excess / (_SkyAmbient + vanilla), 1);

                return fixed4(excess * _Color.a, 1);
            }
            ENDCG
        }
    }

    // No Fallback on purpose. A fallback would let a machine that cannot compile this quietly render
    // something else; VectorLightShader checks Shader.isSupported and stands the whole feature down
    // instead, which is a defined arm rather than an unknown one.
}
