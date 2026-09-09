using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Soft interop with "As above, So below II" (astryl.AsAboveSoBelow2).
//
// WHAT THEIR MOD DOES TO OURS. AASB2 stacks up to seven playable levels as BANDS OF ONE MAP rather
// than as separate maps. One consequence reaches straight into this mod: their
// Patch_LightingOverlay_ABSuppressOnBanded postfixes SectionLayer_LightingOverlay.Visible to false on
// any banded map, and draws its own SectionLayer_ABBelowLighting in its place.
//
// Both of our writers to that mesh — the indoor sky occlusion (§7b) and the vector-light suppression
// (§27) — are Harmony postfixes on SectionLayer_LightingOverlay.Regenerate. Section.TryUpdate does
// not consult Visible, so on their maps vanilla's mesh is still baked and we still write our alphas
// into it, and then nothing ever draws it. A player reported this as "the indoor darkness slider does
// nothing for me", for ordinary surface rooms as well as underground levels — one cause covers both
// halves, because their suppression is map-wide rather than band-wide.
//
// WHY THE INTEROP LIVES HERE RATHER THAN IN THEIR MOD. AASB2 ships a CelestialLightingCompat that
// binds CelestialLighting.CellResolver and CelestialLighting.OverlayPasses by reflection and calls
// our passes for us. We deliberately do not implement that contract. Ownership is the argument: the
// affected feature is ours, the failure mode is ours to reproduce, and the harness that can prove it
// works is in this repo — a contract whose two halves are maintained by different people, in
// different repos, on different release cadences, is one that drifts silently. It is also strictly
// less capable, which is the concrete half of the argument: see the vector-lighting note below.
//
// Their build needs no change for this to work. Their Bind() requires all five of the members named
// above; with none of them present it logs "their build predates the integration hook", sets
// active = false, and ApplyOverlayPasses returns at its first line. So their compat is inert against
// us and there is no double application, whether or not they ever remove it.
//
// AND SO EVERY MEMBER BELOW IS A STRING, exactly as in AxialTiltCompat, and for the same reason: no
// hard assembly reference, so a player without AASB2 loads a CelestialLighting that has never heard
// of it. The price is that upstream drift cannot fail at compile time. It fails at Bind(), and it
// must fail SOFTLY — every resolve is null-checked and every delegate guarded, so a rename degrades
// to "AASB2 treated as absent, our postfixes keep writing vanilla's mesh", which is the behaviour
// that shipped before this file existed. AsAboveSoBelowContractTests pins each name against their
// installed assembly so the drift is caught offline instead of by a player.
public static class AsAboveSoBelowCompat
{
    private const string PackageId = "astryl.AsAboveSoBelow2";

    // Their public band API. Everything we need to answer "is this map theirs" is on it, which is
    // what makes this interop possible without reaching into their internals for the decision.
    private const string BandsTypeName = "AsAboveSoBelow.ABBands";

    // Their per-subsystem kill switch. Rendering is the one that matters here: when it is off they
    // have stood their own renderer down after an exception and vanilla's overlay is visible again,
    // so we must NOT stand down with them.
    private const string GuardTypeName = "AsAboveSoBelow.ABGuard";
    private const string RenderingSwitchName = "Rendering";

    // The layer they draw in vanilla's place, and the LayerSubMesh it caches.
    private const string LayerTypeName = "AsAboveSoBelow.SectionLayer_ABBelowLighting";
    private const string MeshFieldName = "mesh";

    private static bool bound;
    private static bool triedBind;

    private static Func<Map, bool> abBanded;
    private static Func<bool> abRenderingOn;
    private static AccessTools.FieldRef<object, LayerSubMesh> abMesh;

    // Whether somebody else is drawing this map's lighting overlay right now, which is the question
    // our two postfixes on vanilla's Regenerate ask before they do anything.
    //
    // BOTH TERMS ARE LOAD-BEARING AND THEY ARE THEIRS, not ours: this reproduces exactly the
    // condition Patch_LightingOverlay_ABSuppressOnBanded uses to hide vanilla's overlay. Dropping the
    // guard term would leave us standing down on a banded map whose renderer they had already
    // disabled — vanilla's overlay visible, and our contribution silently missing from it. Dropping
    // the banded term would stand us down on the surface-only maps their mod leaves entirely alone.
    //
    // Called on every lighting-overlay regenerate on every map for every player, so it short-circuits
    // on the bind flag first and both live reads are delegates rather than reflective invokes.
    public static bool OwnsOverlay(Map map)
    {
        if (map == null || !Bind())
            return false;

        try
        {
            return abRenderingOn() && abBanded(map);
        }
        catch (Exception e)
        {
            // Answering "not owned" is the safe direction: we keep writing vanilla's mesh, which is
            // what we did before this file existed. Disabling the binding rather than logging per
            // call, because this runs per section per regenerate.
            Log.ErrorOnce(
                "[CelestialLighting] As above, So below II's band API threw; treating every map's "
                + "lighting overlay as ours for the rest of this session. " + e, 0x0CE1A501);
            bound = false;
            return false;
        }
    }

    // Called once at mod init, after PatchAll. No-op without AASB2.
    //
    // Patched manually rather than through a [HarmonyPatch] class because the target type only exists
    // when their assembly is loaded, and an annotated class naming a type that is not there throws
    // out of PatchAll — which does not fail one patch, it fails EVERY patch in this mod, silently,
    // leaving a plausible-looking vanilla sky.
    public static void Install(Harmony harmony)
    {
        if (!Bind())
            return;

        MethodInfo regenerate = AccessTools.Method(LayerTypeName + ":Regenerate");

        if (regenerate == null)
        {
            WarnUnusable(
                LayerTypeName + ".Regenerate", "is not there",
                "Their banded maps keep vanilla's lighting with none of ours on it: no indoor sky "
                + "occlusion and no vector-light suppression underground.");
            bound = false;
            return;
        }

        try
        {
            harmony.Patch(
                regenerate,
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(AsAboveSoBelowCompat), nameof(RegeneratePostfix))));
        }
        catch (Exception e)
        {
            WarnUnusable(LayerTypeName + ".Regenerate", $"could not be patched ({e.Message})",
                "Their banded maps keep vanilla's lighting with none of ours on it.");
            bound = false;
            return;
        }

        Log.Message(
            "[CelestialLighting] As above, So below II detected; our lighting-overlay passes will run "
            + "on their below-lighting layer instead of vanilla's.");
    }

    // Our two passes, run on the mesh they actually draw.
    //
    // WHY THIS IS AN EXACT REUSE RATHER THAN AN APPROXIMATION. Their Regenerate builds its mesh with
    // vanilla's own SectionLayer_LightingOverlay.Bake and then only recolours it, so the vertex
    // layout is vanilla's lattice — the same (Width+1)*(Height+1) corners followed by Width*Height
    // centres that every index we compute assumes. We are handed it at the same point in the bake our
    // own postfixes would have run, for one section rect, with the Section itself in hand.
    //
    // ORDER MIRRORS THE POSTFIX CHAIN IT REPLACES: occlusion (Priority.First, alpha only) then
    // vector light (Priority.Last, RGB only). They do not contend for a channel, but keeping the
    // order identical means there is one answer to "what does our overlay look like" rather than one
    // per map kind.
    //
    // __instance is typed as the vanilla base class both to avoid naming their type in a signature we
    // cannot reference and because that is all we need from it.
    private static void RegeneratePostfix(SectionLayer __instance)
    {
        try
        {
            Section section = SectionLayerAccess.GetSection(__instance);
            Map map = section?.map;

            if (map == null)
                return;

            // Null after their Release(), which is what they call on every early return — an
            // unbanded map, a disabled rendering guard, a degenerate rect. So a null mesh is the
            // ordinary "not ours to touch" answer rather than a fault.
            LayerSubMesh subMesh = abMesh(__instance);

            if (subMesh?.mesh == null)
                return;

            CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, Section.Size, Section.Size);
            rect.ClipInsideMap(map);

            Patch_IndoorSkyOcclusion.ApplyToMesh(map, subMesh, rect, section);
            Patch_VectorLightSuppress.ApplyToMesh(map, subMesh, rect);
        }
        catch (Exception e)
        {
            // A throw inside a Regenerate is SWALLOWED by vanilla and leaves the frame looking exactly
            // like vanilla, with green probes and a passing scenario (grep Player.log for "Could not
            // regenerate layer"). Catching it here means the failure names itself instead.
            Log.ErrorOnce(
                "[CelestialLighting] our lighting passes threw inside As above, So below II's "
                + "below-lighting layer; they are disabled for the rest of this session. Their "
                + "lighting is unaffected. " + e, 0x0CE1A502);
            bound = false;
        }
    }

    // Reflection binding, done once and cached. A miss is silent for the common case (AASB2 not
    // installed) and logged once for the interesting one (AASB2 installed but reshaped), because that
    // is a real problem a player can act on by reporting it.
    private static bool Bind()
    {
        if (triedBind)
            return bound;

        triedBind = true;

        if (!ModIsActive())
            return false;

        Type bands = AccessTools.TypeByName(BandsTypeName);
        Type guard = AccessTools.TypeByName(GuardTypeName);
        Type layer = AccessTools.TypeByName(LayerTypeName);

        if (bands == null || guard == null || layer == null)
        {
            WarnUnusable(
                "its lighting types",
                $"could not be found (ABBands={bands != null} ABGuard={guard != null} "
                + $"SectionLayer_ABBelowLighting={layer != null})",
                BandedConsequence);
            return false;
        }

        bound =
            TryBindBanded(bands)
            && TryBindRenderingGuard(guard)
            && TryBindMeshField(layer);

        return bound;
    }

    // ABBands.Banded(Map) — public, static, and the same call their own suppression patch makes.
    private static bool TryBindBanded(Type bands)
    {
        MethodInfo banded = AccessTools.Method(bands, "Banded", new[] { typeof(Map) });

        if (banded == null)
        {
            WarnUnusable("ABBands.Banded(Map)", "is not there", BandedConsequence);
            return false;
        }

        try
        {
            abBanded = (Func<Map, bool>)Delegate.CreateDelegate(typeof(Func<Map, bool>), banded);
            return true;
        }
        catch (Exception e)
        {
            WarnUnusable("ABBands.Banded(Map)", $"has an unexpected signature ({e.Message})", BandedConsequence);
            return false;
        }
    }

    // ABGuard.On(ABGuardSwitch) closed over ABGuard.Rendering.
    //
    // Bound as a no-argument delegate over that one switch rather than kept as a MethodInfo and
    // invoked with an args array: the argument is a readonly static that never changes, and
    // Delegate.CreateDelegate's firstArgument overload will bind the first parameter of a static
    // method for exactly this case. OwnsOverlay is on the per-regenerate path, and a reflective
    // Invoke with a boxed argument array there would be a per-section allocation.
    private static bool TryBindRenderingGuard(Type guard)
    {
        FieldInfo rendering = AccessTools.Field(guard, RenderingSwitchName);
        object switchValue = rendering?.GetValue(null);

        if (switchValue == null)
        {
            WarnUnusable("ABGuard.Rendering", "is not there", GuardConsequence);
            return false;
        }

        MethodInfo on = AccessTools.Method(guard, "On", new[] { switchValue.GetType() });

        if (on == null)
        {
            WarnUnusable("ABGuard.On(ABGuardSwitch)", "is not there", GuardConsequence);
            return false;
        }

        try
        {
            abRenderingOn = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), switchValue, on);
            return true;
        }
        catch (Exception e)
        {
            WarnUnusable("ABGuard.On(ABGuardSwitch)", $"has an unexpected signature ({e.Message})", GuardConsequence);
            return false;
        }
    }

    // SectionLayer_ABBelowLighting.mesh — the one private member this interop reads, and the only
    // thing here that a tidy-up on their side could take away without renaming anything public.
    //
    // It is read rather than recomputed because it is the mesh they DRAW: getting the LayerSubMesh
    // is what lets the vector-light mask run, since that pass needs the vertex list and not just a
    // colour array. A public accessor for it is the one thing worth asking them for.
    private static bool TryBindMeshField(Type layer)
    {
        try
        {
            abMesh = AccessTools.FieldRefAccess<LayerSubMesh>(layer, MeshFieldName);
            return abMesh != null;
        }
        catch (Exception e)
        {
            WarnUnusable(
                LayerTypeName + "." + MeshFieldName, $"could not be read ({e.Message})", BandedConsequence);
            return false;
        }
    }

    private const string BandedConsequence =
        "Their banded maps keep vanilla's lighting overlay with none of ours composed onto it, so "
        + "indoor sky occlusion and vector-light suppression are absent there. Everywhere else is "
        + "unaffected.";

    private const string GuardConsequence =
        "We cannot tell whether their renderer is live, so we keep writing vanilla's overlay on their "
        + "maps — which is what we did before this interop existed. Their below-lighting layer will "
        + "draw without our passes on it.";

    private static void WarnUnusable(string memberName, string problem, string consequence)
    {
        Log.Warning(
            $"[CelestialLighting] As above, So below II is installed, but its {memberName} {problem}. "
            + $"{consequence} This is an upstream change rather than a mod-list mistake — please report it.");
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
