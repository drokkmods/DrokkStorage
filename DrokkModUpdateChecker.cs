using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

// Shared "check drokkmods.fyi for updates" popup. Lives in mods/_Shared/ and is symlinked into
// each mod that opts in (see mods/_Shared/README.md) — edit it HERE, once. A mod opts in by
// symlinking this file plus XUiC_DrokkUpdateNotice.cs and calling
// DrokkModUpdateChecker.Register(_modInstance) once from its InitMod. It still compiles into
// each assembly separately, so the cross-assembly bookkeeping below is still required.
// Every copy registers its own mod name/version into one shared,
// AppDomain-wide list; only the first copy to run actually installs the Harmony patch and
// fires the request, and its popup reports on every Drokk mod that registered (whether that
// mod loaded before or after the patch went in). Uses a fixed HarmonyId so
// Harmony.HasAnyPatches() is the cross-assembly "did someone already patch this" check —
// a plain static field wouldn't be shared, since each mod compiles its own copy of this type.
public static class DrokkModUpdateChecker
{
    private const string HarmonyId = "drokk.updatechecker";
    // Host comes from DrokkApi so -staging redirects this (and every other Drokk API
    // call) to the staging backend in one place.
    private const string UpdatePath = "/api/updates";
    private const int TimeoutSeconds = 5;
    // Query cap, well under the ~8 KB request line most proxies accept.
    private const int MaxUrlLength = 6000;
    private const string AppDomainKey = "DrokkModUpdateChecker.RegisteredMods";
    private const string SettingsFileName = "drokk.json";

    private static bool hasCheckedThisSession = false;
    private static bool settingsLoaded = false;

    // Shared across every Drokk mod (not just this one) — a single drokk.json in the user's
    // game-data folder, not per-mod, since "stop checking for updates" should be one on/off
    // switch regardless of how many Drokk mods are installed.
    public static bool UpdatesEnabled { get; private set; } = true;

    private static string SettingsPath => Path.Combine(GameIO.GetUserGameDataDir(), SettingsFileName);

    public static void Register(Mod _modInstance)
    {
        try
        {
            GetRegisteredMods()[_modInstance.Name] = _modInstance.VersionString;
            EnsureSettingsLoaded();

            if (!Harmony.HasAnyPatches(HarmonyId))
            {
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(
                    AccessTools.Method(typeof(XUiC_MainMenu), "OnOpen"),
                    postfix: new HarmonyMethod(typeof(DrokkModUpdateChecker), nameof(MainMenu_OnOpen_Postfix)));
                Log.Out($" [DrokkModUpdateChecker] Installed by {_modInstance.Name}.");
            }
        }
        catch (Exception e)
        {
            Log.Error($" [DrokkModUpdateChecker] Register failed for {_modInstance.Name}: {e.Message}");
        }
    }

    private static Dictionary<string, string> GetRegisteredMods()
    {
        var mods = AppDomain.CurrentDomain.GetData(AppDomainKey) as Dictionary<string, string>;
        if (mods == null)
        {
            mods = new Dictionary<string, string>();
            AppDomain.CurrentDomain.SetData(AppDomainKey, mods);
        }
        return mods;
    }

    private static void MainMenu_OnOpen_Postfix(XUiC_MainMenu __instance)
    {
        if (hasCheckedThisSession) return;
        hasCheckedThisSession = true;

        if (!UpdatesEnabled)
        {
            Log.Out(" [DrokkModUpdateChecker] Update checks disabled via drokk.json; not contacting the server.");
            return;
        }

        ThreadManager.StartCoroutine(CheckForUpdates(__instance));
    }

    private static void EnsureSettingsLoaded()
    {
        if (settingsLoaded) return;
        settingsLoaded = true;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var obj = JObject.Parse(File.ReadAllText(SettingsPath));
                if (obj.TryGetValue("updates", out JToken token) && token.Type == JTokenType.Boolean)
                {
                    UpdatesEnabled = token.Value<bool>();
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning($" [DrokkModUpdateChecker] Could not read {SettingsPath}: {e.Message}");
        }
    }

    public static void SetUpdatesEnabled(bool enabled)
    {
        UpdatesEnabled = enabled;
        try
        {
            JObject obj = null;
            if (File.Exists(SettingsPath))
            {
                try { obj = JObject.Parse(File.ReadAllText(SettingsPath)); }
                catch (Exception e) { Log.Warning($" [DrokkModUpdateChecker] {SettingsPath} is not valid JSON, replacing it: {e.Message}"); }
            }
            obj ??= new JObject();
            obj["updates"] = enabled;

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath, obj.ToString());
        }
        catch (Exception e)
        {
            Log.Error($" [DrokkModUpdateChecker] Could not write {SettingsPath}: {e.Message}");
        }
    }

    private static IEnumerator CheckForUpdates(XUiC_MainMenu mainMenu)
    {
        string url = BuildUrl(CollectInstalledMods());

        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = TimeoutSeconds;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Log.Out($" [DrokkModUpdateChecker] Update check failed or timed out: {req.error}");
                yield break;
            }

            if (!TryParseResponse(req.downloadHandler.text, out string message))
            {
                yield break;
            }

            XUiC_DrokkUpdateNotice.Show(mainMenu.xui, message);
        }
    }

    // EVERY mod the player has loaded, not just the Drokk ones that called Register() - the
    // server wants the whole load order to reason about conflicts and compatibility, and a
    // Drokk mod that predates this checker (or never symlinked it) would otherwise be
    // invisible. Registered mods are unioned in afterwards as a safety net: they are all in
    // ModManager already, but a registration that arrives from somewhere unusual should still
    // be reported rather than silently dropped.
    private static Dictionary<string, string> CollectInstalledMods()
    {
        var mods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var mod in ModManager.GetLoadedMods())
            {
                if (mod == null || string.IsNullOrEmpty(mod.Name)) continue;
                mods[mod.Name] = mod.VersionString ?? "";
            }
        }
        catch (Exception e)
        {
            Log.Warning($" [DrokkModUpdateChecker] Could not enumerate installed mods: {e.Message}");
        }

        foreach (var kv in GetRegisteredMods())
        {
            mods[kv.Key] = kv.Value;
        }
        return mods;
    }

    private static string BuildUrl(Dictionary<string, string> mods)
    {
        var sb = new StringBuilder(DrokkApi.Url(UpdatePath));
        sb.Append("?game_version=").Append(Uri.EscapeDataString(Constants.cVersionInformation.SerializableString));

        int sent = 0;
        foreach (var kv in mods)
        {
            // A player with a very large mod folder can otherwise build a query long enough
            // for a proxy to reject outright, which would read as "update check timed out"
            // and lose the popup entirely. Better to report most of the list than none of it.
            if (sb.Length > MaxUrlLength)
            {
                Log.Warning($" [DrokkModUpdateChecker] Mod list too long for one URL; reported {sent} of {mods.Count} installed mods.");
                break;
            }
            sb.Append("&mod=").Append(Uri.EscapeDataString(kv.Key)).Append(':').Append(Uri.EscapeDataString(kv.Value));
            sent++;
        }

        Log.Out($" [DrokkModUpdateChecker] Reporting {sent} installed mod(s) to {DrokkApi.BaseUrl}.");
        return sb.ToString();
    }

    private static bool TryParseResponse(string json, out string message)
    {
        message = null;
        try
        {
            var obj = JObject.Parse(json);
            if (obj.Value<bool?>("popup") != true) return false;
            message = obj.Value<string>("message");
            return !string.IsNullOrEmpty(message);
        }
        catch (Exception e)
        {
            Log.Warning($" [DrokkModUpdateChecker] Could not parse update response: {e.Message}");
            return false;
        }
    }
}
