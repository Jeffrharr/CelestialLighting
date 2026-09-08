using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace CelestialLighting.Probes;

// A scenario step that turns the current map into an "As above, So below II" BANDED map, so the
// interop in AsAboveSoBelowCompat can be exercised live instead of only by contract tests.
//
// WHY A STEP RATHER THAN A FIXTURE. Their bands are normally laid down by their own level
// generation, which needs their world settings and a generated descent — none of which a scenario
// can ask for. But `ABBandMap` is an ordinary MapComponent, so RimWorld instantiates one on every
// map as soon as their assembly is loaded, and it exposes a public
// `Setup(int bandCount, int bandHeight, int surfaceBand)` that is the single thing standing between
// "an ordinary map" and `Banded => bandCount > 1 && bandHeight > 0`. Calling it is a two-line step
// and it puts the real layer on the real map.
//
// WHY NOT ABBands.Register / ABBandLayout.TestRect, which looks purpose-built for exactly this.
// It is a trap, and the trap is quiet. `ABBands.Banded(map)` answers true for a registered spike
// layout — so their Patch_LightingOverlay_ABSuppressOnBanded hides vanilla's overlay and our
// OwnsOverlay stands our postfixes down — but `SectionLayer_ABBelowLighting.Regenerate` gates on
// `ABBands.CompOf(map).Banded` instead, which a spike layout does not move. The result is a map
// with vanilla's overlay suppressed and NOTHING drawing in its place: every probe reads a plausible
// dark number and the scenario passes while measuring a state no player can reach.
//
// THIS STEP IS DEV-ONLY AND DELIBERATELY DESTRUCTIVE to the map it runs on. It reconfigures a band
// layout under a map that was not generated with one, which is fine for a throwaway harness fixture
// and is not something to do to a colony — hence LiveCallable false, and hence the Map residue.
public sealed class AsAboveSoBelowBandStep : IStepSpec
{
    public const string TypeName = "AsAboveSoBelowBand";

    internal const string BandMapTypeName = "AsAboveSoBelow.ABBandMap";

    public string Type => TypeName;

    // It rewrites the map's band layout and dirties every section. Nothing restores that, so a
    // following scenario in the same load must get a fresh map.
    public ScenarioResidue Residue => ScenarioResidue.Map;

    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string error) =>
        TryReadLayout(args, out _, out _, out _, out error);

    // Spelled out here rather than through the harness's internal ArgReader, matching
    // SetTimeSpeedStep. Shared with the action so load-time validation and execution cannot disagree
    // about what a valid layout is.
    internal static bool TryReadLayout(
        IReadOnlyDictionary<string, string> args,
        out int bandCount, out int bandHeight, out int surfaceBand, out string error)
    {
        bandCount = 0;
        bandHeight = 0;
        surfaceBand = 0;

        if (!TryReadInt(args, "bandCount", out bandCount, out error))
            return false;

        if (bandCount < 2)
        {
            // Their own Banded property requires > 1. Accepting 1 here would produce a step that
            // reports success and leaves the map unbanded, which is the failure this whole file
            // exists to avoid.
            error = $"'bandCount' must be at least 2 to make a map banded (got {bandCount})";
            return false;
        }

        if (!TryReadInt(args, "bandHeight", out bandHeight, out error))
            return false;

        if (bandHeight <= 0)
        {
            error = $"'bandHeight' must be positive (got {bandHeight})";
            return false;
        }

        // Optional: which band is the surface. Defaults to 0, their own default.
        if (args.ContainsKey("surfaceBand") && !TryReadInt(args, "surfaceBand", out surfaceBand, out error))
            return false;

        error = null;
        return true;
    }

    private static bool TryReadInt(
        IReadOnlyDictionary<string, string> args, string key, out int value, out string error)
    {
        value = 0;

        if (!args.TryGetValue(key, out string raw) || string.IsNullOrWhiteSpace(raw))
        {
            error = $"'{key}' is required";
            return false;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"'{key}' must be an integer (got '{raw}')";
            return false;
        }

        error = null;
        return true;
    }
}

public sealed class AsAboveSoBelowBandAction : IStepAction
{
    public string Type => AsAboveSoBelowBandStep.TypeName;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!AsAboveSoBelowBandStep.TryReadLayout(
                args, out int bandCount, out int bandHeight, out int surfaceBand, out string error))
            return StepOutcome.Fail(error);

        Map map = Find.CurrentMap;

        if (map == null)
            return StepOutcome.Fail("no current map — AsAboveSoBelowBand needs a game in progress");

        Type bandMapType = AccessTools.TypeByName(AsAboveSoBelowBandStep.BandMapTypeName);

        // A hard failure rather than a skip. A scenario asking for a banded map on a load without
        // their mod would otherwise run every later step against an ordinary map and report pins
        // that look perfectly healthy — the exact failure mode this step's header warns about for
        // the spike layout, arrived at a different way.
        if (bandMapType == null)
            return StepOutcome.Fail(
                "As above, So below II is not loaded (no " + AsAboveSoBelowBandStep.BandMapTypeName
                + ") — add it with --mod, or this scenario measures an ordinary map.");

        MapComponent comp = map.GetComponent(bandMapType) as MapComponent;

        if (comp == null)
            return StepOutcome.Fail(
                "map has no " + AsAboveSoBelowBandStep.BandMapTypeName + " component; their assembly "
                + "is loaded but the component was not instantiated for this map.");

        MethodInfoInvoke(comp, bandCount, bandHeight, surfaceBand, out string setupError);

        if (setupError != null)
            return StepOutcome.Fail(setupError);

        // Their layer only rebuilds when the section is dirtied, and nothing about calling Setup
        // tells the draw layer anything changed. Without this the map keeps whatever it baked
        // before it was banded, for as long as the camera does not move.
        map.mapDrawer?.RegenerateEverythingNow();

        return new StepOutcome();
    }

    // Reflected because we never reference their assembly. Their Setup is public and takes three
    // ints; anything else here is upstream drift and is reported with the signature we expected.
    private static void MethodInfoInvoke(
        object comp, int bandCount, int bandHeight, int surfaceBand, out string error)
    {
        error = null;

        var setup = AccessTools.Method(
            comp.GetType(), "Setup", new[] { typeof(int), typeof(int), typeof(int) });

        if (setup == null)
        {
            error = "ABBandMap.Setup(int, int, int) is not there — upstream changed the shape this "
                  + "step binds to.";
            return;
        }

        try
        {
            setup.Invoke(comp, new object[] { bandCount, bandHeight, surfaceBand });
        }
        catch (Exception e)
        {
            error = "ABBandMap.Setup threw: " + e;
        }
    }
}
