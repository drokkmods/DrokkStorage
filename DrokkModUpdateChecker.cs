using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

// Shared "check drokkmods.fyi for updates" popup. Lives in mods/_Shared/ and is symlinked into
// each mod that opts in (see mods/_Shared/README.md) — edit it HERE, once. A mod opts in by
// symlinking this file plus XUiC_DrokkUpdateNotice.cs and calling
// DrokkModUpdateChecker.Register(_modInstance) once from its InitMod.
//
// ############################################################################################
// # THE ONE RULE: NO MUTABLE STATIC FIELDS IN THIS FILE (or in XUiC_DrokkUpdateNotice.cs).    #
// #                                                                                           #
// # A symlink shares SOURCE, not runtime state. Every mod that links this file compiles its   #
// # own private copy of every type in it, so `private static bool foo` is per-ASSEMBLY: 14    #
// # separate `foo`s, and a write by DrokkStorage is invisible to DrokkTravel. Any value that  #
// # one assembly writes and another reads MUST go through AppDomain.CurrentDomain             #
// # Get/SetData under a fixed string key (see the shared-state block below), and any          #
// # "has someone already done this?" check must interrogate real shared state — which is why  #
// # the Harmony guard is Harmony.HasAnyPatches(HarmonyId) and not a bool field.               #
// #                                                                                           #
// # This is not theoretical: pendingMessage was a plain static and shipped a blank popup.     #
// # AssertNoUnsharedStatics() below fails the build-out loud at startup if a new one appears. #
// ############################################################################################
//
// Every copy registers its own mod name/version into one shared, AppDomain-wide list; exactly
// one copy installs the Harmony patch and fires the request, and its popup reports on every
// Drokk mod that registered (whether that mod loaded before or after the patch went in).
// Which copy wins is decided by Generation below, NOT by load order -- see the note there.
public static class DrokkModUpdateChecker
{
    private const string HarmonyId = "drokk.updatechecker";
    // Bump when this file changes in a way a stale deployed DLL would get wrong (a new API
    // host, a new query parameter, a new response field). Load order is alphabetical, so
    // without this the OLDEST deployed Drokk mod owns the request for the whole session --
    // e.g. a DrokkStorage.dll built before DrokkApi existed would keep sending every check
    // to the hardcoded production URL and silently ignore -staging. A copy with a higher
    // generation unpatches the incumbent and takes over; equal generations leave it alone,
    // so identically-built mods don't ping-pong.
    //
    // gen 3: pendingMessage / hasCheckedThisSession / UpdatesEnabled moved off per-assembly
    //        statics onto AppDomain data. A gen-2 DLL sends the popup but leaves the shared
    //        key unset, so a gen-3 controller would render blank -- hence the legacy bridge
    //        in BroadcastPendingMessage/ReadLegacyPendingMessage, which keeps gen-2 and gen-3
    //        copies interoperating in BOTH directions until every deployed mod is gen 3.
    private const int Generation = 3;
    // Host comes from DrokkApi so -staging redirects this (and every other Drokk API
    // call) to the staging backend in one place.
    private const string UpdatePath = "/api/updates";
    private const int TimeoutSeconds = 5;
    // Query cap, well under the ~8 KB request line most proxies accept.
    private const int MaxUrlLength = 6000;
    private const string SettingsFileName = "drokk.json";

    // ---- Cross-assembly shared state -------------------------------------------------------
    // Keys are fixed strings so every assembly's copy addresses the same slot. Store ONLY BCL
    // types (string, bool, int, Dictionary<string,string>): a value of a mod-defined type boxed
    // by DrokkStorage cannot be cast back by DrokkTravel, because "DrokkStorage.Foo" and
    // "DrokkTravel.Foo" are two distinct runtime types that merely share a name. Never change
    // a key's stored type without bumping Generation — an older DLL will still be reading it
    // with the old cast and will silently get the fallback.
    private const string RegisteredModsKey = "DrokkModUpdateChecker.RegisteredMods";
    private const string GenerationKey = "DrokkModUpdateChecker.Generation";
    private const string PendingMessageKey = "DrokkModUpdateChecker.PendingMessage";
    private const string HasCheckedKey = "DrokkModUpdateChecker.HasCheckedThisSession";
    private const string UpdatesEnabledKey = "DrokkModUpdateChecker.UpdatesEnabled";

    /// <summary>
    /// Message text for the next popup. SHARED: written by whichever assembly owns the Harmony
    /// patch and fires the request, read by whichever assembly owns the window's controller —
    /// and those are picked by two unrelated mechanisms (Generation vs. XUi load order), so
    /// they are routinely different assemblies. This was the blank-popup bug.
    /// </summary>
    public static string PendingMessage
    {
        get => AppDomain.CurrentDomain.GetData(PendingMessageKey) as string ?? "";
        set => AppDomain.CurrentDomain.SetData(PendingMessageKey, value ?? "");
    }

    /// <summary>
    /// One update check per game session. SHARED, though only one copy's postfix can run today
    /// (the HasAnyPatches guard keeps a single patch installed). Shared anyway because it
    /// guards a network request whose duplicate is user-visible as a second popup, and because
    /// a generation takeover unpatches one assembly's postfix and installs another's — the
    /// moment that can happen after the main menu has already opened, a per-assembly flag
    /// would let the check run twice.
    /// </summary>
    private static bool HasCheckedThisSession
    {
        get => AppDomain.CurrentDomain.GetData(HasCheckedKey) as bool? ?? false;
        set => AppDomain.CurrentDomain.SetData(HasCheckedKey, value);
    }

    /// <summary>
    /// Shared across every Drokk mod (not just this one) — a single drokk.json in the user's
    /// game-data folder, not per-mod, since "stop checking for updates" should be one on/off
    /// switch regardless of how many Drokk mods are installed.
    ///
    /// SHARED in memory too, and for the same reason as PendingMessage: the popup's toggle is
    /// written by the CONTROLLER's assembly (OnCheckUpdatesToggled -> SetUpdatesEnabled) and
    /// read by the CHECKER's assembly (MainMenu_OnOpen_Postfix), which are different
    /// assemblies whenever the window and the patch land in different mods. An unset key means
    /// "not read from disk yet" — that null-vs-bool distinction replaces the old separate
    /// settingsLoaded flag, so the two can never disagree about whether a load has happened.
    /// </summary>
    public static bool UpdatesEnabled => AppDomain.CurrentDomain.GetData(UpdatesEnabledKey) as bool? ?? true;

    // The one legitimately per-assembly mutable static in this file, and allowlisted by name in
    // AssertNoUnsharedStatics(). It guards a reflection self-check OVER THIS ASSEMBLY'S OWN
    // types, so "already done" is a per-assembly question by definition — a shared flag would
    // make the first mod to load suppress the check for all 13 others.
    private static bool selfCheckDone = false;

    private static string SettingsPath => Path.Combine(GameIO.GetUserGameDataDir(), SettingsFileName);

    public static void Register(Mod _modInstance)
    {
        try
        {
            AssertNoUnsharedStatics();
            GetRegisteredMods()[_modInstance.Name] = _modInstance.VersionString;
            EnsureSettingsLoaded();

            bool alreadyPatched = Harmony.HasAnyPatches(HarmonyId);
            // A build that predates the generation stamp leaves no data behind, so an
            // unstamped incumbent reads as generation 0 and always loses to us.
            int incumbent = AppDomain.CurrentDomain.GetData(GenerationKey) as int? ?? 0;
            if (alreadyPatched && incumbent >= Generation) return;

            var harmony = new Harmony(HarmonyId);
            if (alreadyPatched)
            {
                // Static UnpatchID, not harmony.UnpatchAll(id): the incumbent patch was
                // installed by a different Harmony instance in a different assembly.
                Harmony.UnpatchID(HarmonyId);
                Log.Out($" [DrokkModUpdateChecker] {_modInstance.Name} (gen {Generation}) taking over from an older copy (gen {incumbent}).");
            }

            harmony.Patch(
                AccessTools.Method(typeof(XUiC_MainMenu), "OnOpen"),
                postfix: new HarmonyMethod(typeof(DrokkModUpdateChecker), nameof(MainMenu_OnOpen_Postfix)));
            AppDomain.CurrentDomain.SetData(GenerationKey, Generation);
            Log.Out($" [DrokkModUpdateChecker] Installed by {_modInstance.Name} (gen {Generation}), API base {DrokkApi.BaseUrl}.");
        }
        catch (Exception e)
        {
            Log.Error($" [DrokkModUpdateChecker] Register failed for {_modInstance.Name}: {e.Message}");
        }
    }

    private static Dictionary<string, string> GetRegisteredMods()
    {
        var mods = AppDomain.CurrentDomain.GetData(RegisteredModsKey) as Dictionary<string, string>;
        if (mods == null)
        {
            mods = new Dictionary<string, string>();
            AppDomain.CurrentDomain.SetData(RegisteredModsKey, mods);
        }
        return mods;
    }

    // Startup regression check for the rule in this file's header. Any mutable static field
    // declared on the two shared types is a cross-assembly bug waiting for the day the sender
    // and the window's controller land in different mods -- which is not a rare edge case, it
    // is the normal state once two Drokk mods are installed. Consts (IsLiteral) and static
    // readonly (IsInitOnly) are immutable and therefore safe to duplicate per assembly.
    // Runs once per assembly, costs two GetFields calls, and only logs when it finds something.
    //
    // NOT applied to DrokkApi: its statics are deliberately per-assembly (see that file).
    private static void AssertNoUnsharedStatics()
    {
        if (selfCheckDone) return;
        selfCheckDone = true;
        try
        {
            CheckTypeForMutableStatics(typeof(DrokkModUpdateChecker), nameof(selfCheckDone));
            CheckTypeForMutableStatics(typeof(XUiC_DrokkUpdateNotice));
        }
        catch (Exception e)
        {
            Log.Warning($" [DrokkModUpdateChecker] Static self-check could not run: {e.Message}");
        }
    }

    private static void CheckTypeForMutableStatics(Type type, params string[] allowedFieldNames)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (FieldInfo field in type.GetFields(flags))
        {
            if (field.IsLiteral || field.IsInitOnly) continue;
            // Compiler-generated lambda/closure caches ("<>9__0") are emitted onto a nested
            // display class, not onto the type itself, but filter the mangled names anyway so
            // a future compiler change can't turn this check into a false alarm.
            if (field.Name.IndexOf('<') >= 0) continue;
            if (Array.IndexOf(allowedFieldNames, field.Name) >= 0) continue;

            Log.Error($" [DrokkModUpdateChecker] SHARED-STATE BUG: {type.Name}.{field.Name} is a mutable static. " +
                      "Each mod compiles its own copy of this type, so that field is per-assembly and a write by one " +
                      "mod is invisible to the others. Route it through AppDomain.CurrentDomain Get/SetData under a " +
                      "fixed key (see the shared-state block in mods/_Shared/DrokkModUpdateChecker.cs), or make it " +
                      "const/static readonly if it is genuinely immutable.");
        }
    }

    private static void MainMenu_OnOpen_Postfix(XUiC_MainMenu __instance)
    {
        if (HasCheckedThisSession) return;
        HasCheckedThisSession = true;

        if (!UpdatesEnabled)
        {
            Log.Out(" [DrokkModUpdateChecker] Update checks disabled via drokk.json; not contacting the server.");
            return;
        }

        ThreadManager.StartCoroutine(CheckForUpdates(__instance));
    }

    private static void EnsureSettingsLoaded()
    {
        // Null (not false) is the "never read from disk" marker — see UpdatesEnabled.
        if (AppDomain.CurrentDomain.GetData(UpdatesEnabledKey) != null) return;
        bool enabled = true;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var obj = JObject.Parse(File.ReadAllText(SettingsPath));
                if (obj.TryGetValue("updates", out JToken token) && token.Type == JTokenType.Boolean)
                {
                    enabled = token.Value<bool>();
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning($" [DrokkModUpdateChecker] Could not read {SettingsPath}: {e.Message}");
        }
        AppDomain.CurrentDomain.SetData(UpdatesEnabledKey, enabled);
    }

    public static void SetUpdatesEnabled(bool enabled)
    {
        AppDomain.CurrentDomain.SetData(UpdatesEnabledKey, enabled);
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

    // ---- gen-2 <-> gen-3 bridge -------------------------------------------------------------
    // Generation decides which copy SENDS, but it cannot decide which copy DISPLAYS: the
    // window's controller is fixed by windows.xml (controller="XUiC_DrokkUpdateNotice, <Mod>")
    // and the engine keeps the FIRST declaration it parses (XUiFromXml.loadWindows uses
    // windowData.TryAdd), so the displaying assembly is chosen by mod load order. A gen-3
    // sender therefore has to cope with a gen-2 controller, and vice versa, until every
    // deployed Drokk mod is gen 3. Both halves are reflection over other assemblies' copies of
    // XUiC_DrokkUpdateNotice, which in gen 2 held the message in a private static field.

    /// <summary>
    /// gen-3 sender -> gen-2 controller. After setting the shared key, poke the legacy private
    /// static on every OTHER loaded copy of the controller type, so an old DLL that wins the
    /// window declaration still has text to show.
    /// </summary>
    public static void BroadcastPendingMessage(string message)
    {
        ForEachOtherNoticeType((type, field) =>
        {
            if (field.FieldType == typeof(string)) field.SetValue(null, message);
        });
    }

    /// <summary>
    /// gen-2 sender -> gen-3 controller. An old copy's Show() sets only its own private static
    /// and never touches the shared key, so when this controller opens with an empty shared
    /// message, sweep the other assemblies for a non-empty legacy one before giving up.
    /// </summary>
    public static string ReadLegacyPendingMessage()
    {
        string found = null;
        ForEachOtherNoticeType((type, field) =>
        {
            if (found != null || field.FieldType != typeof(string)) return;
            if (field.GetValue(null) is string s && !string.IsNullOrEmpty(s)) found = s;
        });
        return found;
    }

    private static void ForEachOtherNoticeType(Action<Type, FieldInfo> action)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Assembly self = typeof(DrokkModUpdateChecker).Assembly;
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == self) continue;
            try
            {
                // GetType(name) rather than GetTypes(): a mod assembly with an unresolvable
                // reference throws ReflectionTypeLoadException from GetTypes and would take
                // the whole sweep down with it.
                Type type = asm.GetType(nameof(XUiC_DrokkUpdateNotice), throwOnError: false);
                FieldInfo field = type?.GetField("pendingMessage", flags);
                if (field != null) action(type, field);
            }
            catch (Exception)
            {
                // A single uncooperative assembly must not cost us the popup.
            }
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
