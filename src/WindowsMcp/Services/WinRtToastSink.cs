using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace WindowsMcp.Services;

/// <summary>
/// C-4: the production <see cref="IToastSink"/> — the WinRT toast API through the
/// <c>net10.0-windows10.0.19041.0</c> projection, the route B-8's app catalog already takes.
/// An id the platform does not know surfaces as a <c>COMException</c> with
/// <c>0x80070490</c>; <see cref="NotificationService"/> owns that case.
/// </summary>
internal sealed class WinRtToastSink : IToastSink
{
    internal static WinRtToastSink Instance { get; } = new();

    public void Show(string appId, string toastXml)
    {
        var xml = new XmlDocument();
        xml.LoadXml(toastXml);
        ToastNotificationManager.CreateToastNotifier(appId).Show(new ToastNotification(xml));
    }
}
