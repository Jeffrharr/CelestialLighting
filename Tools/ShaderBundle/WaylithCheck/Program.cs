using System.Reflection;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

// WaylithCheck <bundle> [<rimworld dir>]
//
// Asks RimWorld 1.7's own loader whether it would accept every shader in a bundle, without launching
// the game. Exit 0 if all pass, 1 if any would be rejected, 2 if the check itself could not run.
//
// WHY THE GAME'S CODE AND NOT A COPY OF ITS RULES. 1.7 (Waylith) does not run Unity's shader
// pipeline; UnityStubs reads the bundle itself, picks one variant, and rewrites that variant's SPIR-V
// bindings into its own layout (UnityShaderAsset.Read -> UnityShaderBindingPlan.Build ->
// SpirvModule.Rewrite). Every step can refuse a shader, and a refused shader is a single warning in
// Player.log while the mod quietly falls back to its CPU path — so a broken bundle looks like a
// working game. The rules are undocumented internals that will change between patches, so a
// reimplementation would be wrong the first time Ludeon touched them, and wrong silently. Calling
// the installed engine by reflection is the opposite trade: when Ludeon renames something this tool
// breaks LOUDLY (exit 2), which is the correct outcome for a gate.
//
// Everything called here is GPU-free; the steps after Rewrite (pipeline creation) need a device and
// are left to the live harness. That is also why this does not replace a live run: it proves the
// shader loads, not that it draws the right thing.
//
// WHAT IT ALSO CHECKS, beyond "accepted":
//   - the variant Waylith picks is KEYWORDLESS. It takes the variant with the fewest keywords and
//     never switches at runtime, so a shader whose real program sits behind a keyword would load
//     and draw the wrong program.
//   - no shader carries m_CommonParameters. Unity hoists parameters shared by every variant into
//     that block and UnityStubs never reads it, which is the failure this tool was written for: a
//     single-variant shader loses all its bindings and is refused with "buffer at set 1 binding 1 is
//     not a known constant buffer". Reported separately from acceptance so a future engine that does
//     read the block can be told apart from a bundle that stopped needing it.

string bundlePath = args.Length > 0 ? args[0] : throw Usage();
string gameDir = args.Length > 1
    ? args[1]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local/share/Steam/steamapps/common/RimWorld");

if (!File.Exists(Path.Combine(gameDir, "UnityStubs.dll")))
{
    Console.Error.WriteLine($"WaylithCheck: no UnityStubs.dll in {gameDir} — is RimWorld 1.7 installed there?");
    return 2;
}

// The game's assemblies come from the install, not from our output folder: UnityStubs pulls in
// Waylith and its Brutal.* dependencies, and pinning copies here would test an engine nobody runs.
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    string candidate = Path.Combine(gameDir, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

try
{
    return Checker.Run(bundlePath);
}
catch (Exception e) when (e is MissingMemberException || e is TypeLoadException || e is TargetInvocationException
                           || e is InvalidOperationException || e is AmbiguousMatchException)
{
    Console.Error.WriteLine("WaylithCheck: could not drive the engine's loader — UnityStubs internals have");
    Console.Error.WriteLine("probably changed in a game update. Update this tool before trusting a bundle.");
    Console.Error.WriteLine(e);
    return 2;
}

static Exception Usage() => new ArgumentException("usage: WaylithCheck <bundle> [<rimworld dir>]");

static class Checker
{
    private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Stage bits SpirvModule.Rewrite takes, as AssetBundleShaders.BuildPass passes them.
    private const int VertexStage = 4;
    private const int FragmentStage = 8;

    public static int Run(string bundlePath)
    {
        Assembly stubs = typeof(UnityEngine.Shader).Assembly;
        Type assetType = Required(stubs.GetType("UnityStubs.Internal.UnityShaderAsset"), "UnityShaderAsset");
        Type planType = Required(stubs.GetType("UnityStubs.Internal.UnityShaderBindingPlan"), "UnityShaderBindingPlan");
        Type spirvType = Required(stubs.GetType("UnityStubs.Internal.SpirvModule"), "SpirvModule");
        MethodInfo read = Method(assetType, "Read");
        MethodInfo buildPlan = Method(planType, "Build");
        MethodInfo rewrite = Method(spirvType, "Rewrite");

        var manager = new AssetsManager();
        BundleFileInstance bundle = manager.LoadBundleFile(bundlePath, true);
        AssetsFileInstance file = manager.LoadAssetsFileFromBundle(bundle, 0, false);

        int failures = 0;
        int shaders = 0;
        foreach (AssetFileInfo info in file.file.GetAssetsOfType(AssetClassID.Shader))
        {
            shaders++;
            AssetTypeValueField field = manager.GetBaseField(file, info);
            string name = field["m_ParsedForm"]["m_Name"].AsString;
            List<string> problems = Problems(field, read, buildPlan, rewrite, out string blend);
            failures += problems.Count == 0 ? 0 : 1;
            Console.WriteLine($"  {(problems.Count == 0 ? "ok  " : "FAIL")}  {name,-40} {blend}");
            foreach (string problem in problems)
                Console.WriteLine($"          {problem}");
        }

        if (shaders == 0)
        {
            Console.WriteLine($"WaylithCheck: {bundlePath} contains no shaders.");
            return 1;
        }
        Console.WriteLine(failures == 0
            ? $"WaylithCheck: all {shaders} shaders accepted by the installed engine."
            : $"WaylithCheck: {failures} of {shaders} shaders would be rejected or misdrawn on 1.7.");
        return failures == 0 ? 0 : 1;
    }

    private static List<string> Problems(
        AssetTypeValueField field, MethodInfo read, MethodInfo buildPlan, MethodInfo rewrite, out string blend)
    {
        var problems = new List<string>();
        blend = "";
        if (HasCommonParameters(field))
            problems.Add("has m_CommonParameters, which UnityStubs never reads (parameters shared by every variant)");

        object?[] readArgs = { field, null };
        object? asset = read.Invoke(null, readArgs);
        if (asset == null)
        {
            problems.Add($"UnityShaderAsset.Read refused it: {readArgs[1]}");
            return problems;
        }

        // Pass 0 only, as AssetBundleShaders.Build does: it is the pass the material draws with, and
        // a failing extra pass is a warning there rather than a refusal.
        object pass = Get(asset, "Pass")!;
        blend = BlendOf(pass);
        string[] keywords = (string[])(Get(pass, "Keywords") ?? Array.Empty<string>());
        if (keywords.Length != 0)
            problems.Add($"Waylith would draw the variant built with [{string.Join(", ", keywords)}]; it needs a keywordless one");

        object plan = buildPlan.Invoke(null, new[] { pass })!;
        string? failure = RewriteFailure(rewrite, (byte[])Get(pass, "VertexSpirv")!, VertexStage, pass, plan)
            ?? RewriteFailure(rewrite, (byte[])Get(pass, "FragmentSpirv")!, FragmentStage, pass, plan);
        if (failure != null)
            problems.Add($"SpirvModule.Rewrite refused it: {failure}");
        return problems;
    }

    // THE BLEND WAYLITH WILL BAKE, as UnityShaderAsset.ReadState resolved it: a property-driven
    // Blend [_SrcBlend] [_DstBlend] is already collapsed to the property's default here, which is
    // exactly the value 1.7 draws with forever. Printed for every shader so a blend that a material was
    // meant to switch at runtime shows up at build time as the one value it will actually have
    // (issue #257) — VectorLightMax and VectorLightMaxLift must read One/One and DstColor/One.
    private static string BlendOf(object pass)
    {
        object state = Get(pass, "State")!;
        return $"blend {Factor((float)Get(state, "SrcBlend")!)} {Factor((float)Get(state, "DstBlend")!)}";
    }

    // Unity's UnityEngine.Rendering.BlendMode numbering, which is what the bundle stores.
    private static string Factor(float value) => (int)value switch
    {
        0 => "Zero", 1 => "One", 2 => "DstColor", 3 => "SrcColor", 4 => "OneMinusDstColor",
        5 => "SrcAlpha", 6 => "OneMinusSrcColor", 7 => "DstAlpha", 8 => "OneMinusDstAlpha",
        9 => "SrcAlphaSaturate", 10 => "OneMinusSrcAlpha", _ => $"#{value}",
    };

    private static string? RewriteFailure(MethodInfo rewrite, byte[] spirv, int stage, object pass, object plan)
    {
        object?[] args = { spirv, stage, pass, plan, null };
        return rewrite.Invoke(null, args) == null ? (string?)args[4] ?? "no reason given" : null;
    }

    // Any populated block counts. An empty m_CommonParameters is what a healthy multi-variant shader
    // serialises, so presence of the field alone means nothing.
    private static bool HasCommonParameters(AssetTypeValueField field)
    {
        foreach (AssetTypeValueField sub in field["m_ParsedForm"]["m_SubShaders"]["Array"].Children)
        foreach (AssetTypeValueField pass in sub["m_Passes"]["Array"].Children)
        foreach (string stage in new[] { "progVertex", "progFragment" })
        {
            AssetTypeValueField common = pass[stage]["m_CommonParameters"];
            if (!common.IsDummy && IsPopulated(common))
                return true;
        }
        return false;
    }

    private static bool IsPopulated(AssetTypeValueField common)
    {
        foreach (AssetTypeValueField block in common.Children)
        {
            AssetTypeValueField array = block["Array"];
            if (!array.IsDummy && array.Children.Count > 0)
                return true;
        }
        return false;
    }

    // Static methods only: the loader's entry points are static, and SpirvModule also has an instance
    // Rewrite that the static one delegates to. Exactly one match or the engine has changed shape.
    private static MethodInfo Method(Type type, string name) =>
        type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(m => m.Name == name)
        ?? throw new MissingMethodException(type.FullName, name);

    private static Type Required(Type? type, string name) =>
        type ?? throw new TypeLoadException($"UnityStubs no longer has {name}");

    private static object? Get(object target, string member)
    {
        Type type = target.GetType();
        return type.GetProperty(member, Any)?.GetValue(target)
            ?? type.GetField(member, Any)?.GetValue(target);
    }
}
