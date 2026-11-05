using System;
using System.Globalization;

namespace LiveDrop
{
    // Platform-neutral boundary: the UWP host supplies its resource lookup once.
    // A future WinRT host can supply the same .resw resources without changing codecs.
    internal static class AppText
    {
        internal static Func<string, string> Lookup { private get; set; }

        internal static string Get(string key)
        {
            var lookup = Lookup;
            if (lookup == null) throw new InvalidOperationException("Resource provider not initialized: " + key);
            var value = lookup(key);
            if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("Missing string resource: " + key);
            return value;
        }

        internal static string Format(string key, params object[] arguments)
        {
            return string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
        }
    }
}
