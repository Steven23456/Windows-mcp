namespace WindowsMcp.Services;

/// <summary>
/// The one line of WinRT the notification service needs, behind a seam so the service is unit
/// tested without the shell: <c>ToastNotificationManager.CreateToastNotifier(appId).Show(xml)</c>.
/// </summary>
internal interface IToastSink
{
    void Show(string appId, string toastXml);
}
