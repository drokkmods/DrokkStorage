using System.Collections;
using UnityEngine.Scripting;

// Native XUi controller for the "mod update available" popup (windowDrokkUpdateNotice),
// opened over the main menu by DrokkModUpdateChecker. A custom window rather than the shared
// messageBox because we need a left-aligned "check for updates" toggle alongside the Ok
// button, and the shared messageBox centers its button row via a fixed <table> layout with
// no room for that.
[Preserve]
public class XUiC_DrokkUpdateNotice : XUiController
{
    public const string GroupID = "DrokkUpdateNotice";

    // Stashed by Show() just before opening the window; OnOpen() reads it once the
    // controller (and its child views) are actually ready to receive text.
    private static string pendingMessage = "";

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
        if (_message != null) _message.Text = pendingMessage;
        if (_cbxCheckUpdates != null) _cbxCheckUpdates.Value = DrokkModUpdateChecker.UpdatesEnabled;

        // The scrollview computes whether it needs to scroll from the label's actual laid-out
        // size, but that only exists once the label's own resize-to-fit-text pass has run a
        // frame after we set its text here, so re-run the scrollview's bounds check a couple of
        // frames later once the label has actually grown.
        if (_messageScroll != null) ThreadManager.StartCoroutine(RefreshScrollBounds());
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
        pendingMessage = _message;
        // Non-modal: floats below the vanilla news boxes without blocking or hiding the main
        // menu underneath it (modal=true was what previously required reopening XUiC_MainMenu
        // on close, since it forced the main menu group closed).
        _xuiInstance.playerUI.windowManager.Open(GroupID, false);
    }
}
