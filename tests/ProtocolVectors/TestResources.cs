using System;
using System.Collections.Generic;
using System.Xml;

internal static class TestResources
{
    internal static void Initialize()
    {
        // Run fixtures from the repository root. Production uses the UWP PRI loader.
        var document = new XmlDocument();
        document.Load("src/LiveDrop/Strings/en-US/Resources.resw");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XmlElement item in document.SelectNodes("/root/data"))
        {
            values.Add(item.GetAttribute("name"), item["value"].InnerText);
            // Exercise actual .NET composite-format parsing, including P0 percentages.
            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                item["value"].InnerText, 1, 2, 3, 4, 5, 6);
        }
        LiveDrop.AppText.Lookup = key => values[key];
    }
}
