// The surface lift's BATCHED program as a shader of its own, for RimWorld 1.7: VectorLightMaxBatched
// with Blend DstColor One baked in, as VectorLightMaxLift is to VectorLightMax. See
// VectorLightMaxBatched.shader for why the batched program is a shader rather than a keyword there.
Shader "CelestialLighting/VectorLightMaxLiftBatched"
{
    Properties
    {
        _MainTex ("Falloff x penumbra gradient", 2D) = "white" {}
        [HideInInspector] _VanillaArray ("Batched emitters' vanilla glow", 2DArray) = "" {}
    }

    SubShader
    {
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
