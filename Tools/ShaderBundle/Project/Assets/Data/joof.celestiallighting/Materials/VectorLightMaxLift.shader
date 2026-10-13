// Vector lighting's max composition drawn as a SURFACE LIFT: the identical program to
// VectorLightMax.shader (both #include VectorLightMax.cginc), fed into Blend DstColor One instead of
// the additive One One, so the beam multiplies the frame by (1 + output) rather than adding to it.
//
// A separate shader rather than a material setting because RimWorld 1.7 bakes blend state per shader
// (issue #257); VectorLightMax.cginc's header has the whole story. On 1.6 the same lift is still
// available through VectorLightMax's runtime _SrcBlend, and VectorLightShader.NewMaterial prefers this
// shader only when the bundle actually carries it.
//
// Everything below the blend — properties, pragmas, variants — is VectorLightMax.shader's, and must
// stay so: Tools/ShaderBundle/build.sh's WaylithCheck prints each shader's blend, and the two should
// differ there and nowhere else.
Shader "CelestialLighting/VectorLightMaxLift"
{
    Properties
    {
        _MainTex ("Falloff x penumbra gradient", 2D) = "white" {}
        _VanillaTex ("Vanilla's delivered glow over this emitter's square", 2D) = "black" {}
        _Color ("Light colour (rgb) and strength (a)", Color) = (1, 1, 1, 1)
        _VanillaWeight ("How much of vanilla's glow to subtract", Float) = 1
        _SkyAmbient ("Sky-only ambient the beam lands on; 0 selects the additive pass", Float) = 0

        // Every batched emitter's square, one slice each. Declared here rather than only in the
        // program so the property survives a material inspector round trip; the batched variant is
        // the only thing that reads it and the per-emitter variant never binds it at all.
        [HideInInspector] _VanillaArray ("Batched emitters' vanilla glow", 2DArray) = "" {}
    }

    SubShader
    {
        // Same tags and depth state as VectorLightMax. The blend is FIXED at DstColor/One — the surface
        // lift, dst * (1 + output) — because that is the only kind of blend RimWorld 1.7 can draw: it
        // bakes blend state per shader and ignores a material's _SrcBlend/_DstBlend. See
        // VectorLightMax.cginc's header and issue #257.
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend DstColor One
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
