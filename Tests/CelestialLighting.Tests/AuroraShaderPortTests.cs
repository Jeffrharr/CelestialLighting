using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

/// <summary>
/// Pins CelestialAurora.shader's copied constants against the pure core they were copied from
/// (issue #196).
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS CATCHES THAT NOTHING ELSE DOES. Moving the curtain's field into HLSL left the repo with
/// two copies of every constant in it, and the copies cannot be linked the way
/// <c>Tests.csproj</c> links a pure core — one of them is text inside a shader. So the ordinary
/// failure is not a bad port, it is a good port going stale: somebody retunes
/// <c>AuroraCurtainHemRays.RayFloor</c> six months from now, every offline test still passes because
/// they all test the C# side, and the aurora on screen quietly keeps the old value. Nothing about
/// that is visible — the whole subsystem's output is "plausible drifting light".
/// </para>
/// <para>
/// The live <c>aurora_shader_agreement</c> probe would catch it, but only on a machine with a GPU,
/// a built bundle and someone running the scenario. This runs on every commit with none of those,
/// which is what makes it the guard that actually fires.
/// </para>
/// <para>
/// It reads the shader as TEXT on purpose. Compiling HLSL offline would need a toolchain the repo
/// does not assume, and the thing worth checking is not that the shader compiles — Unity says that
/// at bundle-build time — but that the numbers in it are still the numbers the core defines.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AuroraShaderPortTests
{
    /// <summary>
    /// Shader constant name to the pure-core value it was copied from.
    /// </summary>
    /// <remarks>
    /// Written out rather than discovered by reflection over matching names. Reflection would pass
    /// vacuously the day someone renames a constant on one side — the pair would simply stop being
    /// found — and a drift test that silently stops testing is worse than no drift test. Listing
    /// them means a rename breaks the build here and has to be resolved deliberately.
    /// </remarks>
    private static readonly Dictionary<string, float> ExpectedConstants = new()
    {
        ["DriftRate"] = AuroraCurtainHemRays.DriftRate,
        ["DriftWrapCycle"] = AuroraCurtainHemRays.DriftWrapCycle,
        ["HemUnderhang"] = AuroraCurtainHemRays.HemUnderhang,
        ["HemCoreHeight"] = AuroraCurtainHemRays.HemCoreHeight,
        ["HemCoreGain"] = AuroraCurtainHemRays.HemCoreGain,
        ["RayFloor"] = AuroraCurtainHemRays.RayFloor,
        ["RaySharpen"] = AuroraCurtainHemRays.RaySharpen,
        ["FalloffCurvature"] = AuroraCurtainHemRays.FalloffCurvature,
        ["RayTopFloor"] = AuroraCurtainHemRays.RayTopFloor,
        ["RayClumpDepth"] = AuroraCurtainHemRays.RayClumpDepth,
        ["HueAtHem"] = AuroraCurtainHemRays.HueAtHem,
        ["HueWobblePeriod"] = AuroraCurtainHemRays.HueWobblePeriod,
        ["HueWobbleAmplitude"] = AuroraCurtainHemRays.HueWobbleAmplitude,
        ["HueWobbleDrift"] = AuroraCurtainHemRays.HueWobbleDrift,
        ["EdgeFeather"] = AuroraCurtainHemRays.EdgeFeather,
        ["HorizontalTaper"] = AuroraCurtainHemRays.HorizontalTaper,
        ["HueGreenLow"] = AuroraMath.HueGreenLow,
        ["HueGreenHigh"] = AuroraMath.HueGreenHigh,
    };

    [Test]
    public void ShaderSourceIsWhereTheBundleBuildLooksForIt()
    {
        Assert.That(File.Exists(ShaderPath), Is.True,
            $"CelestialAurora.shader is missing from {ShaderPath}. The path is not incidental: "
            + "RimWorld resolves a mod shader by its path inside the bundle, so Unity has to see it "
            + "at exactly Assets/Data/<packageId>/Materials/<name>.shader.");
    }

    [Test]
    [TestCaseSource(nameof(ConstantNames))]
    public void ShaderConstantMatchesThePureCore(string name)
    {
        float expected = ExpectedConstants[name];
        float actual = ReadConstant(name);

        // Exact rather than approximate. These are literals copied from one file to another, so any
        // difference at all is a copy that has gone stale — there is no arithmetic in between for a
        // tolerance to absorb.
        Assert.That(actual, Is.EqualTo(expected),
            $"CelestialAurora.shader declares {name} = {actual}, but the pure core says {expected}. "
            + "The shader is a copy of the core's constants and this one has drifted; the aurora on "
            + "screen is using the shader's value, not the core's.");
    }

    /// <summary>
    /// The three curtain rows, which carry the shape of the whole effect and are the copy most
    /// likely to be edited on one side only.
    /// </summary>
    /// <remarks>
    /// Unrolled as literals in the shader — see the comment on <c>AuroraField</c> for why — which
    /// means fifteen numbers per curtain sitting in argument order with nothing naming them. That is
    /// exactly the arrangement in which a transposed pair survives review, so the test reads them
    /// positionally and compares against <see cref="AuroraCurtainHemRays.Curtain"/>.
    /// </remarks>
    [Test]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void ShaderCurtainRowMatchesThePureCore(int index)
    {
        AuroraCurtainHemRays.CurtainSpec expected = AuroraCurtainHemRays.Curtain(index);
        float[] got = ReadCurtainRow(index);

        Assert.That(got.Length, Is.EqualTo(15),
            $"Curtain row {index} in CelestialAurora.shader has {got.Length} arguments, not 15. "
            + "AuroraEvaluateColumn's signature and this row have to agree positionally.");

        Assert.Multiple(() =>
        {
            Assert.That(got[0], Is.EqualTo(expected.HemCenter), "HemCenter");
            Assert.That(got[1], Is.EqualTo(expected.HemPeriod), "HemPeriod");
            Assert.That(got[2], Is.EqualTo(expected.HemOctaves), "HemOctaves");
            Assert.That(got[3], Is.EqualTo(expected.HemAmplitude), "HemAmplitude");
            Assert.That(got[4], Is.EqualTo(expected.HemDrift), "HemDrift");
            Assert.That(got[5], Is.EqualTo(expected.HemRise).Within(1e-6f), "HemRise");
            Assert.That(got[6], Is.EqualTo(expected.RayPeriod), "RayPeriod");
            Assert.That(got[7], Is.EqualTo(expected.RayDrift), "RayDrift");
            Assert.That(got[8], Is.EqualTo(expected.RayClumpPeriod), "RayClumpPeriod");
            Assert.That(got[9], Is.EqualTo(expected.EnvelopePeriod), "EnvelopePeriod");
            Assert.That(got[10], Is.EqualTo(expected.EnvelopeDrift), "EnvelopeDrift");
            Assert.That(got[11], Is.EqualTo(expected.RayHeight), "RayHeight");
            Assert.That(got[12], Is.EqualTo(expected.Weight), "Weight");
            Assert.That(got[13], Is.EqualTo(expected.CurtainHue), "CurtainHue");
            Assert.That(got[14], Is.EqualTo(expected.Seed), "Seed");
        });
    }

    [Test]
    public void ShaderPaletteMatchesThePureCore()
    {
        Assert.Multiple(() =>
        {
            AssertColour("CurtainPurple", AuroraCurtainHemRays.CurtainPurple);
            AssertColour("OxygenGreen", AuroraMath.OxygenGreen);
            AssertColour("OxygenRed", AuroraMath.OxygenRed);
        });
    }

    /// <summary>
    /// The seed offsets the port had to carry across by hand, one per noise call in a column.
    /// </summary>
    /// <remarks>
    /// Worth their own test because they are the one class of typo that produces a completely valid
    /// aurora. Every one of these picks a different hash stream; get one wrong and the rays are
    /// somewhere else, the clumping is somewhere else, and the result is exactly as plausible as the
    /// right answer. Neither a screenshot nor a reviewer can tell.
    /// </remarks>
    [Test]
    [TestCase(701)]
    [TestCase(907)]
    [TestCase(1109)]
    [TestCase(1301)]
    public void ShaderCarriesTheSeedOffset(int offset)
    {
        Assert.That(Source, Does.Contain($"seed + {offset}"),
            $"CelestialAurora.shader no longer offsets a noise seed by {offset}. Each offset selects "
            + "a different hash stream, so a missing or altered one draws a different aurora that is "
            + "exactly as plausible as the right one.");
    }

    /// <summary>The hash's three magic multipliers, which define the noise itself.</summary>
    [Test]
    [TestCase("374761393")]
    [TestCase("1274126177")]
    public void ShaderCarriesTheHashMultiplier(string multiplier)
    {
        Assert.That(Source, Does.Contain(multiplier),
            $"CelestialAurora.shader no longer contains the hash multiplier {multiplier}. "
            + "AuroraNoise.Hash01 and the shader must agree bit for bit or they are different fields.");
    }

    private static IEnumerable<string> ConstantNames() => ExpectedConstants.Keys;

    private static void AssertColour(string name, AuroraMath.Rgb expected)
    {
        float[] got = ReadFloat3(name);

        Assert.That(got, Is.EqualTo(new[] { expected.R, expected.G, expected.B }).AsCollection, name);
    }

    private static float ReadConstant(string name)
    {
        Match match = Regex.Match(
            Source, @"static\s+const\s+(?:float|int)\s+" + Regex.Escape(name) + @"\s*=\s*([-0-9.eE]+)\s*;");

        Assert.That(match.Success, Is.True,
            $"CelestialAurora.shader declares no constant named {name}.");

        return Parse(match.Groups[1].Value);
    }

    private static float[] ReadFloat3(string name)
    {
        Match match = Regex.Match(
            Source,
            @"static\s+const\s+float3\s+" + Regex.Escape(name)
            + @"\s*=\s*float3\(([^)]*)\)\s*;");

        Assert.That(match.Success, Is.True,
            $"CelestialAurora.shader declares no float3 named {name}.");

        return ParseList(match.Groups[1].Value);
    }

    /// <summary>
    /// The argument list of the <c>index</c>-th AuroraEvaluateColumn call, minus its leading
    /// <c>u, drift</c>.
    /// </summary>
    private static float[] ReadCurtainRow(int index)
    {
        MatchCollection matches = Regex.Matches(
            Source, @"AuroraEvaluateColumn\(u,\s*drift,\s*([^)]*)\)");

        Assert.That(matches.Count, Is.EqualTo(AuroraCurtainHemRays.CurtainCount),
            $"CelestialAurora.shader unrolls {matches.Count} curtains, but the pure core defines "
            + $"{AuroraCurtainHemRays.CurtainCount}. A curtain added on one side and not the other "
            + "changes the sky without changing any constant.");

        return ParseList(matches[index].Groups[1].Value);
    }

    private static float[] ParseList(string arguments)
    {
        List<float> values = new();

        foreach (string raw in arguments.Split(','))
        {
            string trimmed = raw.Trim();

            if (trimmed.Length > 0)
                values.Add(Parse(trimmed));
        }

        return values.ToArray();
    }

    /// <summary>
    /// Parses one literal, including the <c>1.0 / 32.0</c> form the hem-rise values are written in.
    /// </summary>
    /// <remarks>
    /// Kept as a division in the shader rather than pre-divided, exactly as the core writes it,
    /// because 2/32 reads as "two thirty-seconds of a tile" and 0.0625 reads as nothing.
    /// </remarks>
    private static float Parse(string literal)
    {
        string[] parts = literal.Split('/');

        if (parts.Length == 2)
            return ParseSingle(parts[0]) / ParseSingle(parts[1]);

        return ParseSingle(literal);
    }

    private static float ParseSingle(string literal) =>
        float.Parse(literal.Trim().TrimEnd('f'), CultureInfo.InvariantCulture);

    private static string Source => _source ??= File.ReadAllText(ShaderPath);

    private static string? _source;

    private static string ShaderPath => Path.Combine(
        RepoRoot, "Tools", "ShaderBundle", "Project", "Assets", "Data", "joof.celestiallighting",
        "Materials", "CelestialAurora.shader");

    // Resolved from this file's own compile-time path rather than the test binary's working
    // directory, which moves with the target framework and the runner (same trick as
    // PackagedDefOfTests).
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
