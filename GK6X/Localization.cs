using MiniJSON;
using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GK6X
{
    public static class Localization
    {
        public static Dictionary<string, Dictionary<string, string>> Values;
        public static string CurrentLocale = "en";

        public static bool Load()
        {
            Values = new Dictionary<string, Dictionary<string, string>>();

            string dir = Path.Combine(Program.DataBasePath, "i18n", "langs");
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                string locale = Path.GetFileNameWithoutExtension(file);

                Dictionary<string, string> localeValues = new Dictionary<string, string>();
                Values[locale] = localeValues;

                string text = File.ReadAllText(file);
                Dictionary<string, object> groups = Json.Deserialize(text) as Dictionary<string, object>;
                if (groups != null)
                {
                    foreach (KeyValuePair<string, object> group in groups)
                    {
                        string groupName = group.Key;
                        Dictionary<string, object> groupValues = group.Value as Dictionary<string, object>;
                        if (groupValues != null)
                        {
                            foreach (KeyValuePair<string, object> value in groupValues)
                            {
                                if (localeValues.ContainsKey(value.Key))
                                {
                                    Console.WriteLine("[WARNING] Duplicate locale key " + value.Key);
                                }
                                localeValues[value.Key] = value.Value.ToString();
                            }
                        }
                    }
                }

                // The shipped language files are JavaScript fragments, not strict json (e.g. "macro": (e = {...})), so the
                // json parser may give up half way. Fill in whatever it missed from the plain "key": "value" lines.
                Regex line = new Regex("^\\s*\"([^\"]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*,?\\s*$");
                foreach (string raw in text.Split('\n'))
                {
                    Match m = line.Match(raw.TrimEnd('\r'));
                    if (m.Success && !localeValues.ContainsKey(m.Groups[1].Value))
                    {
                        localeValues[m.Groups[1].Value] = Regex.Unescape(m.Groups[2].Value);
                    }
                }
            }

            return true;
        }

        public static string GetValue(string key)
        {
            return GetValue(key, CurrentLocale);
        }

        public static string GetValue(string key, string locale)
        {
            string result;
            TryGetValue(key, out result, locale);
            return result;
        }

        public static bool TryGetValue(string key, out string value)
        {
            return TryGetValue(key, out value, CurrentLocale);
        }

        public static bool TryGetValue(string key, out string value, string locale)
        {
            Dictionary<string, string> localeValues;
            if (Values.TryGetValue(locale, out localeValues))
            {
                return localeValues.TryGetValue(key, out value);
            }
            value = null;
            return false;
        }
    }

    public class LocalizedString
    {
        public string KeyName;

        public string Value
        {
            get { return GetValue(); }
        }

        public LocalizedString(string keyName)
        {
            KeyName = keyName;
        }

        public string GetValue()
        {
            return Localization.GetValue(KeyName);
        }

        public string GetValue(string locale)
        {
            return Localization.GetValue(KeyName, locale);
        }

        public bool TryGetValue(out string value)
        {
            return Localization.TryGetValue(KeyName, out value);
        }

        public bool TryGetValue(out string value, string locale)
        {
            return Localization.TryGetValue(KeyName, out value, locale);
        }
    }
}
