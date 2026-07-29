using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Mono.Cecil;

namespace CelestialLighting.Tests;

/// <summary>
/// Pins the agreement between the assembly a release ships and the defs that release ships with it:
/// every <c>[DefOf]</c> field in <c>CelestialLighting.dll</c> must name a def that will actually be
/// in the database at bind time.
/// </summary>
/// <remarks>
/// <para>
/// This exists because v1.0.0 shipped to the Workshop broken in exactly that way. The assembly
/// carried <c>CelestialLightingDefOf.CL_SunShadowAxis</c>, the def that satisfied it lived in
/// <c>1.6/Defs/MapMeshFlagDefs/</c>, and the staging script's guard for "did we forget to ship
/// content" passed vacuously — so subscribers got <c>Failed to find RimWorld.MapMeshFlagDef named
/// CL_SunShadowAxis. There are 15 defs of this type loaded.</c> and a silently null DefOf.
/// </para>
/// <para>
/// Nothing catches this locally, which is the whole point of writing it down as a test. The dev
/// install is a symlink to the repo, so the running game always sees the full tree — assembly and
/// defs together — and the two can only disagree in the staged package, which no one boots.
/// <c>publish.sh</c> runs this test against <c>dist/</c> via <c>CL_PACKAGE_ROOT</c> for that
/// reason; run bare, it checks the repo tree, which is the same check one step earlier.
/// </para>
/// <para>
/// The binding rules mirrored here are <c>RimWorld.DefOfHelper.BindDefsFor</c>: public static fields
/// only, the field's own name is the defName unless <c>[DefAlias]</c> overrides it, and a field
/// gated by <c>[MayRequire…]</c> is allowed to come up null (RimWorld suppresses the error when the
/// named mod is inactive), so those are not required to resolve.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresModDll")]
public class PackagedDefOfTests
{
    private const string DefOfAttributeName = "RimWorld.DefOf";
    private const string DefAliasAttributeName = "RimWorld.DefAliasAttribute";

    // Every MayRequire flavour shares this prefix (MayRequireAttribute, MayRequireAnyOfAttribute,
    // MayRequireRoyaltyAttribute, …), and all of them mean the same thing here: the def is allowed
    // to be absent, so its absence is not a packaging bug.
    private const string MayRequirePrefix = "RimWorld.MayRequire";

    private const string FallbackDataDir =
        "/home/deck/.local/share/Steam/steamapps/common/RimWorld/Data";

    /// <summary>Vanilla + DLC defs, which a shipped DefOf may legitimately point at.</summary>
    private static string DataDir =>
        Environment.GetEnvironmentVariable("RIMWORLD_DATA") ?? FallbackDataDir;

    /// <summary>
    /// The mod tree under test: the staged package when <c>publish.sh</c> sets CL_PACKAGE_ROOT,
    /// otherwise this repo. Both have the same shape — <c>1.6/Assemblies/</c> beside <c>1.6/Defs/</c>
    /// — so one code path checks either.
    /// </summary>
    private static string PackageRoot =>
        Environment.GetEnvironmentVariable("CL_PACKAGE_ROOT") ?? RepoRoot;

    // Resolved from this file's own compile-time path rather than the test binary's working
    // directory, which moves with the target framework and the runner (same trick as
    // NightDesaturationGateTests).
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));

    private static string ModDllPath =>
        Path.Combine(PackageRoot, "1.6", "Assemblies", "CelestialLighting.dll");

    /// <summary>A def some shipped DefOf field will demand: its declared type, and the name bound.</summary>
    private readonly record struct DefRequirement(string DefType, string DefName)
    {
        public override string ToString() => $"{DefType} {DefName}";
    }

    [Test]
    public void EveryDefOfFieldNamesADefTheGameWillHave()
    {
        if (!File.Exists(ModDllPath))
            Assert.Ignore($"CelestialLighting.dll not found at {ModDllPath} — run ./build.sh first.");

        List<DefRequirement> required = RequiredDefs();

        // The common case today is an empty list — this mod defines no DefOf at all — and that costs
        // nothing: with nothing to look for, neither def tree is read.
        List<DefRequirement> missing = required.Where(r => !DefIsDeclared(r.DefName)).ToList();

        Assert.That(missing, Is.Empty,
            $"{ModDllPath} binds DefOf fields to defs that neither {PackageRoot} nor {DataDir} "
            + "declares — this ships as \"Failed to find <type> named <name>\" and a null DefOf:\n"
            + string.Join("\n", missing.Select(m => $"  {m}")));
    }

    // --- what the assembly demands ---

    private static List<DefRequirement> RequiredDefs()
    {
        using ModuleDefinition module = ModuleDefinition.ReadModule(ModDllPath);

        return module.Types
            .Where(HasAttribute(DefOfAttributeName))
            .SelectMany(t => t.Fields)
            .Where(IsBoundField)
            .Select(f => new DefRequirement(f.FieldType.Name, BoundDefName(f)))
            .ToList();
    }

    // BindDefsFor reads GetFields(BindingFlags.Static | BindingFlags.Public) — a private or instance
    // field on a [DefOf] class is never bound and so demands nothing.
    private static bool IsBoundField(FieldDefinition field) =>
        field.IsStatic && field.IsPublic && !IsOptional(field);

    private static bool IsOptional(FieldDefinition field) =>
        field.CustomAttributes.Any(a => a.AttributeType.FullName.StartsWith(MayRequirePrefix, StringComparison.Ordinal));

    private static string BoundDefName(FieldDefinition field)
    {
        CustomAttribute? alias = field.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.FullName == DefAliasAttributeName);

        return alias?.ConstructorArguments.FirstOrDefault().Value as string ?? field.Name;
    }

    private static Func<TypeDefinition, bool> HasAttribute(string fullName) =>
        type => type.CustomAttributes.Any(a => a.AttributeType.FullName == fullName);

    // --- what the def tree declares ---

    /// <summary>
    /// True when some XML in the package or in RimWorld's own Data declares this defName. Matching
    /// is by name alone rather than by name and def class: a base-typed field pointing at a derived
    /// def is legal and would read as a false failure, while two defs of different types sharing one
    /// name — the only way this can pass wrongly — does not happen in practice.
    /// </summary>
    private static bool DefIsDeclared(string defName) =>
        DefFiles().Any(file => Declares(file, defName));

    private static IEnumerable<string> DefFiles() =>
        DefFilesUnder(PackageRoot).Concat(DefFilesUnder(DataDir));

    // Any *.xml sitting under a directory named Defs, at any depth — which is where RimWorld looks
    // (Defs/ and 1.6/Defs/ both load) and where vanilla keeps its own (Data/<content>/Defs/…).
    private static IEnumerable<string> DefFilesUnder(string root)
    {
        if (!Directory.Exists(root))
            return Enumerable.Empty<string>();

        return Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories)
            .Where(IsUnderDefsDirectory);
    }

    private static bool IsUnderDefsDirectory(string path) =>
        path.Split(Path.DirectorySeparatorChar).SkipLast(1).Contains("Defs");

    private static bool Declares(string file, string defName)
    {
        // Cheap reject before the parse. Vanilla's Data/ is thousands of files and only a handful
        // can possibly mention any one name, so this keeps a full check to about a second.
        if (!File.ReadAllText(file).Contains(defName, StringComparison.Ordinal))
            return false;

        XDocument? doc = TryLoad(file);

        return doc is not null
            && doc.Descendants("defName").Any(e => e.Value.Trim() == defName);
    }

    // A def file the game itself could not parse is a different bug with its own loud symptom; for
    // this test it simply declares nothing.
    private static XDocument? TryLoad(string file)
    {
        try
        {
            return XDocument.Load(file);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
