using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §27's per-map registry: what is emitting light, and the mesh each emitter currently casts.
//
// TWO KINDS OF STALENESS, KEPT SEPARATE ON PURPOSE. A light's IDENTITY can change — one is built,
// switched off, refuelled, recoloured — and its GEOMETRY can change under a light that never moved,
// because somebody built a wall inside its radius. Collapsing those into one dirty flag means every
// lamp toggle anywhere rebakes every polygon on the map, which is precisely the shape §16 records
// killing the across-map tilt ramp over. So a glower registration marks the ROSTER dirty and a
// blocker write marks only the polygons that cell can reach.
//
// NO TIMER, NO POLL, NO SELF-SCHEDULED WORK. Issue #48 states this outright — "if the design ends up
// needing a timer, it is the wrong design" — and §16 has the measurement behind it:
// MapComponent_SunShadowAxis cost only +3.4 microseconds per regenerate and still dominated the live
// profile, purely by provoking ~720 whole-map rebakes a game day. Everything here is invalidated by
// something the player did.
//
// Cached per map.uniqueID rather than in a MapComponent, following OpenSkyMask: Map.ExposeComponents
// scribes a permanent node per component, so a component deleted later logs two red errors per map
// forever (Source/MapComponent_SunShadowAxis.cs is the tombstone). For a prototype that may not
// survive its own live A/B, leaving no save-file residue is the only responsible choice.
public static class VectorLightField
{
    // One emitter and the polygon it currently throws. Position, radius and colour are snapshots,
    // re-read on resync — the same thing Verse.Glow.GlowLight does, and for the same reason: a light
    // that moves or recolours goes through deregister/reregister in vanilla, so a snapshot cannot go
    // stale without the roster being marked dirty anyway.
    public sealed class LightEntry
    {
        public IntVec3 Cell;
        public float Radius;
        public Color Color;
        public Mesh Mesh;
        public MaterialPropertyBlock Props;
        public bool GeometryDirty = true;

        // How vanilla's own GlowGrid identifies this same emitter — a thing id for a glower, a cell
        // index for glowing terrain, with the terrain flag folded in because the two number spaces
        // overlap. §27 phase 3 needs it to ask what THIS light delivers to a cell, as opposed to what
        // everything delivers, which is the difference between subtracting our own lamp back out and
        // subtracting somebody else's mod along with it.
        public long VanillaKey;

        // The visibility polygon, cached so the two consumers can share one build. §27 phase 3 reads
        // it during a section regenerate, where no mesh is being built at all, so it cannot ride on
        // the mesh the way it used to.
        public VectorLightMath.LightPolygon Polygon;

        // Kept separate from GeometryDirty rather than folded into it because the two are consumed
        // by different subsystems at different times: the draw clears GeometryDirty when it rebuilds
        // a mesh, and a mask running with the draw switched off would then never rebuild the polygon
        // at all. Both are set together wherever the world changes.
        public bool PolygonDirty = true;

        // The polygon's cell coverage, baked with it. See VectorLightMath.BuildCoverage: computing
        // this per section instead measured 239 us per section against the crossfade's 20.
        public byte[] Coverage;

        // Radius the coverage grid was baked at, in cells. Held rather than recomputed because the
        // lookup needs it and Mathf.CeilToInt on every cell of every section is exactly the sort of
        // per-cell arithmetic this cache exists to remove.
        public int CoverageRadius;

        // Whether this emitter shadows anything at all — no ray stopped short of the radius. The
        // bake skips such an emitter outright rather than looking its grid up cell by cell.
        public bool Unobstructed;

        // The mesh as the pure core built it, kept rather than discarded after upload. Phase 6
        // needs the vertex POSITIONS again after the fact — to resample vanilla's glow when the
        // lighting around this light changed but its geometry did not — and reading them back off
        // the Unity Mesh allocates a fresh array every time it is asked.
        public VectorLightMath.LightMesh Built;

        // A THIRD KIND OF STALENESS, and the reason it is not folded into GeometryDirty. Under the
        // per-fragment max each vertex carries vanilla's delivered glow at that point, and that
        // value moves whenever any OTHER light near this one changes — a lamp switched on across
        // the room leaves this light's polygon identical and its samples wrong. Reusing
        // GeometryDirty for it would rebake the polygon too, which is exactly the
        // every-toggle-rebakes-everything cost this class exists to avoid; resampling on its own
        // rewrites one UV channel and no geometry.
        public bool SampleDirty = true;

        // Vanilla's delivered glow over this emitter's own square, one texel per cell, for the
        // fragment program to look up per fragment. Per emitter and so cannot live on the shared
        // per-radius material; see VectorLightShader.SetVanillaTexture.
        public Texture2D VanillaField;

        // The polygon's area in square cells, kept for the probes: it is the one number that says
        // "the lit region changed shape" without going anywhere near a pixel. Issue #3 records two
        // wrong conclusions drawn from pixel measurement on exactly this kind of effect.
        public float LitArea;
    }

    private sealed class MapLights
    {
        public readonly Dictionary<object, LightEntry> Entries = new Dictionary<object, LightEntry>();
        public bool RosterDirty = true;
    }

    private static readonly Dictionary<int, MapLights> ByMap = new Dictionary<int, MapLights>();

    public static void MarkRosterDirty(Map map)
    {
        if (map == null || !ByMap.TryGetValue(map.uniqueID, out MapLights lights))
            return;

        lights.RosterDirty = true;

        // A light was built, removed, recoloured or switched — so vanilla's glow has moved under
        // every light that can see the same cells, and their samples are stale even though their
        // polygons are not. Marking all of them is deliberately blunt: the roster changing is rare,
        // resampling is a UV rewrite rather than a rebake, and working out which lights overlap the
        // changed one would need the position of a light that may already be gone.
        foreach (LightEntry entry in lights.Entries.Values)
            entry.SampleDirty = true;
    }

    // A blocker appeared or vanished at `cell`: every light that can see that cell now throws a
    // different shape, and no other light is affected at all.
    public static void MarkGeometryDirtyAround(Map map, IntVec3 cell)
    {
        if (map == null || !ByMap.TryGetValue(map.uniqueID, out MapLights lights))
            return;

        foreach (LightEntry entry in lights.Entries.Values)
        {
            // Squared distance against squared radius, so a wall built across the map costs one
            // multiply per light rather than a square root.
            float dx = entry.Cell.x - cell.x;
            float dz = entry.Cell.z - cell.z;
            float reach = entry.Radius + 1f;

            if (dx * dx + dz * dz <= reach * reach)
            {
                entry.GeometryDirty = true;
                entry.PolygonDirty = true;

                // A wall appearing or vanishing also rewrites vanilla's geodesic distances through
                // that cell, so the samples go with the geometry. A rebuild resamples anyway; this
                // is for the case where the rebuild is skipped because the light is off-screen.
                entry.SampleDirty = true;
            }
        }
    }

    // Everything currently emitting on this map, resynced from vanilla's own sets if anything has
    // registered or deregistered since the last call.
    // Build every dirty polygon on this map, once per frame, OUTSIDE the section bake.
    //
    // WHY IT IS HOISTED. §27 phase 3 reads polygons during a section regenerate, and building one
    // there put geometry construction inside the bake: a whole-map rebake measured 49 ms in
    // VectorLightMask.Apply while everything Apply calls summed to 6, and the missing 43 was
    // EnsurePolygon running under CollectReaching. The crossfade builds the same polygons in the
    // DRAW path, so its own bake row never contained them — which made the two rows a comparison
    // between different quantities rather than between two implementations.
    //
    // Called once per frame from the draw, so by the time any section bakes, every polygon it might
    // ask for is already there. The work is not removed — it is the same builds on the same cadence
    // — it simply stops being charged to, and serialised inside, the regenerate.
    // Returns whether it built anything, because the caller has to act on that.
    //
    // A SECTION BAKED WHILE A POLYGON WAS STILL DIRTY SKIPPED THAT EMITTER, and nothing would ever
    // dirty the section again — so "the mask catches up next frame" was permanently false and the
    // feature rendered pixel-identical to vanilla with every probe healthy. Whoever builds the
    // polygons has to re-dirty the map afterwards, once, so the sections bake again with them ready.
    public static bool EnsurePolygons(Map map)
    {
        if (map == null)
            return false;

        bool built = false;

        foreach (LightEntry entry in LightsFor(map))
        {
            if (entry.PolygonDirty || entry.Polygon.Count == 0)
            {
                EnsurePolygon(map, entry);
                built = true;
            }
        }

        return built;
    }

    // The visibility polygon for one emitter, built if the world has changed under it since the last
    // time anybody asked. Shared by the draw and by §27 phase 3's mask so the two cannot disagree
    // about the shape of a shadow — a disagreement would show as the mask darkening cells the draw
    // had just lit.
    public static void EnsurePolygon(Map map, LightEntry entry)
    {
        if (!entry.PolygonDirty && entry.Polygon.Count > 0)
            return;

        VectorLightMath.Segment[] segments =
            VectorLightBlockers.SegmentsAround(map, entry.Cell, entry.Radius);

        entry.Polygon = VectorLightMath.Build(
            entry.Cell.x + 0.5f, entry.Cell.z + 0.5f, entry.Radius, segments,
            VectorLightMath.DefaultBaseRayCount);

        // Baked alongside the polygon, on the same cadence and for the same reason: both change only
        // when somebody builds or removes a wall in range, and both are asked for once per cell of
        // every section that overlaps this emitter.
        entry.CoverageRadius = Mathf.CeilToInt(entry.Radius);
        entry.Coverage = VectorLightMath.BuildCoverage(
            entry.Polygon, entry.Cell.x, entry.Cell.z, entry.CoverageRadius,
            VectorLightMath.DefaultCoverageSamples);
        entry.Unobstructed = VectorLightMath.IsUnobstructed(entry.Polygon, entry.Radius);

        entry.PolygonDirty = false;
    }

    public static Dictionary<object, LightEntry>.ValueCollection LightsFor(Map map)
    {
        MapLights lights = EnsureMap(map);

        if (lights.RosterDirty)
            Resync(map, lights);

        return lights.Entries.Values;
    }

    // Drops every mesh on every map. Called when the feature is switched off, so an off run holds no
    // GPU memory and — more importantly for the harness — leaves nothing behind that could still be
    // drawn and quietly contaminate the A/B baseline.
    public static void ClearAll()
    {
        foreach (MapLights lights in ByMap.Values)
        {
            foreach (LightEntry entry in lights.Entries.Values)
                DestroyMesh(entry);

            lights.Entries.Clear();
            lights.RosterDirty = true;
        }
    }

    private static MapLights EnsureMap(Map map)
    {
        if (!ByMap.TryGetValue(map.uniqueID, out MapLights lights))
        {
            lights = new MapLights();
            ByMap[map.uniqueID] = lights;
        }

        return lights;
    }

    // Rebuilds the roster from GlowGrid's live sets, keeping the mesh of anything that has not moved
    // or changed size. Keeping those is what makes a lamp toggle cost one polygon rather than all of
    // them: the roster is dirty, but every other light's geometry is not.
    private static void Resync(Map map, MapLights lights)
    {
        lights.RosterDirty = false;

        HashSet<object> seen = new HashSet<object>();
        AddGlowers(map, lights, seen);
        AddTerrain(map, lights, seen);
        RemoveUnseen(lights, seen);
    }

    private static void AddGlowers(Map map, MapLights lights, HashSet<object> seen)
    {
        HashSet<CompGlower> glowers = GlowGridAccess.LitGlowers(map.glowGrid);

        if (glowers == null)
            return;

        foreach (CompGlower glower in glowers)
        {
            // A glower can be in the set while its parent is between maps (gravships) or mid-despawn.
            // Filtering here rather than guarding at draw time keeps the roster to things that
            // genuinely exist on this map.
            if (BelongsTo(glower, map))
            {
                ColorInt glow = glower.GlowColor;
                Upsert(lights, seen, glower.parent.thingIDNumber, glower.parent.Position,
                    glower.GlowRadius, glow.r / 255f, glow.g / 255f, glow.b / 255f,
                    GlowGridPerLight.Reader.KeyFor(glower.parent.thingIDNumber, isTerrain: false));
            }
        }
    }

    private static bool BelongsTo(CompGlower glower, Map map)
    {
        Thing parent = glower?.parent;
        return parent != null && parent.Map == map;
    }

    // Glowing terrain is a separate registration path off TerrainDef.glowRadius, with no CompGlower
    // anywhere. It has to be here because §27 suppresses vanilla's render of ALL artificial light:
    // a version that only knew about glowers would put glowing moss out entirely the moment the
    // feature was switched on, which is a regression rather than a missing feature.
    private static void AddTerrain(Map map, MapLights lights, HashSet<object> seen)
    {
        HashSet<IntVec3> litTerrain = GlowGridAccess.LitTerrain(map.glowGrid);

        if (litTerrain == null)
            return;

        foreach (IntVec3 cell in litTerrain)
        {
            TerrainDef terrain = map.terrainGrid.TerrainAt(cell);

            if (terrain != null && terrain.glowRadius > 0f)
            {
                ColorInt glow = terrain.glowColor;
                Upsert(lights, seen, cell, cell, terrain.glowRadius,
                    glow.r / 255f, glow.g / 255f, glow.b / 255f,
                    GlowGridPerLight.Reader.KeyFor(map.cellIndices.CellToIndex(cell), isTerrain: true));
            }
        }
    }

    private static void Upsert(
        MapLights lights, HashSet<object> seen, object key,
        IntVec3 cell, float radius, float r, float g, float b, long vanillaKey)
    {
        seen.Add(key);

        if (!lights.Entries.TryGetValue(key, out LightEntry entry))
        {
            entry = new LightEntry { Props = new MaterialPropertyBlock() };
            lights.Entries[key] = entry;
        }

        // Only a move or a resize invalidates the polygon. A recolour does not — the shape is
        // identical and the colour rides on the material, so a colour-picker lamp being retinted
        // costs nothing but a property block write.
        if (entry.Cell != cell || entry.Radius != radius)
        {
            entry.GeometryDirty = true;
            entry.PolygonDirty = true;
        }

        entry.Cell = cell;
        entry.Radius = radius;
        entry.VanillaKey = vanillaKey;

        float scale = VectorLightMath.PeakScale(r, g, b);
        entry.Color = new Color(r * scale, g * scale, b * scale, 1f);
    }

    private static void RemoveUnseen(MapLights lights, HashSet<object> seen)
    {
        List<object> gone = null;

        foreach (KeyValuePair<object, LightEntry> pair in lights.Entries)
        {
            if (!seen.Contains(pair.Key))
            {
                gone = gone ?? new List<object>();
                gone.Add(pair.Key);
            }
        }

        if (gone == null)
            return;

        foreach (object key in gone)
        {
            DestroyMesh(lights.Entries[key]);
            lights.Entries.Remove(key);
        }
    }

    private static void DestroyMesh(LightEntry entry)
    {
        if (entry.Mesh != null)
            Object.Destroy(entry.Mesh);

        entry.Mesh = null;

        // The vanilla field goes with the mesh. Both are unmanaged Unity objects that the GC will
        // not collect on its own, and an emitter is dropped from the roster whenever a lamp is
        // deconstructed or the whole field is cleared by a settings toggle — which on a large colony
        // is enough textures to matter if they are only ever created.
        if (entry.VanillaField != null)
            Object.Destroy(entry.VanillaField);

        entry.VanillaField = null;
    }
}
