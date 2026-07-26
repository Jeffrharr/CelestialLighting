using System.Xml.Linq;
using CelestialLighting;

namespace CelestialLighting.Tools;

// Dev tool: runs §13's SHIPPED weather classifier over every WeatherDef and BiomeDef installed on this
// machine, and prints what each one would be dimmed by.
//
// WHY THIS IS CHECKED IN. §13's whole premise is that a modded weather classifies itself, from data it
// already ships, with no defName list — which is a claim about content nobody on the team wrote and
// that changes every time the user's mod list does. The first audit of that claim (issue #31) was a
// one-off script, and it made two errors that a checked-in tool makes structurally hard to repeat: it
// reimplemented the formula instead of linking it (so its numbers were not the game's numbers), and it
// read RimWorld's colour XML as literal floats (so it misread every 0-255 palette). This tool links
// Source/WeatherDimmingMath.cs directly and mirrors ParseHelper.ParseColor.
//
// Run it after touching the classifier, and after a big mod-list change:
//
//     dotnet run --project Tools/WeatherAudit                # this machine's install
//     dotnet run --project Tools/WeatherAudit -- --changed    # only defs that would dim
//     dotnet run --project Tools/WeatherAudit -- /path/to/Data /path/to/workshop/294100
//
// What to look for, in the order it matters:
//
//   1. A biome in the "no climate" list that is actually open-air. That biome loses weather dimming
//      entirely — the guard is wrong for it and needs a look.
//   2. A weather at opacity 1.00 that is a map environment rather than weather (a cave, a pocket
//      dimension, an interior). Check whether its biome is already in the no-climate list; if it is,
//      the map guard already spares it and there is nothing to do.
//   3. A weather strictly between 0 and 1. That is a judgement call the data could not make. Leave it
//      alone unless it looks visibly wrong in game, and if it does, the fix is a WeatherCloudDeck
//      extension on that def, not a change to the formula.
internal static class Program
{
    // This machine's install, per the parent CLAUDE.md's key-paths table. Overridable by argument so
    // the tool is useful on someone else's box.
    private static readonly (string Label, string Path)[] DefaultRoots =
    {
        ("vanilla", "/home/deck/.local/share/Steam/steamapps/common/RimWorld/Data"),
        ("local", "/home/deck/.local/share/Steam/steamapps/common/RimWorld/Mods"),
        ("workshop", "/home/deck/.local/share/Steam/steamapps/workshop/content/294100"),
    };

    private static int Main(string[] args)
    {
        bool changedOnly = args.Contains("--changed");
        string[] rootArgs = args.Where(a => !a.StartsWith("--")).ToArray();

        (string Label, string Path)[] roots = rootArgs.Length > 0
            ? rootArgs.Select(p => (Path.GetFileName(p.TrimEnd('/')), p)).ToArray()
            : DefaultRoots;

        DefLoader loader = new();
        Console.Error.WriteLine("Loading defs:");
        foreach ((string label, string path) in roots)
            loader.LoadFrom(label, path);

        List<BiomeRow> biomes = ReadBiomes(loader);
        List<WeatherRow> weathers = ReadWeathers(loader);
        HashSet<string> unreachable = WeathersOnlyOfferedBySkylessBiomes(loader, biomes);

        Dictionary<string, float> opacityByWeather =
            weathers.ToDictionary(w => w.DefName, w => w.Opacity);

        ReportBiomes(biomes, opacityByWeather);
        ReportWeathers(weathers, unreachable, changedOnly);
        ReportSummary(biomes, weathers, unreachable, opacityByWeather);
        return 0;
    }

    // Which weathers are only ever offered by biomes we have already ruled out.
    //
    // This is the cross-reference that makes the weather table readable, because a cave "weather" whose
    // palette looks overcast is not a bug if the only biomes that can roll it have no climate — the map
    // guard already spares it and its row is noise. It is what turns "eleven suspicious rows" into the
    // one or two that actually need a human.
    //
    // Conservative in the safe direction: a weather offered by NO biome at all (forced by mapgen, a
    // GameCondition or an incident) is not claimed to be unreachable, because we cannot see how it is
    // applied from the defs alone.
    private static HashSet<string> WeathersOnlyOfferedBySkylessBiomes(
        DefLoader loader, List<BiomeRow> biomes)
    {
        Dictionary<string, bool> biomeHasSky = biomes.ToDictionary(b => b.DefName, b => b.HasSky);
        Dictionary<string, (int Total, int WithSky)> offers = new();

        foreach (DefLoader.LoadedDef def in loader.DefsOfType("BiomeDef"))
        {
            Dictionary<string, XElement> fields = loader.Resolve(def.Element);
            string? biomeName = DefLoader.Text(fields, "defName");
            if (biomeName == null || !biomeHasSky.TryGetValue(biomeName, out bool hasSky))
                continue;

            foreach (XElement record in DefLoader.Children(fields, "baseWeatherCommonalities"))
            {
                if (!IsPossible(record))
                    continue;

                string weather = record.Name.LocalName;
                (int total, int withSky) = offers.GetValueOrDefault(weather);
                offers[weather] = (total + 1, withSky + (hasSky ? 1 : 0));
            }
        }

        return offers.Where(o => o.Value.Total > 0 && o.Value.WithSky == 0)
            .Select(o => o.Key)
            .ToHashSet();
    }

    private static bool IsPossible(XElement record) =>
        float.TryParse(record.Value.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float commonality)
        && commonality > 0f;

    // --- Biomes: the structural guard's input ---

    private readonly record struct BiomeRow(
        string DefName, string Source, int WeatherChoices, bool DisableSkyLighting,
        List<string> Weathers)
    {
        // Mirrors WeatherDimming.HasSky exactly, so the tool's verdicts are the game's verdicts.
        //
        // `generatesNaturally` was measured here as a candidate third condition — it would have caught
        // vanilla's Undercave, which the weather count does not — and rejected: it also catches
        // Deep Mining's DMSE_ImpactCraterBiome, an open-air crater with Clear/Rain/DryThunderstorm that
        // genuinely wants dimming. It buys only biomes whose weathers already classify to 0 and costs a
        // real one, so it is not in the guard.
        internal bool HasSky =>
            !DisableSkyLighting && WeatherDimmingMath.BiomeHasChangingWeather(WeatherChoices);
    }

    private static List<BiomeRow> ReadBiomes(DefLoader loader)
    {
        Dictionary<string, BiomeRow> rows = new();
        foreach (DefLoader.LoadedDef def in loader.DefsOfType("BiomeDef"))
        {
            Dictionary<string, XElement> fields = loader.Resolve(def.Element);
            string? defName = DefLoader.Text(fields, "defName");
            if (defName == null || rows.ContainsKey(defName))
                continue;

            List<string> weathers = DefLoader.Children(fields, "baseWeatherCommonalities")
                .Where(IsPossible)
                .Select(r => r.Name.LocalName)
                .ToList();

            rows[defName] = new BiomeRow(
                defName,
                def.Source,
                weathers.Count,
                DefLoader.Bool(fields, "disableSkyLighting", false),
                weathers);
        }

        return rows.Values.ToList();
    }

    // Mirrors WeatherDimming.WeatherChoiceCount: entries at commonality 0 are suppressions, not
    // possibilities.
    private static int CountWeatherChoices(Dictionary<string, XElement> fields) =>
        DefLoader.Children(fields, "baseWeatherCommonalities").Count(IsPossible);

    // --- Weathers: the classifier's input ---

    private readonly record struct WeatherRow(
        string DefName, string Source, float PaletteOpacity, float Opacity, float Dimming,
        float RainRate, float SnowRate, float SandRate, float? Declared);

    private static List<WeatherRow> ReadWeathers(DefLoader loader)
    {
        Dictionary<string, WeatherRow> rows = new();
        foreach (DefLoader.LoadedDef def in loader.DefsOfType("WeatherDef"))
        {
            Dictionary<string, XElement> fields = loader.Resolve(def.Element);
            string? defName = DefLoader.Text(fields, "defName");
            if (defName == null || rows.ContainsKey(defName))
                continue;

            WeatherRow? row = ClassifyWeather(defName, def.Source, loader, fields);
            if (row.HasValue)
                rows[defName] = row.Value;
        }

        return rows.Values.ToList();
    }

    private static WeatherRow? ClassifyWeather(
        string defName, string source, DefLoader loader, Dictionary<string, XElement> fields)
    {
        if (!fields.TryGetValue("skyColorsDay", out XElement? dayElement))
            return null;

        Dictionary<string, XElement> day = loader.Resolve(dayElement);
        (float R, float G, float B)? sky = DefLoader.ParseColor(DefLoader.Text(day, "sky"));
        if (sky == null)
            return null;

        // WeatherDef.ConfigErrors rejects a saturation of 0, and vanilla's own default palette is the
        // clear family's, so 1.25 is the right fallback for a def that omits it.
        float saturation = DefLoader.Float(day, "saturation", WeatherDimmingMath.ClearSaturation);
        float rainRate = DefLoader.Float(fields, "rainRate", 0f);
        float snowRate = DefLoader.Float(fields, "snowRate", 0f);
        float sandRate = DefLoader.Float(fields, "sandRate", 0f);

        float palette = WeatherDimmingMath.PaletteOpacity(sky.Value.R, sky.Value.G, sky.Value.B, saturation);
        float? declared = ReadDeclaredOpacity(fields);
        float opacity = declared
            ?? WeatherDimmingMath.CloudOpacity(
                sky.Value.R, sky.Value.G, sky.Value.B, saturation, rainRate, snowRate, sandRate);

        float dimming = WeatherDimmingMath.DimmingFraction(
            opacity, rainRate, snowRate, sandRate, WeatherDimmingMath.DefaultMaxDimming);

        return new WeatherRow(
            defName, source, palette, opacity, dimming, rainRate, snowRate, sandRate, declared);
    }

    // Picks up our own escape hatch where a def already carries it, so the audit reports what the game
    // will actually do rather than what the palette implies.
    private static float? ReadDeclaredOpacity(Dictionary<string, XElement> fields)
    {
        foreach (XElement extension in DefLoader.Children(fields, "modExtensions"))
        {
            string? cls = extension.Attribute("Class")?.Value;
            if (cls != "CelestialLighting.WeatherCloudDeck")
                continue;

            XElement? opacity = extension.Element("opacity");
            if (opacity != null
                && float.TryParse(opacity.Value.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value)
                && value >= 0f)
                return Math.Clamp(value, 0f, 1f);
        }

        return null;
    }

    // --- Reporting ---

    private static void ReportBiomes(List<BiomeRow> biomes, Dictionary<string, float> opacity)
    {
        List<BiomeRow> skyless = biomes.Where(b => !b.HasSky).OrderBy(b => b.DefName).ToList();

        Console.WriteLine();
        Console.WriteLine($"=== BIOMES WITH NO CLIMATE — weather dimming is off entirely on these "
            + $"({skyless.Count} of {biomes.Count}) ===");
        Console.WriteLine($"{"biome",-34} {"source",-20} {"weathers",8} {"worst",6}  why");
        foreach (BiomeRow biome in skyless)
        {
            string why = biome.DisableSkyLighting
                ? "sets disableSkyLighting"
                : $"only {biome.WeatherChoices} possible weather(s)";
            Console.WriteLine($"{Trim(biome.DefName, 34),-34} {Trim(biome.Source, 20),-20} "
                + $"{biome.WeatherChoices,8} {WorstOpacity(biome, opacity),6:0.00}  {why}");
        }

        Console.WriteLine();
        Console.WriteLine("  The `worst` column is the highest cloud opacity any weather this biome can");
        Console.WriteLine("  roll would classify to. It is what the guard is actually preventing: a row at");
        Console.WriteLine("  0.00 would have been harmless anyway, a row above 0 is a false positive the");
        Console.WriteLine("  guard caught.");

        ReportBoundaryBiomes(biomes, opacity);
    }

    // The biomes sitting exactly on the >= 2 rule, with what they could roll.
    //
    // THIS IS THE LIST TO READ AFTER A MOD-LIST CHANGE, because the >= 2 rule does NOT cleanly
    // partition skyless from open-air and it is important not to pretend otherwise: vanilla's Undercave
    // offers two weathers (its own, plus `Underground` inherited from Biome_Underground) and so is
    // treated as having a climate, exactly like the open-air Duskwood. What makes that safe is not the
    // count but the `worst` column — every skyless biome above the threshold only offers weathers that
    // already classify to 0, so the guard never has to fire for them.
    //
    // A row here that names an enclosed place AND shows a nonzero worst opacity is a real bug: the
    // count rule will not spare it and its palette will dim. That is the signal this list exists for.
    private static void ReportBoundaryBiomes(List<BiomeRow> biomes, Dictionary<string, float> opacity)
    {
        List<BiomeRow> boundary = biomes
            .Where(b => b.HasSky && b.WeatherChoices <= WeatherDimmingMath.MinWeatherChoicesForClimate)
            .OrderByDescending(b => WorstOpacity(b, opacity))
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"=== BIOMES ON THE BOUNDARY — treated as having a climate with only "
            + $"{WeatherDimmingMath.MinWeatherChoicesForClimate} weather(s) ===");
        Console.WriteLine($"{"biome",-34} {"source",-20} {"worst",6}  weathers");
        foreach (BiomeRow biome in boundary)
        {
            Console.WriteLine($"{Trim(biome.DefName, 34),-34} {Trim(biome.Source, 20),-20} "
                + $"{WorstOpacity(biome, opacity),6:0.00}  {string.Join(", ", biome.Weathers)}");
        }

        Console.WriteLine();
        Console.WriteLine("  Eyeball these: any that is an enclosed or skyless place AND shows a nonzero");
        Console.WriteLine("  worst opacity is a false positive the guard does not catch.");
    }

    private static float WorstOpacity(BiomeRow biome, Dictionary<string, float> opacity)
    {
        float worst = 0f;
        foreach (string weather in biome.Weathers)
        {
            if (opacity.TryGetValue(weather, out float value) && value > worst)
                worst = value;
        }

        return worst;
    }

    private static void ReportWeathers(
        List<WeatherRow> weathers, HashSet<string> unreachable, bool changedOnly)
    {
        IEnumerable<WeatherRow> shown = weathers.OrderBy(w => w.Opacity).ThenBy(w => w.DefName);
        if (changedOnly)
            shown = shown.Where(w => w.Dimming > 0f && !unreachable.Contains(w.DefName));

        Console.WriteLine();
        Console.WriteLine("=== WEATHERS — the dims column is what a map WITH a climate would show ===");
        Console.WriteLine($"{"weather",-38} {"source",-20} {"palette",7} {"opacity",7} {"dims",6}  note");
        foreach (WeatherRow w in shown)
        {
            Console.WriteLine($"{Trim(w.DefName, 38),-38} {Trim(w.Source, 20),-20} "
                + $"{Zeroed(w.PaletteOpacity),7:0.00} {Zeroed(w.Opacity),7:0.00} "
                + $"{Zeroed(w.Dimming) * 100f,5:0.0}%  {NoteFor(w, unreachable)}");
        }
    }

    private static string NoteFor(WeatherRow w, HashSet<string> unreachable)
    {
        if (w.Declared.HasValue)
            return "declared via WeatherCloudDeck";
        if (unreachable.Contains(w.DefName))
            return "never dimmed in practice — only offered by biomes with no climate";
        if (w.Opacity > w.PaletteOpacity + 1e-4f)
            return "precipitation overrides the palette";
        if (w.Opacity > 0f && w.Opacity < 1f)
            return "AMBIGUOUS — partial deck, judgement call";
        return string.Empty;
    }

    private static void ReportSummary(
        List<BiomeRow> biomes, List<WeatherRow> weathers, HashSet<string> unreachable,
        Dictionary<string, float> opacity)
    {
        int partial = weathers.Count(
            w => w.Opacity > 0f && w.Opacity < 1f && !unreachable.Contains(w.DefName));

        Console.WriteLine();
        Console.WriteLine($"{weathers.Count} weathers, {biomes.Count} biomes.");
        Console.WriteLine($"  {weathers.Count(w => w.Opacity <= 0f),4} never dimmed (clear or not weather)");
        Console.WriteLine($"  {weathers.Count(w => w.Opacity >= 1f),4} fully dimmed (full deck)");
        Console.WriteLine($"  {weathers.Count(w => unreachable.Contains(w.DefName)),4} unreachable "
            + "— spared by the map guard whatever their palette says");
        Console.WriteLine($"  {partial,4} partial AND reachable — these are the judgement calls");
        Console.WriteLine($"  {weathers.Count(w => w.Declared.HasValue),4} declared by a "
            + "WeatherCloudDeck extension");
        Console.WriteLine($"  {biomes.Count(b => !b.HasSky),4} biomes where dimming is off entirely");
        Console.WriteLine($"  {biomes.Count(b => !b.HasSky && WorstOpacity(b, opacity) > 0f),4} of those "
            + "would actually have been wrongly dimmed — the guard's real work");
    }

    // Normalises negative zero, which InverseLerpClamped produces for any exactly-clear saturation
    // (0 / -0.35), so the table does not print "-0.00" and invite a double-take.
    private static float Zeroed(float value) => value + 0f;

    private static string Trim(string value, int width) =>
        value.Length <= width ? value : value[..width];
}
