using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Platform;
using UnityEngine;
using UnityEngine.Scripting;

public class DrokkStorageConfig
{
    public bool craftFromContainersEnabled = true;
    public bool enableForRepairAndUpgrade = true;
    public float range = 20f; // Range in blocks to search for containers
    public bool allowLockedContainers = true;
    public bool enableForReload = true;
    public bool enableForRefuel = true;
    public bool isDebug = false;

    // Multiple players viewing/modifying the same container at once. In 3.0 this is achieved by
    // marking storage containers as SHARED locks in the engine's LockManager (see the
    // TEFeatureAbs.IsSharedLock patch); turning this off restores vanilla single-viewer behavior.
    public bool multiViewersEnabled = true;

    // Additional pullable sources (beyond storage crates). Their contents are counted and
    // consumed for crafting/repair/reload just like nearby containers.
    public bool pullFromVehicles = true;            // owned, unlocked vehicle storage
    public bool pullFromDrones = true;              // owned drone storage
    public bool pullFromWorkstationOutputs = true;  // forge/campfire/etc. OUTPUT slots only
    public bool pullFromDewCollectors = true;       // dew collectors / apiaries

    // Live recipe tracking: when a nearby workstation finishes crafting/cooking, refresh the
    // pinned recipe tracker and open crafting windows so available counts update immediately.
    public bool liveRecipeTracking = true;

    // QuickStack settings
    public bool lockModeIconVisible = true;
    public Vector3i stashDistance = new Vector3i(20, 20, 20);
    public KeyCode[] quickLockHotkeys = new KeyCode[] { KeyCode.LeftAlt };
    public KeyCode[] quickStackHotkeys = new KeyCode[] { KeyCode.LeftAlt, KeyCode.X };
    public KeyCode[] quickRestockHotkeys = new KeyCode[] { KeyCode.LeftAlt, KeyCode.Z };
    public Color32 lockIconColor = new Color32(255, 0, 0, 255);
    public Color32 lockBorderColor = new Color32(128, 0, 0, 0);
}

public enum QuickStackType : byte
{
    Stack,
    Restock,
    Count
}

public class DrokkStorage : IModApi
{
    public static DrokkStorageConfig config = new DrokkStorageConfig();
    
    public void InitMod(Mod _modInstance)
    {
        Log.Out(" [DrokkStorage] Loading MultipleViewers Mod (v2.1 with Quest Fix)");
        LoadConfig(_modInstance);
        var harmony = new Harmony(GetType().ToString());
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // Register custom packages with NetPackageManager
        try
        {
            var knownPackageTypes = (Dictionary<string, Type>)AccessTools.Field(typeof(NetPackageManager), "knownPackageTypes").GetValue(null);
            
            if (!knownPackageTypes.ContainsKey(typeof(NetPackageCloseLootWindow).Name))
            {
                knownPackageTypes.Add(typeof(NetPackageCloseLootWindow).Name, typeof(NetPackageCloseLootWindow));
                Log.Out(" [DrokkStorage] Registered NetPackageCloseLootWindow");
            }

            if (!knownPackageTypes.ContainsKey(typeof(NetPackageDoQuickStack).Name))
            {
                knownPackageTypes.Add(typeof(NetPackageDoQuickStack).Name, typeof(NetPackageDoQuickStack));
                Log.Out(" [DrokkStorage] Registered NetPackageDoQuickStack");
            }

            if (!knownPackageTypes.ContainsKey(typeof(NetPackageFindOpenableContainers).Name))
            {
                knownPackageTypes.Add(typeof(NetPackageFindOpenableContainers).Name, typeof(NetPackageFindOpenableContainers));
                Log.Out(" [DrokkStorage] Registered NetPackageFindOpenableContainers");
            }
        }
        catch (Exception e)
        {
            Log.Error(" [DrokkStorage] Failed to register custom package: " + e.Message);
        }
    }
    
    // Loads user-editable distance settings from Config/settings.xml in the mod folder.
    // Any value that is missing or unparseable falls back to the hardcoded default in
    // DrokkStorageConfig, so a malformed/edited file can never stop the mod from loading.
    private static void LoadConfig(Mod _modInstance)
    {
        try
        {
            string path = System.IO.Path.Combine(_modInstance.Path, "Config", "settings.xml");
            if (!System.IO.File.Exists(path))
            {
                Log.Warning($" [DrokkStorage] Config not found at {path}, using defaults.");
                return;
            }

            var doc = new System.Xml.XmlDocument();
            doc.Load(path);

            var rangeNode = doc.SelectSingleNode("/DrokkStorage/ContainerRange");
            if (rangeNode?.Attributes?["value"] != null &&
                float.TryParse(rangeNode.Attributes["value"].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float range))
            {
                config.range = range;
            }

            var stashNode = doc.SelectSingleNode("/DrokkStorage/QuickStackDistance");
            if (stashNode?.Attributes?["value"] != null &&
                int.TryParse(stashNode.Attributes["value"].Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int stash))
            {
                config.stashDistance = new Vector3i(stash, stash, stash);
            }

            LoadBool(doc, "CraftFromContainersEnabled", v => config.craftFromContainersEnabled = v);
            LoadBool(doc, "EnableForRepairAndUpgrade", v => config.enableForRepairAndUpgrade = v);
            LoadBool(doc, "EnableForReload", v => config.enableForReload = v);
            LoadBool(doc, "EnableForRefuel", v => config.enableForRefuel = v);
            LoadBool(doc, "AllowLockedContainers", v => config.allowLockedContainers = v);
            LoadBool(doc, "MultiViewersEnabled", v => config.multiViewersEnabled = v);
            LoadBool(doc, "PullFromVehicles", v => config.pullFromVehicles = v);
            LoadBool(doc, "PullFromDrones", v => config.pullFromDrones = v);
            LoadBool(doc, "PullFromWorkstationOutputs", v => config.pullFromWorkstationOutputs = v);
            LoadBool(doc, "PullFromDewCollectors", v => config.pullFromDewCollectors = v);
            LoadBool(doc, "LiveRecipeTracking", v => config.liveRecipeTracking = v);
            LoadBool(doc, "LockModeIconVisible", v => config.lockModeIconVisible = v);

            Log.Out($" [DrokkStorage] Loaded config: ContainerRange={config.range}, QuickStackDistance={config.stashDistance.x}, "
                + $"CraftFromContainersEnabled={config.craftFromContainersEnabled}, EnableForReload={config.enableForReload}, "
                + $"EnableForRepairAndUpgrade={config.enableForRepairAndUpgrade}, EnableForRefuel={config.enableForRefuel}");
        }
        catch (Exception e)
        {
            Log.Error($" [DrokkStorage] Failed to load config, using defaults: {e.Message}");
        }
    }

    // Reads /DrokkStorage/<nodeName value="true|false"/> and, if present and parseable,
    // hands the value to apply(). Missing/malformed nodes silently leave the default untouched.
    private static void LoadBool(System.Xml.XmlDocument doc, string nodeName, Action<bool> apply)
    {
        var node = doc.SelectSingleNode($"/DrokkStorage/{nodeName}");
        if (node?.Attributes?["value"] != null &&
            bool.TryParse(node.Attributes["value"].Value, out bool value))
        {
            apply(value);
        }
    }

    public static void Dbgl(object str)
    {
        if (config.isDebug)
            Log.Out($"[DrokkStorage] {str}");
    }

    public static int GetItemCount(ITileEntity _te)
    {
        if (_te != null && _te.TryGetSelfOrFeature<ITileEntityLootable>(out var lootable) && lootable.items != null)
        {
            int count = 0;
            for (int i = 0; i < lootable.items.Length; i++)
            {
                if (lootable.items[i] != null && !lootable.items[i].IsEmpty()) count++;
            }
            return count;
        }
        return -1;
    }

    // Positions of containers known to be (or to have been) quest fetch/buried-supplies
    // containers. Quest items are injected client-side only - the server never owns them -
    // so the mod's multi-viewer sync must never touch these containers. We REMEMBER them by
    // position because the supply leaves the container once picked up: a stale server echo
    // arriving after pickup would otherwise re-add it ("pick up twice") and break the quest.
    public static readonly HashSet<Vector3i> KnownQuestContainers = new HashSet<Vector3i>();

    // True if the container currently holds any quest item.
    public static bool HasQuestItem(ITileEntity _te)
    {
        if (_te != null && _te.TryGetSelfOrFeature<ITileEntityLootable>(out var lootable))
            return HasQuestItem(lootable);
        return false;
    }

    public static bool HasQuestItem(ITileEntityLootable lootable)
    {
        if (lootable?.items == null) return false;
        for (int i = 0; i < lootable.items.Length; i++)
        {
            var stack = lootable.items[i];
            if (stack != null && !stack.IsEmpty() && stack.itemValue?.ItemClass != null && stack.itemValue.ItemClass.IsQuestItem)
                return true;
        }
        return false;
    }

    // True if this container holds a quest item now, or ever has (remembered by position).
    // Use this to gate the multi-viewer sync so quest containers are left to vanilla handling.
    public static bool IsQuestContainer(ITileEntity _te)
    {
        if (_te == null) return false;
        Vector3i pos = _te.ToWorldPos();
        if (HasQuestItem(_te))
        {
            KnownQuestContainers.Add(pos);
            return true;
        }
        return KnownQuestContainers.Contains(pos);
    }
}

[Preserve]
public class NetPackageCloseLootWindow : NetPackage
{
    public NetPackageCloseLootWindow() { }
    public override void read(PooledBinaryReader _br) { }
    public override void write(PooledBinaryWriter _bw) { base.write(_bw); }
    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        if (_world == null)
        {
            Log.Warning("[DrokkStorage] NetPackageCloseLootWindow: _world is null");
            return;
        }
        
        bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
        Log.Out($"[DrokkStorage] NetPackageCloseLootWindow.ProcessPackage called on {(isServer ? "SERVER" : "CLIENT")}");
        Log.Out($"[DrokkStorage] NetPackageCloseLootWindow: LocalPlayerUI.PlayerUIs count: {LocalPlayerUI.PlayerUIs.Count}");
        
        int closedCount = 0;
        foreach (var ui in LocalPlayerUI.PlayerUIs)
        {
            if (ui != null && ui.entityPlayer != null)
            {
                Log.Out($"[DrokkStorage] NetPackageCloseLootWindow: Checking windows for player {ui.entityPlayer.entityId} ({ui.entityPlayer.EntityName})");
                if (CloseWindowIfShowing(ui, "looting")) closedCount++;
                if (CloseWindowIfShowing(ui, "workstation_forge")) closedCount++;
                if (CloseWindowIfShowing(ui, "workstation_campfire")) closedCount++;
                if (CloseWindowIfShowing(ui, "workstation_workbench")) closedCount++;
                if (CloseWindowIfShowing(ui, "workstation_cementMixer")) closedCount++;
                if (CloseWindowIfShowing(ui, "workstation_chemLab")) closedCount++;
                if (CloseWindowIfShowing(ui, "vehicleStorage")) closedCount++;
            }
            else
            {
                Log.Out($"[DrokkStorage] NetPackageCloseLootWindow: Skipping null ui or entityPlayer");
            }
        }
        
        Log.Out($"[DrokkStorage] NetPackageCloseLootWindow: Closed {closedCount} windows total");
    }

    private bool CloseWindowIfShowing(LocalPlayerUI ui, string name)
    {
        if (ui.windowManager.IsWindowOpen(name))
        {
            Log.Out($"[DrokkStorage] Closing window '{name}' for player {ui.entityPlayer?.EntityName ?? "unknown"}");
            ui.windowManager.Close(name);
            return true;
        }
        return false;
    }

    public override int GetLength() => 1;
}

public static class DrokkStoragePatches
{
    public static readonly Dictionary<ITileEntity, HashSet<int>> Viewers = new Dictionary<ITileEntity, HashSet<int>>();
    private static readonly Dictionary<int, float> lastProcessedTimes = new Dictionary<int, float>();
    // Track the customUi used for each viewer so we can reopen with the same UI
    private static readonly Dictionary<int, string> viewerCustomUi = new Dictionary<int, string>();
    // Track viewers that are closing (to avoid refresh on close)
    private static readonly HashSet<int> viewersClosing = new HashSet<int>();

    // QuickStack state
    public static float[] lastClickTimes = new float[(int)QuickStackType.Count];
    public static XUiC_BackpackWindow backpackWindow;
    public static XUiC_ContainerStandardControls playerControls;
    public static XUiC_Backpack playerBackpack;

    // Always-on diagnostic for transpilers. A transpiler that fails to find its IL injection
    // point silently no-ops (the feature just doesn't work), so we log success/failure at
    // startup regardless of the isDebug flag - this is the fastest way to spot a patch that
    // stopped matching after a game update.
    private static void LogPatchResult(string method, int patched)
    {
        if (patched > 0)
            Log.Out($"[DrokkStorage] Patched {method} ({patched} injection point(s))");
        else
            Log.Warning($"[DrokkStorage] FAILED to patch {method} - injection point NOT found; this feature will not work!");
    }

    // 1. Multi-viewer access. 3.0 deleted the old TELock single-lock flow (GameManager.TELockServer
    // + NetPackageTELock) and replaced it with LockManager, which natively supports SHARED locks -
    // many players holding one lock target at once (traders use this, see EntityTrader.IsSharedLock).
    // Storage containers default to single-lock (one viewer at a time, hence the vanilla "It is
    // already locked by another player" rejection). We flip TEFeatureStorage to a shared lock so any
    // number of players can open and modify the same container simultaneously - the whole feature the
    // mod used to hand-roll on top of TELock. Patched on the base TEFeatureAbs (where IsSharedLock is
    // declared) with an instance-type guard so only storage - not signs/lockable/etc. - is affected.
    [HarmonyPatch(typeof(TEFeatureAbs), nameof(TEFeatureAbs.IsSharedLock))]
    public static class TEFeatureAbs_IsSharedLock_Patch
    {
        public static void Postfix(TEFeatureAbs __instance, ref bool __result)
        {
            if (__result) return;                                   // already shared, nothing to do
            if (!DrokkStorage.config.multiViewersEnabled) return;   // feature toggle
            if (!(__instance is TEFeatureStorage)) return;          // storage containers only
            // QUEST GUARD: never share quest fetch/buried-supply containers - the supply is injected
            // client-side only, so a second viewer could pick it up too ("steal it"). See known_bugs.md.
            if (DrokkStorage.IsQuestContainer(__instance.Parent)) return;
            __result = true;
        }
    }

    // 1b. Viewer tracking via the engine lock flow (replaces the removed TELock bookkeeping).
    // LockManager calls OnLockedServer for every player granted a (now shared) lock and
    // OnUnlockedServer when they close; we mirror that into Viewers so the content-sync patches
    // below can push live updates to the OTHER viewers when one of them changes the contents.
    [HarmonyPatch(typeof(TEFeatureStorage), nameof(TEFeatureStorage.OnLockedServer))]
    public static class TEFeatureStorage_OnLockedServer_Patch
    {
        public static void Postfix(TEFeatureStorage __instance, bool _success, int _lockingPlayerID)
        {
            if (!_success) return;
            TileEntity parent = __instance.Parent;
            if (parent == null) return;
            if (!Viewers.TryGetValue(parent, out HashSet<int> viewers))
            {
                viewers = new HashSet<int>();
                Viewers[parent] = viewers;
            }
            viewers.Add(_lockingPlayerID);
            Log.Out($"[DrokkStorage] Viewer {_lockingPlayerID} acquired shared lock on {parent}. Total viewers: {viewers.Count}");
        }
    }

    [HarmonyPatch(typeof(TEFeatureStorage), nameof(TEFeatureStorage.OnUnlockedServer))]
    public static class TEFeatureStorage_OnUnlockedServer_Patch
    {
        public static void Postfix(TEFeatureStorage __instance, int _unlockingPlayerId)
        {
            TileEntity parent = __instance.Parent;
            if (parent == null) return;
            if (Viewers.TryGetValue(parent, out HashSet<int> viewers))
            {
                viewers.Remove(_unlockingPlayerId);
                if (viewers.Count == 0) Viewers.Remove(parent);
                Log.Out($"[DrokkStorage] Viewer {_unlockingPlayerId} released lock on {parent}. Remaining viewers: {viewers.Count}");
            }
        }
    }

    // 2. Change Detection & Auto-Refresh
    [HarmonyPatch(typeof(NetPackageTileEntity), nameof(NetPackageTileEntity.ProcessPackage))]
    public static class NetPackageTileEntity_ProcessPackage_Patch
    {
        // 3.0 NetPackageTileEntity is position-only: the old bValidEntityId/teEntityId/clrIdx fields
        // are gone (entity-backed TEs no longer exist), and teWorldPos is now a public field.
        public static void Prefix(NetPackageTileEntity __instance, World _world)
        {
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) return;

            TileEntity tileEntity = _world.GetTileEntity(__instance.teWorldPos);
            if (tileEntity != null)
            {
                // Log.Out($"[DrokkStorage] NetPackageTileEntity.ProcessPackage (RECEIVE) PREFIX: TE={tileEntity}, ItemsBefore={DrokkStorage.GetItemCount(tileEntity)}, Sender={__instance.Sender?.entityId ?? -1}, bUserAccessing={tileEntity.IsUserAccessing()}");
            }
        }

        public static void Postfix(NetPackageTileEntity __instance, World _world)
        {
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) return;

            int senderId = -1;
            if (__instance.Sender != null) senderId = __instance.Sender.entityId;

            // Duplicate prevention
            float currentTime = Time.time;
            if (senderId != -1)
            {
                if (lastProcessedTimes.TryGetValue(senderId, out float lastTime) && currentTime - lastTime < 0.2f) return;
                lastProcessedTimes[senderId] = currentTime;
            }

            TileEntity tileEntity = _world.GetTileEntity(__instance.teWorldPos);
            if (tileEntity == null || !tileEntity.TryGetSelfOrFeature<ITileEntityLootable>(out var lootable)) return;

            // QUEST GUARD: do not echo quest containers back to viewers. The supply is
            // client-side only; re-broadcasting re-adds it after pickup and breaks the quest.
            if (DrokkStorage.IsQuestContainer(tileEntity))
            {
                Log.Out($"[DrokkStorage] NetPackageTileEntity.ProcessPackage: QUEST container {tileEntity}, skipping multi-viewer echo.");
                return;
            }

            // Log.Out($"[DrokkStorage] NetPackageTileEntity.ProcessPackage (RECEIVE) POSTFIX: TE={tileEntity}, ItemsAfter={DrokkStorage.GetItemCount(tileEntity)}, Sender={senderId}");

            if (Viewers.TryGetValue(tileEntity, out HashSet<int> viewers))
            {
                Log.Out($"[DrokkStorage] Found {viewers.Count} viewers for modified container");
                List<int> viewersList = viewers.ToList();
                foreach (int viewerId in viewersList)
                {
                    if (viewerId == senderId) 
                    {
                        Log.Out($"[DrokkStorage] Skipping refresh for sender {viewerId}");
                        continue;
                    }

                    EntityPlayer viewer = _world.GetEntity(viewerId) as EntityPlayer;
                    if (viewer == null) 
                    {
                        Log.Out($"[DrokkStorage] Viewer {viewerId} not found in world");
                        continue;
                    }

                    if (viewer is EntityPlayerLocal localPlayer)
                    {
                        // Host (Server Player) sees changes automatically because they share the TileEntity object in memory.
                        // We just need to check if they are still viewing to clean up.
                        LocalPlayerUI ui = LocalPlayerUI.GetUIForPlayer(localPlayer);
                        if (ui == null || !ui.windowManager.IsWindowOpen("looting"))
                        {
                            Log.Out($"[DrokkStorage] Host not viewing {tileEntity}, removing from viewers.");
                            viewers.Remove(viewerId);
                        }
                        else
                        {
                            Log.Out($"[DrokkStorage] Host is viewing {tileEntity}. Smooth update should be visible.");
                        }
                    }
                    else
                    {
                        // Remote player - send sync package instead of closing window
                        Log.Out($"[DrokkStorage] Sending smooth sync to remote viewer {viewerId}");
                        SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                            NetPackageManager.GetPackage<NetPackageTileEntity>().Setup(tileEntity, TileEntity.StreamModeWrite.ToClient),
                            false, viewerId);
                    }
                }

                if (viewers.Count == 0) Viewers.Remove(tileEntity);
            }
        }
    }

    private static IEnumerator ClearClosingMarker(int viewerId)
    {
        yield return new WaitForSeconds(0.5f);
        viewersClosing.Remove(viewerId);
    }

    // 3. Cleanup on Disconnect
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.PlayerDisconnected))]
    public static class GameManager_PlayerDisconnected_Patch
    {
        public static void Prefix(ClientInfo _cInfo)
        {
            if (_cInfo == null) return;
            int entityId = _cInfo.entityId;

            List<ITileEntity> toRemove = new List<ITileEntity>();
            foreach (var kvp in Viewers)
            {
                kvp.Value.Remove(entityId);
                if (kvp.Value.Count == 0) toRemove.Add(kvp.Key);
            }
            foreach (var te in toRemove) Viewers.Remove(te);
            
            // Clean up customUi tracking
            viewerCustomUi.Remove(entityId);
        }
    }

    // 4/4b/5. (Removed.) The old GameManager.TEUnlockServer / NetPackageTELock / TEAccessClient
    // patches tracked viewers and pushed access packets on top of the deleted TELock system. In 3.0
    // the engine drives this through LockManager: viewer add/remove now happens in the
    // TEFeatureStorage.OnLockedServer / OnUnlockedServer patches above, and there is no client
    // "access" packet to intercept - shared locks open the window for every viewer natively.
#if false
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.TEAccessClient))]
    public static class GameManager_TEAccessClient_Patch
    {
        public static void Prefix(GameManager __instance, int _clrIdx, Vector3i _blockPos, int _lootEntityId, int _entityIdThatOpenedIt, string _customUi)
        {
            bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
            Log.Out($"[DrokkStorage] TEAccessClient called on {(isServer ? "SERVER" : "CLIENT")} for entityId {_entityIdThatOpenedIt}, lootEntityId: {_lootEntityId}, pos: {_blockPos}, customUi: {_customUi ?? "null"}");
            
            // DEBUG HYPOTHESIS 1: Check if entity exists and what type it is
            if (__instance.World != null)
            {
                Entity entity = __instance.World.GetEntity(_entityIdThatOpenedIt);
                if (entity == null)
                {
                    Log.Error($"[DrokkStorage] TEAccessClient: Entity {_entityIdThatOpenedIt} is NULL!");
                }
                else
                {
                    bool isLocal = entity is EntityPlayerLocal;
                    Log.Out($"[DrokkStorage] TEAccessClient: Entity type: {entity.GetType().Name}, IsLocal: {isLocal}, IsRemote: {entity.isEntityRemote}");
                    
                    if (isLocal)
                    {
                        EntityPlayerLocal localPlayer = entity as EntityPlayerLocal;
                        LocalPlayerUI ui = LocalPlayerUI.GetUIForPlayer(localPlayer);
                        Log.Out($"[DrokkStorage] TEAccessClient: LocalPlayerUI is {(ui == null ? "NULL" : "VALID")}");
                    }
                }
            }
            
            // DEBUG HYPOTHESIS 2: Check if TileEntity exists and is valid
            if (__instance.World != null)
            {
                TileEntity tileEntity = (_lootEntityId != -1) ? __instance.World.GetTileEntity(_lootEntityId) : __instance.World.GetTileEntity(_blockPos);
                if (tileEntity == null)
                {
                    Log.Error($"[DrokkStorage] TEAccessClient: TileEntity is NULL! LootEntityId: {_lootEntityId}, Pos: {_blockPos}");
                }
                else
                {
                    Log.Out($"[DrokkStorage] TEAccessClient: TileEntity found - Type: {tileEntity.GetType().Name}, EntityId: {tileEntity.EntityId}");
                    if (tileEntity.TryGetSelfOrFeature<ITileEntityLootable>(out ITileEntityLootable lootable))
                    {
                        Log.Out($"[DrokkStorage] TEAccessClient: TileEntity is lootable, items count: {lootable.items?.Length ?? 0}");
                    }
                }
            }
            
            // DEBUG HYPOTHESIS 3: Check window manager state
            foreach (var ui in LocalPlayerUI.PlayerUIs)
            {
                if (ui != null && ui.entityPlayer != null && ui.entityPlayer.entityId == _entityIdThatOpenedIt)
                {
                    bool lootingOpen = ui.windowManager.IsWindowOpen("looting");
                    Log.Out($"[DrokkStorage] TEAccessClient: WindowManager state for player {_entityIdThatOpenedIt} - looting window is {(lootingOpen ? "OPEN" : "CLOSED")}");
                }
            }
        }
    }
#endif

    // 6. Detection for modifications
    [HarmonyPatch(typeof(TileEntity), nameof(TileEntity.SetModified))]
    public static class TileEntity_SetModified_Patch
    {
        public static void Postfix(TileEntity __instance)
        {
            bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
            if (!__instance.TryGetSelfOrFeature<ITileEntityLootable>(out _)) return;

            // QUEST GUARD: flag (and remember) quest containers as soon as the client-side
            // supply injection modifies them, so later echoes are recognized even after the
            // supply has been picked up. Never run multi-viewer sync on quest containers.
            if (DrokkStorage.IsQuestContainer(__instance))
            {
                Log.Out($"[DrokkStorage] SetModified: QUEST container {__instance}, leaving to vanilla (no multi-viewer sync).");
                return;
            }

            if (!isServer)
            {
                Log.Out($"[DrokkStorage] SetModified (TRIGGER SYNC) on CLIENT for {__instance}. Items: {DrokkStorage.GetItemCount(__instance)}");
                return;
            }

            if (DrokkStoragePatches.Viewers.TryGetValue(__instance, out HashSet<int> viewers))
            {
                // Check if any viewer is closing - if so, skip refresh entirely
                bool anyViewerClosing = false;
                foreach (int viewerId in viewers)
                {
                    if (DrokkStoragePatches.viewersClosing.Contains(viewerId))
                    {
                        anyViewerClosing = true;
                        Log.Out($"[DrokkStorage] SetModified: Viewer {viewerId} is closing, skipping refresh for all viewers");
                        break;
                    }
                }
                
                if (anyViewerClosing) return;
                
                float currentTime = Time.time;
                List<int> viewersList = viewers.ToList();
                foreach (int viewerId in viewersList)
                {
                    EntityPlayer viewer = GameManager.Instance.World.GetEntity(viewerId) as EntityPlayer;
                    if (viewer == null) continue;

                    if (DrokkStoragePatches.lastProcessedTimes.TryGetValue(viewerId, out float lastTime) && currentTime - lastTime < 0.2f) continue;

                    if (viewer is EntityPlayerLocal localPlayer)
                    {
                        // BUT, if we are in the viewers list but DON'T have the window open, clean it up
                        LocalPlayerUI ui = LocalPlayerUI.GetUIForPlayer(localPlayer);
                        if (ui != null && !ui.windowManager.IsWindowOpen("looting"))
                        {
                            Log.Out($"[DrokkStorage] SetModified: Host not viewing {__instance}, removing from viewers.");
                            viewers.Remove(viewerId);
                        }
                    }
                    else
                    {
                        Log.Out($"[DrokkStorage] SetModified detected on server for {__instance}, sending smooth sync to remote viewer {viewerId}");
                        DrokkStoragePatches.lastProcessedTimes[viewerId] = currentTime;
                        SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                            NetPackageManager.GetPackage<NetPackageTileEntity>().Setup(__instance, TileEntity.StreamModeWrite.ToClient),
                            false, viewerId);
                    }
                }
                
                if (viewers.Count == 0) Viewers.Remove(__instance);
            }
        }
    }

    // 7/8. (Removed.) ReopenAfterFrames sent NetPackageTELock AccessClient packets to re-open a
    // viewer's window, and the OpenTileEntityUi patch hooked GameManager.OpenTileEntityUi to track
    // viewers and pump custom UI. Both the packet and that method are gone in 3.0 - shared locks
    // open the loot window for every viewer via TEFeatureStorage.OnLockedLocal/ShowUI, and viewer
    // tracking moved to the OnLockedServer/OnUnlockedServer patches above.
#if false
    private static IEnumerator ReopenAfterFrames(int viewerId, ITileEntity tileEntity, string customUi)
    {
        // Wait 3 frames for close to fully complete on client
        yield return null;
        yield return null;
        yield return null;
        
        if (tileEntity == null)
        {
            Log.Warning($"[DrokkStorage] ReopenAfterFrames: TileEntity is null");
            yield break;
        }

        EntityPlayer viewer = GameManager.Instance.World.GetEntity(viewerId) as EntityPlayer;
        if (viewer == null || viewer.IsDead())
        {
            Log.Warning($"[DrokkStorage] ReopenAfterFrames: Viewer {viewerId} not found or dead");
            yield break;
        }
        
        // Check if viewer is still in the viewers list
        if (!Viewers.TryGetValue((TileEntity)tileEntity, out HashSet<int> viewers) || !viewers.Contains(viewerId))
        {
            Log.Out($"[DrokkStorage] ReopenAfterFrames: Viewer {viewerId} no longer viewing this container");
            yield break;
        }

        // Verify the tile entity still exists
        Vector3i tePos = tileEntity.ToWorldPos();
        int teClrIdx = tileEntity.GetClrIdx();
        int teEntityId = tileEntity.EntityId;
        
        TileEntity worldTE = (teEntityId != -1) ? GameManager.Instance.World.GetTileEntity(teEntityId) : GameManager.Instance.World.GetTileEntity(tePos);
        if (worldTE == null)
        {
            Log.Warning($"[DrokkStorage] ReopenAfterFrames: TileEntity no longer exists in world");
            yield break;
        }

        Log.Out($"[DrokkStorage] ReopenAfterFrames: Sending AccessClient package to viewer {viewerId}");
        SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
            NetPackageManager.GetPackage<NetPackageTELock>().Setup(
                NetPackageTELock.TELockType.AccessClient, 
                teClrIdx, 
                tePos, 
                teEntityId, 
                viewerId,
                customUi),
            false, viewerId);
    }

    // 8. Debug OpenTileEntityUi to see what happens when trying to open
    [HarmonyPatch(typeof(GameManager), "OpenTileEntityUi")]
    public static class GameManager_OpenTileEntityUi_Patch
    {
        public static void Prefix(GameManager __instance, int _entityIdThatOpenedIt, ITileEntity _te, string _customUi)
        {
            Log.Out($"[DrokkStorage] OpenTileEntityUi (OPEN) called: EntityId={_entityIdThatOpenedIt}, TileEntity={_te?.GetType().Name ?? "null"}, Items={DrokkStorage.GetItemCount((TileEntity)_te)}, CustomUi={_customUi ?? "null"}");
            
            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer && _te is TileEntity tileEntity)
            {
                if (!Viewers.ContainsKey(tileEntity)) Viewers[tileEntity] = new HashSet<int>();
                Viewers[tileEntity].Add(_entityIdThatOpenedIt);
                viewerCustomUi[_entityIdThatOpenedIt] = _customUi ?? "";
                Log.Out($"[DrokkStorage] Added viewer {_entityIdThatOpenedIt} via OpenTileEntityUi. Total viewers for {tileEntity}: {Viewers[tileEntity].Count}");
            }

            if (__instance.World != null)
            {
                EntityPlayerLocal entityPlayerLocal = __instance.World.GetEntity(_entityIdThatOpenedIt) as EntityPlayerLocal;
                LocalPlayerUI uIForPlayer = LocalPlayerUI.GetUIForPlayer(entityPlayerLocal);
                
                Log.Out($"[DrokkStorage] OpenTileEntityUi: EntityPlayerLocal is {(entityPlayerLocal == null ? "NULL" : "VALID")}, LocalPlayerUI is {(uIForPlayer == null ? "NULL" : "VALID")}");
                
                if (_te != null)
                {
                    if (_te.TryGetSelfOrFeature<ITileEntityLootable>(out ITileEntityLootable lootable))
                    {
                        Log.Out($"[DrokkStorage] OpenTileEntityUi: TileEntity is lootable, container type check will happen");
                        if (string.IsNullOrEmpty(_customUi))
                        {
                            Log.Out($"[DrokkStorage] OpenTileEntityUi: Will call lootContainerOpened");
                        }
                        else if (_customUi == "container")
                        {
                            Log.Out($"[DrokkStorage] OpenTileEntityUi: CustomUi='container', will call lootContainerOpened");
                        }
                    }
                }
            }
        }
        
        public static void Postfix(int _entityIdThatOpenedIt)
        {
            Log.Out($"[DrokkStorage] OpenTileEntityUi completed for EntityId={_entityIdThatOpenedIt}");
        }
    }
#endif

    // ===== CRAFT FROM CONTAINERS FUNCTIONALITY =====
    
    // A pullable storage source: the backing ItemStack[] we read/decrement in place, plus an
    // action to mark the owning object modified (save/sync/UI) once we change it. This unifies
    // containers, vehicles, drones, workstation outputs and dew collectors behind one model.
    private sealed class StorageSource
    {
        public readonly ItemStack[] Items;
        public readonly Action MarkModified;
        public readonly string DebugName;
        public StorageSource(ItemStack[] items, Action markModified, string debugName)
        {
            Items = items;
            MarkModified = markModified;
            DebugName = debugName;
        }
    }

    private static readonly List<StorageSource> currentSources = new List<StorageSource>();

    // True if another (living) player currently has this tile entity open. 3.0 removed
    // GameManager.lockedTileEntities; we now answer from the viewer set we maintain off the
    // LockManager flow (TEFeatureStorage.OnLockedServer/OnUnlockedServer above).
    private static bool IsBeingAccessedByOther(TileEntity te)
    {
        if (te != null && Viewers.TryGetValue(te, out HashSet<int> viewers))
        {
            int meId = GameManager.Instance.World.GetPrimaryPlayerId();
            foreach (int id in viewers)
            {
                if (id == meId) continue;
                if (GameManager.Instance.World.GetEntity(id) is EntityAlive ea && !ea.IsDead())
                    return true;
            }
        }
        return false;
    }

    private static void ReloadStorages()
    {
        if (!DrokkStorage.config.craftFromContainersEnabled)
            return;

        currentSources.Clear();
        var player = GameManager.Instance.World.GetPrimaryPlayer();
        if (player == null) return;

        var pos = player.position;
        var world = GameManager.Instance.World;
        float range = DrokkStorage.config.range;

        DrokkStorage.Dbgl($"ReloadStorages: Scanning for sources near player at {pos}");

        // --- Tile entities: containers, workstation outputs, dew collectors ---
        // 3.0 collapsed World.ChunkClusters (a list) into the single World.ChunkCache cluster.
        var clusters = new List<ChunkCluster> { world.ChunkCache };
        for (int i = 0; i < clusters.Count; i++)
        {
            var cc = clusters[i];
            if (cc == null) continue;
            foreach (var c in cc.chunks.dict.Values.ToArray())
            {
                c.EnterReadLock();
                try
                {
                    foreach (var key in c.tileEntities.dict.Keys.ToArray())
                    {
                        if (!c.tileEntities.dict.TryGetValue(key, out var val))
                            continue;

                        var loc = val.ToWorldPos();

                        // Check range
                        if (range > 0 && Vector3.Distance(pos, loc) > range)
                            continue;

                        // Dew collectors / apiaries (their own item array, no slot locks)
                        if (val is TileEntityCollector collector)
                        {
                            if (DrokkStorage.config.pullFromDewCollectors && !collector.bUserAccessing && !IsBeingAccessedByOther(val))
                            {
                                var col = collector;
                                currentSources.Add(new StorageSource(col.Items,
                                    () => { col.SetChunkModified(); col.SetModified(); }, "DewCollector"));
                                DrokkStorage.Dbgl($"  Added TileEntityCollector at {loc}");
                            }
                            continue;
                        }

                        // Workstations: pull from OUTPUT slots only (never fuel/ingredients/tools)
                        if (val is TileEntityWorkstation workstation)
                        {
                            if (DrokkStorage.config.pullFromWorkstationOutputs && workstation.IsPlayerPlaced
                                && workstation.output != null && !IsBeingAccessedByOther(val))
                            {
                                var ws = workstation;
                                currentSources.Add(new StorageSource(ws.output,
                                    () => { ws.SetChunkModified(); ws.SetModified(); }, "Workstation"));
                                DrokkStorage.Dbgl($"  Added TileEntityWorkstation at {loc}");
                            }
                            continue;
                        }

                        // Everything else must be a lootable container to qualify
                        if (!val.TryGetSelfOrFeature<ITileEntityLootable>(out _))
                            continue;

                        // Handle TileEntityComposite (storage boxes, etc)
                        var entity = val as TileEntityComposite;
                        if (entity != null)
                        {
                            var lootable = entity.GetFeature<ITileEntityLootable>() as TEFeatureStorage;
                            if (lootable != null && lootable.bPlayerStorage && lootable.items != null)
                            {
                                var lockable = entity.GetFeature<ILockable>();
                                if (lockable == null || !lockable.IsLocked() ||
                                    (DrokkStorage.config.allowLockedContainers && lockable.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier)))
                                {
                                    if (IsBeingAccessedByOther(val))
                                        continue;

                                    var teComposite = val;
                                    currentSources.Add(new StorageSource(lootable.items, teComposite.SetModified, "Composite"));
                                    DrokkStorage.Dbgl($"  Added TileEntityComposite at {loc}");
                                }
                            }
                            continue;
                        }

                        // Handle non-composite lootable containers. 3.0 removed the standalone
                        // TileEntitySecureLootContainer class; the remaining non-composite loot
                        // exposes ITileEntityLootable directly (and ILockable if it's lockable).
                        if (val.TryGetSelfOrFeature<ITileEntityLootable>(out ITileEntityLootable secureLootable) && secureLootable.items != null)
                        {
                            if (val is ILockable lockable2 && lockable2.IsLocked() && !lockable2.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier))
                                continue;

                            if (IsBeingAccessedByOther(val))
                                continue;

                            var teSecure = val;
                            currentSources.Add(new StorageSource(secureLootable.items, teSecure.SetModified, "Loot"));
                            DrokkStorage.Dbgl($"  Added non-composite loot container at {loc}");
                        }
                    }
                }
                finally
                {
                    c.ExitReadLock();
                }
            }
        }

        // --- Entities: vehicles and drones ---
        if (DrokkStorage.config.pullFromVehicles || DrokkStorage.config.pullFromDrones)
        {
            var entities = world.Entities?.list;
            if (entities != null)
            {
                foreach (var entity in entities)
                {
                    if (entity == null)
                        continue;
                    if (range > 0 && Vector3.Distance(pos, entity.position) > range)
                        continue;

                    if (DrokkStorage.config.pullFromVehicles && entity is EntityVehicle vehicle)
                    {
                        // Owned, unlocked, has storage with items
                        if (vehicle.bag != null && !vehicle.bag.IsEmpty() && vehicle.bag.items != null
                            && vehicle.hasStorage() && vehicle.LocalPlayerIsOwner()
                            && !vehicle.IsLockedForLocalPlayer(player))
                        {
                            var veh = vehicle;
                            currentSources.Add(new StorageSource(veh.bag.items,
                                () => veh.SetBagModified(), "Vehicle"));
                            DrokkStorage.Dbgl($"  Added EntityVehicle {vehicle.entityId}");
                        }
                    }
                    else if (DrokkStorage.config.pullFromDrones && entity is EntityDrone drone)
                    {
                        // 3.0 removed EntityDrone.lootContainer; drone storage is now the entity's
                        // bag, and SendSyncData(8) is what the drone's own storage UI uses to sync it.
                        var dl = drone.bag;
                        if (dl != null && dl.items != null && drone.LocalPlayerIsOwner()
                            && !drone.isOwnerSyncPending && !drone.isShutdownPending && !drone.isShutdown
                            && drone.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier))
                        {
                            var dr = drone;
                            currentSources.Add(new StorageSource(dl.items, () => dr.SendSyncData(8), "Drone"));
                            DrokkStorage.Dbgl($"  Added EntityDrone {drone.entityId}");
                        }
                    }
                }
            }
        }

        DrokkStorage.Dbgl($"ReloadStorages: Found {currentSources.Count} accessible sources");
    }

    private static List<ItemStack> GetStorageItems()
    {
        ReloadStorages();
        List<ItemStack> list = new List<ItemStack>();
        foreach (var src in currentSources)
        {
            if (src.Items != null)
                list.AddRange(src.Items);
        }
        return list;
    }

    private static int GetAllItemCount(ItemValue item)
    {
        int count = 0;
        ReloadStorages();

        foreach (var src in currentSources)
        {
            ItemStack[] items = src.Items;
            if (items == null)
                continue;

            for (int j = 0; j < items.Length; j++)
            {
                if (items[j]?.itemValue?.type == item.type)
                    count += items[j].count;
            }
        }
        return count;
    }

    private static int DecItem(ItemValue item, int count)
    {
        int numLeft = count;
        DrokkStorage.Dbgl($"DecItem: Trying to remove {count} {item.ItemClass.GetItemName()}");

        foreach (var src in currentSources)
        {
            ItemStack[] items = src.Items;
            if (items == null)
                continue;

            bool modified = false;
            for (int j = 0; j < items.Length; j++)
            {
                if (items[j]?.itemValue?.type == item.type)
                {
                    int toRem = Math.Min(numLeft, items[j].count);
                    DrokkStorage.Dbgl($"  Removing {toRem}/{numLeft} from {src.DebugName}");
                    numLeft -= toRem;

                    if (items[j].count <= toRem)
                        items[j].Clear();
                    else
                        items[j].count -= toRem;

                    modified = true;

                    if (numLeft <= 0)
                        break;
                }
            }

            // Mark modified once per source - this triggers save/sync and viewer refresh
            if (modified)
                src.MarkModified?.Invoke();

            if (numLeft <= 0)
                return count;
        }
        return count - numLeft;
    }
    
    // Helper methods for adding storage item counts
    private static int AddAllStoragesCountItemValue(int count, ItemValue item)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled)
            return count;
        return count + GetAllItemCount(item);
    }
    
    private static int AddAllStoragesCountItemClass(int count, ItemClass itemClass)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled)
            return count;
        var item = new ItemValue(itemClass.Id, false);
        return count + GetAllItemCount(item);
    }
    
    private static int AddAllStoragesCountIngEntry(int count, XUiC_IngredientEntry entry)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled)
            return count;
        return AddAllStoragesCountItemValue(count, entry.Ingredient.itemValue);
    }
    
    private static int AddAllStoragesCountItemStack(int count, ItemStack itemStack)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled)
            return count;
        return AddAllStoragesCountItemValue(count, itemStack.itemValue);
    }
    
    private static List<ItemStack> GetAllStorageStacksList(List<ItemStack> items)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled)
            return items;
        
        List<ItemStack> itemList = new List<ItemStack>();
        itemList.AddRange(items);
        itemList.AddRange(GetStorageItems());
        return itemList;
    }
    
    private static ItemStack[] GetAllStorageStacksArray(ItemStack[] items)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled)
            return items;
        return GetAllStorageStacksList(items.ToList()).ToArray();
    }
    
    private static void AddAllStorageStacks(List<ItemStack> items)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled)
            return;
        items.AddRange(GetStorageItems());
    }
    
    private static int GetTrueRemaining(IList<ItemStack> _itemStacks, int i, int numLeft)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled)
            return numLeft;
        numLeft -= GetAllItemCount(_itemStacks[i].itemValue);
        return numLeft;
    }
    
    private static void DecItemForRemoveItems(IList<ItemStack> _itemStacks, int i, int numLeft)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled)
            return;
        DrokkStorage.Dbgl($"DecItemForRemoveItems: Removing {numLeft} {_itemStacks[i].itemValue.ItemClass.GetItemName()}");
        DecItem(_itemStacks[i].itemValue, numLeft);
    }
    
    private static int DecItemForGetAmmoCountToReload(Inventory inv, ItemValue item, int count, bool modded, IList<ItemStack> _removedItems)
    {
        int num = inv.DecItem(item, count, modded, _removedItems);
        if (num == count || !DrokkStorage.config.enableForReload || !DrokkStorage.config.craftFromContainersEnabled)
            return num;
        
        int numLeft = count - num;
        DrokkStorage.Dbgl($"DecItemForGetAmmoCountToReload: Removing {numLeft} {item.ItemClass.GetItemName()} for reload");
        return DecItem(item, numLeft);
    }
    
    private static int RemoveRemainingForUpgrade(int numRemoved, ItemActionRepair action, BlockValue blockValue)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled || !DrokkStorage.config.enableForRepairAndUpgrade)
            return numRemoved;
            
        Block block = blockValue.Block;
        ItemValue item = ItemClass.GetItem(action.GetUpgradeItemName(block), false);
        int totalToRemove;
        // 3.0 nests the upgrade item count under the "UpgradeBlock" class -> "ItemCount" subkey.
        if (!block.Properties.TryGetValue(Block.PropUpgradeBlockClass, Block.PropUpgradeBlockItemCount, out string itemCountStr) || !int.TryParse(itemCountStr, out totalToRemove))
        {
            DrokkStorage.Dbgl($"RemoveRemainingForUpgrade: couldn't get total to remove");
            return numRemoved;
        }
        
        DrokkStorage.Dbgl($"RemoveRemainingForUpgrade: need to remove {totalToRemove}, removed {numRemoved} from bag");
        
        if (totalToRemove <= numRemoved)
            return numRemoved;
        
        var numLeft = totalToRemove - numRemoved;
        numLeft -= DecItem(item, numLeft);
        
        if (numLeft <= 0)
            return totalToRemove;
            
        DrokkStorage.Dbgl($"RemoveRemainingForUpgrade: still missing {numLeft}!");
        return totalToRemove - numLeft;
    }
    
    private static int RemoveRemainingForRepair(int numRemoved, ItemStack _itemStack)
    {
        if (!DrokkStorage.config.craftFromContainersEnabled || !DrokkStorage.config.enableForRepairAndUpgrade)
            return numRemoved;
            
        int totalToRemove = _itemStack.count;
        
        if (totalToRemove <= numRemoved)
            return numRemoved;
        
        var numLeft = totalToRemove - numRemoved;
        numLeft -= DecItem(_itemStack.itemValue, numLeft);
        
        if (numLeft <= 0)
            return totalToRemove;
            
        DrokkStorage.Dbgl($"RemoveRemainingForRepair: still missing {numLeft}!");
        return totalToRemove - numLeft;
    }

    // ===== QUICK STACK AND QUICK RESTOCK FUNCTIONALITY =====

    public static bool IsContainerUnlocked(int _entityIdThatOpenedIt, TileEntity _tileEntity)
    {
        try
        {
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                return false;

            if (_tileEntity == null)
                return false;

            // Handle locked non-composite containers. 3.0 removed TileEntitySecureLootContainer;
            // lockable non-composite loot now implements ILockable (composite storage is the else-if).
            if ((_tileEntity is ILockable lootContainer) && lootContainer.IsLocked())
            {
                // Handle Host
                if (!GameManager.IsDedicatedServer && _entityIdThatOpenedIt == GameManager.Instance.World.GetPrimaryPlayerId())
                {
                    if (!lootContainer.IsUserAllowed(GameManager.Instance.persistentLocalPlayer.PrimaryId))
                        return false;
                }
                else
                {
                    // Handle Client
                    var cinfo = ConnectionManager.Instance.Clients.ForEntityId(_entityIdThatOpenedIt);
                    if (cinfo == null || !lootContainer.IsUserAllowed(cinfo.CrossplatformId))
                        return false;
                }
            }
            // Handle locked composite storages
            else if ((_tileEntity is TileEntityComposite compositeContainer) && compositeContainer.teData.GetFeatureIndex<TEFeatureLockable>() > 0)
            {
                TEFeatureLockable fLockable = compositeContainer.GetFeature<TEFeatureLockable>();
                // Handle Host
                if (!GameManager.IsDedicatedServer && _entityIdThatOpenedIt == GameManager.Instance.World.GetPrimaryPlayerId())
                {
                    if (!fLockable.IsUserAllowed(GameManager.Instance.persistentLocalPlayer.PrimaryId))
                        return false;
                }
                else
                {
                    // Handle Client
                    var cinfo = ConnectionManager.Instance.Clients.ForEntityId(_entityIdThatOpenedIt);
                    if (cinfo == null || !fLockable.IsUserAllowed(cinfo.CrossplatformId))
                        return false;
                }
            }

            // Skip containers another living player currently has open (avoids racing their live
            // QuickStack edits). 3.0 removed GameManager.lockedTileEntities, so this now reads from
            // the viewer set maintained off the LockManager flow.
            if (IsBeingAccessedByOther(_tileEntity))
                return false;

            return true;
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
            return false;
        }
    }

    public static bool IsValidLoot(TileEntity _tileEntity)
    {
        try
        {
            return (_tileEntity.GetTileEntityType() == TileEntityType.Loot ||
                _tileEntity.GetTileEntityType() == TileEntityType.SecureLoot ||
                _tileEntity.GetTileEntityType() == TileEntityType.SecureLootSigned);
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
            return false;
        }
    }

    public static (ITileEntityLootable, TileEntity) GetInventoryFromBlockPosition(Vector3i position)
    {
        try
        {
            // 3.0 removed the standalone TileEntityLootContainer class; non-composite loot (e.g.
            // secure loot containers) now identifies itself through ITileEntityLootable. Composite
            // storage is handled by the TEFeatureStorage branch below, so skip composites here.
            TileEntity loot = GameManager.Instance.World.GetTileEntity(position);
            if (loot != null && !(loot is TileEntityComposite) && loot.TryGetSelfOrFeature<ITileEntityLootable>(out ITileEntityLootable lootable))
            {
                if (IsValidLoot(loot) && !loot.IsUserAccessing())
                    return (lootable, loot);

                return (null, loot);
            }

            TileEntityComposite compositeContainer = GameManager.Instance.World.GetTileEntity(position) as TileEntityComposite;

            if (compositeContainer == null)
                return (null, null);

            if (compositeContainer.teData.GetFeatureIndex<TEFeatureStorage>() == 0)
                return (null, compositeContainer);

            if (compositeContainer.IsUserAccessing())
                return (null, compositeContainer);

            return (compositeContainer.GetFeature<TEFeatureStorage>(), compositeContainer);
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
            return (null, null);
        }
    }

    public static IEnumerable<ValueTuple<Vector3i, TileEntity>> FindNearbyLootContainers(Vector3i _center, int _playerEntityId)
    {
        for (int i = -DrokkStorage.config.stashDistance.x; i <= DrokkStorage.config.stashDistance.x; i++)
        {
            for (int j = -DrokkStorage.config.stashDistance.y; j <= DrokkStorage.config.stashDistance.y; j++)
            {
                for (int k = -DrokkStorage.config.stashDistance.z; k <= DrokkStorage.config.stashDistance.z; k++)
                {
                    var offset = new Vector3i(i, j, k);
                    var val = GetInventoryFromBlockPosition(_center + offset);

                    if (val.Item1 == null)
                        continue;

                    if (!IsContainerUnlocked(_playerEntityId, val.Item2))
                        continue;

                    yield return new ValueTuple<Vector3i, TileEntity>(offset, val.Item2);
                }
            }
        }
    }

    internal static XUiM_LootContainer.EItemMoveKind GetMoveKind(QuickStackType _type = QuickStackType.Stack)
    {
        try
        {
            float unscaledTime = Time.unscaledTime;
            float lastClickTime = lastClickTimes[(int)_type];
            lastClickTimes[(int)_type] = unscaledTime;

            if (unscaledTime - lastClickTime < 2.0f)
                return XUiM_LootContainer.EItemMoveKind.FillAndCreate;
            else
                return XUiM_LootContainer.EItemMoveKind.FillOnly;
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
            return XUiM_LootContainer.EItemMoveKind.FillOnly;
        }
    }

    public static void MoveQuickStack()
    {
        try
        {
            // 3.0 loot containers are always position-backed tile entities (no entity-id concept),
            // so an open LootContainer means a loot window is up - skip the auto QuickStack then.
            if (backpackWindow.xui.LootContainer != null)
                return;

            var moveKind = GetMoveKind();
            EntityPlayerLocal primaryPlayer = GameManager.Instance.World.GetPrimaryPlayer();

            foreach (var pair in FindNearbyLootContainers(new Vector3i(primaryPlayer.position), primaryPlayer.entityId))
            {
                if (pair.Item2.TryGetSelfOrFeature<ITileEntityLootable>(out var lootable))
                {
                    XUiM_LootContainer.StashItems(backpackWindow, playerBackpack, lootable, 0, playerControls.LockedSlots, moveKind, playerControls.MoveStartBottomRight);
                    pair.Item2.SetModified();
                }
            }
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    public static void ClientMoveQuickStack(Vector3i center, IEnumerable<Vector3i> _entityContainers)
    {
        try
        {
            if (backpackWindow == null || backpackWindow.xui.LootContainer != null)
                return;

            var moveKind = GetMoveKind();

            if (_entityContainers == null)
                return;

            foreach (var offset in _entityContainers)
            {
                var val = GetInventoryFromBlockPosition(center + offset);
                if (val.Item1 == null)
                    continue;

                XUiM_LootContainer.StashItems(backpackWindow, playerBackpack, val.Item1, 0, playerControls.LockedSlots, moveKind, playerControls.MoveStartBottomRight);
                val.Item2.SetModified();
            }
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    public static void MoveQuickRestock()
    {
        try
        {
            // 3.0 loot containers are always position-backed tile entities (no entity-id concept),
            // so an open LootContainer means a loot window is up - skip the auto QuickStack then.
            if (backpackWindow.xui.LootContainer != null)
                return;

            var moveKind = GetMoveKind(QuickStackType.Restock);
            EntityPlayerLocal primaryPlayer = GameManager.Instance.World.GetPrimaryPlayer();
            LocalPlayerUI playerUI = LocalPlayerUI.GetUIForPlayer(primaryPlayer);
            XUiC_LootWindowGroup lootWindowGroup = (XUiC_LootWindowGroup)((XUiWindowGroup)playerUI.windowManager.GetWindow("looting")).Controller;

            foreach (var pair in FindNearbyLootContainers(new Vector3i(primaryPlayer.position), primaryPlayer.entityId))
            {
                if (pair.Item2.TryGetSelfOrFeature<ITileEntityLootable>(out var lootable))
                {
                    lootWindowGroup.lootWindow.SetTileEntityChest("QUICKSTACK", lootable);
                    PackedBoolArray lockedSlots = new PackedBoolArray(lootWindowGroup.lootWindow.lootContainer.items.Length);
                    XUiM_LootContainer.StashItems(backpackWindow, lootWindowGroup.lootWindow.lootContainer, playerUI.mXUi.PlayerInventory, 0, lockedSlots, moveKind, playerControls.MoveStartBottomRight);
                    pair.Item2.SetModified();
                }
            }
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    public static void ClientMoveQuickRestock(Vector3i center, IEnumerable<Vector3i> _entityContainers)
    {
        try
        {
            if (backpackWindow == null || backpackWindow.xui.LootContainer != null)
                return;

            var moveKind = GetMoveKind(QuickStackType.Restock);

            if (_entityContainers == null)
                return;

            EntityPlayerLocal primaryPlayer = GameManager.Instance.World.GetPrimaryPlayer();
            LocalPlayerUI playerUI = LocalPlayerUI.GetUIForPlayer(primaryPlayer);
            XUiC_LootWindowGroup lootWindowGroup = (XUiC_LootWindowGroup)((XUiWindowGroup)playerUI.windowManager.GetWindow("looting")).Controller;

            foreach (var offset in _entityContainers)
            {
                var val = GetInventoryFromBlockPosition(center + offset);
                if (val.Item1 == null)
                    continue;

                lootWindowGroup.lootWindow.SetTileEntityChest("QUICKSTACK", val.Item1);
                PackedBoolArray lockedSlots = new PackedBoolArray(lootWindowGroup.lootWindow.lootContainer.items.Length);
                XUiM_LootContainer.StashItems(backpackWindow, lootWindowGroup.lootWindow.lootContainer, playerUI.mXUi.PlayerInventory, 0, lockedSlots, moveKind, playerControls.MoveStartBottomRight);
                val.Item2.SetModified();
            }
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    public static void QuickStackOnClick()
    {
        try
        {
            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsSinglePlayer)
                MoveQuickStack();
            else if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(NetPackageManager.GetPackage<NetPackageFindOpenableContainers>().Setup(GameManager.Instance.World.GetPrimaryPlayerId(), QuickStackType.Stack));
            else if (!GameManager.IsDedicatedServer)
            {
                var player = GameManager.Instance.World.GetPrimaryPlayer();
                var center = new Vector3i(player.position);
                List<Vector3i> offsets = new List<Vector3i>(1024);
                foreach (var pair in FindNearbyLootContainers(center, player.entityId))
                    offsets.Add(pair.Item1);
                ClientMoveQuickStack(center, offsets);
            }
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    public static void QuickRestockOnClick()
    {
        try
        {
            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsSinglePlayer)
                MoveQuickRestock();
            else if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(NetPackageManager.GetPackage<NetPackageFindOpenableContainers>().Setup(GameManager.Instance.World.GetPrimaryPlayerId(), QuickStackType.Restock));
            else if (!GameManager.IsDedicatedServer)
            {
                var player = GameManager.Instance.World.GetPrimaryPlayer();
                var center = new Vector3i(player.position);
                List<Vector3i> offsets = new List<Vector3i>(1024);
                foreach (var pair in FindNearbyLootContainers(center, player.entityId))
                    offsets.Add(pair.Item1);
                ClientMoveQuickRestock(center, offsets);
            }
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    public static void UpdateUI()
    {
        try
        {
            if (playerBackpack == null)
                return;

            XUiController[] slots = playerBackpack.GetItemStackControllers();
            for (int i = 0; i < slots.Length; ++i)
            {
                var sprite = slots[i].GetChildById("iconSlotLock")?.ViewComponent as XUiV_Sprite;
                if (sprite != null)
                    sprite.Color = DrokkStorage.config.lockIconColor;
            }

            playerControls.GetChildById("btnToggleLockMode").ViewComponent.IsVisible = DrokkStorage.config.lockModeIconVisible;
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    // ===== TRANSPILER PATCHES FOR CRAFTING =====
    
    [HarmonyPatch(typeof(ItemActionEntryCraft), nameof(ItemActionEntryCraft.OnActivated))]
    public static class ItemActionEntryCraft_OnActivated_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetAllItemStacks)))
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(GetAllStorageStacksList))));
                    patched++;
                    break;
                }
            }
            LogPatchResult("ItemActionEntryCraft.OnActivated", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.CanSwapItems))]
    public static class XUiM_PlayerInventory_CanSwapItems_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetAllItemStacks)))
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(GetAllStorageStacksList))));
                    patched++;
                    break;
                }
            }
            LogPatchResult("XUiM_PlayerInventory.CanSwapItems", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.HasItems))]
    public static class XUiM_PlayerInventory_HasItems_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (i > 0 && i < codes.Count - 1 && codes[i].opcode == OpCodes.Ldc_I4_0 && codes[i + 1].opcode == OpCodes.Ret)
                {
                    codes.Insert(i, codes[i - 1].Clone());
                    codes.Insert(i, codes[i - 2].Clone());
                    codes.Insert(i, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(GetTrueRemaining))));
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldloc_1));
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldloc_0));
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_1));
                    patched++;
                    break;
                }
            }
            LogPatchResult("XUiM_PlayerInventory.HasItems", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.RemoveItems))]
    public static class XUiM_PlayerInventory_RemoveItems_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(Inventory), nameof(Inventory.DecItem)))
                {
                    var ci = codes[i + 3];
                    var ciNew = new CodeInstruction(OpCodes.Ldarg_1);
                    ci.MoveLabelsTo(ciNew);
                    codes.Insert(i + 3, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(DecItemForRemoveItems))));
                    codes.Insert(i + 3, new CodeInstruction(OpCodes.Ldloc_1));
                    codes.Insert(i + 3, new CodeInstruction(OpCodes.Ldloc_0));
                    codes.Insert(i + 3, ciNew);
                    patched++;
                    break;
                }
            }
            LogPatchResult("XUiM_PlayerInventory.RemoveItems", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(XUiC_RecipeCraftCount), "calcMaxCraftable")]
    public static class XUiC_RecipeCraftCount_calcMaxCraftable_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetAllItemStacks)))
                {
                    codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(GetAllStorageStacksArray))));
                    patched++;
                    break;
                }
            }
            LogPatchResult("XUiC_RecipeCraftCount.calcMaxCraftable", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(XUiC_IngredientEntry), nameof(XUiC_IngredientEntry.GetBindingValueInternal))]
    public static class XUiC_IngredientEntry_GetBindingValueInternal_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getItemCount = AccessTools.Method(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetItemCount), new Type[] { typeof(ItemValue) });
            int patched = 0;
            // GetItemCount(ItemValue) is called by SEVERAL bindings here ("havecount" and
            // "haveneedcount"); the "have/need" display uses a later occurrence, so we must
            // patch EVERY call, not just the first one.
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == getItemCount)
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(AddAllStoragesCountIngEntry))));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                    patched++;
                    i += 2; // skip past the two instructions we just inserted
                }
            }
            LogPatchResult("XUiC_IngredientEntry.GetBindingValueInternal", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(XUiC_RecipeList), nameof(XUiC_RecipeList.Update))]
    public static class XUiC_RecipeList_Update_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (i > 2 && codes[i].opcode == OpCodes.Call && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(XUiC_RecipeList), "BuildRecipeInfosList"))
                {
                    var ci = codes[i - 2];
                    var ciNew = new CodeInstruction(OpCodes.Ldloc_0);
                    ci.MoveLabelsTo(ciNew);
                    codes.Insert(i - 2, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(AddAllStorageStacks))));
                    codes.Insert(i - 2, ciNew);
                    patched++;
                    break;
                }
            }
            LogPatchResult("XUiC_RecipeList.Update", patched);
            return codes.AsEnumerable();
        }
    }
    
    // Recipe Tracker (pinned recipe on the side of the screen). The setter computes
    // currentCount = PlayerInventory.GetItemCount(ingredient.itemValue) and uses it for the
    // "have/need" text and the IsComplete (green) check. Without this patch the pinned
    // tracker ignores items sitting in nearby containers. We add the storage count right
    // after the vanilla GetItemCount call so both the count and IsComplete reflect storage.
    [HarmonyPatch(typeof(XUiC_RecipeTrackerIngredientEntry), nameof(XUiC_RecipeTrackerIngredientEntry.Ingredient), MethodType.Setter)]
    public static class XUiC_RecipeTrackerIngredientEntry_Ingredient_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetItemCount), new Type[] { typeof(ItemValue) }))
                {
                    // Stack after GetItemCount: [.., int count]. Push this.ingredient on top,
                    // then call AddAllStoragesCountItemStack(int, ItemStack) -> int.
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(AddAllStoragesCountItemStack))));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(XUiC_RecipeTrackerIngredientEntry), nameof(XUiC_RecipeTrackerIngredientEntry.ingredient))));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                    patched++;
                    i += 3;
                }
            }
            LogPatchResult("XUiC_RecipeTrackerIngredientEntry.Ingredient", patched);
            return codes.AsEnumerable();
        }
    }

    // ===== LIVE RECIPE TRACKING =====
    // The pinned recipe tracker (and crafting recipe list) only recompute their available
    // counts when the player inventory raises OnBackpackItemsChanged. So when a nearby
    // workstation finishes crafting/cooking (its output is a pullable source), the tracker
    // would not update until the player's own inventory next changes. We nudge that event so
    // the tracker re-reads counts - which now include nearby storage - immediately.
    private static float lastRecipeUiRefresh = 0f;

    public static void RefreshCraftingUI()
    {
        try
        {
            if (!DrokkStorage.config.liveRecipeTracking || !DrokkStorage.config.craftFromContainersEnabled)
                return;

            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            var xui = player?.playerUI?.xui;
            if (xui?.PlayerInventory == null)
                return;

            // Throttle: workstation output can update rapidly; one refresh per 0.4s is plenty.
            float now = Time.unscaledTime;
            if (now - lastRecipeUiRefresh < 0.4f)
                return;
            lastRecipeUiRefresh = now;

            // Marks the recipe tracker and open crafting windows dirty -> they recompute counts.
            // 3.0 renamed the trigger; onBackpackItemsChanged is now the OnBackpackItemsChanged event,
            // raised via dispatchBackpackItemsChanged().
            xui.PlayerInventory.dispatchBackpackItemsChanged();
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    // Fires when a workstation NOT currently open finishes a craft (e.g. forge/campfire in the background).
    [HarmonyPatch(typeof(TileEntityWorkstation), nameof(TileEntityWorkstation.AddCraftComplete))]
    public static class TileEntityWorkstation_AddCraftComplete_Patch
    {
        public static void Postfix()
        {
            RefreshCraftingUI();
        }
    }

    // Fires when the output grid of the currently open workstation window updates (foreground craft).
    [HarmonyPatch(typeof(XUiC_WorkstationOutputGrid), nameof(XUiC_WorkstationOutputGrid.UpdateData))]
    public static class XUiC_WorkstationOutputGrid_UpdateData_Patch
    {
        public static void Postfix()
        {
            RefreshCraftingUI();
        }
    }

    // ===== TRANSPILER PATCHES FOR REPAIR =====

    [HarmonyPatch(typeof(ItemActionEntryRepair), nameof(ItemActionEntryRepair.RefreshEnabled))]
    public static class ItemActionEntryRepair_RefreshEnabled_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            if (!DrokkStorage.config.enableForRepairAndUpgrade)
                return codes;
                
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetItemCount), new Type[] { typeof(ItemValue) }))
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(AddAllStoragesCountItemClass))));
                    codes.Insert(i + 1, codes[i - 4].Clone());
                    patched++;
                    break;
                }
            }
            LogPatchResult("ItemActionEntryRepair.RefreshEnabled", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(ItemActionEntryRepair), nameof(ItemActionEntryRepair.OnActivated))]
    public static class ItemActionEntryRepair_OnActivated_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            if (!DrokkStorage.config.enableForRepairAndUpgrade)
                return codes;
                
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetItemCount), new Type[] { typeof(ItemValue) }))
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(AddAllStoragesCountItemClass))));
                    codes.Insert(i + 1, codes[i - 4].Clone());
                    patched++;
                    break;
                }
            }
            LogPatchResult("ItemActionEntryRepair.OnActivated", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(ItemActionRepair), "CanRemoveRequiredResource")]
    public static class ItemActionRepair_CanRemoveRequiredResource_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            if (!DrokkStorage.config.enableForRepairAndUpgrade)
                return codes;
                
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(Bag), nameof(Bag.GetItemCount), new Type[] { typeof(ItemValue), typeof(int), typeof(int), typeof(bool) }))
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(AddAllStoragesCountItemValue))));
                    codes.Insert(i + 1, codes[i - 4].Clone());
                    patched++;
                    break;
                }
            }
            LogPatchResult("ItemActionRepair.CanRemoveRequiredResource", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(ItemActionRepair), "RemoveRequiredResource")]
    public static class ItemActionRepair_RemoveRequiredResource_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            if (!DrokkStorage.config.enableForRepairAndUpgrade)
                return codes;
                
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(Bag), nameof(Bag.DecItem)))
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(RemoveRemainingForUpgrade))));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_2));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                    patched++;
                    break;
                }
            }
            LogPatchResult("ItemActionRepair.RemoveRequiredResource", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(ItemActionRepair), "canRemoveRequiredItem")]
    public static class ItemActionRepair_canRemoveRequiredItem_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            if (!DrokkStorage.config.enableForRepairAndUpgrade)
                return codes;
                
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(Bag), nameof(Bag.GetItemCount), new Type[] { typeof(ItemValue), typeof(int), typeof(int), typeof(bool) }))
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(AddAllStoragesCountItemStack))));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_2));
                    patched++;
                    break;
                }
            }
            LogPatchResult("ItemActionRepair.canRemoveRequiredItem", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(ItemActionRepair), "removeRequiredItem")]
    public static class ItemActionRepair_removeRequiredItem_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            if (!DrokkStorage.config.enableForRepairAndUpgrade)
                return codes;
                
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(Bag), nameof(Bag.DecItem)))
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(RemoveRemainingForRepair))));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_2));
                    patched++;
                    break;
                }
            }
            LogPatchResult("ItemActionRepair.removeRequiredItem", patched);
            return codes.AsEnumerable();
        }
    }
    
    // ===== TRANSPILER PATCHES FOR RELOAD =====
    
    // 3.0 merged GetAmmoCountToReload into GetAmmoCount; the reload-availability calculation we
    // inject "count nearby storages too" into now lives in GetAmmoCount.
    [HarmonyPatch(typeof(AnimatorRangedReloadState), nameof(AnimatorRangedReloadState.GetAmmoCount))]
    public static class AnimatorRangedReloadState_GetAmmoCountToReload_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(Inventory), nameof(Inventory.DecItem)))
                {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = AccessTools.Method(typeof(DrokkStoragePatches), nameof(DecItemForGetAmmoCountToReload));
                    patched++;
                }
                else if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(Inventory), nameof(Inventory.GetItemCount), new Type[] { typeof(ItemValue), typeof(bool), typeof(int), typeof(int), typeof(bool) }))
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(AddAllStoragesCountItemValue))));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_2));
                    patched++;
                }
            }
            LogPatchResult("AnimatorRangedReloadState.GetAmmoCountToReload", patched);
            return codes.AsEnumerable();
        }
    }
    
    [HarmonyPatch(typeof(Animator3PRangedReloadState), nameof(Animator3PRangedReloadState.GetAmmoCount))]
    public static class Animator3PRangedReloadState_GetAmmoCountToReload_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(Inventory), nameof(Inventory.DecItem)))
                {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = AccessTools.Method(typeof(DrokkStoragePatches), nameof(DecItemForGetAmmoCountToReload));
                    patched++;
                }
                else if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == AccessTools.Method(typeof(Inventory), nameof(Inventory.GetItemCount), new Type[] { typeof(ItemValue), typeof(bool), typeof(int), typeof(int), typeof(bool) }))
                {
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrokkStoragePatches), nameof(AddAllStoragesCountItemValue))));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_2));
                    patched++;
                }
            }
            LogPatchResult("Animator3PRangedReloadState.GetAmmoCountToReload", patched);
            return codes.AsEnumerable();
        }
    }

    // 9. (Removed.) This patched TileEntityLootContainer.read to bypass the bUserAccessing guard so
    // an open window would accept incoming syncs. That class is gone in 3.0; composite storage now
    // reads through TEFeatureStorage.Read, which the equivalent patch below (#11) already handles.
#if false
    [HarmonyPatch(typeof(TileEntityLootContainer), nameof(TileEntityLootContainer.read))]
    public static class TileEntityLootContainer_read_Patch
    {
        private static bool wasUserAccessing;

        public static void Prefix(TileEntityLootContainer __instance, PooledBinaryReader _br, TileEntity.StreamModeRead _eStreamMode)
        {
            if (_eStreamMode == TileEntity.StreamModeRead.FromClient || _eStreamMode == TileEntity.StreamModeRead.FromServer)
            {
                // QUEST GUARD: quest fetch/buried-supplies items are injected client-side and
                // are not authoritatively owned by the server. Forcing the bUserAccessing
                // bypass here lets a stale server echo overwrite the open window and re-add the
                // supply after pickup, breaking quest completion. Skip the bypass for these
                // containers and let vanilla's bUserAccessing guard discard the incoming sync.
                if (DrokkStorage.IsQuestContainer(__instance))
                {
                    wasUserAccessing = false;
                    Log.Out($"[DrokkStorage] TileEntityLootContainer.read (RECEIVE): QUEST container {__instance}, skipping bUserAccessing bypass (vanilla handling).");
                    return;
                }

                wasUserAccessing = __instance.IsUserAccessing();
                if (wasUserAccessing)
                {
                    Log.Out($"[DrokkStorage] TileEntityLootContainer.read (RECEIVE): WINDOW OPEN. Temporarily setting bUserAccessing to false for TE {__instance} to allow update.");
                    __instance.SetUserAccessing(false);
                }

                Log.Out($"[DrokkStorage] TileEntityLootContainer.read (RECEIVE) PREFIX: TE={__instance}, bUserAccessing={__instance.IsUserAccessing()}, ItemsBefore={DrokkStorage.GetItemCount(__instance)}, StreamMode={_eStreamMode}");
            }
        }

        public static void Postfix(TileEntityLootContainer __instance, TileEntity.StreamModeRead _eStreamMode)
        {
            if (_eStreamMode == TileEntity.StreamModeRead.FromClient || _eStreamMode == TileEntity.StreamModeRead.FromServer)
            {
                Log.Out($"[DrokkStorage] TileEntityLootContainer.read (RECEIVE) POSTFIX: TE={__instance}, ItemsAfter={DrokkStorage.GetItemCount(__instance)}");

                if (wasUserAccessing)
                {
                    Log.Out($"[DrokkStorage] TileEntityLootContainer.read (RECEIVE): Restoring bUserAccessing to true for TE {__instance}");
                    __instance.SetUserAccessing(true);
                    wasUserAccessing = false;
                }
            }
        }
    }
#endif

    // 10. Debug NetPackageTileEntity.Setup to see when data is SENT
    [HarmonyPatch(typeof(NetPackageTileEntity), nameof(NetPackageTileEntity.Setup), new Type[] { typeof(TileEntity), typeof(TileEntity.StreamModeWrite) })]
    public static class NetPackageTileEntity_Setup_Patch
    {
        public static void Postfix(TileEntity _te, TileEntity.StreamModeWrite _eStreamMode)
        {
            bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
            Log.Out($"[DrokkStorage] NetPackageTileEntity.Setup (SENDING): TE={_te}, Items={DrokkStorage.GetItemCount(_te)}, StreamMode={_eStreamMode}, From={(isServer ? "SERVER" : "CLIENT")}");
        }
    }

    // 11. Fix for Composite TileEntities (V1.0 containers like writable crates)
    [HarmonyPatch(typeof(TEFeatureStorage), nameof(TEFeatureStorage.Read))]
    public static class TEFeatureStorage_Read_Patch
    {
        private static bool wasUserAccessing;

        public static void Prefix(TEFeatureStorage __instance, PooledBinaryReader _br, TileEntity.StreamModeRead _eStreamMode)
        {
            if (_eStreamMode == TileEntity.StreamModeRead.FromClient || _eStreamMode == TileEntity.StreamModeRead.FromServer)
            {
                TileEntity parent = __instance.Parent;
                if (parent != null)
                {
                    // QUEST GUARD: skip the bypass for quest containers (see TileEntityLootContainer.read).
                    if (DrokkStorage.IsQuestContainer(parent))
                    {
                        wasUserAccessing = false;
                        Log.Out($"[DrokkStorage] TEFeatureStorage.Read (RECEIVE): QUEST container {parent}, skipping bUserAccessing bypass (vanilla handling).");
                        return;
                    }

                    wasUserAccessing = parent.IsUserAccessing();
                    if (wasUserAccessing)
                    {
                        Log.Out($"[DrokkStorage] TEFeatureStorage.Read (RECEIVE): WINDOW OPEN. Temporarily setting bUserAccessing to false for COMPOSITE TE {parent} to allow update.");
                        parent.SetUserAccessing(false);
                    }
                    Log.Out($"[DrokkStorage] TEFeatureStorage.Read (RECEIVE) PREFIX: Parent={parent}, ItemsBefore={DrokkStorage.GetItemCount(parent)}");
                }
            }
        }

        public static void Postfix(TEFeatureStorage __instance, TileEntity.StreamModeRead _eStreamMode)
        {
            if (_eStreamMode == TileEntity.StreamModeRead.FromClient || _eStreamMode == TileEntity.StreamModeRead.FromServer)
            {
                TileEntity parent = __instance.Parent;
                if (parent != null)
                {
                    Log.Out($"[DrokkStorage] TEFeatureStorage.Read (RECEIVE) POSTFIX: Parent={parent}, ItemsAfter={DrokkStorage.GetItemCount(parent)}");
                    if (wasUserAccessing)
                    {
                        Log.Out($"[DrokkStorage] TEFeatureStorage.Read (RECEIVE): Restoring bUserAccessing to true for COMPOSITE TE {parent}");
                        parent.SetUserAccessing(true);
                        wasUserAccessing = false;
                    }
                }
            }
        }
    }

    // ===== QUICK STACK AND QUICK RESTOCK PATCHES =====

    [HarmonyPatch(typeof(XUiC_BackpackWindow), "Init")]
    public static class BackpackWindow_Init_Patch
    {
        public static void Postfix(XUiC_BackpackWindow __instance)
        {
            try
            {
                backpackWindow = __instance;
                playerControls = __instance.GetChildByType<XUiC_ContainerStandardControls>();
                playerBackpack = __instance.backpackGrid;
                lastClickTimes.Fill(0.0f);

                XUiController[] slots = playerBackpack.GetItemStackControllers();

                // Handle hotkey for locking slots
                for (int i = 0; i < slots.Length; ++i)
                {
                    slots[i].OnPress += (XUiController _sender, int _mouseButton) =>
                    {
                        for (int j = 0; j < DrokkStorage.config.quickLockHotkeys.Length; j++)
                        {
                            if (!UICamera.GetKey(DrokkStorage.config.quickLockHotkeys[j]))
                                return;
                        }

                        XUiC_ItemStack itemStack = _sender as XUiC_ItemStack;
                        if (itemStack != null)
                        {
                            itemStack.UserLockedSlot = !itemStack.UserLockedSlot;
                            backpackWindow.UpdateLockedSlots(playerControls);
                            itemStack.xui.PlayMenuClickSound();
                        }
                    };
                }

                // Handle clicking on QuickStack and QuickRestock
                XUiController childById = playerControls.GetChildById("btnMoveQuickStack");
                if (childById != null)
                {
                    childById.OnPress += delegate (XUiController _sender, int _args)
                    {
                        QuickStackOnClick();
                    };
                }

                childById = playerControls.GetChildById("btnMoveQuickRestock");
                if (childById != null)
                {
                    childById.OnPress += delegate (XUiController _sender, int _args)
                    {
                        QuickRestockOnClick();
                    };
                }
            }
            catch (Exception e)
            {
                DrokkStorage.Dbgl(e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(XUiC_BackpackWindow), "OnOpen")]
    public static class BackpackWindow_OnOpen_Patch
    {
        public static void Postfix()
        {
            UpdateUI();
        }
    }

    [HarmonyPatch(typeof(XUiC_BackpackWindow), "GetBindingValueInternal")]
    public static class BackpackWindow_GetBindingValueInternal_Patch
    {
        public static void Postfix(ref bool __result, XUiC_BackpackWindow __instance, ref string value, string bindingName)
        {
            try
            {
                if (!__result && bindingName == "notlootingorvehiclestorage")
                {
                    // 3.0: XUi.vehicle -> XUi.Vehicle (XUiM_Vehicle), whose current vehicle is
                    // CurrentVehicle.GetVehicle(); XUi.lootContainer -> XUi.LootContainer.
                    bool flag1 = __instance.xui.Vehicle != null && __instance.xui.Vehicle.CurrentVehicle != null && __instance.xui.Vehicle.CurrentVehicle.GetVehicle().HasStorage();
                    // 3.0: loot containers are position-backed (no EntityId), and drone storage no
                    // longer opens through XUi.LootContainer, so an open LootContainer just means
                    // "looting a container" and the old drone-via-loot check (flag3) is obsolete.
                    bool flag2 = __instance.xui.LootContainer != null;
                    bool flag3 = false;
                    value = (!flag1 && !flag2 && !flag3).ToString();
                    __result = true;
                }
            }
            catch (Exception e)
            {
                DrokkStorage.Dbgl(e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), "UpdateTick")]
    public static class GameManager_UpdateTick_Patch
    {
        public static void Postfix()
        {
            try
            {
                if (GameManager.Instance.World == null || GameManager.Instance.World.GetPrimaryPlayer() == null)
                    return;

                if (UICamera.GetKeyDown(DrokkStorage.config.quickStackHotkeys[DrokkStorage.config.quickStackHotkeys.Length - 1]))
                {
                    for (int i = 0; i < DrokkStorage.config.quickStackHotkeys.Length - 1; i++)
                    {
                        if (!UICamera.GetKey(DrokkStorage.config.quickStackHotkeys[i]))
                            return;
                    }

                    QuickStackOnClick();
                    Audio.Manager.PlayButtonClick();
                }
                else if (UICamera.GetKeyDown(DrokkStorage.config.quickRestockHotkeys[DrokkStorage.config.quickRestockHotkeys.Length - 1]))
                {
                    for (int i = 0; i < DrokkStorage.config.quickRestockHotkeys.Length - 1; i++)
                    {
                        if (!UICamera.GetKey(DrokkStorage.config.quickRestockHotkeys[i]))
                            return;
                    }

                    QuickRestockOnClick();
                    Audio.Manager.PlayButtonClick();
                }
            }
            catch (Exception e)
            {
                DrokkStorage.Dbgl(e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(XUiC_ItemStack), "updateBorderColor")]
    public static class XUiC_ItemStack_updateBorderColor_Patch
    {
        public static void Postfix(XUiC_ItemStack __instance)
        {
            try
            {
                if (__instance.UserLockedSlot && DrokkStorage.config.lockBorderColor.a > 0)
                    __instance.selectionBorderColor = DrokkStorage.config.lockBorderColor;
            }
            catch (Exception e)
            {
                DrokkStorage.Dbgl(e.Message);
            }
        }
    }

    // ===== QUEST ITEM FIX - ALLOW MOVING/DROPPING WHITE RIVER SUPPLIES =====

    // Allow quest items to be dropped (fixes stuck White River Supplies bug)
    [HarmonyPatch(typeof(ItemClassQuest), nameof(ItemClassQuest.CanDrop))]
    public static class ItemClassQuest_CanDrop_Patch
    {
        public static bool Prefix(ItemClassQuest __instance, ref bool __result)
        {
            // Allow all quest items to be dropped
            __result = true;
            if (DrokkStorage.config.isDebug)
                Log.Out($"[DrokkStorage] ItemClassQuest.CanDrop called for {__instance.GetItemName()}, returning true (overridden)");
            return false; // Skip original method
        }
    }

    // Allow quest items to be placed in containers (fixes stuck White River Supplies bug)
    [HarmonyPatch(typeof(ItemClassQuest), nameof(ItemClassQuest.CanPlaceInContainer))]
    public static class ItemClassQuest_CanPlaceInContainer_Patch
    {
        public static bool Prefix(ItemClassQuest __instance, ref bool __result)
        {
            // Allow all quest items to be placed in containers
            __result = true;
            if (DrokkStorage.config.isDebug)
                Log.Out($"[DrokkStorage] ItemClassQuest.CanPlaceInContainer called for {__instance.GetItemName()}, returning true (overridden)");
            return false; // Skip original method
        }
    }

    // Prevent QuestLock from locking items in the inventory
    [HarmonyPatch(typeof(XUiC_ItemStack), "QuestLock", MethodType.Setter)]
    public static class XUiC_ItemStack_QuestLock_Patch
    {
        public static bool Prefix(XUiC_ItemStack __instance, ref bool value)
        {
            if (value)
            {
                if (DrokkStorage.config.isDebug && __instance.ItemStack != null && !__instance.ItemStack.IsEmpty())
                {
                    Log.Out($"[DrokkStorage] XUiC_ItemStack.QuestLock setter called with true for {__instance.ItemStack.itemValue.ItemClass.GetItemName()}. FORCING FALSE.");
                }
                value = false; // Force it to false
            }
            return true; // Continue to original setter which will now set it to false
        }
    }

    // Debugging for ItemStack behavior
    [HarmonyPatch(typeof(XUiC_ItemStack), nameof(XUiC_ItemStack.Update))]
    public static class XUiC_ItemStack_Update_Patch
    {
        private static float lastLogTime = 0f;
        public static void Postfix(XUiC_ItemStack __instance)
        {
            if (!DrokkStorage.config.isDebug) return;
            
            // Access private isOver field via Traverse
            bool isOver = Traverse.Create(__instance).Field("isOver").GetValue<bool>();
            
            // Only log if mouse is over and it's a quest item, and only every 2 seconds
            if (isOver && !__instance.ItemStack.IsEmpty() && __instance.ItemStack.itemValue.ItemClass.IsQuestItem)
            {
                if (Time.time - lastLogTime > 2.0f)
                {
                    lastLogTime = Time.time;
                    Log.Out($"[DrokkStorage] Debug Stack: Item={__instance.ItemStack.itemValue.ItemClass.GetItemName()}, StackLock={__instance.StackLock}, QuestLock={__instance.QuestLock}, AllowDropping={__instance.AllowDropping}, Location={__instance.StackLocation}");
                }
            }
        }
    }
}
