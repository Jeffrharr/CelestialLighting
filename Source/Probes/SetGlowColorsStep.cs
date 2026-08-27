using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace CelestialLighting.Probes;

// Repaints every lamp already standing on the map from a palette, so a scenario can put five hundred
// DIFFERENTLY coloured emitters on screen without naming five hundred cells.
//
// WHY A STEP AND NOT MORE LAMP DEFS. Vanilla ships six glowing furniture defs and four distinct
// glow colours between them, so a scene built out of defs alone can only ever show four colours
// however many lamps it places. Since 1.4 the interesting case is the opposite one: StandingLamp and
// WallLamp carry `colorPickerEnabled`, so a real colony's lamps hold arbitrary per-instance
// ColorInts, and CompGlower.GlowColor returns that override rather than the def's value. That is the
// population §27 actually has to composite, and nothing in the harness could build it.
//
// WHY IT ASSIGNS BY POSITION AND NOT BY thingIDNumber. The obvious ordering is the one the glow grid
// already has, but `litGlowers` is a HashSet and thingIDNumber depends on how many things the fixture
// spawned before these — so both would hand the same scenario a different palette on a different
// save, and an A/B whose lamps changed colour between arms measures the palette rather than the
// change. Sorting by cell (z, then x) is stable across runs, saves and RimWorld versions, so a
// scenario's frames are reproducible and its arms are comparable.
//
// WHY IT DRIVES THE PUBLIC SETTER rather than writing `glowColorOverride`. Same reason SetDoorOpen
// goes through DoorOpen: CompGlower.GlowColor's setter deregisters the glower from the glow grid and
// registers it again, and Patch_VectorLightInvalidation hooks exactly those two methods. Poking the
// backing field would recolour the lamp while leaving §27's roster believing the old colour, which is
// the bug a colour scenario exists to catch — the test would pass by reproducing it.
//
// A NOTE ON WHAT A RECOLOUR COSTS, because it decides what a colour-only scenario can claim.
// VectorLightField.Upsert dirties a polygon on a MOVE or a RESIZE and deliberately not on a
// recolour: the shape is identical and the colour rides on the material property block. So a palette
// alone provokes uploads and composition, NOT rebakes. `radii` is here for the other half — a radius
// change does resize, and it is also what spreads emitters across the per-radius gradient/material
// cache, which a fixture built at one radius never touches at all.
//
// Dev-only, like everything else in this folder: compiled into CelestialLighting.Probes, never into
// the shipped DLL.
public sealed class SetGlowColorsStepSpec : IStepSpec
{
    public string Type => "SetGlowColors";

    // A recolour outlives the scenario that applied it — the override is per-Thing state on a lamp
    // this scenario did not create and will not remove — so a suite has to reload the fixture rather
    // than let the next scenario inherit a map lit in someone else's palette.
    public ScenarioResidue Residue => ScenarioResidue.Map;

    // Never against a real colony. It would silently repaint every lamp in someone's base, and there
    // is no undo: the previous overrides are not recorded anywhere this step could restore them from.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string error)
    {
        if (!args.ContainsKey("colors"))
        {
            error = "SetGlowColors needs a 'colors' argument, e.g. colors=\"255,80,80; 80,255,80\".";
            return false;
        }

        if (!TryParseColors(args["colors"], out _, out string colorError))
        {
            error = colorError;
            return false;
        }

        // Optional, but a malformed one has to fail here rather than half-way through repainting.
        if (args.ContainsKey("radii") && !TryParseRadii(args["radii"], out _, out string radiusError))
        {
            error = radiusError;
            return false;
        }

        error = null;
        return true;
    }

    // "r,g,b" triples separated by semicolons, in vanilla's own 0-255 ColorInt units — the same
    // numbers CompProperties_Glower.glowColor is written in, so a scenario author can copy a value
    // straight out of Buildings_Furniture.xml and get the colour it names.
    //
    // A NOTE ON THE RANGE. Vanilla's own SunLamp is (370,370,370) and the sanguphage torch is
    // (460,220,205), i.e. glow colours are deliberately allowed past 255 to mean "brighter than
    // white". So the ceiling here is not 255. It is 1000, which is well clear of anything vanilla
    // ships while still catching a transposed or garbage value rather than uploading it.
    internal static bool TryParseColors(string raw, out List<ColorInt> colors, out string error)
    {
        colors = new List<ColorInt>();
        error = null;

        foreach (string entry in Split(raw))
        {
            string[] parts = entry.Split(',');
            if (parts.Length != 3)
            {
                error = $"SetGlowColors could not parse colour '{entry}' — expected \"r,g,b\".";
                return false;
            }

            int[] channels = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out channels[i]))
                {
                    error = $"SetGlowColors could not parse colour channel '{parts[i].Trim()}' " +
                            $"in '{entry}' — expected a whole number.";
                    return false;
                }

                if (channels[i] < 0 || channels[i] > 1000)
                {
                    error = $"SetGlowColors got channel {channels[i]} in '{entry}' — expected 0..1000 " +
                            "(vanilla glow colours exceed 255 to mean brighter than white, so the " +
                            "ceiling is not 255, but this is not a plausible one).";
                    return false;
                }
            }

            colors.Add(new ColorInt(channels[0], channels[1], channels[2], 0));
        }

        if (colors.Count == 0)
        {
            error = "SetGlowColors got an empty 'colors' list.";
            return false;
        }

        return true;
    }

    // Optional glow radii, cycled over the same sorted glowers as the palette but on its own cycle
    // length, so a scenario can vary colour and size independently instead of pairing them one to one.
    //
    // THE CEILING IS DELIBERATELY LOW. VectorLightOverlay allocates a gradient texture and a material
    // per distinct integer radius and the field allocates a (2r+1)-square texture per emitter, so a
    // radius arrives on the GPU as an allocation, not just a number. 64 is far past any vanilla lamp
    // (SunLamp is 14) and still bounded.
    internal static bool TryParseRadii(string raw, out List<float> radii, out string error)
    {
        radii = new List<float>();
        error = null;

        foreach (string entry in Split(raw))
        {
            if (!float.TryParse(entry, NumberStyles.Float, CultureInfo.InvariantCulture, out float radius))
            {
                error = $"SetGlowColors could not parse radius '{entry}' — expected a number.";
                return false;
            }

            if (radius <= 0f || radius > 64f)
            {
                error = $"SetGlowColors got radius {radius} — expected a value in (0, 64].";
                return false;
            }

            radii.Add(radius);
        }

        if (radii.Count == 0)
        {
            error = "SetGlowColors got an empty 'radii' list.";
            return false;
        }

        return true;
    }

    // Semicolon-separated, with blanks dropped so a generated list may end in a trailing separator —
    // every scenario here is written by a Python generator, and making it trim its own output is one
    // more thing for a generator to get wrong for no gain.
    internal static IEnumerable<string> Split(string raw) =>
        (raw ?? string.Empty)
            .Split(';')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0);
}

public sealed class SetGlowColorsStepAction : IStepAction
{
    public string Type => "SetGlowColors";

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        Map map = ctx.Map;
        if (map == null)
        {
            return StepOutcome.Fail("SetGlowColors ran with no map loaded.");
        }

        if (!SetGlowColorsStepSpec.TryParseColors(args["colors"], out List<ColorInt> colors, out string error))
        {
            return StepOutcome.Fail(error);
        }

        List<float> radii = null;
        if (args.ContainsKey("radii")
            && !SetGlowColorsStepSpec.TryParseRadii(args["radii"], out radii, out string radiusError))
        {
            return StepOutcome.Fail(radiusError);
        }

        HashSet<string> defFilter = ParseDefFilter(args);
        List<CompGlower> targets = SortedTargets(map, defFilter);

        if (targets.Count == 0)
        {
            // A no-op recolour is the failure this step is most likely to produce and the hardest to
            // see: every later probe reads a plausible number off lamps in their default colour, and
            // the scenario passes having tested one colour. Fail loudly instead.
            return StepOutcome.Fail(
                "SetGlowColors matched no lamps on the map" +
                (defFilter == null ? "." : $" for defs '{args["defs"]}'."));
        }

        for (int i = 0; i < targets.Count; i++)
        {
            // Radius BEFORE colour, and this order matters. The radius setter writes a field and
            // tells nobody; the colour setter is what deregisters and re-registers the glower, which
            // is what reaches Patch_VectorLightInvalidation. Doing them the other way round leaves
            // the new radius unannounced until something else happens to dirty the lamp.
            if (radii != null)
            {
                targets[i].GlowRadius = radii[i % radii.Count];
            }

            targets[i].GlowColor = colors[i % colors.Count];
        }

        return new StepOutcome();
    }

    // Optional semicolon-separated ThingDef defNames. Omitted means every lamp on the map, which is
    // what a stress scenario wants; naming defs is for a scene that has to leave the fixture's own
    // lighting alone.
    private static HashSet<string> ParseDefFilter(IReadOnlyDictionary<string, string> args)
    {
        if (!args.ContainsKey("defs"))
        {
            return null;
        }

        HashSet<string> defs = new HashSet<string>(
            SetGlowColorsStepSpec.Split(args["defs"]), StringComparer.Ordinal);

        return defs.Count == 0 ? null : defs;
    }

    // Every CompGlower on this map, in an order that does not depend on spawn order or on hash
    // iteration — see the header for why that is what makes the palette reproducible. All comps
    // rather than only the lit ones: an unpowered lamp is still a lamp a scenario asked to repaint,
    // and skipping it would make the palette shift the moment the power net settled.
    private static List<CompGlower> SortedTargets(Map map, HashSet<string> defFilter)
    {
        List<CompGlower> targets = new List<CompGlower>();

        foreach (Thing thing in map.listerThings.AllThings)
        {
            if (Wanted(thing, defFilter))
            {
                targets.Add(((ThingWithComps)thing).GetComp<CompGlower>());
            }
        }

        targets.Sort(ByCell);
        return targets;
    }

    // A thing this step should repaint: it carries a glower at all, and it passes the def filter if
    // one was given. Named rather than inlined as a `continue` because the two halves are unrelated
    // — one is "is this a lamp", the other is "did the scenario ask for this lamp" — and reading
    // them as one predicate is what makes the loop above a single statement.
    private static bool Wanted(Thing thing, HashSet<string> defFilter)
    {
        if (defFilter != null && !defFilter.Contains(thing.def.defName))
        {
            return false;
        }

        return (thing as ThingWithComps)?.GetComp<CompGlower>() != null;
    }

    // Row-major by cell, with thingIDNumber only as the last resort for two glowers stacked on one
    // cell (a wall lamp and a torch on the same square). That tie is rare and its order does not
    // affect anything but which of the two got which colour.
    private static int ByCell(CompGlower a, CompGlower b)
    {
        IntVec3 ca = a.parent.Position;
        IntVec3 cb = b.parent.Position;

        if (ca.z != cb.z)
            return ca.z.CompareTo(cb.z);

        if (ca.x != cb.x)
            return ca.x.CompareTo(cb.x);

        return a.parent.thingIDNumber.CompareTo(b.parent.thingIDNumber);
    }
}
