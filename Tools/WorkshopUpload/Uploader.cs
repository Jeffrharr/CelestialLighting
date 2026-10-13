using Steamworks;

namespace CelestialLighting.Tools;

// Kept apart from Program.cs so nothing touches a Steamworks type until Program has installed the
// assembly and native-library resolvers; the JIT resolves a method's referenced types when it
// compiles that method, and Program's top-level code is one method.
static class Uploader
{
    public static int Show(ulong itemId)
    {
        if (!SteamAPI.Init())
        {
            Console.Error.WriteLine("WorkshopUpload: SteamAPI.Init failed — is Steam running and signed in?");
            return 2;
        }

        try
        {
            UGCQueryHandle_t query = SteamUGC.CreateQueryUGCDetailsRequest(new[] { new PublishedFileId_t(itemId) }, 1);
            SteamUGCQueryCompleted_t? completed = Await<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(query));
            bool found = completed is { m_eResult: EResult.k_EResultOK }
                && SteamUGC.GetQueryUGCResult(query, 0, out SteamUGCDetails_t details)
                && details.m_eResult == EResult.k_EResultOK;
            if (!found)
            {
                SteamUGC.ReleaseQueryUGCRequest(query);
                Console.Error.WriteLine($"WorkshopUpload: could not read item {itemId}");
                return 1;
            }

            SteamUGC.GetQueryUGCResult(query, 0, out SteamUGCDetails_t item);
            SteamUGC.ReleaseQueryUGCRequest(query);
            Console.WriteLine(item.m_rgchTags.Replace(",", ", "));
            return 0;
        }
        finally
        {
            SteamAPI.Shutdown();
        }
    }

    public static int Run(ulong itemId, string contentDir, string previewPath, string note, List<string> tags)
    {
        if (!SteamAPI.Init())
        {
            Console.Error.WriteLine("WorkshopUpload: SteamAPI.Init failed — is Steam running and signed in?");
            return 2;
        }

        try
        {
            UGCUpdateHandle_t handle = SteamUGC.StartItemUpdate(new AppId_t(294100), new PublishedFileId_t(itemId));
            bool set = SteamUGC.SetItemContent(handle, contentDir)
                && SteamUGC.SetItemPreview(handle, previewPath)
                && SteamUGC.SetItemTags(handle, tags);
            if (!set)
            {
                Console.Error.WriteLine("WorkshopUpload: Steam rejected the update fields (bad path or handle)");
                return 2;
            }

            Console.WriteLine($"WorkshopUpload: item {itemId}, tags [{string.Join(", ", tags)}]");
            SubmitItemUpdateResult_t? result = Submit(handle, note);
            if (result is not { } r || r.m_eResult != EResult.k_EResultOK)
            {
                Console.Error.WriteLine($"WorkshopUpload: upload failed: {result?.m_eResult.ToString() ?? "no response from Steam"}");
                return 1;
            }

            if (r.m_bUserNeedsToAcceptWorkshopLegalAgreement)
            {
                Console.WriteLine("WorkshopUpload: uploaded, but the item stays hidden until the Workshop legal agreement is accepted on the item page");
            }
            Console.WriteLine("WorkshopUpload: done");
            return 0;
        }
        finally
        {
            SteamAPI.Shutdown();
        }
    }

    // Submission is asynchronous, so progress is printed on change while waiting: a long upload
    // should look like it is moving rather than hung.
    static SubmitItemUpdateResult_t? Submit(UGCUpdateHandle_t handle, string note)
    {
        EItemUpdateStatus lastStatus = EItemUpdateStatus.k_EItemUpdateStatusInvalid;
        return Await<SubmitItemUpdateResult_t>(SteamUGC.SubmitItemUpdate(handle, note), () =>
        {
            EItemUpdateStatus status = SteamUGC.GetItemUpdateProgress(handle, out ulong done, out ulong total);
            if (status != lastStatus)
            {
                Console.WriteLine($"WorkshopUpload: {status} {done}/{total} bytes");
                lastStatus = status;
            }
        });
    }

    // Steam answers API calls through a CallResult that only fires while SteamAPI.RunCallbacks is
    // being pumped. Null means Steam reported an IO failure rather than a result.
    static T? Await<T>(SteamAPICall_t call, Action? onTick = null) where T : struct
    {
        T? result = null;
        bool finished = false;
        var callResult = CallResult<T>.Create((r, ioFailure) =>
        {
            result = ioFailure ? null : r;
            finished = true;
        });
        callResult.Set(call);

        while (!finished)
        {
            SteamAPI.RunCallbacks();
            onTick?.Invoke();
            Thread.Sleep(100);
        }
        return result;
    }
}
