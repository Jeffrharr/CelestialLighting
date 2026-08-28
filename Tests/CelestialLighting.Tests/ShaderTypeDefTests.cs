using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CelestialLighting.Tests;

/// <summary>
/// Pins the four places each shipped shader's identity is written down against each other: the
/// <c>ShaderTypeDef</c>, the <c>ShaderPath</c> const in the adapter, the asset name the Unity bundle
/// build puts in the bundle, and the name the <c>.shader</c> file itself declares.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS CATCHES THAT NOTHING ELSE DOES, and why four copies exist at all rather than one. The
/// path cannot be linked the way <c>Tests.csproj</c> links a pure core, because the copies are not
/// all C#: one is XML the game reads, one is a string inside an editor script the game never sees,
/// and one is a <c>Shader "…"</c> declaration in HLSL. Nothing at build time compares them, and
/// nothing at run time complains when they disagree — <c>ShaderDatabase.LoadShader</c> hands back
/// <c>DefaultShader</c> for a path that resolves to nothing, so a one-character drift in any of the
/// four ends as a subsystem quietly running its fallback for the rest of the mod's life.
/// </para>
/// <para>
/// That is not hypothetical for this repo. The volumetric cloud march shipped once with a shader
/// that loaded "successfully" and was vanilla's default wearing the slot, and it drew white slabs
/// while <c>cloud_volume_shader</c> read 1 throughout — see <c>CloudVolumeShader.ShaderName</c>. The
/// <c>ShaderName</c> consts are the run-time half of that guard; this is the offline half, and it
/// runs on every commit with no GPU, no bundle and no RimWorld.
/// </para>
/// <para>
/// It reads all four as TEXT, on purpose and for the same reason <c>AuroraShaderPortTests</c> does:
/// the question is not whether any of them compiles, it is whether they still say the same string.
/// <c>PackagedDefOfTests</c> covers the neighbouring question — that the <c>defName</c> a shipped
/// <c>[DefOf]</c> field binds is one the shipped def tree actually declares — from the built
/// assembly, so it is deliberately not repeated here.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ShaderTypeDefTests
{
    /// <summary>One shipped shader, named in all four places.</summary>
    /// <param name="DefName">The <c>ShaderTypeDef</c>'s defName, and the DefOf field's name.</param>
    /// <param name="Adapter">The <c>Source/</c> file holding the <c>ShaderPath</c> const.</param>
    /// <param name="Path">The bundle-relative path, minus the <c>.shader</c> extension.</param>
    /// <param name="DeclaredName">The name inside the <c>.shader</c> file's own <c>Shader "…"</c>.</param>
    public readonly record struct ShippedShader(
        string DefName, string Adapter, string Path, string DeclaredName);

    private static readonly ShippedShader[] Shaders =
    {
        new("CL_CloudVolume", "CloudVolumeShader.cs", "CelestialCloudVolume",
            "CelestialLighting/CloudVolume"),
        new("CL_VectorLightMax", "VectorLightShader.cs", "VectorLightMax",
            "CelestialLighting/VectorLightMax"),
        new("CL_Aurora", "AuroraShader.cs", "CelestialAurora", "CelestialLighting/Aurora"),
    };

    /// <summary>
    /// The def's <c>shaderPath</c> is what the game loads through, so it is the copy the other three
    /// are measured against rather than one more equal party.
    /// </summary>
    [TestCaseSource(nameof(Shaders))]
    public void DefDeclaresTheExpectedPath(ShippedShader shader)
    {
        XElement? def = DefsXml()
            .Descendants("ShaderTypeDef")
            .FirstOrDefault(e => (string?)e.Element("defName") == shader.DefName);

        Assert.That(def, Is.Not.Null,
            $"No ShaderTypeDef named {shader.DefName} in {DefsPath}. CelestialShaderDefOf binds a "
            + "field of that name, so this ships as a red \"Failed to find Verse.ShaderTypeDef "
            + $"named {shader.DefName}\" and a fall back to the literal path.");

        Assert.That((string?)def!.Element("shaderPath"), Is.EqualTo(shader.Path));
    }

    /// <summary>
    /// The const is the fallback <c>ShaderLoader</c> uses when the def is missing — which is the
    /// normal state of a live harness run, since <c>--mod-overlay</c> swaps assemblies and leaves
    /// <c>Defs/</c> coming from the main checkout. A const that has drifted from its def therefore
    /// fails in precisely the runs meant to verify the shader, and nowhere else.
    /// </summary>
    [TestCaseSource(nameof(Shaders))]
    public void AdapterConstMatchesTheDef(ShippedShader shader)
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, "Source", shader.Adapter));
        Match match = Regex.Match(source, @"const\s+string\s+ShaderPath\s*=\s*""([^""]+)""");

        Assert.That(match.Success, Is.True, $"No ShaderPath const found in {shader.Adapter}.");
        Assert.That(match.Groups[1].Value, Is.EqualTo(shader.Path));
    }

    /// <summary>
    /// The Unity editor script decides where the asset actually lands inside the bundle. It is the
    /// copy furthest from anything that runs in game — nobody opens that project — and so the most
    /// likely of the four to be forgotten when a shader is renamed.
    /// </summary>
    [TestCaseSource(nameof(Shaders))]
    public void BundleBuildShipsTheAssetAtThatPath(ShippedShader shader)
    {
        string build = File.ReadAllText(Path.Combine(
            RepoRoot, "Tools", "ShaderBundle", "Project", "Assets", "Editor",
            "BuildShaderBundles.cs"));

        Assert.That(build, Does.Contain(
            $"\"Assets/Data/joof.celestiallighting/Materials/{shader.Path}.shader\""),
            $"BuildShaderBundles.cs does not put {shader.Path} in the bundle, so the def resolves "
            + "to nothing and the subsystem silently runs its fallback.");
    }

    /// <summary>
    /// The name the HLSL declares, which is what the adapters' <c>ShaderName</c> consts compare a
    /// loaded shader against. A load that failed returns a non-null, supported shader, so this
    /// string is the only thing separating "ours" from "vanilla's default in our slot".
    /// </summary>
    [TestCaseSource(nameof(Shaders))]
    public void ShaderFileDeclaresTheNameTheAdapterChecksFor(ShippedShader shader)
    {
        string path = Path.Combine(
            RepoRoot, "Tools", "ShaderBundle", "Project", "Assets", "Data", "joof.celestiallighting",
            "Materials", shader.Path + ".shader");

        Assert.That(File.Exists(path), Is.True, $"{shader.Path}.shader is missing from {path}.");

        Match declared = Regex.Match(File.ReadAllText(path), @"^\s*Shader\s+""([^""]+)""",
            RegexOptions.Multiline);

        Assert.That(declared.Success, Is.True, $"No Shader \"…\" declaration in {shader.Path}.shader.");
        Assert.That(declared.Groups[1].Value, Is.EqualTo(shader.DeclaredName));
    }

    /// <summary>
    /// Every def in the file is bound by a DefOf field, and every field has a def. The first half is
    /// what stops a def being shipped that nothing reads; the second is the packaging bug v1.0.0
    /// shipped, caught here at source level so it fails without a build as well as with one.
    /// </summary>
    [Test]
    public void DefOfFieldsAndDefsAreTheSameSet()
    {
        string[] fields = Regex.Matches(
                File.ReadAllText(Path.Combine(RepoRoot, "Source", "CelestialShaderDefOf.cs")),
                @"public\s+static\s+ShaderTypeDef\s+(\w+)\s*;")
            .Select(m => m.Groups[1].Value)
            .OrderBy(n => n)
            .ToArray();

        string[] defNames = DefsXml()
            .Descendants("ShaderTypeDef")
            .Select(e => (string)e.Element("defName")!)
            .OrderBy(n => n)
            .ToArray();

        Assert.That(fields, Is.EqualTo(defNames));
    }

    private static XDocument DefsXml() => XDocument.Load(DefsPath);

    private static string DefsPath => Path.Combine(
        RepoRoot, "1.6", "Defs", "ShaderTypeDefs", "ShaderTypes.xml");

    // Resolved from this file's own compile-time path rather than the test binary's working
    // directory, which moves with the target framework and the runner (same trick as
    // PackagedDefOfTests).
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
