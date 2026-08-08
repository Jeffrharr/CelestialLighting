using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Soft interop with Ambient Light (f1995.ambientlight) — see DESIGN.md §7b.
//
// Ambient Light patches Verse.GlowGrid.GroundGlowAt to redistribute a fraction of the outdoor sky's
// current glow into roofed cells, graded by BFS distance from the nearest unroofed/unblocked cell:
// roughly `curSkyGlow * passThroughPercent * (1 - depth/maxDepth)`. That is a real, per-cell,
// gameplay-visible number, and their own mouseover readout reports exactly it. But it never reaches
// the screen: §7b (Patch_IndoorSkyOcclusion) forces the *rendered* sky-cover alpha of every enclosed
// roofed cell up toward full occlusion, and does so without ever consulting GroundGlowAt — by design,
// §7b's job is "how much of the sky's colour should this cell show", which has nothing to do with
// gameplay glow UNTIL a mod like this one makes glow a legitimate proxy for it. Investigated and
// confirmed by decompiling AmbientLightFalloff.dll directly (issue #80); the user's reproduction was
// an interior cell reading "46% lit" by Ambient Light's own readout while rendering flat black.
//
// This class supplies the missing input: the same fraction Ambient Light's postfix computes, read
// independently rather than through GroundGlowAt itself, because by the time GroundGlowAt returns it
// has already been Max()'d against ordinary artificial (lamp) light — a value it would be wrong to
// treat as "sky reaching this cell" (see IndoorOcclusionMath.AmbientLightSkyFraction for why that
// distinction matters to the occlusion cap).
//
// NO HARD REFERENCE, same convention as PlanetsmithCompat/AxialTiltCompat: every member is a string
// resolved at runtime, so a build without Ambient Light installed has never heard of it. The formula
// itself is re-derived rather than invoked through reflection (ComputeUnderRoofSkyLight is a private
// implementation detail, a weaker contract than a public field) — see
// IndoorOcclusionMath.AmbientLightSkyFraction, which mirrors ALFUtils.ComputeUnderRoofSkyLight /
// ComputeFalloffNoSky byte-for-byte and is covered by its own [TestCase]s so a future Ambient Light
// update that changes the formula surfaces as a live-A/B mismatch rather than silently drifting.
public static class AmbientLightCompat
{
    private const string PackageId = "f1995.ambientlight";

    private const string MapComponentTypeName = "AmbientLightFalloff.MapComp_AmbientLight";
    private const string ModTypeName = "AmbientLightFalloff.AmbientLightMod";

    // AmbientLightMod.Settings is a public STATIC field (not a property, unlike Planetsmith's world
    // settings) — the mod's single ModSettings instance, shared across every map.
    private const string ModSettingsFieldName = "Settings";
    private const string MaxDepthFieldName = "maxDepth";
    private const string PassThroughPercentFieldName = "passThroughPercent";
    private const string GetDepthMethodName = "GetDepth";

    private static bool bound;
    private static bool triedBind;

    private static Type mapComponentType;
    private static MethodInfo getDepthMethod;
    private static FieldInfo modSettingsField;
    private static FieldInfo maxDepthField;
    private static FieldInfo passThroughPercentField;

    // What is cached is the COMPONENT LOOKUP for the current map, not the depth value — the depth is
    // re-read per cell per call, same reasoning PlanetsmithCompat.CachedWorld gives: a
    // GetComponent(Type) walk is the part worth avoiding, and a WeakReference<Map> cannot keep a
    // discarded map alive after the player leaves it.
    private static readonly System.WeakReference<Map> CachedMap = new System.WeakReference<Map>(null);
    private static object cachedComponent;

    // True only when our own feature flag is on AND Ambient Light is installed. SkyFractionAt already
    // answers 0 for every case this is false, so callers never need a separate branch on it — it is
    // exposed for the settings screen and the probe, the same split PlanetsmithCompat.Active makes.
    public static bool Active => CelestialLightingFeatures.AmbientLightCompat && ModIsActive();

    // Whether Ambient Light is in the load order at all, regardless of our own feature flag — what the
    // settings screen should ask, so a toggle appears for anyone who has it installed.
    public static bool ModIsInstalled => ModIsActive();

    // Their redistributed sky fraction at one cell, in [0, 1]. Answers 0 for every ordinary "no" (mod
    // absent, our compat feature off, no world/map component yet, a cell their BFS never reached i.e.
    // unroofed or beyond maxDepth) — which is exactly the behaviour IndoorOcclusionMath.CapOcclusion
    // already has without this class, so "absent" and "present but zero" compose identically.
    public static float SkyFractionAt(Map map, IntVec3 cell)
    {
        if (!Active || map == null)
            return 0f;

        if (!Bind())
            return 0f;

        object component = ComponentForMap(map);
        if (component == null)
            return 0f;

        if (!TryReadSettings(out int maxDepth, out float passThroughPercent))
            return 0f;

        if (!TryReadDepth(component, cell, out int depth))
            return 0f;

        return IndoorOcclusionMath.AmbientLightSkyFraction(
            depth, map.skyManager.CurSkyGlow, maxDepth, passThroughPercent);
    }

    // Resolves Ambient Light's map component for whatever map is asked about, reusing the last answer
    // while the map is the same object. Null covers both "no such component" (a map loaded before the
    // player installed the mod) and "no map" — neither worth a log line.
    private static object ComponentForMap(Map map)
    {
        if (CachedMap.TryGetTarget(out Map cached) && ReferenceEquals(cached, map))
            return cachedComponent;

        cachedComponent = map.GetComponent(mapComponentType);
        CachedMap.SetTarget(map);
        return cachedComponent;
    }

    private static bool TryReadSettings(out int maxDepth, out float passThroughPercent)
    {
        maxDepth = 0;
        passThroughPercent = 0f;

        try
        {
            object settings = modSettingsField.GetValue(null);
            if (settings == null)
                return false;

            maxDepth = (int)maxDepthField.GetValue(settings);
            passThroughPercent = (float)passThroughPercentField.GetValue(settings);
            return true;
        }
        catch (Exception e)
        {
            Log.WarningOnce(
                "[CelestialLighting] Could not read Ambient Light's settings "
                + $"({e.GetType().Name}: {e.Message}). Indoor occlusion will not account for its "
                + "under-roof brightening this session — this is an upstream change rather than a "
                + "mod-list mistake, please report it.",
                AmbientLightReadWarningKey);
            return false;
        }
    }

    private static bool TryReadDepth(object component, IntVec3 cell, out int depth)
    {
        depth = 0;

        try
        {
            depth = (int)getDepthMethod.Invoke(component, new object[] { cell });
            return true;
        }
        catch (Exception e)
        {
            Log.WarningOnce(
                "[CelestialLighting] Could not read Ambient Light's per-cell depth "
                + $"({e.GetType().Name}: {e.Message}). Indoor occlusion will not account for its "
                + "under-roof brightening this session — this is an upstream change rather than a "
                + "mod-list mistake, please report it.",
                AmbientLightReadWarningKey);
            return false;
        }
    }

    // Verse.Log.WarningOnce keys on an int the caller supplies; an arbitrary constant, unique within
    // this mod, is the convention PlanetsmithCompat also follows.
    private const int AmbientLightReadWarningKey = 0x0AA5_1954;

    // Resolved once and cached, including the negative case: a player without Ambient Light pays one
    // pass over the running mod list at first read and nothing thereafter.
    private static bool Bind()
    {
        if (triedBind)
            return bound;

        triedBind = true;

        if (!ModIsActive())
            return false;

        mapComponentType = AccessTools.TypeByName(MapComponentTypeName);
        if (mapComponentType == null)
            return WarnUnbindable($"its {MapComponentTypeName} type is gone");

        getDepthMethod = AccessTools.Method(mapComponentType, GetDepthMethodName, new[] { typeof(IntVec3) });
        if (getDepthMethod == null)
            return WarnUnbindable($"its {GetDepthMethodName} method is gone");

        Type modType = AccessTools.TypeByName(ModTypeName);
        if (modType == null)
            return WarnUnbindable($"its {ModTypeName} type is gone");

        modSettingsField = AccessTools.Field(modType, ModSettingsFieldName);
        if (modSettingsField == null || !modSettingsField.IsStatic)
            return WarnUnbindable($"its {ModSettingsFieldName} field is gone");

        Type settingsType = modSettingsField.FieldType;
        maxDepthField = AccessTools.Field(settingsType, MaxDepthFieldName);
        if (maxDepthField == null || maxDepthField.FieldType != typeof(int))
            return WarnUnbindable($"its {MaxDepthFieldName} field is gone");

        passThroughPercentField = AccessTools.Field(settingsType, PassThroughPercentFieldName);
        if (passThroughPercentField == null || passThroughPercentField.FieldType != typeof(float))
            return WarnUnbindable($"its {PassThroughPercentFieldName} field is gone");

        Log.Message(
            "[CelestialLighting] Ambient Light detected; indoor occlusion will let its under-roof "
            + "sky brightening reach the screen.");

        bound = true;
        return true;
    }

    // Says the consequence rather than the fault, the same way PlanetsmithCompat's warnings do: the
    // player cannot act on "a field moved", but they can recognise "roofed cells near a door look
    // darker than Ambient Light's own readout says they should" once someone has told them to expect
    // it.
    private static bool WarnUnbindable(string problem)
    {
        Log.Warning(
            $"[CelestialLighting] Ambient Light is installed but {problem}. Indoor occlusion will not "
            + "account for its under-roof brightening, so roofed cells near an opening may look darker "
            + "than its own mouseover readout reports. This is an upstream change rather than a "
            + "mod-list mistake — please report it.");

        ClearBindings();
        return false;
    }

    // Left in the state a never-bound CelestialLighting is in, so a half-resolved type cannot leave a
    // live MemberInfo behind a `bound == false` gate.
    private static void ClearBindings()
    {
        mapComponentType = null;
        getDepthMethod = null;
        modSettingsField = null;
        maxDepthField = null;
        passThroughPercentField = null;
    }

    private static bool ModIsActive()
    {
        foreach (ModContentPack pack in LoadedModManager.RunningMods)
        {
            if (pack.PackageId.Equals(PackageId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
