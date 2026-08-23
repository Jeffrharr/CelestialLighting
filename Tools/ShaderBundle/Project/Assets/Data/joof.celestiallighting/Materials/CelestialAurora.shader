// The aurora curtain's field, evaluated per fragment instead of baked into a 192-square texture
// (DESIGN.md §11a, issue #196).
//
// WHY THIS IS A SHADER. Not to buy frames back — the bake was cheap. What the bake could not give is
// RESOLUTION: 192 texels stretched over a sheet 88 cells wide is 2.2 texels per cell, magnified
// bilinearly, and the rays are the single most recognisable thing about an aurora. Two decisions in
// AuroraCurtainHemRays are concessions to that cap rather than statements about how an aurora looks —
// the one-octave ray limit ("half-size features fall below what bilinear filtering can hold") and
// §11a's "an aurora is the one effect that loses nothing to blur". Both are answered here by
// evaluating the field at the resolution of the screen, and the gap widens as the camera closes in,
// because magnification is exactly what the camera controls.
//
// WHAT IT DELETES IS THE LARGER PRIZE. Everything the CPU path does to make baking affordable — the
// rolling row cursor, pinning `time` and the driver tint per sweep, the cached ColumnTable, the two
// textures and the exact-linear cross-fade between completed sweeps, the second draw call per live
// display and the ~0.5 s of display lag that cross-fade costs — exists solely because a bake is
// expensive. A fragment program evaluates the field at NOW, so none of it has anything to do.
//
// THIS IS A PORT, NOT A REIMPLEMENTATION, and that distinction is load-bearing. Every constant,
// every seed offset and every curve below is copied from AuroraCurtainHemRays / AuroraNoise /
// AuroraMath, which remain the offline-tested reference AND the fallback path. That matters more
// here than it does for CelestialCloudVolume.shader, which marches a volume the CPU baked and so
// leaves the pure core still governing the pixels. This file is the first place in the repo where a
// pure core does NOT govern what is drawn, so `aurora_shader_agreement` renders this shader to a
// RenderTexture, reads it back and compares it against AuroraCurtainHemRays.At — the only thing
// standing between a subtle typo in the port and an aurora that is simply a different aurora.
//
// FLOAT THROUGHOUT, never half or fixed. The noise is lattice-based: the hash is exact 32-bit
// integer arithmetic and the coordinate feeding floor() decides WHICH lattice cell a fragment is in.
// A half-precision coordinate lands fragments in the wrong cell near every lattice boundary, and the
// hash of a neighbouring cell is not a nearby value — it is an unrelated one. That reads as
// confetti, not as a slightly blurrier aurora.
Shader "CelestialLighting/Aurora"
{
    Properties
    {
        // NEVER SAMPLED. It is declared so that Material.mainTextureScale / mainTextureOffset — which
        // is what SheetMaterial.SetScale / SetOffset write, on both the shader and the CPU path —
        // keep driving _MainTex_ST. Placement code therefore stays one code path across both
        // renderers rather than forking on which one is live, which is the property that makes the
        // feature flag a real A/B instead of two different layouts.
        _MainTex ("Unused; present so mainTextureScale drives _MainTex_ST", 2D) = "white" {}

        _Color ("Sheet colour (rgb, always white) and this display's alpha (a)", Color) = (1, 1, 1, 1)

        // The wrapped tick count, already reduced modulo AuroraCurtainHemRays.DriftWrapTicks by the
        // adapter. Wrapping happens in INTEGER arithmetic on the C# side, before it ever reaches a
        // float: past 16,777,216 a float cannot represent every integer, so an unwrapped TicksGame
        // would make an old colony's aurora advance in jerks and then stop. 4,000,000 is exactly
        // representable, so by the time it arrives here the precision problem is already solved.
        _FieldTime ("Wrapped tick count to evaluate the field at", Float) = 0

        // §11's driver colour in rgb, and in alpha how far it pulls the palette
        // (AuroraCurtainHemRays.DriverTintWeight, 0.3). Partial on purpose: at 1 the curtain is a
        // single hue again, which is the exact failure §11a exists to fix, and at 0 the ribbons and
        // the flat wash beneath them visibly disagree about the hue during an event.
        _DriverTint ("Driver colour (rgb) and how far it pulls the palette (a)", Color) = (1, 1, 1, 0)
    }

    SubShader
    {
        // Same tags, blend and depth state as the MoteGlow path this replaces. Additive because an
        // aurora emits light rather than replacing the sky behind it.
        //
        // THE QUEUE TAG BELOW IS NOT WHAT SHIPS. AuroraShader.NewMaterial copies MoteGlow's queue at
        // runtime, and #151 is why: a bundle declaring "Queue" = "Transparent" (3000) against
        // MoteGlow's 3151 puts the additive pass UNDER the lighting overlay's multiply, which
        // measured as a masked ΔE of 5.58 and reads as a wrong formula rather than an ordering bug.
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

            // 3.5 rather than 3.0 for the integer bitwise operators the value-noise hash needs. Any
            // machine running RimWorld in 2026 clears this comfortably; the ones that do not fail
            // Shader.isSupported and AuroraShader stands the whole path down to the CPU bake, which
            // is a defined arm rather than an unknown one.
            #pragma target 3.5

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float _FieldTime;
            float4 _DriverTint;

            // --- constants, copied from AuroraCurtainHemRays -------------------------------------

            static const float DriftRate = 0.0006;
            static const float DriftWrapCycle = 2400.0;
            static const float HemUnderhang = 0.022;
            static const float HemCoreHeight = 0.055;
            static const float HemCoreGain = 0.46;
            static const float RayFloor = 0.46;
            static const float RaySharpen = 0.62;
            static const float FalloffCurvature = 0.62;
            static const float RayTopFloor = 0.34;
            static const float RayClumpDepth = 0.38;
            static const float HueAtHem = 0.46;
            static const int HueWobblePeriod = 4;
            static const float HueWobbleAmplitude = 0.09;
            static const float HueWobbleDrift = 0.25;
            static const int HueWobbleSeed = 61001;
            static const float EdgeFeather = 0.16;
            static const float HorizontalTaper = 0.22;

            // AuroraMath's palette. HueGreenLow/High are the edges of the band where green holds
            // undiluted; violet fringes below it and red above.
            static const float3 CurtainPurple = float3(0.62, 0.10, 0.92);
            static const float3 OxygenGreen = float3(0.16, 1.00, 0.36);
            static const float3 OxygenRed = float3(1.00, 0.15, 0.18);
            static const float HueGreenLow = 0.30;
            static const float HueGreenHigh = 0.66;

            // --- AuroraNoise ---------------------------------------------------------------------

            // AuroraNoise.Hash01, bit for bit. Done in uint rather than int because signed overflow
            // is undefined in HLSL while unsigned is defined to wrap — and the two produce identical
            // low 32 bits under two's complement, which is all the hash reads.
            //
            // The y term of the 2-D original is dropped rather than passed as zero: every call in
            // this field is Value1, i.e. yPeriod 1, and y * 668265263 with y == 0 contributes
            // nothing. See Value1 for why that is exact rather than approximately right.
            float AuroraHash01(int x, int seed)
            {
                uint h = (uint)x * 374761393u + (uint)seed * 1274126177u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return float(h & 0xFFFFFFu) * (1.0 / 16777216.0);
            }

            // AuroraNoise's quintic fade and its integer wrap. HLSL's % on ints truncates toward
            // zero exactly as C#'s does, so the negative correction is the same correction.
            float AuroraFade(float t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }

            // SIGNED, AND THE COMPILER WARNS ABOUT IT: "integer modulus may be much slower, try
            // using uints if possible" on every API. Kept signed anyway, because v genuinely goes
            // negative — three of the drift coefficients are (-0.50, -0.75, -0.25) against a drift
            // that reaches 2400, so the noise coordinate reaches about -1800 — and the unsigned
            // rewrite needs a bias whose size depends on constants declared elsewhere in the file.
            // Getting that bound wrong would not warn, it would silently sample the wrong lattice
            // cell for one curtain at one end of the drift cycle.
            //
            // Whether the warning matters is a measurement, not a guess: there are 38 of these per
            // fragment, on a bounded patch, during a rare event. See the profile in issue #196 before
            // trading exactness for it.
            int AuroraWrap(int v, int period)
            {
                int m = v % period;
                return m < 0 ? m + period : m;
            }

            // AuroraCurtainHemRays.Value1 — AuroraNoise.Value with yPeriod pinned to 1.
            //
            // TWO HASHES, NOT FOUR, AND STILL BIT-IDENTICAL. With yPeriod 1 the wrap sends both
            // lattice rows to row 0, so v00 == v01 and v10 == v11, the top and bottom lerps produce
            // the same number, and lerp(a, a, fy) returns a exactly. The C# side notes this saving
            // and declines to take it, because it would mean forking AuroraNoise's shipped primitive
            // for one caller. Here there is no primitive to fork.
            float AuroraValue1(float x, int period, int seed)
            {
                period = max(period, 1);

                int xi = (int)floor(x);
                float fx = AuroraFade(x - xi);

                float v0 = AuroraHash01(AuroraWrap(xi, period), seed);
                float v1 = AuroraHash01(AuroraWrap(xi + 1, period), seed);

                return lerp(v0, v1, fx);
            }

            // AuroraCurtainHemRays.Fbm1. The period DOUBLES with the frequency, which is what keeps
            // every octave tileable over the same tile — a lattice that did not double would put the
            // seam of octave 1 somewhere other than the seam of octave 0, and the field would not
            // wrap. Tileability is not cosmetic here: a field that does not wrap shows a hard seam
            // sweeping across the colony once per cycle.
            float AuroraFbm1(float x, int period, int seed, int octaves)
            {
                float sum = 0.0;
                float norm = 0.0;
                float amplitude = 1.0;
                float frequency = 1.0;
                int p = max(period, 1);

                for (int octave = 0; octave < octaves; octave++)
                {
                    sum += amplitude * AuroraValue1(x * frequency, p, seed + octave * 1013);
                    norm += amplitude;
                    amplitude *= 0.5;
                    frequency *= 2.0;
                    p *= 2;
                }

                return sum / norm;
            }

            // --- AuroraMath ----------------------------------------------------------------------

            // AuroraMath.Amplify: the CC0 power chain, m^4 v^2 + m v^4 + v^8. Its job is to crush
            // everything off the bright contour toward black, which is what gives the curtain a
            // defined edge rather than a soft gradient.
            float AuroraAmplify(float value)
            {
                const float magic = 0.166504;

                float v = saturate(value);
                float v2 = v * v;
                float v4 = v2 * v2;
                float v8 = v4 * v4;

                return magic * magic * magic * magic * v2 + magic * v4 + v8;
            }

            // AuroraCurtainHemRays.PaletteColor. Green holds the middle because fBm clusters near
            // 0.5, so the coloured fringes appear at the tails — green-dominant with red above and
            // violet below, which is what an aurora looks like rather than a rainbow.
            float3 AuroraPaletteColor(float hue01)
            {
                float h = saturate(hue01);

                if (h < HueGreenLow)
                    return lerp(CurtainPurple, OxygenGreen, h / HueGreenLow);

                if (h > HueGreenHigh)
                    return lerp(OxygenGreen, OxygenRed, (h - HueGreenHigh) / (1.0 - HueGreenHigh));

                return OxygenGreen;
            }

            // --- the field -----------------------------------------------------------------------

            float AuroraSmoothstep01(float t)
            {
                float c = saturate(t);
                return c * c * (3.0 - 2.0 * c);
            }

            float AuroraWrap01(float v)
            {
                float f = fmod(v, 1.0);
                return f < 0.0 ? f + 1.0 : f;
            }

            float AuroraDrift(float t)
            {
                float drift = fmod(t * DriftRate, DriftWrapCycle);
                return drift < 0.0 ? drift + DriftWrapCycle : drift;
            }

            // A triangle wave over the whole drift cycle, used to raise and lower the hems. Drift is
            // already reduced into [0, DriftWrapCycle) before it arrives, so the phase is in [0, 1).
            float AuroraOscillate(float drift)
            {
                float phase = drift / DriftWrapCycle;
                phase -= floor(phase);
                return 4.0 * abs(phase - 0.5) - 1.0;
            }

            // The sheet's own edges, feathered so a bounded patch fades out instead of ending on a
            // straight cut. Horizontal on u, vertical on v, different widths because the curtain's
            // v axis is ALTITUDE UP THE CURTAIN rather than map-north.
            float AuroraHorizontalFeather(float u)
            {
                float t = u - floor(u);
                return AuroraSmoothstep01(t / HorizontalTaper)
                     * AuroraSmoothstep01((1.0 - t) / HorizontalTaper);
            }

            float AuroraVerticalFeather(float v)
            {
                float t = v - floor(v);
                return AuroraSmoothstep01(t / EdgeFeather)
                     * AuroraSmoothstep01((1.0 - t) / EdgeFeather);
            }

            float AuroraEnvelopeGate(float raw) { return saturate((raw - 0.20) / 0.42); }

            float AuroraHueWobble(float u, float drift)
            {
                float raw = AuroraValue1(
                    u * HueWobblePeriod + drift * HueWobbleDrift, HueWobblePeriod, HueWobbleSeed);

                return (raw - 0.5) * 2.0 * HueWobbleAmplitude;
            }

            // AuroraCurtainHemRays.ColumnState, minus InvRayHeight — which the C# struct carries but
            // the fill path never reads.
            struct ColumnState
            {
                float hem;
                float invRayTop;
                float rayWeight;
                float coreWeight;
                float curtainHue;
            };

            // AuroraCurtainHemRays.EvaluateColumn. On the CPU this is hoisted out of the pixel loop
            // and cached per column, because it holds every noise sample the field takes and that
            // hoist is the whole performance story there. Here each fragment simply pays for it: 19
            // hashes is nothing on a GPU, and the hoist has no meaning when there are no columns.
            //
            // The CurtainSpec fields arrive as loose parameters rather than as a struct array,
            // because indexing a constant array of structs by loop counter is where shader compilers
            // start silently unrolling into register spills.
            ColumnState AuroraEvaluateColumn(
                float u, float drift,
                float hemCenter, int hemPeriod, int hemOctaves, float hemAmplitude, float hemDrift,
                float hemRise, int rayPeriod, float rayDrift, int rayClumpPeriod, int envelopePeriod,
                float envelopeDrift, float rayHeight, float weight, float curtainHue, int seed)
            {
                float wander = AuroraFbm1(u * hemPeriod + drift * hemDrift, hemPeriod, seed, hemOctaves);

                // Vertical drift is applied to the hem rather than to v, so it costs nothing and so
                // the wrap argument only has to hold modulo 1.
                float hem = hemCenter + (wander - 0.5) * 2.0 * hemAmplitude
                          + AuroraOscillate(drift) * hemRise;

                // One octave for the rays, not two — and unlike on the CPU this is no longer a
                // concession to texel density. It stays because a second octave halves the feature
                // size, and the field was tuned by eye against one.
                float rayRaw = AuroraValue1(u * rayPeriod + drift * rayDrift, rayPeriod, seed + 701);
                float raySharp = lerp(
                    rayRaw, AuroraAmplify(rayRaw) / AuroraAmplify(1.0), RaySharpen);

                // The clump field MULTIPLIES the sharpened rays rather than being summed with them,
                // so a quiet stretch of curtain has faint rays rather than a different set of rays.
                float clump = AuroraValue1(
                    u * rayClumpPeriod + drift * rayDrift, rayClumpPeriod, seed + 907);
                float bundled = raySharp * (1.0 - RayClumpDepth + RayClumpDepth * clump);
                float rayMod = RayFloor + (1.0 - RayFloor) * bundled;

                float lengthRaw = AuroraValue1(u * rayPeriod + drift * rayDrift, rayPeriod, seed + 1109);
                float rayTop = rayHeight * (RayTopFloor + (1.0 - RayTopFloor) * lengthRaw);

                float envelope = AuroraValue1(
                    u * envelopePeriod + drift * envelopeDrift, envelopePeriod, seed + 1301);
                float gate = AuroraEnvelopeGate(envelope) * weight;

                float edge = AuroraHorizontalFeather(u);

                ColumnState col;
                col.hem = AuroraWrap01(hem);
                // The max guard is not defensive noise: 0 * infinity is NaN, and a NaN alpha
                // propagates into every overlapping curtain through the sum below.
                col.invRayTop = 1.0 / max(rayTop, 1e-4);
                col.rayWeight = rayMod * gate * edge;
                col.coreWeight = HemCoreGain * gate * edge;
                col.curtainHue = curtainHue;
                return col;
            }

            // AuroraCurtainHemRays.SignedHeightAboveHem. Taking the difference MODULO THE TILE and
            // then folding the top sliver down to negative is what makes the field seamless in v for
            // free: v and v+1 land on the same s by construction. The fold is also what gives the
            // curtain a below-the-hem side at all — without it every curtain would step from black to
            // full brightness at its hem in one texel.
            float AuroraSignedHeightAboveHem(float v, float hem)
            {
                float s = v - hem;

                if (s < 0.0)
                    s += 1.0;

                return s > 1.0 - HemUnderhang ? s - 1.0 : s;
            }

            // AuroraCurtainHemRays.Falloff. A blend of smoothstep and a linear-to-squared tilt:
            // smoothstep alone is symmetric and flattens the curtain into a slab, while the tilt
            // keeps the hem the brightest part without restoring a hard top edge. Reaching exactly
            // zero rather than trailing off is what makes the vertical wrap provable.
            float AuroraFalloff(float fraction)
            {
                float f = 1.0 - fraction;

                if (f <= 0.0)
                    return 0.0;

                return AuroraSmoothstep01(f) * (1.0 - FalloffCurvature + FalloffCurvature * f);
            }

            // AuroraCurtainHemRays.BelowHem / AboveHem, inlined into one branch on the sign of s.
            float AuroraCurtainAlpha(ColumnState col, float v)
            {
                float s = AuroraSignedHeightAboveHem(v, col.hem);

                if (s < 0.0)
                {
                    // One shared ramp below the hem, because the sheet and the hem ridge both simply
                    // stop at the curtain's lower border. Real auroras cut off there hard — it is the
                    // altitude where the incoming electrons run out — so this is a short smoothstep,
                    // not a falloff.
                    float ramp = AuroraSmoothstep01(1.0 + s * (1.0 / HemUnderhang));
                    return saturate(ramp * (col.rayWeight + col.coreWeight));
                }

                // The ray-modulated sheet decaying to exactly zero at this column's ray top, plus the
                // continuous hem ridge — deliberately OUTSIDE the ray modulation, so the hem stays an
                // unbroken line where the rays leave gaps.
                float body = AuroraFalloff(s * col.invRayTop) * col.rayWeight;
                float core = AuroraFalloff(s * (1.0 / HemCoreHeight)) * col.coreWeight;
                return saturate(body + core);
            }

            // Accumulates one curtain into the running alpha and alpha-weighted hue. Written as a
            // macro-free helper taking inout so the three unrolled calls below read as three
            // curtains rather than as three copies of six lines.
            void AuroraAccumulate(ColumnState col, float v, float wobble,
                                  inout float alpha, inout float hueWeighted)
            {
                float a = AuroraCurtainAlpha(col, v);
                alpha += a;

                // Flat hue: the sheet is one colour top to bottom. The wobble still rides on it so
                // the colour varies slightly ALONG the curtain, which keeps it from looking like a
                // printed band.
                hueWeighted += a * saturate(col.curtainHue + wobble);
            }

            // The whole field at (u, v, time), matching AuroraCurtainHemRays.At composed with the
            // palette and driver tint that FillFromColumns applies. Returns premultiplied colour:
            // rgb already scaled by the field's own alpha, which is what additive blending wants.
            //
            // The three curtains are unrolled with their CurtainSpec values as literals. They are a
            // fixed table in C# too — see AuroraCurtainHemRays.Curtains — and keeping them literal
            // here means the compiler folds every period and seed into the noise calls instead of
            // branching on a uniform.
            float3 AuroraField(float u, float v, float t)
            {
                float drift = AuroraDrift(t);
                float wobble = AuroraHueWobble(u, drift);

                float alpha = 0.0;
                float hueWeighted = 0.0;

                AuroraAccumulate(AuroraEvaluateColumn(u, drift,
                    0.28, 3, 2, 0.045, 0.25, 1.0 / 32.0, 60, 1.00, 12, 2, 0.25, 0.45, 1.00, 0.46, 10009),
                    v, wobble, alpha, hueWeighted);

                AuroraAccumulate(AuroraEvaluateColumn(u, drift,
                    0.38, 5, 2, 0.040, -0.50, 2.0 / 32.0, 40, -0.75, 8, 3, -0.25, 0.36, 0.82, 1.00, 20011),
                    v, wobble, alpha, hueWeighted);

                AuroraAccumulate(AuroraEvaluateColumn(u, drift,
                    0.48, 4, 2, 0.035, 0.75, -2.0 / 32.0, 30, 0.50, 6, 2, 0.50, 0.27, 0.68, 0.06, 30011),
                    v, wobble, alpha, hueWeighted);

                // AuroraCurtainHemRays.Resolve. Empty sky is empty rather than "the hue at the hem
                // with zero alpha", but the hue still has to be defined there or the divide is 0/0.
                if (alpha <= 0.0)
                    return float3(0.0, 0.0, 0.0);

                float outAlpha = saturate(alpha) * AuroraVerticalFeather(v);
                float3 colour = AuroraPaletteColor(saturate(hueWeighted / alpha));

                // The driver tint, exactly as FillFromColumns applies it — a lerp per channel toward
                // §11's condition colour, at DriverTintWeight.
                colour = lerp(colour, _DriverTint.rgb, saturate(_DriverTint.a));

                return colour * outAlpha;
            }

            // --- plumbing --------------------------------------------------------------------------

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

                // TRANSFORM_TEX applies _MainTex_ST, i.e. the same mainTextureScale/Offset the CPU
                // path uses to place its sheets. A COORDINATE is interpolated here, not a value, so
                // it is exact across the quad — the distinction #151 paid for learning.
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // _Color.rgb is white on every draw this mod issues; it is honoured anyway so the
                // material behaves like the MoteGlow material it replaces. _Color.a carries the
                // display's own brightness — night visibility, the condition's ramp, the display's
                // peak and where it sits in its own life, all already multiplied together.
                float3 field = AuroraField(i.uv.x, i.uv.y, _FieldTime);

                return float4(field * _Color.rgb * _Color.a, 1.0);
            }
            ENDCG
        }
    }

    // No Fallback, for the same reason CelestialLighting/VectorLightMax has none. A fallback would
    // let a machine that cannot compile this quietly render something else; AuroraShader checks
    // Shader.isSupported and stands the path down to the CPU bake instead, which is an arm we have
    // measured rather than an unknown one.
}
