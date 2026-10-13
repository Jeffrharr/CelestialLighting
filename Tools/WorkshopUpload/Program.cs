using System.Reflection;
using System.Runtime.InteropServices;
using CelestialLighting.Tools;

// WorkshopUpload <item id> <content dir> <preview png> <change note> <tag>...
// WorkshopUpload --show <item id>
//
// Updates an existing Workshop item from a staged folder: content, preview, change note and tags.
// --show changes nothing and prints the item's current tags, which is what publish.sh's dry run
// uses to show the tag change a real push would make.
// Exit 0 on success, 1 if Steam refused the update, 2 if the upload could not be attempted.
//
// WHY NOT STEAMCMD. RimWorld's mod browser files items under version tags ("1.6", "1.7"), and
// `steamcmd +workshop_build_item` has no way to set tags: its VDF maps onto ISteamUGC's SetItem*
// calls but skips SetItemTags, and a "tags" block in the VDF is silently ignored. A release that
// adds a game version therefore uploaded fine and stayed invisible to players filtering by that
// version. This tool makes the calls vanilla's uploader does (Verse.Steam.Workshop.
// SetWorkshopItemDataFrom) but against the staged dist/ folder rather than the mod's repo root.
//
// It deliberately leaves title and description alone. Vanilla rewrites the title from About.xml on
// every upload and the description only on creation; the Workshop page is edited by hand, so
// neither is ours to overwrite from here.
//
// SetItemTags REPLACES the whole tag set, so the caller passes every tag the item should carry,
// including vanilla's "Mod" (ModMetaData.GetWorkshopTags), not just the versions.
//
// Runs as app 294100 through the logged-in Steam client, so Steam must be running and signed in to
// the account that owns the item. No password or Steam Guard prompt is involved, unlike steamcmd.

bool show = args.Length == 2 && args[0] == "--show";
if (!show && args.Length < 5)
{
    Console.Error.WriteLine("usage: WorkshopUpload <item id> <content dir> <preview png> <change note> <tag>...");
    Console.Error.WriteLine("       WorkshopUpload --show <item id>");
    return 2;
}

string gameDir = Environment.GetEnvironmentVariable("RIMWORLD_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local/share/Steam/steamapps/common/RimWorld");

if (!File.Exists(Path.Combine(gameDir, "com.rlabrecque.steamworks.net.dll")))
{
    Console.Error.WriteLine($"WorkshopUpload: no Steamworks.NET in {gameDir} — is RimWorld installed there?");
    return 2;
}

// Steamworks.NET comes from the install, and its P/Invokes to "steam_api" are pointed at the
// install's libsteam_api.so, so the managed and native halves always match each other.
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    string candidate = Path.Combine(gameDir, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};
AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
{
    if (e.LoadedAssembly.GetName().Name == "com.rlabrecque.steamworks.net")
    {
        NativeLibrary.SetDllImportResolver(e.LoadedAssembly, (_, _, _) =>
            NativeLibrary.Load(Path.Combine(gameDir, "libsteam_api.so")));
    }
};

// SteamAPI.Init reads the app id from the environment when there is no steam_appid.txt beside us.
// It has to be libc's setenv: on Unix, Environment.SetEnvironmentVariable only edits .NET's private
// copy, which native code never sees, and Init fails with "no appID found".
setenv("SteamAppId", "294100", 1);
setenv("SteamGameId", "294100", 1);

if (show)
{
    return Uploader.Show(ulong.Parse(args[1]));
}

return Uploader.Run(
    ulong.Parse(args[0]),
    Path.GetFullPath(args[1]),
    Path.GetFullPath(args[2]),
    args[3],
    args.Skip(4).ToList());

[DllImport("libc")]
static extern int setenv(string name, string value, int overwrite);
