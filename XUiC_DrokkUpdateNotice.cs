using System.Collections;
using UnityEngine.Scripting;

// Native XUi controller for the "mod update available" popup (windowDrokkUpdateNotice),
// opened over the main menu by DrokkModUpdateChecker. A custom window rather than the shared
// messageBox because we need a left-aligned "check for updates" toggle alongside the Ok
// button, and the shared messageBox centers its button row via a fixed <table> layout with
// no room for that.
//
// NO MUTABLE STATIC FIELDS IN THIS FILE. Each mod compiles its own copy of this type, so a
// static here is per-assembly, and the assembly that OPENS the window is routinely not the
// assembly that SENDS the message — see the header of DrokkModUpdateChecker.cs, which holds
// the shared state and the full explanation. The message text used to live here as
// `private static string pendingMessage` and shipped a blank popup because of exactly that.
[Preserve]
public class XUiC_DrokkUpdateNotice : XUiController
{
    public const string GroupID = "DrokkUpdateNotice";

    private XUiV_Label _message;
    private XUiV_ScrollView _messageScroll;
    private XUiC_ToggleButton _cbxCheckUpdates;
    private XUiC_SimpleButton _btnOk;

    public override void Init()
    {
        base.Init();
        _message = GetChildById("message")?.ViewComponent as XUiV_Label;
        _messageScroll = GetChildById("messageScroll")?.ViewComponent as XUiV_ScrollView;
        _cbxCheckUpdates = GetChildById("cbxCheckUpdates") as XUiC_ToggleButton;
        _btnOk = GetChildById("btnOk") as XUiC_SimpleButton;

        if (_cbxCheckUpdates != null) _cbxCheckUpdates.OnValueChanged += OnCheckUpdatesToggled;
        if (_btnOk != null) _btnOk.OnPressed += (s, mb) => CloseWindow();
    }

    public override void OnOpen()
    {
        base.OnOpen();
        if (_message != null) _message.Text = ResolveMessage();
        if (_cbxCheckUpdates != null) _cbxCheckUpdates.Value = DrokkModUpdateChecker.UpdatesEnabled;

        // The scrollview computes whether it needs to scroll from the label's actual laid-out
        // size, but that only exists once the label's own resize-to-fit-text pass has run a
        // frame after we set its text here, so re-run the scrollview's bounds check a couple of
        // frames later once the label has actually grown.
        if (_messageScroll != null) ThreadManager.StartCoroutine(RefreshScrollBounds());
    }

    // Shared state first. The fallback covers a gen-2 sender (whose Show() only set its own
    // assembly's private static and never wrote the shared key) opening a gen-3 window; see
    // the gen-2/gen-3 bridge in DrokkModUpdateChecker.
    private static string ResolveMessage()
    {
        string message = DrokkModUpdateChecker.PendingMessage;
        if (!string.IsNullOrEmpty(message)) return message;

        message = DrokkModUpdateChecker.ReadLegacyPendingMessage();
        if (!string.IsNullOrEmpty(message))
        {
            Log.Out(" [DrokkModUpdateChecker] Message came from an older Drokk mod's copy of the checker (pre-gen-3).");
            return message;
        }

        // Reached only if the window was opened by something that never set a message at all.
        // Say so rather than presenting an empty panel the player can't interpret.
        Log.Warning(" [DrokkModUpdateChecker] Update notice opened with no pending message; showing placeholder text.");
        return "No update details were received. Check drokkmods.fyi for the latest versions.";
    }

    private IEnumerator RefreshScrollBounds()
    {
        yield return null;
        yield return null;
        _messageScroll.ResetPosition();
    }

    private void OnCheckUpdatesToggled(XUiC_ToggleButton _sender, bool _value)
    {
        DrokkModUpdateChecker.SetUpdatesEnabled(_value);
    }

    private void CloseWindow()
    {
        xui?.playerUI?.windowManager?.Close(GroupID);
    }

    public static void Show(XUi _xuiInstance, string _message)
    {
        DrokkModUpdateChecker.PendingMessage = _message;
        // Also poke the legacy per-assembly static on any older copy of this type, in case a
        // pre-gen-3 mod's controller is the one that won the window declaration.
        DrokkModUpdateChecker.BroadcastPendingMessage(_message);

        var windowManager = _xuiInstance?.playerUI?.windowManager;
        if (windowManager == null)
        {
            Log.Error(" [DrokkModUpdateChecker] No window manager available; cannot show the update notice.");
            Log.Out($" [DrokkModUpdateChecker] Update notice text was: {_message}");
            return;
        }

        // The window group is global to the merged XUi, so it only has to be declared ONCE
        // across all installed Drokk mods — any mod may send, and whichever mod supplied the
        // markup displays. If NO installed mod declared it, the engine's own response is a
        // "Window unknown!" warning plus a full stack-trace dump that never names the real
        // cause, so check first and say exactly what is missing. The update text goes to the
        // log either way: a notice the player can't see must still not vanish silently.
        if (!windowManager.TryGetWindow(GroupID, out _))
        {
            Log.Error($" [DrokkModUpdateChecker] No installed Drokk mod declares the '{GroupID}' window group, " +
                      "so the update notice cannot be displayed. Add the windowDrokkUpdateNotice markup to one " +
                      "mod's Config/XUi_Menu/windows.xml and xui.xml (DrokkStorage's is the reference copy).");
            Log.Out($" [DrokkModUpdateChecker] Update notice text was: {_message}");
            return;
        }

        // Non-modal: floats below the vanilla news boxes without blocking or hiding the main
        // menu underneath it (modal=true was what previously required reopening XUiC_MainMenu
        // on close, since it forced the main menu group closed).
        windowManager.Open(GroupID, false);
    }
}
