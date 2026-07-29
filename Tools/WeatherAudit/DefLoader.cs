using System.Globalization;
using System.Xml.Linq;

namespace CelestialLighting.Tools;

// Just enough of RimWorld's XML def loader to answer §13's question offline: find every WeatherDef and
// BiomeDef under a set of roots, resolve Name/ParentName inheritance, and read fields off the merged
// result.
//
// WHAT THIS DELIBERATELY DOES NOT DO. This is not a def loader; it is a census reader. It ignores
// PatchOperations, Inherit="false", ListItem merge semantics, DefInjected translations and cross-mod
// load order, all of which can change what the game finally sees. That is an accepted limitation, and
// the reason the tool's job is to *find candidates worth looking at* rather than to be authoritative:
// a def whose classification only becomes wrong after another mod patches it will not show up here.
// Anything this tool flags should be confirmed in-game.
//
// The two things it DOES have to get exactly right, because getting them wrong invents findings that
// are not there, are inheritance (WeatherDefs are heavily parented — most modded ones inherit their
// palette wholesale) and colour parsing (see ParseColor).
internal sealed class DefLoader
{
    private readonly Dictionary<string, XElement> namedDefs = new();
    private readonly List<LoadedDef> concreteDefs = new();

    internal readonly record struct LoadedDef(string DefType, string Source, XElement Element);

    internal IEnumerable<LoadedDef> DefsOfType(string defType) =>
        concreteDefs.Where(d => d.DefType == defType);

    // Two passes matter here and only in this order: abstract parents must all be registered before
    // any child is resolved, because a mod's Defs folder can define a child in a file that sorts
    // before its parent's.
    internal void LoadFrom(string label, string root)
    {
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"  (skipping {label}: {root} not found)");
            return;
        }

        foreach (string path in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
        {
            XElement? defs = TryParseDefsFile(path);
            if (defs == null)
                continue;

            foreach (XElement element in defs.Elements())
                Register(label, root, path, element);
        }
    }

    private void Register(string label, string root, string path, XElement element)
    {
        string? name = element.Attribute("Name")?.Value;
        if (name != null)
            namedDefs[name] = element;

        bool isAbstract = string.Equals(
            element.Attribute("Abstract")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        if (isAbstract)
            return;

        // The mod folder immediately under the root is the most useful attribution: for workshop mods
        // it is the numeric Steam id, for vanilla it is Core/Royalty/Odyssey/Anomaly.
        string relative = Path.GetRelativePath(root, path);
        string owner = relative.Split(Path.DirectorySeparatorChar)[0];
        concreteDefs.Add(new LoadedDef(element.Name.LocalName, $"{label}:{owner}", element));
    }

    private static XElement? TryParseDefsFile(string path)
    {
        try
        {
            XElement root = XDocument.Load(path).Root!;
            return root.Name.LocalName == "Defs" ? root : null;
        }
        catch (Exception)
        {
            // Malformed or non-def XML is routine across two dozen mods (About manifests, editor
            // scratch files, half-migrated 1.4 folders). Skipping quietly is correct: a file the game
            // cannot parse either contributes no defs to the census.
            return null;
        }
    }

    // Merges a def with its ancestors. Depth-capped rather than cycle-detected because a cycle in real
    // def XML would fail to load in-game anyway, and a cap keeps this from hanging on one.
    //
    // The merge is RECURSIVE, mirroring Verse.XmlInheritance.RecursiveNodeCopyOverwriteElements, and
    // that is not pedantry — a shallow "child field replaces parent field" merge gets real defs wrong
    // in a way that invents findings. `Undercave`'s BiomeDef declares
    // `<baseWeatherCommonalities><Undercave>1</Undercave></baseWeatherCommonalities>` and inherits
    // `Biome_Underground`, which declares `<Underground>1</Underground>` in the same node. RimWorld
    // recurses into the shared node and *appends* the child's key, so the live def offers TWO weathers.
    // A shallow merge reports one — which is exactly the error that made an earlier version of §13's
    // structural guard look like it separated the census perfectly when it did not.
    internal Dictionary<string, XElement> Resolve(XElement element, int depth = 0)
    {
        XElement merged = ResolveElement(element, depth);
        Dictionary<string, XElement> fields = new();
        foreach (XElement child in merged.Elements())
            fields[child.Name.LocalName] = child;

        return fields;
    }

    private XElement ResolveElement(XElement element, int depth)
    {
        if (depth > 20)
            return element;

        string? parentName = element.Attribute("ParentName")?.Value;
        if (parentName == null || !namedDefs.TryGetValue(parentName, out XElement? parent))
            return element;

        XElement resolved = ResolveElement(parent, depth + 1);
        // Copy before mutating: a named parent is shared by every child that inherits it, so merging
        // into it in place would leak one child's fields into the next.
        XElement working = new(resolved);
        MergeInto(element, working);
        return working;
    }

    // `child` wins over `current`, with vanilla's three cases: an <li> is appended (list concatenation),
    // a matching named node is recursed into, and a name the parent does not have is appended.
    // Inherit="false" replaces the node's contents outright.
    private static void MergeInto(XElement child, XElement current)
    {
        if (string.Equals(child.Attribute("Inherit")?.Value, "false", StringComparison.OrdinalIgnoreCase))
        {
            current.ReplaceNodes(child.Nodes());
            return;
        }

        // A node with text and no element children is a leaf value: the child's text replaces the
        // parent's outright rather than being merged into it.
        if (!child.HasElements)
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
                current.ReplaceNodes(child.Nodes());
            return;
        }

        foreach (XElement node in child.Elements())
        {
            if (node.Name.LocalName == "li")
            {
                current.Add(new XElement(node));
                continue;
            }

            XElement? existing = current.Element(node.Name);
            if (existing == null)
                current.Add(new XElement(node));
            else
                MergeInto(node, existing);
        }
    }

    // --- Field readers ---

    internal static string? Text(Dictionary<string, XElement> fields, string name) =>
        fields.TryGetValue(name, out XElement? element) ? element.Value.Trim() : null;

    internal static float Float(Dictionary<string, XElement> fields, string name, float fallback)
    {
        string? text = Text(fields, name);
        if (text == null)
            return fallback;

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;
    }

    internal static bool Bool(Dictionary<string, XElement> fields, string name, bool fallback)
    {
        string? text = Text(fields, name);
        return text == null ? fallback : string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<XElement> Children(Dictionary<string, XElement> fields, string name) =>
        fields.TryGetValue(name, out XElement? element) ? element.Elements() : Enumerable.Empty<XElement>();

    // Mirrors Verse.ParseHelper.ParseColor, and this is not a detail worth glossing: if ANY of the
    // three components exceeds 1, RimWorld reads the whole triple as 0-255 bytes and divides. Reading
    // the XML floats literally is exactly the mistake §13's original audit made — it read Vanilla
    // Psycasts Expanded Hemosage's Bloodstorm palette of "(255,0,0)" as a superwhite sky and reported
    // that a full rainstorm dimmed 0%, when the game sees pure red and dims it 25.5%.
    internal static (float R, float G, float B)? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string[] parts = text.Trim().Trim('(', ')').Split(',');
        if (parts.Length < 3)
            return null;

        float[] rgb = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out rgb[i]))
                return null;
        }

        bool isBytes = rgb[0] > 1f || rgb[1] > 1f || rgb[2] > 1f;
        float scale = isBytes ? 1f / 255f : 1f;
        return (rgb[0] * scale, rgb[1] * scale, rgb[2] * scale);
    }
}
