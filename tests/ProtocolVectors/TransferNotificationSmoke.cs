using System;
using LiveDrop.Services;

internal static class TransferNotificationSmoke
{
    private static void Main()
    {
        var legacy = TransferNotification.BuildXml("Sending...", "photo <1> & two.jpg", "transfer", false);
        if (legacy.GetElementsByTagName("progress").Count != 0 || legacy.GetElementsByTagName("text")[1].InnerText != "photo <1> & two.jpg")
            throw new Exception("The legacy text fallback or XML escaping failed.");
        var modern = TransferNotification.BuildXml("Sending...", "photo.jpg", "transfer", true);
        if (modern.GetElementsByTagName("progress").Count != 1 || modern.GetElementsByTagName("progress")[0].Attributes.GetNamedItem("value").NodeValue.ToString() != "{progressValue}")
            throw new Exception("The native progress binding failed.");
        if (modern.GetElementsByTagName("progress")[0].Attributes.GetNamedItem("status").NodeValue.ToString() != "Sending" || modern.GetElementsByTagName("text").Count != 0)
            throw new Exception("Native progress must show only the filename, bar, status and percentage without duplicate headings.");
        var complete = TransferNotification.BuildXml("Share complete", "Saved file", "open-saved:0123456789abcdef0123456789abcdef", false);
        if (complete.GetElementsByTagName("progress").Count != 0 || !complete.DocumentElement.GetAttribute("launch").StartsWith("open-saved:"))
            throw new Exception("Completion must remove progress and preserve file activation.");
        if (TransferNotification.IsTransferActivation("file:///C:/arbitrary.txt") || !TransferNotification.IsTransferActivation("transfer"))
            throw new Exception("Unexpected activation arguments were accepted.");
        Console.WriteLine("PASS: legacy fallback, native progress binding, escaped filenames and completion activation XML");
    }
}
