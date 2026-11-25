using MiniJSON;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GK6X
{
    /// <summary>
    /// The key mapping / macro / lighting profile of one keyboard as the control page edits it.
    /// It is stored in the original GK6X text format (UserData/MODELID.txt), so the files stay compatible with the
    /// command line tool and with hand editing. (Comments are not preserved when the page saves the file; a .bak copy is kept.)
    /// </summary>
    public class ProfileModel
    {
        public static readonly string[] LayerNames =
        {
            "Base", "Layer1", "Layer2", "Layer3", "FnBase", "FnLayer1", "FnLayer2", "FnLayer3"
        };

        public class MacroAction
        {
            public string State = "Press";// Press / Down / Up
            public string Keys = string.Empty;// A+B+C
            public int? Delay;
        }

        public class MacroDef
        {
            public string Name = string.Empty;
            public string Category = string.Empty;
            public int Delay;
            public string Repeat = "RepeatXTimes";
            public int Count = 1;
            public bool TrailingDelay;
            public List<MacroAction> Actions = new List<MacroAction>();
        }

        /// <summary>layer name (Base, Layer1, FnLayer1...) -> key id -> destination expression ("LCtrl+A", "Macro(x)")</summary>
        public Dictionary<string, Dictionary<string, string>> Layers = new Dictionary<string, Dictionary<string, string>>();
        public List<MacroDef> Macros = new List<MacroDef>();
        /// <summary>layer name (Base, Layer1...) -> lighting effect names</summary>
        public Dictionary<string, List<string>> Lighting = new Dictionary<string, List<string>>();
        public List<string> NoLightingLayers = new List<string>();
        public bool NoLighting;
        public Dictionary<string, string> Aliases = new Dictionary<string, string>();

        private static string CanonicalLayer(string name)
        {
            foreach (string layer in LayerNames)
            {
                if (string.Equals(layer, name, StringComparison.OrdinalIgnoreCase))
                {
                    return layer;
                }
            }
            return null;
        }

        // ------------------------------------------------------------ text

        public static ProfileModel Parse(string text)
        {
            ProfileModel model = new ProfileModel();
            string group = null;// "layer", "macro", "lighting", "alias"
            List<string> currentLayers = new List<string>();
            MacroDef currentMacro = null;
            string pendingCategory = null;

            foreach (string raw in (text ?? string.Empty).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                if (line.StartsWith("#"))
                {
                    string c = line.TrimStart('#').Trim();
                    if (c.StartsWith("gk6x:category=", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingCategory = c.Substring("gk6x:category=".Length).Trim();
                    }
                    continue;
                }
                if (line.StartsWith("["))
                {
                    group = null;
                    currentLayers.Clear();
                    currentMacro = null;
                    int end = line.IndexOf(']');
                    if (end < 1)
                    {
                        continue;
                    }
                    string full = line.Substring(1, end - 1).Trim();
                    int open = full.IndexOf('(');
                    int close = full.LastIndexOf(')');
                    if (open > 0 && close > open)
                    {
                        string kind = full.Substring(0, open).Trim().ToLowerInvariant();
                        string[] inner = full.Substring(open + 1, close - open - 1).Split(',').Select(x => x.Trim()).ToArray();
                        switch (kind)
                        {
                            case "macro":
                                if (inner.Length > 0 && inner[0].Length > 0)
                                {
                                    currentMacro = new MacroDef { Name = inner[0], Category = pendingCategory ?? string.Empty };
                                    int v;
                                    if (inner.Length > 1 && int.TryParse(inner[1], out v)) currentMacro.Delay = v;
                                    if (inner.Length > 2) currentMacro.Repeat = inner[2];
                                    if (inner.Length > 3 && int.TryParse(inner[3], out v)) currentMacro.Count = Math.Max(1, v);
                                    bool b;
                                    if (inner.Length > 4 && bool.TryParse(inner[4], out b)) currentMacro.TrailingDelay = b;
                                    model.Macros.RemoveAll(m => m.Name == currentMacro.Name);
                                    model.Macros.Add(currentMacro);
                                    group = "macro";
                                }
                                break;
                            case "lighting":
                                if (inner.Length > 0 && inner[0].Length > 0)
                                {
                                    for (int i = 1; i < inner.Length; i++)
                                    {
                                        string layer = CanonicalLayer(inner[i]);
                                        if (layer == null) continue;
                                        List<string> list;
                                        if (!model.Lighting.TryGetValue(layer, out list))
                                        {
                                            model.Lighting[layer] = list = new List<string>();
                                        }
                                        if (!list.Contains(inner[0])) list.Add(inner[0]);
                                    }
                                }
                                break;
                            case "nolighting":
                                foreach (string n in inner)
                                {
                                    string layer = CanonicalLayer(n);
                                    if (layer != null && !model.NoLightingLayers.Contains(layer)) model.NoLightingLayers.Add(layer);
                                }
                                break;
                        }
                    }
                    else if (full.Equals("NoLighting", StringComparison.OrdinalIgnoreCase))
                    {
                        model.NoLighting = true;
                    }
                    else if (full.Equals("KeyAlias", StringComparison.OrdinalIgnoreCase))
                    {
                        group = "alias";
                    }
                    else
                    {
                        foreach (string n in full.Split(','))
                        {
                            string layer = CanonicalLayer(n.Trim());
                            if (layer == null) continue;
                            currentLayers.Add(layer);
                            if (!model.Layers.ContainsKey(layer)) model.Layers[layer] = new Dictionary<string, string>();
                        }
                        if (currentLayers.Count > 0) group = "layer";
                    }
                    pendingCategory = null;
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }
                string left = line.Substring(0, colon).Trim();
                string right = line.Substring(colon + 1).Trim();
                switch (group)
                {
                    case "layer":
                        foreach (string layer in currentLayers)
                        {
                            model.Layers[layer][left] = right;
                        }
                        break;
                    case "alias":
                        model.Aliases[left] = right;
                        break;
                    case "macro":
                        {
                            string[] parts = line.Split(':');
                            string state = parts[0].Trim();
                            if (parts.Length > 1 && (state.Equals("Press", StringComparison.OrdinalIgnoreCase) ||
                                state.Equals("Down", StringComparison.OrdinalIgnoreCase) || state.Equals("Up", StringComparison.OrdinalIgnoreCase)))
                            {
                                MacroAction action = new MacroAction();
                                action.State = char.ToUpperInvariant(state[0]) + state.Substring(1).ToLowerInvariant();
                                action.Keys = parts[1].Trim();
                                int d;
                                if (parts.Length > 2 && int.TryParse(parts[2], out d)) action.Delay = d;
                                currentMacro.Actions.Add(action);
                            }
                        }
                        break;
                }
            }
            return model;
        }

        public string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# GK6X profile - written by the control page (you can also edit it by hand, a .bak copy is kept on save).");
            sb.AppendLine("# Format: see Sample.txt");
            sb.AppendLine();
            if (Aliases.Count > 0)
            {
                sb.AppendLine("[KeyAlias]");
                foreach (KeyValuePair<string, string> a in Aliases) sb.AppendLine(a.Key + ":" + a.Value);
                sb.AppendLine();
            }
            foreach (MacroDef m in Macros)
            {
                if (!string.IsNullOrEmpty(m.Category)) sb.AppendLine("# gk6x:category=" + m.Category);
                sb.Append("[Macro(" + m.Name + "," + m.Delay + "," + m.Repeat + "," + m.Count);
                if (m.TrailingDelay) sb.Append(",true");
                sb.AppendLine(")]");
                foreach (MacroAction a in m.Actions)
                {
                    sb.AppendLine(a.State + ":" + a.Keys + (a.Delay.HasValue ? ":" + a.Delay.Value : string.Empty));
                }
                sb.AppendLine();
            }
            foreach (string layer in LayerNames)
            {
                Dictionary<string, string> keys;
                if (Layers.TryGetValue(layer, out keys) && keys.Count > 0)
                {
                    sb.AppendLine("[" + layer + "]");
                    foreach (KeyValuePair<string, string> kv in keys) sb.AppendLine(kv.Key + ":" + kv.Value);
                    sb.AppendLine();
                }
            }
            foreach (string layer in LayerNames)
            {
                List<string> list;
                if (Lighting.TryGetValue(layer, out list))
                {
                    foreach (string name in list) sb.AppendLine("[Lighting(" + name + "," + layer + ")]");
                }
            }
            if (NoLighting) sb.AppendLine("[NoLighting]");
            if (NoLightingLayers.Count > 0) sb.AppendLine("[NoLighting(" + string.Join(",", NoLightingLayers) + ")]");
            return sb.ToString();
        }

        // ------------------------------------------------------------ json

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            Dictionary<string, object> layers = new Dictionary<string, object>();
            foreach (string layer in LayerNames)
            {
                Dictionary<string, string> keys;
                Dictionary<string, object> o = new Dictionary<string, object>();
                if (Layers.TryGetValue(layer, out keys)) foreach (KeyValuePair<string, string> kv in keys) o[kv.Key] = kv.Value;
                layers[layer] = o;
            }
            d["layers"] = layers;
            d["macros"] = Macros.Select(m =>
            {
                Dictionary<string, object> mo = new Dictionary<string, object>();
                mo["name"] = m.Name;
                mo["category"] = m.Category;
                mo["delay"] = m.Delay;
                mo["repeat"] = m.Repeat;
                mo["count"] = m.Count;
                mo["trailingDelay"] = m.TrailingDelay;
                mo["actions"] = m.Actions.Select(a =>
                {
                    Dictionary<string, object> ao = new Dictionary<string, object>();
                    ao["state"] = a.State;
                    ao["keys"] = a.Keys;
                    ao["delay"] = a.Delay.HasValue ? (object)a.Delay.Value : null;
                    return (object)ao;
                }).ToList();
                return (object)mo;
            }).ToList();
            Dictionary<string, object> lighting = new Dictionary<string, object>();
            foreach (string layer in LayerNames)
            {
                List<string> list;
                lighting[layer] = Lighting.TryGetValue(layer, out list) ? list.Cast<object>().ToList() : new List<object>();
            }
            d["lighting"] = lighting;
            d["noLighting"] = NoLighting;
            d["noLightingLayers"] = NoLightingLayers.Cast<object>().ToList();
            d["aliases"] = Aliases.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            return d;
        }

        public static ProfileModel FromJson(Dictionary<string, object> d)
        {
            ProfileModel model = new ProfileModel();
            Dictionary<string, object> layers;
            if (Json.TryGetValue(d, "layers", out layers))
            {
                foreach (KeyValuePair<string, object> kv in layers)
                {
                    string layer = CanonicalLayer(kv.Key);
                    Dictionary<string, object> keys = kv.Value as Dictionary<string, object>;
                    if (layer == null || keys == null) continue;
                    Dictionary<string, string> map = new Dictionary<string, string>();
                    foreach (KeyValuePair<string, object> k in keys)
                    {
                        string v = k.Value as string;
                        if (!string.IsNullOrWhiteSpace(v)) map[k.Key.Trim()] = v.Trim();
                    }
                    if (map.Count > 0) model.Layers[layer] = map;
                }
            }
            List<object> macros;
            if (Json.TryGetValue(d, "macros", out macros))
            {
                foreach (object mo in macros)
                {
                    Dictionary<string, object> m = mo as Dictionary<string, object>;
                    if (m == null) continue;
                    MacroDef def = new MacroDef();
                    Json.TryGetValue(m, "name", out def.Name);
                    Json.TryGetValue(m, "category", out def.Category);
                    double num;
                    if (LightingSettingsFile.TryGetNumber(m, "delay", out num)) def.Delay = (int)num;
                    string repeat;
                    if (Json.TryGetValue(m, "repeat", out repeat)) def.Repeat = repeat;
                    if (LightingSettingsFile.TryGetNumber(m, "count", out num)) def.Count = Math.Max(1, (int)num);
                    Json.TryGetValue(m, "trailingDelay", out def.TrailingDelay);
                    List<object> actions;
                    if (Json.TryGetValue(m, "actions", out actions))
                    {
                        foreach (object ao in actions)
                        {
                            Dictionary<string, object> a = ao as Dictionary<string, object>;
                            if (a == null) continue;
                            MacroAction action = new MacroAction();
                            string s;
                            if (Json.TryGetValue(a, "state", out s)) action.State = s;
                            if (Json.TryGetValue(a, "keys", out s)) action.Keys = s;
                            if (LightingSettingsFile.TryGetNumber(a, "delay", out num)) action.Delay = (int)num;
                            def.Actions.Add(action);
                        }
                    }
                    model.Macros.Add(def);
                }
            }
            Dictionary<string, object> lighting;
            if (Json.TryGetValue(d, "lighting", out lighting))
            {
                foreach (KeyValuePair<string, object> kv in lighting)
                {
                    string layer = CanonicalLayer(kv.Key);
                    List<object> list = kv.Value as List<object>;
                    if (layer == null || list == null || list.Count == 0) continue;
                    model.Lighting[layer] = list.OfType<string>().ToList();
                }
            }
            Json.TryGetValue(d, "noLighting", out model.NoLighting);
            List<object> nl;
            if (Json.TryGetValue(d, "noLightingLayers", out nl))
            {
                model.NoLightingLayers = nl.OfType<string>().Select(CanonicalLayer).Where(x => x != null).ToList();
            }
            Dictionary<string, object> aliases;
            if (Json.TryGetValue(d, "aliases", out aliases))
            {
                foreach (KeyValuePair<string, object> kv in aliases)
                {
                    if (kv.Value is string) model.Aliases[kv.Key] = (string)kv.Value;
                }
            }
            return model;
        }

        // ------------------------------------------------------------ validation

        private static bool ValidToken(string token, ProfileModel model)
        {
            token = token.Trim();
            if (token.Length == 0) return false;
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                return uint.TryParse(token.Substring(2), NumberStyles.HexNumber, null, out v);
            }
            if (token.StartsWith("macro(", StringComparison.OrdinalIgnoreCase) && token.EndsWith(")"))
            {
                string name = token.Substring(6, token.Length - 7);
                return model.Macros.Any(m => m.Name == name);
            }
            return TryParseName(token, model.Aliases);
        }

        public static bool TryParseName(string str, Dictionary<string, string> aliases, out DriverValue value)
        {
            string real;
            if (aliases != null && aliases.TryGetValue(str, out real)) str = real;
            if (Enum.TryParse(str, true, out value) && Enum.IsDefined(typeof(DriverValue), value))
            {
                return true;
            }
            int us = str.IndexOf('_');
            int dup;
            if (us > 0 && int.TryParse(str.Substring(us + 1).Trim(), out dup) && dup > 1 &&
                Enum.TryParse(str.Substring(0, us), true, out value) && Enum.IsDefined(typeof(DriverValue), value))
            {
                return true;
            }
            return false;
        }

        private static bool TryParseName(string str, Dictionary<string, string> aliases)
        {
            DriverValue v;
            return TryParseName(str, aliases, out v);
        }

        /// <summary>Returns a list of problems (empty = valid).</summary>
        public List<string> Validate(KeyboardState keyboard)
        {
            List<string> errors = new List<string>();
            foreach (KeyValuePair<string, Dictionary<string, string>> layer in Layers)
            {
                foreach (KeyValuePair<string, string> kv in layer.Value)
                {
                    if (kv.Key.Contains(":") || kv.Key.Contains("\n") || kv.Value.Contains("\n"))
                    {
                        errors.Add(layer.Key + ": bad characters in '" + kv.Key + "'");
                        continue;
                    }
                    if (keyboard != null && !keyboard.KeysByDriverValueName.Keys.Any(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase)) &&
                        !Aliases.ContainsKey(kv.Key))
                    {
                        errors.Add(layer.Key + ": this keyboard has no key '" + kv.Key + "'");
                    }
                    foreach (string token in kv.Value.Split('+'))
                    {
                        if (!ValidToken(token, this))
                        {
                            errors.Add(layer.Key + ": '" + kv.Key + "' -> unknown value '" + token + "'");
                        }
                    }
                }
            }
            HashSet<string> names = new HashSet<string>();
            foreach (MacroDef m in Macros)
            {
                if (string.IsNullOrWhiteSpace(m.Name) || m.Name.IndexOfAny(new[] { '(', ')', ',', '[', ']', '\n', ':' }) >= 0)
                {
                    errors.Add("Macro name '" + m.Name + "' is empty or has one of ( ) , [ ] :");
                    continue;
                }
                if (!names.Add(m.Name)) errors.Add("Duplicate macro '" + m.Name + "'");
                MacroRepeatType rt;
                if (!Enum.TryParse(m.Repeat, true, out rt)) errors.Add("Macro '" + m.Name + "': unknown repeat mode '" + m.Repeat + "'");
                if (m.Actions.Count == 0) errors.Add("Macro '" + m.Name + "' has no actions");
                if (m.Actions.Count * 2 > 255) errors.Add("Macro '" + m.Name + "' has too many actions (max 127)");
                if (m.Category != null && (m.Category.Contains("\n") || m.Category.Contains("\r"))) errors.Add("Bad category for macro '" + m.Name + "'");
                foreach (MacroAction a in m.Actions)
                {
                    if (a.State != "Press" && a.State != "Down" && a.State != "Up") errors.Add("Macro '" + m.Name + "': bad action '" + a.State + "'");
                    if (string.IsNullOrWhiteSpace(a.Keys) || a.Keys.Split('+').Any(t => !TryParseName(t.Trim(), Aliases)))
                    {
                        errors.Add("Macro '" + m.Name + "': unknown key in '" + a.Keys + "'");
                    }
                    if (a.Delay.HasValue && (a.Delay < 0 || a.Delay > 65535)) errors.Add("Macro '" + m.Name + "': delay out of range");
                }
            }
            foreach (KeyValuePair<string, List<string>> kv in Lighting)
            {
                if (kv.Value.Count > UserDataFile.LightingEffect.MaxEffects) errors.Add(kv.Key + ": at most " + UserDataFile.LightingEffect.MaxEffects + " lighting effects");
                foreach (string name in kv.Value)
                {
                    if (Program.FindLightingFile(name) == null) errors.Add(kv.Key + ": lighting effect '" + name + "' not found");
                    if (name.IndexOfAny(new[] { '(', ')', ',', '[', ']' }) >= 0) errors.Add("Lighting effect name '" + name + "' has one of ( ) , [ ]");
                }
            }
            return errors;
        }
    }

    /// <summary>Everything the control page needs besides the live lighting: palettes, profile files, lighting files, apply.</summary>
    public static class ConfigService
    {
        public static string ProfilePath(KeyboardState keyboard)
        {
            return Path.Combine(Program.UserDataPath, keyboard.ModelId + ".txt");
        }

        public static ProfileModel LoadProfile(KeyboardState keyboard)
        {
            string path = ProfilePath(keyboard);
            return ProfileModel.Parse(File.Exists(path) ? File.ReadAllText(path) : string.Empty);
        }

        public static void SaveProfile(KeyboardState keyboard, ProfileModel model)
        {
            string path = ProfilePath(keyboard);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", true);
            }
            File.WriteAllText(path, model.Serialize(), new UTF8Encoding(false));
        }

        // ------------------------------------------------------------ palette

        private static string TokenFor(uint value)
        {
            if (Enum.IsDefined(typeof(DriverValue), value))
            {
                return ((DriverValue)value).ToString();
            }
            return "0x" + value.ToString("X8");
        }

        private static string Localized(KeyValues.Key key)
        {
            string title = key.Title != null ? key.Title.GetValue() : null;
            if (!string.IsNullOrEmpty(title) && !title.StartsWith("key_"))
            {
                return title;
            }
            return key.Name;
        }

        private static Dictionary<string, object> Entry(string label, string token, string tip)
        {
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["label"] = label;
            e["token"] = token;
            if (tip != null) e["tip"] = tip;
            return e;
        }

        private static Dictionary<string, object> Group(string id, string title, List<object> keys)
        {
            Dictionary<string, object> g = new Dictionary<string, object>();
            g["id"] = id;
            g["title"] = title;
            g["keys"] = keys;
            return g;
        }

        public static List<object> BuildKeyset()
        {
            List<object> groups = new List<object>();
            Dictionary<string, string> titles = new Dictionary<string, string>
            {
                { "primary", "Primary" }, { "pad", "Number Pad" }, { "media", "Media" },
                { "system", "System/Net" }, { "mouse", "Mouse" }, { "disable", "Disable" }
            };
            foreach (KeyValues.Group g in KeyValues.Groups)
            {
                string id = g.KeyType;
                string title;
                if (!titles.TryGetValue(id ?? string.Empty, out title)) continue;
                List<object> keys = new List<object>();
                HashSet<string> seen = new HashSet<string>();
                foreach (KeyValues.Key key in g.Keys)
                {
                    if (key.DriverValue == 0 && id != "disable") continue;
                    string token = TokenFor(key.DriverValue);
                    // keys.json lists a few "virtual" entries without a real value
                    if (key.DriverValue == 0 && key.Name != "Disabled") continue;
                    if (!seen.Add(token)) continue;
                    keys.Add(Entry(Localized(key), token, null));
                }
                groups.Add(Group(id, title, keys));
            }
            groups.Add(Group("layers", "Layers", new List<object>
            {
                Entry("Hold: Standard", "TempSwitchLayerBase", "Temporarily switch to the standard layer while held"),
                Entry("Hold: Layer 1", "TempSwitchLayer1", "Temporarily switch to layer 1 while held"),
                Entry("Hold: Layer 2", "TempSwitchLayer2", "Temporarily switch to layer 2 while held"),
                Entry("Hold: Layer 3", "TempSwitchLayer3", "Temporarily switch to layer 3 while held"),
                Entry("Layer 1 button", "Layer1Button", "Same as the layer 1 combo (press again to return to standard)"),
                Entry("Layer 2 button", "Layer2Button", ""),
                Entry("Layer 3 button", "Layer3Button", ""),
                Entry("Driver button", "DriverLayerButton", "Switch to the driver layer (software layer)")
            }));
            groups.Add(Group("lighting", "Lighting", new List<object>
            {
                Entry("Next effect", "NextLightingEffect", "Standard layer only"),
                Entry("Next reactive effect", "NextReactiveLightingEffect", "Standard layer only"),
                Entry("Brightness +", "BrightnessUp", null),
                Entry("Brightness -", "BrightnessDown", null),
                Entry("Speed +", "LightingSpeedIncrease", null),
                Entry("Speed -", "LightingSpeedDecrease", null),
                Entry("Pause / resume", "LightingPauseResume", null),
                Entry("Lighting on/off", "ToggleLighting", null),
                Entry("Lock Win key", "ToggleLockWindowsKey", "Toggles the Windows key lock (game mode)")
            }));
            return groups;
        }

        /// <summary>HID usage (as sent by browsers / macros) -> key token, for recording macros in the page.</summary>
        public static Dictionary<string, object> BuildUsageNames()
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            foreach (KeyValuePair<byte, uint> kv in KeyValues.ShortToLongDriverValues)
            {
                if (kv.Key != 0 && Enum.IsDefined(typeof(DriverValue), kv.Value))
                {
                    map[kv.Key.ToString()] = ((DriverValue)kv.Value).ToString();
                }
            }
            string[] mods = { "LCtrl", "LShift", "LAlt", "LWin", "RCtrl", "RShift", "RAlt", "RWin" };
            for (int i = 0; i < mods.Length; i++)
            {
                map[(0xE0 + i).ToString()] = mods[i];
            }
            return map;
        }

        // ------------------------------------------------------------ lighting files

        public static List<object> ListLightingFiles()
        {
            List<object> list = new List<object>();
            HashSet<string> seen = new HashSet<string>();
            foreach (KeyValuePair<string, string> source in new[]
            {
                new KeyValuePair<string, string>("user", Program.UserLightingPath),
                new KeyValuePair<string, string>("bundled", Path.Combine(Program.DataBasePath, "lighting"))
            })
            {
                if (!Directory.Exists(source.Value)) continue;
                foreach (string file in Directory.GetFiles(source.Value, "*.le").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!seen.Add(name)) continue;
                    string type = "dynamic";
                    try
                    {
                        string head;
                        using (StreamReader r = new StreamReader(file)) { char[] buf = new char[200]; int n = r.Read(buf, 0, buf.Length); head = new string(buf, 0, n); }
                        if (head.Contains("\"Static\"") || head.Contains("\"Type\": 0") || head.Contains("\"Type\":0")) type = "static";
                    }
                    catch { }
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["name"] = name;
                    d["type"] = type;
                    d["source"] = source.Key;
                    list.Add(d);
                }
            }
            return list;
        }

        public static string SaveStaticLighting(KeyboardState keyboard, string name, Dictionary<int, uint> colors)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\', '(', ')', ',', '[', ']', ':' }) >= 0 || name.Contains("..") || name.Length > 60)
            {
                throw new FormatException("Invalid effect name (no / \\ ( ) , [ ] : characters)");
            }
            Directory.CreateDirectory(Program.UserLightingPath);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("\t\"Type\": \"Static\",");
            sb.AppendLine("\t\"Data\": {");
            List<string> lines = new List<string>();
            foreach (KeyValuePair<int, uint> kv in colors.OrderBy(k => k.Key))
            {
                KeyboardState.Key key;
                if (!keyboard.KeysByLocationCode.TryGetValue(kv.Key, out key)) continue;
                lines.Add("\t\t\"" + key.DriverValueName + "\": \"0x" + (kv.Value & 0xFFFFFF).ToString("x6") + "\"");
            }
            sb.AppendLine(string.Join(",\n", lines));
            sb.AppendLine("\t}");
            sb.AppendLine("}");
            string path = Path.Combine(Program.UserLightingPath, name + ".le");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        public static bool DeleteUserLighting(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains("..")) return false;
            string path = Path.Combine(Program.UserLightingPath, name + ".le");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        // ------------------------------------------------------------ apply to the keyboard

        /// <summary>
        /// Writes the saved profile to the keyboard's layers (the same steps as the 'map' command).
        /// Returns the number of packets the keyboard did not acknowledge (0 = everything was accepted).
        /// </summary>
        public static int Apply(KeyboardDevice device, IEnumerable<KeyboardLayer> layers, out string error)
        {
            error = null;
            string path = ProfilePath(device.State);
            UserDataFile userData = File.Exists(path) ? UserDataFile.Load(device.State, path) : new UserDataFile();
            if (userData == null)
            {
                error = "Couldn't read " + path;
                return -1;
            }
            int before = device.FailedPackets;
            foreach (KeyboardLayer layer in layers.Distinct())
            {
                if (layer == KeyboardLayer.Driver || layer == KeyboardLayer.Invalid || !device.State.Layers.ContainsKey(layer))
                {
                    continue;
                }
                device.SetLighting(layer, userData);
                device.SetMacros(layer, userData);
                for (int i = 0; i < 2; i++)
                {
                    bool fn = i == 1;
                    uint[] driverValues = new uint[device.State.MaxLogicCode];
                    for (int j = 0; j < driverValues.Length; j++)
                    {
                        driverValues[j] = KeyValues.UnusedKeyValue;
                    }
                    UserDataFile.Layer userLayer;
                    if (fn) userData.FnLayers.TryGetValue(layer, out userLayer); else userData.Layers.TryGetValue(layer, out userLayer);
                    if (userLayer != null)
                    {
                        uint all;
                        if (userLayer.Keys.TryGetValue("all", out all))
                        {
                            for (int j = 0; j < driverValues.Length; j++) driverValues[j] = all;
                        }
                        for (int j = 0; j < driverValues.Length; j++)
                        {
                            KeyboardState.Key key = device.State.GetKeyByLogicCode(j);
                            if (key != null)
                            {
                                uint v = userLayer.GetKey(key);
                                if (v != KeyValues.UnusedKeyValue) driverValues[j] = v;
                            }
                        }
                    }
                    device.SetKeys(layer, driverValues, fn);
                }
            }
            return device.FailedPackets - before;
        }
    }
}
