using System;
using LiveDrop.Services;

internal static class TransferNotificationSmoke
{
    private static void Main()
    {
        TestResources.Initialize();
        var legacy = TransferNotification.BuildXml("Sending...", "photo <1> & two.jpg", "transfer", false);
        if (legacy.GetElementsByTagName("progress").Count != 0 || legacy.GetElementsByTagName("text")[1].InnerText != "photo <1> & two.jpg")
            throw new Exception("The legacy text fallback or XML escaping failed.");
        var modern = TransferNotification.BuildXml("Sending...", "photo.jpg", "transfer", true, "Sending to Phone <1> & two");
        if (modern.GetElementsByTagName("progress").Count != 1 || modern.GetElementsByTagName("progress")[0].Attributes.GetNamedItem("value").NodeValue.ToString() != "{progressValue}")
            throw new Exception("The native progress binding failed.");
        if (modern.GetElementsByTagName("progress")[0].Attributes.GetNamedItem("status").NodeValue.ToString() != "Sending" || modern.GetElementsByTagName("text").Count != 1 || modern.GetElementsByTagName("text")[0].InnerText != "Sending to Phone <1> & two")
            throw new Exception("Native sending must have one escaped device heading plus filename/bar/status/percentage.");
        var receiving = TransferNotification.BuildXml("Receiving...", "photo.jpg", "transfer", true, "Receiving from Windows phone");
        if (receiving.GetElementsByTagName("text")[0].InnerText != "Receiving from Windows phone" || receiving.GetElementsByTagName("progress")[0].Attributes.GetNamedItem("status").NodeValue.ToString() != "Receiving")
            throw new Exception("Receiving must use the sender's name and Receiving status.");
        var complete = TransferNotification.BuildXml("Share complete", "Saved file", "open-saved:0123456789abcdef0123456789abcdef", false);
        if (complete.GetElementsByTagName("progress").Count != 0 || !complete.DocumentElement.GetAttribute("launch").StartsWith("open-saved:"))
            throw new Exception("Completion must remove progress and preserve file activation.");
        if (TransferNotification.IsTransferActivation("file:///C:/arbitrary.txt") || !TransferNotification.IsTransferActivation("transfer"))
            throw new Exception("Unexpected activation arguments were accepted.");
        Console.WriteLine("PASS: legacy fallback, native progress binding, escaped filenames and completion activation XML");
    }
}
