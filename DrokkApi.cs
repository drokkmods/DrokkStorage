using System;

// Shared "where do Drokk API requests go" resolver. Lives in mods/_Shared/ and is symlinked
// into each mod that needs it (see mods/_Shared/README.md) - edit it HERE, once. Build every
// request URL with DrokkApi.Url("/api/...") instead of hardcoding a host, so one launch flag
// switches ALL of them at once.
//
// Normally requests go to production (drokkmods.fyi). Launching the game with -staging
// (build_and_run.sh -staging passes it through) points them at the staging Cloud Run
// revision instead.
//
// SHARED-STATE AUDIT: `resolved` and `baseUrl` below are plain mutable statics, which in any
// other _Shared file would be the bug documented in DrokkModUpdateChecker.cs (each mod
// compiles its own copy of this type, so those fields are per-assembly). Here it is correct
// and deliberately left alone. They are a memoized pure function of
// Environment.GetCommandLineArgs(), which is fixed for the life of the process and identical
// for every assembly in it, so all 14 copies compute the same answer and no copy ever
// observes another's write. Nothing outside this file writes them, so there is no
// cross-assembly sender/reader split to get wrong.
//
// The only per-assembly artifact is cosmetic: the "-staging detected" line logs once per
// assembly that ever calls BaseUrl, not once per process. Left as-is — it names no mod, and
// routing the resolve through AppDomain data to silence a duplicate log line would add a
// shared key that has to stay type-compatible across generations forever.
//
// If this file ever gains state that is NOT derived from immutable process input — a cached
// auth token, a retry counter, a runtime host override — that state must move to
// AppDomain.CurrentDomain Get/SetData. See mods/_Shared/README.md.
public static class DrokkApi
{
    private const string ProductionBaseUrl = "https://drokkmods.fyi";
    private const string StagingBaseUrl = "https://staging---drokkmods-aggnz4ncba-uc.a.run.app";
    private const string StagingArg = "-staging";

    private static bool resolved = false;
    private static string baseUrl = ProductionBaseUrl;

    /// <summary>True when the game was launched with -staging.</summary>
    public static bool IsStaging => BaseUrl == StagingBaseUrl;

    /// <summary>Scheme + host of the Drokk backend, with no trailing slash.</summary>
    public static string BaseUrl
    {
        get
        {
            if (!resolved)
            {
                resolved = true;
                baseUrl = HasStagingArg() ? StagingBaseUrl : ProductionBaseUrl;
                if (baseUrl == StagingBaseUrl)
                {
                    Log.Out($" [DrokkApi] -staging detected; API requests go to {baseUrl}");
                }
            }
            return baseUrl;
        }
    }

    /// <summary>Absolute URL for an API path, e.g. Url("/api/updates").</summary>
    public static string Url(string path)
    {
        if (string.IsNullOrEmpty(path)) return BaseUrl;
        return path[0] == '/' ? BaseUrl + path : BaseUrl + "/" + path;
    }

    private static bool HasStagingArg()
    {
        try
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg.Equals(StagingArg, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning($" [DrokkApi] Could not read command line, using production: {e.Message}");
        }
        return false;
    }
}
