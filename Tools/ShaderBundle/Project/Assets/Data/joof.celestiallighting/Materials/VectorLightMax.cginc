// The program body of vector lighting's max composition, shared by two shaders that differ only in
// their blend state:
//
//   VectorLightMax.shader      Blend [_SrcBlend] [_DstBlend]  (One One unless a 1.6 material says otherwise)
//   VectorLightMaxLift.shader  Blend DstColor One             (the surface lift, fixed)
//
// WHY TWO SHADERS FOR ONE PROGRAM. RimWorld 1.7's Waylith engine bakes a bundle shader's blend into
// its pipeline when it builds it, from the PROPERTY DEFAULT, and never reads _SrcBlend/_DstBlend off a
// material afterwards (UnityStubs UnityShaderAsset.ReadState). So the runtime switch the surface lift
// was built on — one shader, blend chosen per material — draws every material One One on 1.7, and the
// lift becomes additive light flooding the room (issue #257). One shader per blend state is the shape
// 1.7 can draw. The program lives here so both shaders compile the identical fragment code; a second
// copy would let them drift, and the surface lift's whole claim is that its OFF arm reproduces the
// additive pass exactly.
//
// Everything below is the original body, moved verbatim. Pragmas stay in each .shader file, where
// Unity is guaranteed to see them.

// How many emitters one batched draw may carry, and it must match
// VectorLightDrawBatch.MaxPerDraw exactly. A uniform array is a fixed allocation in the
// constant buffer, so this is a cost paid by the variant whether or not a frame fills
// it; 64 covers every emitter that has ever been on screen at once in this repo's
// scenarios with room to spare, and two arrays of 64 float4 is 2 KB of constants.
#define VECTORLIGHT_BATCH_MAX 64

struct appdata
{
    float4 vertex : POSITION;

    // x: distance from the light as a fraction of its radius. y: how far across a soft
    // shadow edge this vertex sits. Both already spent before this feature existed.
    float2 uv : TEXCOORD0;

    // Where this vertex sits in the emitter's own square, in [0, 1] on both axes — a
    // COORDINATE, not a value, which is the entire correction over #151.
    //
    // z IS THE EMITTER'S SLOT in the batched variant, and the reason batching needed no
    // new vertex channel: this was already a float4 because Unity's mesh API hands UV
    // channels over as float4, and zw were already being written as zero. A slot index is
    // constant across every triangle of one emitter's fan, so the hardware interpolates
    // it to exactly itself — the repo's rule that a per-vertex VALUE must not vary across
    // a triangle is satisfied because this one does not vary at all. w is still unused.
    float4 vanillaUv : TEXCOORD1;
};

struct v2f
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float2 vanillaUv : TEXCOORD1;

    #if defined(VECTORLIGHT_BATCHED)
    // Resolved in the vertex shader and interpolated, not looked up per fragment. The
    // index is per-emitter constant, so the values are constant across every triangle
    // and the interpolation is exact; doing the array read once per vertex instead of
    // once per fragment is the point of the channel existing.
    fixed4 lightColor : TEXCOORD2;

    // x: how much of vanilla's glow to subtract. y: the sky ambient the beam lands on,
    // zero for the additive pass. z: which slice of _VanillaArray is this emitter's
    // square. Packed into one interpolator rather than three because the batched variant
    // is the only one that pays for them.
    float4 lightParams : TEXCOORD3;
    #endif
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

#if defined(VECTORLIGHT_BATCHED)
// One slice per batched emitter, all the same size — which is why the batch key carries
// the field diameter as well as the radius bucket. A texture array rather than an atlas
// so each slice keeps its own clamp: an atlas would bleed one emitter's rim texels into
// its neighbour's under the bilinear filter, which would read as a faint wrong edge on a
// shadow boundary rather than as a batching bug.
UNITY_DECLARE_TEX2DARRAY(_VanillaArray);

// rgb: this emitter's light colour. a: its strength this frame. Exactly what _Color
// carries on the per-emitter path, for each emitter in the draw.
fixed4 _BatchColor[VECTORLIGHT_BATCH_MAX];

// x: _VanillaWeight. y: _SkyAmbient. z: the emitter's slice of _VanillaArray. w unused.
float4 _BatchParams[VECTORLIGHT_BATCH_MAX];
#endif

v2f vert(appdata v)
{
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex);
    o.uv = v.uv;

    // A texture coordinate interpolates exactly, because position across a triangle IS
    // linear. That is the whole difference: #151 interpolated the VALUE of a field that
    // is not linear, and this interpolates the place to look it up.
    o.vanillaUv = v.vanillaUv.xy;

    // NOT IN THE DECOY. The batched shaders carry a never-drawn CL_WAYLITH_DECOY variant (see
    // VectorLightMaxBatched.shader), and a decoy that still read the uniform arrays would leave
    // them hoisted into the block RimWorld 1.7 cannot see. Inert everywhere else: the per-emitter
    // shaders never declare the keyword.
    #if defined(VECTORLIGHT_BATCHED) && !defined(CL_WAYLITH_DECOY)
    // Rounded rather than truncated. The slot arrives as a float that came from an int,
    // and a float that is meant to be 3 can arrive as 2.9999995 through the vertex
    // stream's own precision — truncation would then read emitter 2's colour for every
    // vertex of emitter 3's fan, which is a wrong-coloured light rather than a crash.
    int slot = (int)(v.vanillaUv.z + 0.5);
    o.lightColor = _BatchColor[slot];
    o.lightParams = _BatchParams[slot];
    #endif

    return o;
}

fixed4 frag(v2f i) : SV_Target
{
    // The decoy touches nothing — see the vertex program's matching guard.
    #ifdef CL_WAYLITH_DECOY
    return 0;
    #endif

    // WHERE THE FOUR PER-EMITTER NUMBERS COME FROM IS THE WHOLE VARIANT. Everything
    // below this block is one copy of the composition, shared by both paths, so a change
    // to the arithmetic cannot land on one variant and miss the other — which is the
    // failure mode that would make the batched arm measure a different formula and read
    // as a batching defect.
    #if defined(VECTORLIGHT_BATCHED)
    fixed4 lightColor = i.lightColor;
    float vanillaWeight = i.lightParams.x;
    float skyAmbient = i.lightParams.y;
    fixed4 vanillaTexel =
        UNITY_SAMPLE_TEX2DARRAY(_VanillaArray, float3(i.vanillaUv, i.lightParams.z));
    #else
    fixed4 lightColor = _Color;
    float vanillaWeight = _VanillaWeight;
    float skyAmbient = _SkyAmbient;
    fixed4 vanillaTexel = tex2D(_VanillaTex, i.vanillaUv);
    #endif

    // White throughout with the falloff curve in alpha — see BuildGradient. Reading alpha
    // and not rgb is what keeps the curve from being squared.
    fixed4 gradient = tex2D(_MainTex, i.uv);

    // Our own model's glow at this fragment, in vanilla's units, before strength.
    fixed3 ours = lightColor.rgb * gradient.a;

    // Vanilla's, at this fragment, from its own grid. Bilinear, which is the same
    // filtering vanilla's lighting overlay applies to the same numbers.
    fixed3 vanilla = vanillaTexel.rgb * vanillaWeight;

    // THE VOID VETO: whether this fragment has a surface for light to land on at all.
    // One means ordinary ground, zero means the open space outside a gravship hull or an
    // orbital platform's deck, and CopyField writes it per cell into the same texture's
    // alpha. Light on a surface is albedo * illuminance and the void has no albedo, so
    // the contribution there is not dimmer, it is nothing.
    //
    // BILINEAR IS THE FEATURE HERE, NOT AN APPROXIMATION. Sampled the same way vanilla's
    // own glow is, the mask fades across a half cell at the hull rim instead of ending
    // on a stair-step — which is what light spilling off the edge of a deck looks like,
    // and is free because this is the sampler the composition was already using.
    //
    // ONE MEANS SURFACE, AND THE POLARITY IS DELIBERATE. An unbound _VanillaTex reads as
    // Unity's blackTexture, (0,0,0,1) — alpha one, so "all surface", so a pass whose
    // texture was never filled draws exactly what it drew before this term existed.
    // Storing void-ness instead would make that same unbound sampler read "all void" and
    // delete every fan on the map: a plausible wrong frame rather than an error, which is
    // the failure mode the bundle rules exist to keep out.
    //
    // It multiplies the OUTPUT rather than `ours`, so it survives both compositions
    // below and cannot be cancelled by the surface lift's divisor. Nothing about the
    // max, the subtraction or the lift changes — only whether the result lands anywhere.
    float surface = vanillaTexel.a;

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
    if (skyAmbient > 0)
        return fixed4(surface * lightColor.a * excess / (skyAmbient + vanilla), 1);

    return fixed4(surface * excess * lightColor.a, 1);
}
