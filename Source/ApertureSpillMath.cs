using System;

namespace CelestialLighting;

// The wash a doorway throws into the room beyond it, as a SECOND emitter standing in the opening
// rather than as anything vanilla is asked to compute.
//
// WHY THIS EXISTS. Light through open doors currently gets its wash by clearing vanilla's own
// light-blocker bit for the door cell, so vanilla's flood arrives through the opening alongside our
// beam. That works and it is measured, and it is also the single most expensive thing the subsystem
// does: writing the bit dirties the cell, re-floods every light that can see it, and regenerates the
// lighting overlay — where roughly 1.2 ms of mask is spent per section. Measured on the door storm,
// it is the difference between 7.4 and 1.9 lighting-overlay regenerates a frame and between 15.8 and
// 7.6 ms of mod per frame. It is also a gameplay-light write, which is the one kind of change this
// mod otherwise refuses to make for a purely visual reason.
//
// THE OBSERVATION THIS RESTS ON. Vanilla's flood is Dijkstra on the cell lattice, so the light
// arriving at a cell beyond a one-cell doorway travelled a path that passed THROUGH that doorway
// cell — there is nowhere else for it to go. Its brightness is therefore the falloff curve evaluated
// at (distance to the doorway) + (distance from the doorway), which is exactly what a second emitter
// standing in the opening would produce if it continued the first one's curve instead of restarting
// it. So the wash is not an approximation of vanilla's model here; for a single-cell aperture it is
// the same arithmetic reached from the other end.
//
// WHERE IT STOPS BEING THE SAME, stated because it is the thing to look for in a capture. Vanilla's
// geodesic keeps bending after the doorway, so a cell tucked behind an internal corner in the far
// room still receives light by a path that wraps around it. This emitter uses straight-line
// visibility from the opening, so it lights what the doorway can SEE and nothing else. One bounce,
// not many. That difference is the whole of vector lighting's argument with the flood, so keeping it
// here is the point rather than a shortfall — but it means the two agree closely in an open room and
// diverge in a cluttered one, which is what a preview has to be read for.
//
// COMBINED WITH MAX, AND THE TRIANGLE INEQUALITY IS WHY THAT IS EXACT. A cell reachable directly
// from the lamp is never dimmer by the direct route than by the doorway: d(L,C) <= d(L,A) + d(A,C)
// for any A, and the falloff curve is monotonically decreasing, so the direct value dominates
// wherever both exist. Taking the max therefore needs no explicit suppression of the overlap, and in
// particular the spill emitter may radiate a full circle — back into the room the lamp is already
// standing in — without brightening a single cell there. That is what lets this be a plain second
// fan instead of a half-plane clipped to the far side of the wall.
//
// The corollary is a real constraint on the caller: this is only correct under a composition that
// takes a maximum. Summed into a plain additive pass the overlap would be counted twice and the
// doorway would read as a bright bar. See CelestialLightingFeatures.VectorLightApertureSpill.
public static class ApertureSpillMath
{
    // How far the spill still reaches, in cells, once the light has spent d0 getting to the opening.
    //
    // The curve is not restarted at the doorway, so the reach is what is LEFT of the lamp's radius
    // rather than a radius of its own. A doorway at the very edge of a lamp's reach throws nothing,
    // which is both correct and the case that decides whether this emitter is worth building at all.
    public static float ResidualReach(float radius, float distanceToAperture)
    {
        if (radius <= 0f)
        {
            return 0f;
        }

        float reach = radius - Math.Max(distanceToAperture, 0f);
        return reach > 0f ? reach : 0f;
    }

    // Whether there is anything left to draw. Named rather than left as a `> 0` at the call site
    // because the caller's decision is "build a second emitter or do not", and that is a question
    // worth being able to answer without reading the arithmetic.
    public static bool Spills(float radius, float distanceToAperture)
    {
        return ResidualReach(radius, distanceToAperture) > 0f;
    }

    // Where on the SHARED falloff gradient the opening itself sits, as the texture coordinate the
    // existing fan already indexes by.
    //
    // REUSING THE LAMP'S OWN GRADIENT IS THE POINT. VectorLightMath.BuildMesh writes U =
    // distance/radius and the draw looks the falloff up from a 1-D texture baked per radius, so a
    // spill that starts partway along that same texture needs no gradient of its own — no second
    // material, no per-door texture, nothing added to the per-emitter upload the frame budget
    // already accounts for. It is the same curve, entered late.
    public static float ApertureU(float radius, float distanceToAperture)
    {
        if (radius <= 0f)
        {
            return 1f;
        }

        float u = Math.Max(distanceToAperture, 0f) / radius;
        return u < 1f ? u : 1f;
    }

    // The texture coordinate for a spill vertex `distanceFromAperture` cells out from the opening.
    //
    // This is the whole model in one line: the two distances ADD, and the sum is read off the lamp's
    // curve. A spill vertex at the residual reach lands exactly on U = 1, which is where the gradient
    // goes to zero, so the wash fades out at the lamp's true radius rather than at a radius of its
    // own — the seam at the doorway and the rim out in the far room are both continuous by
    // construction instead of by tuning.
    public static float SpillU(float radius, float distanceToAperture, float distanceFromAperture)
    {
        if (radius <= 0f)
        {
            return 1f;
        }

        float travelled = Math.Max(distanceToAperture, 0f) + Math.Max(distanceFromAperture, 0f);
        float u = travelled / radius;
        return u < 1f ? u : 1f;
    }

    // What the wash is worth at the opening, on the same 0..1 scale the lamp's own fan uses.
    //
    // Handed out separately from the geometry because the caller needs it BEFORE deciding to build
    // anything: a doorway the light barely reaches throws a wash nobody can see, and a second
    // polygon, coverage grid and mesh for it is a per-frame cost paid for nothing. The caller's
    // threshold is a look question and belongs with the caller; this only answers what the value is.
    public static float ApertureStrength(float radius, float distanceToAperture, float falloffAtAperture)
    {
        if (!Spills(radius, distanceToAperture))
        {
            return 0f;
        }

        return falloffAtAperture < 0f ? 0f : falloffAtAperture;
    }

    // How far past the opening the wash radiates from, in cells.
    //
    // WHY IT IS NOT ZERO, and the preview is what found this. An emitter sitting exactly in the
    // doorway is COPLANAR with the wall it stands in, so the jambs on either side clip it at grazing
    // incidence and the cells hugging the far wall next to the opening come out dark. Vanilla's
    // flood fills them, because it propagates cell to cell along the wall face and does not care
    // about angles. The result is a pinch — a bowtie waist at the opening — visible in
    // window_spill_abc.png before this existed, and it reads as the wash being pushed away from the
    // wall rather than as an occlusion artefact.
    //
    // Half a cell puts the origin at the far MOUTH of the doorway instead of its middle, which is
    // where the light genuinely re-radiates from: it has already passed the jambs by then. It is a
    // geometric statement about where the aperture ends and not a tuning constant, which is why it
    // is exactly half a cell rather than a number somebody liked the look of.
    public const float DefaultSpillPush = 0.5f;

    // Where the wash radiates from: the aperture centre, pushed away from the light by `push`.
    //
    // ALONG THE LIGHT'S OWN BEARING, so the push is always through the opening rather than along it.
    // A door is a hole in a wall and the light arrives at some angle to it; pushing along the wall's
    // normal would need to know which way the wall runs, and would be wrong for a light approaching
    // obliquely. The bearing from the light is the direction the light was already travelling, which
    // is the direction it continues in.
    //
    // A light standing exactly on the aperture has no bearing to push along, so the origin stays put.
    // That is degenerate rather than an error — a lamp in a doorway lights both rooms directly and
    // has no wash to throw.
    public static void SpillOrigin(
        float lightX, float lightZ, float apertureX, float apertureZ, float push,
        out float originX, out float originZ)
    {
        float dx = apertureX - lightX;
        float dz = apertureZ - lightZ;
        float distance = (float)Math.Sqrt(dx * dx + dz * dz);

        if (distance <= 0f)
        {
            originX = apertureX;
            originZ = apertureZ;
            return;
        }

        originX = apertureX + dx / distance * push;
        originZ = apertureZ + dz / distance * push;
    }

    // The centre of an aperture spanning cells [minCell, maxCell] on one axis, in world coordinates.
    //
    // Cell centres sit at integer + 0.5, so a single-cell doorway at x = 30 radiates from x = 30.5
    // and a two-cell one from x = 31.0. Getting this wrong puts the wash half a cell off the opening,
    // which reads as the doorway being slightly askew rather than as an arithmetic error — the class
    // of defect that survives review because nobody can name what is wrong with the picture.
    public static float ApertureCentre(int minCell, int maxCell)
    {
        return (minCell + maxCell + 1) * 0.5f;
    }
}
