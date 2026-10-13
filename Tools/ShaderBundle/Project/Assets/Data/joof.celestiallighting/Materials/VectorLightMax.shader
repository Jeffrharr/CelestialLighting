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
//
// THE BATCHED VARIANT, AND WHY IT IS A VARIANT RATHER THAN A SECOND SHADER. The idle frame's largest
// term is one Graphics.DrawMesh per visible emitter, and what forces it to be per emitter is that
// four numbers — the light's colour, its strength, how much of vanilla to subtract, and vanilla's
// own square — are render state carried on a per-emitter MaterialPropertyBlock. VECTORLIGHT_BATCHED
// moves all four off the property block: the first three into uniform arrays indexed by a slot the
// mesh carries in UV1.z, the fourth into a texture array sliced by the same slot. One mesh holding
// every emitter of one radius bucket and composition then draws in one call.
//
// The two variants share one copy of the composition on purpose. They differ ONLY in the four lines
// that fetch those numbers; everything from the falloff sample down is written once. A batched arm
// that measured a different frame would otherwise be impossible to tell from a batching defect, and
// the whole point of the arm is that it must measure the same frame.
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

        // Every batched emitter's square, one slice each. Declared here rather than only in the
        // program so the property survives a material inspector round trip; the batched variant is
        // the only thing that reads it and the per-emitter variant never binds it at all.
        [HideInInspector] _VanillaArray ("Batched emitters' vanilla glow", 2DArray) = "" {}
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

            // 3.5 FOR THE TEXTURE ARRAY the batched variant samples. CelestialAurora already ships
            // at this level and works on every machine this mod has been run on, so it is a
            // precedent here rather than a new requirement — and VectorLightShader.Available checks
            // isSupported either way, so a machine that cannot run it falls back to the crossfade
            // instead of drawing something else.
            #pragma target 3.5

            // ONE SHADER, TWO VARIANTS, AND multi_compile RATHER THAN shader_feature. The batched
            // variant reads its per-emitter values out of a uniform array instead of off the
            // property block, which is the whole of what batching is: the per-emitter numbers stop
            // being render state and become data the one draw call carries for all of its emitters.
            // The arithmetic below is shared between the two by construction — the variants differ
            // only in WHERE _Color, _VanillaWeight, _SkyAmbient and vanilla's texel come from, and
            // the composition itself is written once.
            //
            // shader_feature would be wrong and silently so: it strips variants no material in the
            // Unity project references, and nothing in Tools/ShaderBundle's project references
            // either of these — the materials are built at runtime. The batched variant would be
            // absent from the bundle, EnableKeyword would quietly do nothing, and every batched draw
            // would run the per-emitter variant with no _Color bound. That is a frame of black fans,
            // not an error. VectorLightShader.BatchAvailable checks the keyword really exists in the
            // loaded shader for the same reason.
            #pragma multi_compile_local _ VECTORLIGHT_BATCHED

            #include "UnityCG.cginc"

            // The program itself — vertex, fragment and the batched variant's inputs. Shared with
            // VectorLightMaxLift.shader; see the include's header for why it is split out.
            #include "VectorLightMax.cginc"
            ENDCG
        }
    }

    // No Fallback on purpose. A fallback would let a machine that cannot compile this quietly render
    // something else; VectorLightShader checks Shader.isSupported and stands the whole feature down
    // instead, which is a defined arm rather than an unknown one.
}
