using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace LiveDrop.Services
{
    internal static class TransferNotification
    {
        internal static void Show(string title, string body)
        {
            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var text = xml.GetElementsByTagName("text");
                text[0].AppendChild(xml.CreateTextNode(title ?? "LiveDrop"));
                text[1].AppendChild(xml.CreateTextNode(body ?? string.Empty));
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
            }
            catch
            {
                // A disabled notification channel must never interrupt a transfer.
            }
        }
    }
}
