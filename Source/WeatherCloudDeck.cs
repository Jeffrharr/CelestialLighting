using Verse;

namespace CelestialLighting;

// §13's per-def escape hatch: lets a WeatherDef state outright how much cloud deck it has, instead of
// being classified from its palette and precipitation rates (DESIGN.md §13).
//
// WHY THIS EXISTS RATHER THAN A LIST OF OURS. §13's premise is that a modded weather should be
// handled automatically, from data it already ships — and the audit behind §13 shows that
// premise holds for 79 of the 81 WeatherDefs across vanilla plus 24 installed workshop mods. But
// "holds for almost everything" is not "holds for everything", and the residue is genuinely
// undecidable from data: Vanilla Psycasts Expanded's VPE_RadioactiveFog and Vanilla Events
// Expanded's VEE_Inferno ship palettes that are partway between clear and overcast, and no rule can
// tell a mod author's "hazy heat shimmer, do not darken" from their "thin high cloud, do darken".
//
// A defName list of ours would answer those two and then rot: it would need an entry for every
// future mod, it would be invisible to the authors it is about, and it would quietly contradict
// whatever they later changed their palette to. A DefModExtension inverts all three. The author (or
// a user, or an XML patch we ship later) states the answer next to the def it is about, we carry no
// list, and anything without the extension keeps being classified automatically — so this changes
// nothing about the data-driven design, it just gives it an override where data runs out.
//
// Usage, in any mod's Defs or a PatchOperationAdd against ours:
//
//   <Verse.WeatherDef>
//     <defName>MyHeatShimmer</defName>
//     <modExtensions>
//       <li Class="CelestialLighting.WeatherCloudDeck">
//         <!-- 0 = no deck (never dim this weather); 1 = full deck; anything between is partial. -->
//         <opacity>0</opacity>
//         <!-- §23: cloud base height in metres, only meaningful once opacity is above 0. Omit to
//              keep classifying the altitude automatically from rain/snow/sand rate. -->
//         <altitudeMetres>10000</altitudeMetres>
//       </li>
//     </modExtensions>
//   </Verse.WeatherDef>
//
// Read cost is a null check in the common case: Def.GetModExtension walks `modExtensions`, which is
// null for every def that does not use one, and §13 reads it a handful of times per frame (once per
// weather per SkyManagerUpdate) rather than per cell.
public class WeatherCloudDeck : DefModExtension
{
    // How much cloud deck this weather has, in [0,1]. Negative means "unset" — keep classifying this
    // def automatically. Defaults to unset so that merely attaching the extension (to set some future
    // field) does not silently pin the opacity to 0.
    public float opacity = -1f;

    // Whether this extension actually overrides the classifier. Kept as a named property rather than
    // an `opacity >= 0` test at the call site, so the sentinel's meaning lives next to the field it
    // belongs to instead of being re-derived by every reader.
    public bool OverridesOpacity => opacity >= 0f;

    // §23 (DESIGN.md, issue #88): cloud base height in metres, the second axis
    // WeatherDimmingMath.DefaultAltitudeMetres's dry/precipitating classifier cannot settle on its
    // own — nothing about a rain rate says whether the rain is falling from a low nimbostratus or an
    // unusually tall storm cell. Same escape-hatch shape as `opacity` above: negative means "unset,
    // keep classifying automatically", so attaching this extension for `opacity` alone never silently
    // pins an altitude nobody asked for.
    public float altitudeMetres = -1f;

    // Same reasoning as OverridesOpacity: named next to the field it reads rather than re-derived at
    // every call site.
    public bool OverridesAltitude => altitudeMetres >= 0f;
}
