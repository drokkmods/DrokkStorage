using System;

// Shared "where do Drokk API requests go" resolver. Lives in mods/_Shared/ and is symlinked
// into each mod that needs it (see mods/_Shared/README.md) - edit it HERE, once. Build every
// request URL with DrokkApi.Url("/api/...") instead of hardcoding a host, so one launch flag
// switches ALL of them at once.
//
// Normally requests go to production (drokkmods.fyi). Launching the game with -staging
// (build_and_run.sh -staging passes it through) points them at the staging Cloud Run
// revision instead. Each mod compiles its own copy of this type, so the flag is read
// per-assembly; it is a launch argument, so every copy resolves to the same answer.
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
