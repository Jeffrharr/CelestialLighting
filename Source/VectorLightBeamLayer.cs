using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Verse;
using RimWorld;
using Verse.Glow;

namespace CelestialLighting;

// §27 phase 3d: the adapter that samples where light is OWED and draws the beam as its own geometry.
//
// Thin by design, per the repo's pure-core rule: everything about what the mesh looks like lives in
// VectorLightBeamMath, and this file only reads live state (the per-emitter glow arrays, the polygon,
// the roof grid) and hands primitives across. Read that file's header for why the layer exists at all
// — the short version is that phase 3c's arithmetic is correct and the overlay's cell-resolution
// averaging removed two thirds of it before it reached the screen.
//
// WHY IT CAN SHARE THE FAN'S MATERIAL. The gradient texture bakes vanilla's own falloff into alpha
// against U = distance/radius, and the beam's quads carry exactly that U. So the light coming out of
// the door follows the same curve as the light inside the room, which is what makes it read as the
// room's light continuing instead of as a second effect pasted alongside it.
[StaticConstructorOnStartup]
public static class VectorLightBeamLayer
{
    // Sampled once per rebuild and reused, because a radius-10 lamp is 48 sectors x 41 steps and
    // doing that per frame per emitter would cost more than the lighting it corrects.
    private static bool[] owed = new bool[0];

    // The first radius at which each POLYGON RAY owes light. See BuildOwedMesh: without it the
    // beam's mouth is an arc centred on the lamp rather than a chord across the aperture, and the
    // arc bulges back through the wall into the lit room.
    private static float[] rayNear = new float[0];

    // How occluded each ray's own edge is: 0 in the body of the beam, 1 on a ray that bounds it. Feeds
    // the vertex V, i.e. the gradient's penumbra ramp, so the beam fades across its flanks instead of
    // ending at a knife edge. See VectorLightBeamMath.BuildOwedMesh.
    private static float[] rayEdge = new float[0];

    // Whether each sector owes light anywhere along its length, which is what decides where the beam's
    // angular boundary is.
    private static bool[] sectorOwes = new bool[0];

    public static bool Active =>
        CelestialLightingFeatures.VectorLightBeamLayer
        && CelestialLightingFeatures.VectorLightBeamDifferential
        && VectorLightMask.Active;

    public static void Draw(Map map)
    {
        if (!Active || map == null)
            return;

        GlowGridPerLight.Reader reader = GlowGridPerLight.For(map);

        if (reader == null)
            return;

        Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights =
            VectorLightField.LightsFor(map);

        if (lights.Count == 0)
            return;

        CellRect view = Find.CameraDriver.CurrentViewRect;
        float skyGlow = map.skyManager.CurSkyGlow;
        float altitude = AltitudeLayer.VisEffects.AltitudeFor();

        foreach (VectorLightField.LightEntry entry in lights)
            DrawBeam(map, reader, entry, view, skyGlow, altitude);
    }

    private static void DrawBeam(
        Map map, GlowGridPerLight.Reader reader, VectorLightField.LightEntry entry, CellRect view,
        float skyGlow, float altitude)
    {
        // An emitter nothing obstructs owes nothing anywhere: vanilla's flood reached every cell of
        // its circle by the straight path. Skipping it here is the same saving the mask takes, and in
        // open ground it is most of the emitters on the map.
        if (entry.Unobstructed || entry.PolygonDirty || entry.Polygon.Count == 0)
            return;

        if (!Overlaps(view, entry))
            return;

        if (entry.OwedDirty || entry.OwedMesh == null)
            Rebuild(map, reader, entry, altitude);

        if (entry.OwedMesh == null)
            return;

        float strength = StrengthFor(map, entry, skyGlow);

        if (strength <= 0f)
            return;

        Color color = entry.Color;

        entry.BeamProps.SetColor(
            ShaderPropertyIDs.Color, new Color(color.r, color.g, color.b, strength));

        Graphics.DrawMesh(
            entry.OwedMesh, Vector3.zero, Quaternion.identity,
            VectorLightOverlay.MaterialFor(entry.Radius), 0, null, 0, entry.BeamProps);
    }

    // Where does this emitter owe light? One boolean per (sector, step), sampled off vanilla's own
    // per-emitter array.
    //
    // THE TEST IS `delivered == 0`, AND THE NARROWNESS IS THE POINT. This layer takes only the case
    // where vanilla's flood delivered nothing at all — the open door, the whole visible headline —
    // and leaves the partial case, where vanilla bent around a corner and arrived dimmer than a
    // straight line would have, on phase 3c's per-cell path. A partial shortfall is a magnitude and
    // this is a geometric mask; it cannot express one. The two are disjoint by construction
    // (delivered == 0 versus delivered > 0), so they cannot double-count, and VectorLightMask is
    // made to enforce the split rather than trusted to respect it.
    private static void Rebuild(
        Map map, GlowGridPerLight.Reader reader, VectorLightField.LightEntry entry, float altitude)
    {
        entry.OwedDirty = false;
        entry.OwedMesh = null;

        if (!reader.TryResolveEmitter(entry.VanillaKey, out GlowLight light, out UnsafeList<Color32> colors))
            return;

        int steps = VectorLightBeamMath.StepsFor(entry.Radius);
        int sectors = entry.Polygon.Count;

        if (steps <= 0 || sectors <= 0)
            return;

        Grow(ref owed, sectors * steps);
        Grow(ref sectorOwes, sectors);
        GrowFloats(ref rayNear, sectors);
        GrowFloats(ref rayEdge, sectors);

        float lightX = entry.Cell.x + 0.5f;
        float lightZ = entry.Cell.z + 0.5f;

        // Per RAY, not per sector: this is the quantity the quad's corners are clamped to, and a
        // corner sits on a ray rather than in a sector's middle.
        for (int ray = 0; ray < sectors; ray++)
        {
            rayNear[ray] = FirstOwedRadius(
                map, light, colors, lightX, lightZ, entry.Polygon.Angles[ray], steps);
        }

        for (int sector = 0; sector < sectors; sector++)
        {
            float angle = VectorLightBeamMath.SectorMidAngle(entry.Polygon, sector);
            float dx = Mathf.Cos(angle);
            float dz = Mathf.Sin(angle);

            for (int step = 0; step < steps; step++)
            {
                float distance = VectorLightBeamMath.RadiusAtStep(step) - VectorLightBeamMath.MarchStep * 0.5f;
                IntVec3 cell = new IntVec3(
                    Mathf.FloorToInt(lightX + dx * distance), 0,
                    Mathf.FloorToInt(lightZ + dz * distance));

                owed[sector * steps + step] = OwesAt(map, light, colors, cell);
            }
        }

        TaperFarEnds(map, light, colors, lightX, lightZ, entry, steps, sectors);
        MarkEdgeRays(sectors, steps);

        VectorLightMath.LightMesh built = VectorLightBeamMath.BuildOwedMesh(
            lightX, lightZ, entry.Radius, entry.Polygon, owed, steps, rayNear, rayEdge);

        entry.OwedMesh = VectorLightOverlay.Upload(entry.OwedMesh, built, altitude, "CelestialLighting_VectorBeam");
    }

    // Whether vanilla delivered NOTHING to this cell from this emitter.
    //
    // Out of bounds and outside the emitter's own square both answer NO rather than yes. They are the
    // cases where we know least, and the failure modes are not symmetric: a wrong yes paints light
    // onto ground nobody can justify, a wrong no leaves a beam slightly short. Erring short is the
    // one that stays diagnosable.
    private static bool OwesAt(Map map, GlowLight light, UnsafeList<Color32> colors, IntVec3 cell)
    {
        if (!cell.InBounds(map))
            return false;

        // A light-blocking cell is the wall itself. Vanilla stores nothing there and neither should
        // we, or every beam would start half a cell inside the wall it comes through.
        //
        // ASKED THROUGH DoorOcclusionMath, NOT def.blockLight, and the difference is a visible notch.
        // A Building_Door's def.blockLight is true whether or not the door is standing open —
        // vanilla's glow grid never learns a door opened, which is the whole reason §27e exists — so
        // testing the def alone rejects the doorway cell itself and the beam starts one cell out into
        // the open. Measured on the first build of this layer: the door cell read +0 where phase 3c's
        // per-cell path gave it +21, leaving a dark bite out of the beam exactly where it should be
        // brightest. This is the same predicate VectorLightBlockers uses to build the polygon, so the
        // aperture the beam comes through and the aperture the geometry was cut for are the same one.
        Building edifice = map.edificeGrid[cell];
        Building_Door door = edifice as Building_Door;

        bool occludes = edifice?.def != null && DoorOcclusionMath.Occludes(
            edifice.def.blockLight,
            door != null,
            door != null && door.Open,
            CelestialLightingFeatures.VectorLightOpenDoors);

        if (occludes)
            return false;

        if (!light.AffectedRect.Contains(cell))
            return false;

        int local = light.WorldToLocalIndex(cell);

        if (local < 0 || local >= colors.Length)
            return false;

        Color32 delivered = colors[local];

        if (delivered.r != 0 || delivered.g != 0 || delivered.b != 0)
            return false;

        // AND A STRAIGHT LINE WOULD HAVE DELIVERED SOMETHING. `delivered == 0` alone is not the
        // question, and getting that wrong drew a ragged bright crescent along the far wall of the
        // lit room on the first build of this layer.
        //
        // The reason is underflow, not geometry. At the rim of the lamp's reach vanilla's own value
        // truncates to zero -- (int)(184 * F) is 0 once F drops far enough -- so the cells in that
        // last ring store nothing while being in perfectly plain sight. `delivered == 0` calls that
        // an unpaid debt, the layer paints the ring, and because this is an ADDITIVE pass over an
        // already-lit floor a value too small to matter on black ground is clearly visible there.
        //
        // The honest test is the formula's own: owed = ours - delivered > 0. At the rim `ours`
        // underflows to zero for exactly the same reason `delivered` did, so the two agree and
        // nothing is owed. Through a doorway `ours` is 58 against a delivered 0, and the beam draws.
        // Same predicate as VectorLightMask's, which is what keeps the two paths from disagreeing
        // about where the light is owed while agreeing about how much.
        int dx = cell.x - light.position.x;
        int dz = cell.z - light.position.z;

        float falloff = VectorLightMath.VanillaFalloff(
            VectorLightMath.VanillaGlowDistance(dx, dz), light.glowRadius);

        VectorLightMath.OurLightAt(
            light.glowColor.r, light.glowColor.g, light.glowColor.b, falloff,
            out int ourR, out int ourG, out int ourB);

        return ourR > 0 || ourG > 0 || ourB > 0;
    }

    // The same daylight attenuation the fan uses, for the same reason: this is an ADDITIVE pass above
    // the sky's multiply, so with nothing to scale it a beam would be as bright at noon as at
    // midnight. Asks the roof over the EMITTER rather than the global sky, because a roofed lamp is
    // competing with a roofed cell's fraction of the sky and not with the sky itself.
    private static float StrengthFor(Map map, VectorLightField.LightEntry entry, float skyGlow)
    {
        bool sheltered = map.roofGrid.Roofed(entry.Cell);

        return VectorLightMath.DaylightScale(sheltered ? 0f : skyGlow)
            * VectorLightSettings.OwedLayerStrength;
    }

    private static bool Overlaps(CellRect view, VectorLightField.LightEntry entry)
    {
        int reach = Mathf.CeilToInt(entry.Radius) + 1;

        return entry.Cell.x + reach >= view.minX && entry.Cell.x - reach <= view.maxX
            && entry.Cell.z + reach >= view.minZ && entry.Cell.z - reach <= view.maxZ;
    }

    // Carry every sector's LAST run out to the polygon boundary, through the region where vanilla's
    // falloff has underflowed to nothing.
    //
    // WHY THE BEAM NEEDED THIS. `ours` is an integer, so it hits zero a cell or two before the
    // emitter's actual reach — (int)(184 * F) is 0 once F drops below about 1/184. The owed test goes
    // false there and the beam simply STOPPED, mid-air, at a hard vertical cut. Extending the run lets
    // the baked gradient do what it is for: U walks on toward 1, alpha walks down to nothing, and the
    // beam fades out instead of being chopped.
    //
    // EXTENDS AN EXISTING RUN, NEVER STARTS ONE. That distinction is the whole safety of it and it is
    // the same underflow that drew a bright crescent through the lit room on the first build of this
    // layer: at the rim INSIDE the room the cells also read delivered 0 and ours 0. The difference is
    // that no run reaches them, because everything closer to the lamp along those rays was paid for.
    // So a sector with nothing owed stays empty however far its polygon reaches.
    private static void TaperFarEnds(
        Map map, GlowLight light, UnsafeList<Color32> colors, float lightX, float lightZ,
        VectorLightField.LightEntry entry, int steps, int sectors)
    {
        for (int sector = 0; sector < sectors; sector++)
        {
            int last = -1;

            for (int step = 0; step < steps; step++)
            {
                if (owed[sector * steps + step])
                    last = step;
            }

            if (last < 0)
                continue;

            float angle = VectorLightBeamMath.SectorMidAngle(entry.Polygon, sector);
            float dx = Mathf.Cos(angle);
            float dz = Mathf.Sin(angle);

            for (int step = last + 1; step < steps; step++)
            {
                float distance =
                    VectorLightBeamMath.RadiusAtStep(step) - VectorLightBeamMath.MarchStep * 0.5f;

                IntVec3 cell = new IntVec3(
                    Mathf.FloorToInt(lightX + dx * distance), 0,
                    Mathf.FloorToInt(lightZ + dz * distance));

                // Stop the moment vanilla has paid again. Past a lit cell is somebody else's light and
                // extending into it would double what is already there.
                if (!InBoundsUnlit(map, light, colors, cell))
                    break;

                owed[sector * steps + step] = true;
            }
        }
    }

    // Vanilla delivered nothing here AND the cell is somewhere we are allowed to draw. Deliberately
    // does NOT ask whether a straight line still owes anything, which is exactly what separates it
    // from OwesAt: this is the taper's test, and the taper's whole job is the region past underflow.
    private static bool InBoundsUnlit(
        Map map, GlowLight light, UnsafeList<Color32> colors, IntVec3 cell)
    {
        if (!cell.InBounds(map) || !light.AffectedRect.Contains(cell))
            return false;

        Building edifice = map.edificeGrid[cell];
        Building_Door door = edifice as Building_Door;

        if (edifice?.def != null && DoorOcclusionMath.Occludes(
                edifice.def.blockLight, door != null, door != null && door.Open,
                CelestialLightingFeatures.VectorLightOpenDoors))
        {
            return false;
        }

        int local = light.WorldToLocalIndex(cell);

        if (local < 0 || local >= colors.Length)
            return false;

        Color32 delivered = colors[local];

        return delivered.r == 0 && delivered.g == 0 && delivered.b == 0;
    }

    // A ray bounds the beam when the sectors either side of it disagree about owing light. Those are
    // the rays that get the penumbra ramp; everything in the beam's body stays fully lit.
    //
    // A NARROW BEAM IS ALL EDGE, and that is correct rather than a degenerate case. Through a one-cell
    // door the beam is a few sectors wide, so both flanks ramp and the brightest line runs up the
    // middle — which is what a shaft of light through a gap actually looks like.
    private static void MarkEdgeRays(int sectors, int steps)
    {
        for (int sector = 0; sector < sectors; sector++)
        {
            sectorOwes[sector] = false;

            for (int step = 0; step < steps; step++)
            {
                if (owed[sector * steps + step])
                    sectorOwes[sector] = true;
            }
        }

        for (int ray = 0; ray < sectors; ray++)
        {
            int before = (ray - 1 + sectors) % sectors;

            rayEdge[ray] = sectorOwes[before] == sectorOwes[ray] ? 0f : 1f;
        }
    }

    // How far along this ray before vanilla stops having paid. Returns a radius past the emitter's
    // reach when the ray owes nothing anywhere, which clamps that corner out to the polygon and
    // collapses the quad rather than drawing a wedge nobody asked for.
    private static float FirstOwedRadius(
        Map map, GlowLight light, UnsafeList<Color32> colors,
        float lightX, float lightZ, float angle, int steps)
    {
        float dx = Mathf.Cos(angle);
        float dz = Mathf.Sin(angle);

        for (int step = 0; step < steps; step++)
        {
            float distance =
                VectorLightBeamMath.RadiusAtStep(step) - VectorLightBeamMath.MarchStep * 0.5f;

            IntVec3 cell = new IntVec3(
                Mathf.FloorToInt(lightX + dx * distance), 0,
                Mathf.FloorToInt(lightZ + dz * distance));

            if (OwesAt(map, light, colors, cell))
                return step * VectorLightBeamMath.MarchStep;
        }

        return float.MaxValue;
    }

    private static void GrowFloats(ref float[] buffer, int needed)
    {
        if (buffer.Length < needed)
            buffer = new float[needed];
    }

    private static void Grow(ref bool[] buffer, int needed)
    {
        if (buffer.Length < needed)
            buffer = new bool[needed];
    }
}
