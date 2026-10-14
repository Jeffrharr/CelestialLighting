// Vector lighting's BATCHED max composition as a shader of its own, for RimWorld 1.7.
//
// WHY NOT THE KEYWORD. VectorLightMax.shader carries the batched program as its VECTORLIGHT_BATCHED
// variant, and on 1.6 a material enables it. RimWorld 1.7 builds each bundle shader from the one
// variant with the fewest keywords and never switches (UnityStubs TryPickProgram), so that variant
// is unreachable there. This file #defines the keyword instead and includes the same program, so the
// composition is still written once; only how the variant is selected differs.
//
// Additive (One One), VectorLightMax's default blend. The surface lift's batched program is
// VectorLightMaxLiftBatched.shader, because 1.7 bakes blend per shader (issue #257).
Shader "CelestialLighting/VectorLightMaxBatched"
{
    Properties
    {
        _MainTex ("Falloff x penumbra gradient", 2D) = "white" {}
        [HideInInspector] _VanillaArray ("Batched emitters' vanilla glow", 2DArray) = "" {}
    }

    SubShader
    {
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
            #pragma target 3.5

            // The batched program, always: this shader IS the VECTORLIGHT_BATCHED variant.
            #define VECTORLIGHT_BATCHED

            // A variant nothing ever draws, so the bundle holds two and Unity keeps the parameters
            // in each variant's own blob. With one variant they are hoisted into the shared block,
            // which RimWorld 1.7's loader never reads ("not a known constant buffer"). The decoy
            // must touch no parameter; VectorLightMax.cginc guards both stages. See
            // Assets/Editor/WaylithDecoyVariant.cs.
            #pragma multi_compile_local _ CL_WAYLITH_DECOY

            #include "UnityCG.cginc"
            #include "VectorLightMax.cginc"
            ENDCG
        }
    }
}
