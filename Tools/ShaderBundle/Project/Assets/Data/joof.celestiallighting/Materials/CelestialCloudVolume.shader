// §25c variant D: the cloud volume raymarched per fragment (DESIGN.md §25c, issue #144).
//
// THIS IS A TRANSCRIPTION OF Source/CloudRaymarchMath.cs AND MUST STAY ONE. That file is what the
// offline tests run and what Tools/CloudPreview renders; this is what the screen shows. Nothing
// offline can execute HLSL, so the ONLY thing making those tests say anything about what a player
// sees is that the two are the same arithmetic in two languages. A change to one is a change to the
// other, and the shipped AssetBundle has to be rebuilt (Tools/ShaderBundle/build.sh) or the mod goes
// on drawing the old version while the tests pass on the new one.
//
// The two places where the transcription is deliberately NOT literal, both because the GPU does the
// job better in fixed-function hardware than a loop can:
//
//   1. TRILINEAR FILTERING. `CloudRaymarchMath.Sample` interpolates eight voxels by hand; here that
//      is one tex3D. The conventions match — both sample at texel CENTRES — which is why the C# has
//      a test pinning a half-texel offset that looks like it could not possibly be wrong.
//   2. THE VOLUME'S EDGES. The C# returns zero above and below the deck; hardware clamping would
//      instead smear the outermost slice outward forever. So the uploaded texture carries a zero
//      slice at each end (CloudVolumeTexture.Build) and the w coordinate is offset past it, which
//      reproduces the fade in hardware rather than in a branch.
//
// WHY A CUSTOM SHADER AT ALL, WHEN §25 AND §25b NEEDED NONE. Two things are unreachable without one,
// and both are the difference between this looking like cloud and looking like a lit texture:
//
//   * PER-PIXEL SHADING. A baked atlas shades at one sample per atlas texel and is then magnified
//     three or four times on the way to the screen, so its self-shadow edges are the one part of the
//     picture with fine structure and the part resampled hardest.
//   * HEADROOM ABOVE THE SHEET COLOUR. `ShaderDatabase.Transparent` multiplies an 8-bit texture into
//     `material.color` and has no value above 1, so a bake can only ever DARKEN. A silver lining is
//     by definition brighter than the cloud it edges. `Blend One OneMinusSrcAlpha` gives that back.
Shader "CelestialLighting/CloudVolume"
{
    Properties
    {
        _Volume ("Density volume", 3D) = "" {}
        _Color ("Sunlit colour", Color) = (1, 1, 1, 1)
        _ShadowColor ("Shadowed colour", Color) = (0.3, 0.3, 0.3, 1)
    }

    SubShader
    {
        // Matched to ShaderDatabase.Transparent's own tags, so this sorts against vanilla's
        // transparent geometry exactly where the sheet did before — the sheet's altitude is what
        // decides what it draws over, and swapping the shader must not quietly change that.
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Fog { Mode Off }

        // PREMULTIPLIED, not SrcAlpha OneMinusSrcAlpha. The march accumulates radiance already
        // weighted by the coverage it passed through, so the source term is not bounded by the
        // destination it mixes toward — which is the whole reason a rim can come out brighter than
        // the cloud's own colour here and cannot in a bake.
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            // Kept in sync with CloudRaymarchMath.ViewSteps / .LightSteps by hand. They are #defines
            // rather than uniforms because the loop bounds have to be compile-time known for the
            // compiler to schedule the inner loop at all.
            #define VIEW_STEPS 24
            #define LIGHT_STEPS 8

            sampler3D _Volume;
            float4 _Volume_ST;

            fixed4 _Color;
            fixed4 _ShadowColor;

            // (atlas size in texels, padded slice count, peak height in texels, layer height in texels)
            float4 _VolumeParams;

            // The blob's own cell in atlas texels: (minX, minY, maxX, maxY). The march is clamped to
            // it because neighbouring atlas cells are DIFFERENT CLOUDS stored side by side, and
            // letting one shadow the next puts a hard seam down the middle of a sheet.
            float4 _CellBounds;

            // The unit vector toward the sun, in this sheet's own texture space — already mirrored to
            // match the sheet's UV flip by the C# that sets it.
            //
            // THAT MIRRORING IS A BUG A BAKE CANNOT FIX. A sheet drawn with a negative
            // mainTextureScale reads the atlas backwards, so a baked lit side arrives on the wrong
            // flank and half the sky is lit from the east. Here the light direction is mirrored with
            // the texture and the two stay consistent, which is why this path can keep the flips that
            // give the sheets their variety.
            float3 _SunDir;

            // (view extinction per texel, ambient wrap, light-span cap in texels, LIGHT extinction)
            //
            // TWO EXTINCTIONS, AND THE SPLIT IS DELIBERATE RATHER THAN PHYSICAL. One coefficient is
            // the honest model: how opaque a column is and how far the sun reaches into it are the
            // same quantity. It cannot survive this subsystem's deck table. A cirrus deck is 3.4
            // texels thick against cumulus at 22.4, so a grazing sunset ray stays inside it for ~80
            // texels of horizontal travel — and at the density needed to make the deck VISIBLE from
            // above, that path is fully occluded. The result is a cirrus sheet pinned at the ambient
            // floor: uniformly dark, at exactly the elevations §25b's deck windows exist to light.
            //
            // Real cirrus escapes this by being genuinely thin — optical depth well under one across
            // the whole path — but a deck that thin cannot be seen from above at all, which is the
            // trade the 2-D lane already resolves by fiat (a density gamma and a per-deck opacity).
            // So the view ray keeps the geometric coefficient and the light ray gets one scaled by
            // the deck's own thickness, which is the same fiat spelled out where it can be read.
            float4 _MarchParams;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);

                // The sheet's UV transform picks the atlas cell and applies the mirroring, exactly as
                // it does for the baked path — the geometry and the placement are unchanged, and only
                // what happens per fragment is new.
                o.uv = v.uv * _Volume_ST.xy + _Volume_ST.zw;
                return o;
            }

            // One density sample, in atlas texels and texels above the deck base. Zero outside the
            // blob's own cell; the vertical fade is carried by the uploaded texture's zero slices
            // rather than by a test here.
            float SampleVolume(float2 texel, float height)
            {
                float3 uvw = float3(
                    texel.x / _VolumeParams.x,
                    texel.y / _VolumeParams.x,
                    // Past the zero slice the upload puts at each end: voxel k lives at slice
                    // k + 1, so the padded slice index is height/layerTexels + 0.5 and the texture
                    // coordinate is that plus another half slice, over the padded count.
                    (height / _VolumeParams.w + 1.0) / _VolumeParams.y);

                // tex3Dlod, not tex3D: a plain sample asks the hardware for screen-space
                // derivatives to pick a mip level, and inside a dynamic loop those are undefined —
                // the compiler says so on all three backends. The volume has no mips to pick
                // between anyway, so naming level 0 is both the correct fix and the cheaper one.
                float density = tex3Dlod(_Volume, float4(uvw, 0)).a;

                // Four steps rather than four branches: a fragment shader pays for both sides of a
                // branch whenever a warp disagrees about it, and along a light ray the warp always
                // disagrees.
                float inside =
                    step(_CellBounds.x, texel.x) * step(texel.x, _CellBounds.z) *
                    step(_CellBounds.y, texel.y) * step(texel.y, _CellBounds.w);

                return density * inside;
            }

            // Optical depth between a point in the volume and the sun.
            //
            // The segments GROW, and that is not a micro-optimisation. At a grazing sun the ray has
            // to be followed most of a blob to find what is shadowing this fragment, so a uniform
            // 8-step march puts its samples ten texels apart and gives the near field — where the
            // cloud's own lumps carve the detail the eye reads as shape — a single sample. Geometric
            // steps put four of the eight inside the first tenth of the ray for the same cost.
            float LightDepth(float2 texel, float height)
            {
                float verticalSpan = _VolumeParams.z / max(abs(_SunDir.z), 0.02);
                float span = min(_MarchParams.z, verticalSpan);

                // The closed form for the first segment of a geometric series summing to `span`,
                // with the growth constant folded in as a literal — 1.7 to the eighth, less one,
                // over 0.7. Written out rather than looped because the compiler cannot fold a loop
                // whose bound it can see but whose body it cannot.
                float step0 = span / 98.2648;

                float tau = 0.0;
                float travelled = 0.0;
                float segment = step0;

                [loop]
                for (int i = 0; i < LIGHT_STEPS; i++)
                {
                    // The segment's MIDPOINT, weighted by its own length, so a long far-field
                    // segment counts for the distance it covers rather than for one texel of it.
                    float t = travelled + segment * 0.5;
                    float density = SampleVolume(
                        texel + _SunDir.xy * t, height + _SunDir.z * t);

                    tau += density * _MarchParams.w * segment;
                    travelled += segment;
                    segment *= 1.7;
                }

                return tau;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 texel = i.uv * _VolumeParams.x;

                float peak = _VolumeParams.z;
                float viewStep = peak / VIEW_STEPS;

                float transmittance = 1.0;
                float3 colour = 0.0;

                // Front to back, and the camera is above, so front is the TOP of the deck.
                [loop]
                for (int v = 0; v < VIEW_STEPS; v++)
                {
                    float height = peak - (v + 0.5) * viewStep;
                    float density = SampleVolume(texel, height);

                    // Most of a cloud's bounding box is empty, and skipping the eight-fold inner loop
                    // there is what pays for this shader. Kept as a real branch: a warp that is
                    // entirely outside the cloud takes it together, which is the common case at the
                    // edges of a sheet.
                    if (density > 0.002)
                    {
                        float tau = LightDepth(texel, height);

                        // Sky light, occluded by everything above this sample — and `transmittance`
                        // IS that occlusion, already accumulated, at a cost of zero fetches. A cloud
                        // is lit from above by the whole sky as well as by the sun, and the camera is
                        // also above, so the downward transmittance this loop is already carrying is
                        // exactly the fraction of sky the sample can see.
                        float ambient = _MarchParams.y * transmittance;
                        float direct = ambient + (1.0 - _MarchParams.y) * exp(-tau);

                        float absorbed = 1.0 - exp(-density * _MarchParams.x * viewStep);
                        float weight = transmittance * absorbed;

                        colour += weight * lerp(_ShadowColor.rgb, _Color.rgb, direct);
                        transmittance *= 1.0 - absorbed;
                    }
                }

                float alpha = 1.0 - transmittance;

                // The sheet's own colour and opacity, applied last. `_Color.a` carries the sheet
                // alpha — how much cloud this deck is showing — and multiplies the march's own
                // coverage rather than replacing it.
                return fixed4(colour * _Color.a, alpha * _Color.a);
            }
            ENDCG
        }
    }

    // NO FALLBACK, deliberately. A fallback would let an unsupported card draw the sheet through
    // some other shader entirely and call it success; the C# checks `shader.isSupported` and returns
    // to §25b's baked atlas instead, which is a picture somebody has looked at.
}
