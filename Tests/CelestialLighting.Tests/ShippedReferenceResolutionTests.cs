using Mono.Cecil;

namespace CelestialLighting.Tests;

/// <summary>
/// Resolves every type and member reference the BUILT CelestialLighting.dll makes against the
/// installed game, so a reference the engine no longer satisfies fails here rather than as a
/// TypeLoadException on a subscriber's machine.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS ALONGSIDE ApiCompatibilityTests. That fixture asks whether named vanilla members
/// still exist in Assembly-CSharp — a list we wrote by hand. RimWorld 1.7.4990 moved Waylith's
/// TexturePixelData from Waylith.Rendering to Waylith.Graphics.Textures; nothing in the hand list
/// named it, so the suite stayed green while the shipped DLL threw the first time the vector-light
/// field upload JIT-compiled. The compiler already recorded every reference we make, in every engine
/// assembly, so this reads that record instead of a list that can only cover what someone thought
/// of. A reference into a moved, renamed or removed type fails whichever assembly it lives in.
///
/// It reads whatever ./build.sh last wrote to 1.7/Assemblies, so run build.sh first (the pre-commit
/// hook does) — a stale DLL tests the previous build.
/// </remarks>
[TestFixture]
[Category("RequiresGameDll")]
public class ShippedReferenceResolutionTests
{
    private const string GameDir = "/home/deck/.local/share/Steam/steamapps/common/RimWorld";

    // Harmony is the only non-game assembly the shipped DLL references; 0Harmony lives in the
    // Harmony mod, not the game root. Workshop copy first, then a hand-installed one.
    private static readonly string[] HarmonyDirs =
    {
        "/home/deck/.local/share/Steam/steamapps/workshop/content/294100/2009463077/1.7/Assemblies",
        GameDir + "/Mods/Harmony/1.7/Assemblies",
    };

    private static string ShippedDllPath =>
        Environment.GetEnvironmentVariable("CELESTIAL_LIGHTING_ASSEMBLY")
        ?? Path.Combine(RepoRoot(), "1.7", "Assemblies", "CelestialLighting.dll");

    private ModuleDefinition _module = null!;

    [OneTimeSetUp]
    public void LoadAssembly()
    {
        if (!File.Exists(Path.Combine(GameDir, "Assembly-CSharp.dll")))
            Assert.Ignore($"No 1.7 game install at {GameDir}.");

        // A missing shipped DLL is a failure, not a skip: an ignored fixture reports the suite green,
        // which is exactly how the namespace move got through.
        Assert.That(File.Exists(ShippedDllPath), Is.True,
            $"{ShippedDllPath} not found — run ./build.sh first.");

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(GameDir);
        foreach (string dir in HarmonyDirs.Where(Directory.Exists))
            resolver.AddSearchDirectory(dir);

        _module = ModuleDefinition.ReadModule(ShippedDllPath, new ReaderParameters { AssemblyResolver = resolver });
    }

    [OneTimeTearDown]
    public void Dispose() => _module?.Dispose();

    [Test]
    public void EveryTypeReference_Resolves()
    {
        string[] unresolved = _module.GetTypeReferences()
            .Where(t => !Resolves(() => t.Resolve()))
            .Select(t => $"{t.Scope.Name}: {t.FullName}")
            .ToArray();

        Assert.That(unresolved, Is.Empty);
    }

    [Test]
    public void EveryMemberReference_Resolves()
    {
        string[] unresolved = _module.GetMemberReferences()
            .Where(r => !Resolves(() => r.Resolve()))
            .Select(r => $"{r.DeclaringType.Scope.Name}: {r.FullName}")
            .ToArray();

        Assert.That(unresolved, Is.Empty);
    }

    // Resolve returns null for a missing type or member and throws when a whole assembly is missing;
    // both mean the game cannot load what we compiled against.
    private static bool Resolves(Func<object?> resolve)
    {
        try
        {
            return resolve() != null;
        }
        catch (AssemblyResolutionException)
        {
            return false;
        }
    }

    // The repo root is the first ancestor of the test binary holding About/About.xml.
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "About", "About.xml")))
            dir = dir.Parent;
        return dir?.FullName ?? TestContext.CurrentContext.TestDirectory;
    }
}
