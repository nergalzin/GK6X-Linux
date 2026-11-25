using MiniJSON;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace GK6X
{
    /// <summary>
    /// Lighting settings which can be changed at any time while the engine is running.
    /// </summary>
    public class LightingSettings
    {
        public string Effect = "rainbow";
        /// <summary>Name of the .le lighting file played by the "le" effect</summary>
        public string Le = string.Empty;
        /// <summary>1 - 100</summary>
        public int Speed = 50;
        /// <summary>0 - 100</summary>
        public int Brightness = 100;
        /// <summary>Primary color (0xRRGGBB)</summary>
        public uint Color1 = 0xFF0000;
        /// <summary>Secondary color (0xRRGGBB)</summary>
        public uint Color2 = 0x0000FF;
        /// <summary>0 = left to right, 1 = right to left, 2 = top to bottom, 3 = bottom to top</summary>
        public int Direction = 0;
        /// <summary>Per key colors (LocationCode -> 0xRRGGBB) drawn on top of the effect</summary>
        public Dictionary<int, uint> Paint = new Dictionary<int, uint>();

        public LightingSettings Clone()
        {
            LightingSettings result = (LightingSettings)MemberwiseClone();
            result.Paint = new Dictionary<int, uint>(Paint);
            return result;
        }
    }

    /// <summary>
    /// Receives finished frames (R,G,B,A for each of the <see cref="KeyboardDevice.RealtimeLedCount"/> LEDs).
    /// </summary>
    public interface IFrameSink
    {
        bool Send(byte[] rgba);
    }

    public class DeviceFrameSink : IFrameSink
    {
        private readonly KeyboardDevice device;
        public DeviceFrameSink(KeyboardDevice device)
        {
            this.device = device;
        }
        public bool Send(byte[] rgba)
        {
            return !device.IsClosed && device.SendRealtimeFrame(rgba);
        }
    }

    /// <summary>A sink which discards frames (UI development / testing without a keyboard).</summary>
    public class NullFrameSink : IFrameSink
    {
        public bool Send(byte[] rgba)
        {
            return true;
        }
    }

    public static class Colors
    {
        public static uint Parse(string text)
        {
            if (text == null)
            {
                throw new FormatException("Empty color");
            }
            text = text.Trim().TrimStart('#');
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
            }
            if (text.Length == 3)
            {
                text = string.Concat(text.Select(c => new string(c, 2)));
            }
            uint value;
            if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            switch (text.ToLowerInvariant())
            {
                case "black": return 0x000000;
                case "white": return 0xFFFFFF;
                case "red": return 0xFF0000;
                case "green": return 0x00FF00;
                case "blue": return 0x0000FF;
                case "yellow": return 0xFFFF00;
                case "cyan": return 0x00FFFF;
                case "magenta": return 0xFF00FF;
                case "orange": return 0xFF8000;
                case "purple": return 0x8000FF;
                case "pink": return 0xFF4080;
            }
            throw new FormatException("Invalid color '" + text + "' (use RRGGBB)");
        }

        public static string ToHex(uint rgb)
        {
            return "#" + (rgb & 0xFFFFFF).ToString("x6");
        }

        public static void Unpack(uint rgb, out float r, out float g, out float b)
        {
            r = ((rgb >> 16) & 0xFF) / 255f;
            g = ((rgb >> 8) & 0xFF) / 255f;
            b = (rgb & 0xFF) / 255f;
        }

        public static void Hsv(float h, float s, float v, out float r, out float g, out float b)
        {
            h = h - (float)Math.Floor(h);
            float hh = h * 6f;
            int i = (int)Math.Floor(hh);
            float f = hh - i;
            float p = v * (1 - s);
            float q = v * (1 - s * f);
            float t = v * (1 - s * (1 - f));
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
        }
    }

    public class LightingEngine
    {
        public static readonly string[] EffectNames =
        {
            "static", "rainbow", "cycle", "breathing", "wave", "gradient", "reactive", "ripple", "le", "custom", "off"
        };

        private class Led
        {
            public int Location;
            public string Name;
            public string Id;
            public string Show;
            public int X;
            public int Y;
            public int W;
            public int H;
            public float NormX;
            public float NormY;
            public float CenterX;
            public float CenterY;
        }

        private class Press
        {
            public int Location;
            public double Time;
        }

        private readonly KeyboardState state;
        private readonly IFrameSink sink;
        private readonly List<Led> leds = new List<Led>();
        private readonly Dictionary<int, Led> ledsByLocation = new Dictionary<int, Led>();
        private readonly object locker = new object();
        private readonly List<Press> presses = new List<Press>();
        private LightingSettings settings = new LightingSettings();
        private Thread thread;
        private volatile bool stopRequested;
        private byte[] lastFrame;
        private byte[] currentFrame = new byte[KeyboardDevice.RealtimeLedCount * 4];
        private long lastSendTicks;
        private int consecutiveFailures;
        private int forceSend;

        public int Fps = 30;
        /// <summary>While true no frames are sent (used while the profile is being written to the keyboard).</summary>
        public volatile bool Paused;
        public bool IsRunning { get; private set; }
        public double MeasuredFps { get; private set; }
        public long FramesSent { get; private set; }
        public long FramesFailed { get; private set; }
        public event Action<LightingEngine> Failed;
        public event Action SettingsChanged;

        public KeyboardState State { get { return state; } }

        public LightingEngine(KeyboardState state, IFrameSink sink)
        {
            this.state = state;
            this.sink = sink;
            BuildLayout();
        }

        private void BuildLayout()
        {
            foreach (KeyboardState.Key key in state.KeysByLocationCode.Values)
            {
                if (key.LocationCode < 0 || key.LocationCode >= KeyboardDevice.RealtimeLedCount || key.Position.Left < 0)
                {
                    continue;
                }
                Led led = new Led();
                led.Location = key.LocationCode;
                led.Name = key.KeyName;
                led.Id = key.DriverValueName;
                led.Show = key.Show;
                led.X = key.Position.Left;
                led.Y = key.Position.Top;
                led.W = key.Position.Width;
                led.H = key.Position.Height;
                led.CenterX = key.Position.Left + key.Position.Width / 2f;
                led.CenterY = key.Position.Top + key.Position.Height / 2f;
                leds.Add(led);
                ledsByLocation[led.Location] = led;
            }
            if (leds.Count > 0)
            {
                float minX = leds.Min(l => l.CenterX), maxX = leds.Max(l => l.CenterX);
                float minY = leds.Min(l => l.CenterY), maxY = leds.Max(l => l.CenterY);
                foreach (Led led in leds)
                {
                    led.NormX = maxX > minX ? (led.CenterX - minX) / (maxX - minX) : 0;
                    led.NormY = maxY > minY ? (led.CenterY - minY) / (maxY - minY) : 0;
                }
            }
            leds.Sort((a, b) => a.Location.CompareTo(b.Location));
        }

        // ------------------------------------------------------------ settings

        public LightingSettings GetSettings()
        {
            lock (locker)
            {
                return settings.Clone();
            }
        }

        public void SetSettings(LightingSettings value)
        {
            lock (locker)
            {
                settings = value.Clone();
                Normalize(settings);
                forceSend = 1;
            }
            RaiseChanged();
        }

        public void Update(Action<LightingSettings> change)
        {
            lock (locker)
            {
                change(settings);
                Normalize(settings);
                forceSend = 1;
            }
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            Action handler = SettingsChanged;
            if (handler != null)
            {
                handler();
            }
        }

        private static void Normalize(LightingSettings s)
        {
            s.Speed = Math.Max(1, Math.Min(100, s.Speed));
            s.Brightness = Math.Max(0, Math.Min(100, s.Brightness));
            s.Direction = ((s.Direction % 4) + 4) % 4;
            s.Color1 &= 0xFFFFFF;
            s.Color2 &= 0xFFFFFF;
            if (!EffectNames.Contains(s.Effect))
            {
                s.Effect = "static";
            }
        }

        /// <summary>
        /// Finds LocationCodes by key name ("Escape", "Esc", "W", "LeftShift"...) or by numeric location code (#12).
        /// </summary>
        public List<int> FindLocations(string name)
        {
            List<int> result = new List<int>();
            if (string.IsNullOrEmpty(name))
            {
                return result;
            }
            int loc;
            if (name.StartsWith("#") && int.TryParse(name.Substring(1), out loc) && ledsByLocation.ContainsKey(loc))
            {
                result.Add(loc);
                return result;
            }
            string normalized = Normalize(name);
            foreach (Led led in leds)
            {
                if (Normalize(led.Name) == normalized)
                {
                    result.Add(led.Location);
                }
            }
            if (result.Count == 0)
            {
                foreach (Led led in leds)
                {
                    if (Normalize(led.Show) == normalized)
                    {
                        result.Add(led.Location);
                    }
                }
            }
            return result;
        }

        private static string Normalize(string name)
        {
            return (name ?? string.Empty).Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        }

        public List<Dictionary<string, object>> DescribeKeys()
        {
            LightingSettings s = GetSettings();
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            byte[] frame;
            lock (locker)
            {
                frame = (byte[])currentFrame.Clone();
            }
            foreach (Led led in leds)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["loc"] = led.Location;
                d["name"] = led.Name;
                d["id"] = led.Id;
                d["def"] = led.Id != null && led.Id.IndexOf('_') > 0 ? led.Id.Substring(0, led.Id.IndexOf('_')) : led.Id;
                d["show"] = led.Show;
                d["x"] = led.X;
                d["y"] = led.Y;
                d["w"] = led.W;
                d["h"] = led.H;
                int i = led.Location * 4;
                d["color"] = Colors.ToHex((uint)((frame[i] << 16) | (frame[i + 1] << 8) | frame[i + 2]));
                uint painted;
                d["painted"] = s.Paint.TryGetValue(led.Location, out painted);
                result.Add(d);
            }
            return result;
        }

        public void NotifyKeyPress(int location)
        {
            lock (locker)
            {
                presses.Add(new Press { Location = location, Time = clock.Elapsed.TotalSeconds });
                if (presses.Count > 64)
                {
                    presses.RemoveAt(0);
                }
            }
        }

        // ------------------------------------------------------------ loop

        private readonly Stopwatch clock = Stopwatch.StartNew();

        public void Start()
        {
            if (IsRunning)
            {
                return;
            }
            stopRequested = false;
            IsRunning = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "GK6X realtime lighting";
            thread.Start();
        }

        public void Stop()
        {
            stopRequested = true;
            Thread t = thread;
            if (t != null && t != Thread.CurrentThread)
            {
                t.Join(2000);
            }
            IsRunning = false;
        }

        private void Loop()
        {
            double fpsWindowStart = clock.Elapsed.TotalSeconds;
            int fpsFrames = 0;
            try
            {
                while (!stopRequested)
                {
                    double frameStart = clock.Elapsed.TotalSeconds;
                    LightingSettings s;
                    lock (locker)
                    {
                        s = settings.Clone();
                    }
                    byte[] frame = Render(s, frameStart);
                    lock (locker)
                    {
                        Buffer.BlockCopy(frame, 0, currentFrame, 0, frame.Length);
                    }

                    bool changed = lastFrame == null || !frame.SequenceEqual(lastFrame);
                    bool keepAlive = clock.ElapsedMilliseconds - lastSendTicks > 1000;
                    bool force = Interlocked.Exchange(ref forceSend, 0) != 0;
                    if (Paused)
                    {
                        lastFrame = null;
                        Thread.Sleep(20);
                        continue;
                    }
                    if (changed || keepAlive || force)
                    {
                        bool ok;
                        try
                        {
                            ok = sink.Send(frame);
                        }
                        catch
                        {
                            ok = false;
                        }
                        lastSendTicks = clock.ElapsedMilliseconds;
                        if (ok)
                        {
                            lastFrame = frame;
                            consecutiveFailures = 0;
                            FramesSent++;
                            fpsFrames++;
                        }
                        else
                        {
                            FramesFailed++;
                            lastFrame = null;
                            if (++consecutiveFailures >= 10)
                            {
                                Program.Log("Realtime lighting: keyboard stopped answering, stopping the engine");
                                IsRunning = false;
                                Action<LightingEngine> failed = Failed;
                                if (failed != null)
                                {
                                    failed(this);
                                }
                                return;
                            }
                        }
                    }

                    double now = clock.Elapsed.TotalSeconds;
                    if (now - fpsWindowStart >= 1)
                    {
                        MeasuredFps = fpsFrames / (now - fpsWindowStart);
                        fpsFrames = 0;
                        fpsWindowStart = now;
                    }

                    double sleep = (1.0 / Math.Max(1, Fps)) - (clock.Elapsed.TotalSeconds - frameStart);
                    if (sleep > 0.0005)
                    {
                        Thread.Sleep((int)(sleep * 1000));
                    }
                }
            }
            finally
            {
                IsRunning = false;
            }
        }

        // ------------------------------------------------------------ rendering

        private static float Dir(Led led, int direction)
        {
            switch (direction)
            {
                case 1: return 1 - led.NormX;
                case 2: return led.NormY;
                case 3: return 1 - led.NormY;
                default: return led.NormX;
            }
        }

        private static void Mix(uint a, uint b, float t, out float r, out float g, out float bl)
        {
            float ar, ag, ab, br, bg, bb;
            Colors.Unpack(a, out ar, out ag, out ab);
            Colors.Unpack(b, out br, out bg, out bb);
            t = Math.Max(0, Math.Min(1, t));
            r = ar + (br - ar) * t;
            g = ag + (bg - ag) * t;
            bl = ab + (bb - ab) * t;
        }

        public byte[] Render(LightingSettings s, double t)
        {
            byte[] frame = new byte[KeyboardDevice.RealtimeLedCount * 4];
            double rate = 0.05 + (s.Speed / 100.0) * 0.95;// cycles per second
            float brightness = s.Brightness / 100f;

            float[] leColors = null;
            if (s.Effect == "le")
            {
                LePlayer player = GetLePlayer(s.Le);
                if (player != null)
                {
                    leColors = player.Render(t, s.Speed);
                }
            }

            List<Press> activePresses = null;
            if (s.Effect == "reactive" || s.Effect == "ripple")
            {
                lock (locker)
                {
                    presses.RemoveAll(p => t - p.Time > 3);
                    activePresses = new List<Press>(presses);
                }
            }

            foreach (Led led in leds)
            {
                float r = 0, g = 0, b = 0;
                float pos = Dir(led, s.Direction);
                switch (s.Effect)
                {
                    case "static":
                        Colors.Unpack(s.Color1, out r, out g, out b);
                        break;
                    case "rainbow":
                        Colors.Hsv((float)(pos * 0.8 - t * rate * 0.5), 1, 1, out r, out g, out b);
                        break;
                    case "cycle":
                        Colors.Hsv((float)(t * rate * 0.5), 1, 1, out r, out g, out b);
                        break;
                    case "breathing":
                        {
                            float k = (float)((Math.Sin(2 * Math.PI * t * rate * 0.5 - Math.PI / 2) + 1) / 2);
                            k = 0.03f + 0.97f * k * k;
                            Colors.Unpack(s.Color1, out r, out g, out b);
                            r *= k; g *= k; b *= k;
                        }
                        break;
                    case "wave":
                        {
                            float k = (float)((Math.Sin(2 * Math.PI * (pos * 1.2 - t * rate * 0.5)) + 1) / 2);
                            Mix(s.Color1, s.Color2, k, out r, out g, out b);
                        }
                        break;
                    case "gradient":
                        Mix(s.Color1, s.Color2, pos, out r, out g, out b);
                        break;
                    case "reactive":
                    case "ripple":
                        {
                            // The background is color 2, key presses flash color 1
                            Colors.Unpack(s.Color2, out r, out g, out b);
                            float fr, fg, fb;
                            Colors.Unpack(s.Color1, out fr, out fg, out fb);
                            float decay = (float)(0.25 + (1 - s.Speed / 100.0) * 1.5);// seconds
                            foreach (Press p in activePresses)
                            {
                                double age = t - p.Time;
                                float k;
                                if (s.Effect == "reactive")
                                {
                                    if (p.Location != led.Location)
                                    {
                                        continue;
                                    }
                                    k = (float)Math.Max(0, 1 - age / decay);
                                }
                                else
                                {
                                    Led origin;
                                    if (!ledsByLocation.TryGetValue(p.Location, out origin))
                                    {
                                        continue;
                                    }
                                    float dx = led.CenterX - origin.CenterX, dy = led.CenterY - origin.CenterY;
                                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                                    double radius = age * 450 * (0.5 + s.Speed / 100.0);
                                    double ring = 1 - Math.Abs(dist - radius) / 70.0;
                                    k = (float)(Math.Max(0, ring) * Math.Max(0, 1 - age / (decay * 2.5)));
                                }
                                if (k > 0)
                                {
                                    r += (fr - r) * k; g += (fg - g) * k; b += (fb - b) * k;
                                }
                            }
                        }
                        break;
                    case "le":
                        if (leColors != null)
                        {
                            r = leColors[led.Location * 3];
                            g = leColors[led.Location * 3 + 1];
                            b = leColors[led.Location * 3 + 2];
                        }
                        break;
                    case "custom":
                    case "off":
                    default:
                        break;
                }

                uint painted;
                if (s.Paint.TryGetValue(led.Location, out painted) && s.Effect != "off")
                {
                    Colors.Unpack(painted, out r, out g, out b);
                }

                int i = led.Location * 4;
                frame[i + 0] = ToByte(r * brightness);
                frame[i + 1] = ToByte(g * brightness);
                frame[i + 2] = ToByte(b * brightness);
                // The 4th byte is an alpha/intensity byte: 0 keeps the LED dark (verified on a SK61S RGB)
                frame[i + 3] = 255;
            }
            return frame;
        }

        private readonly Dictionary<string, LePlayer> lePlayers = new Dictionary<string, LePlayer>();

        private LePlayer GetLePlayer(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            lock (lePlayers)
            {
                LePlayer player;
                if (!lePlayers.TryGetValue(name, out player))
                {
                    UserDataFile.LightingEffect effect = new UserDataFile.LightingEffect(new UserDataFile(), name);
                    player = effect.Load(state) ? new LePlayer(effect) : null;
                    lePlayers[name] = player;
                }
                return player;
            }
        }

        /// <summary>True when the named .le file can be loaded for this keyboard.</summary>
        public bool CanPlay(string name)
        {
            return GetLePlayer(name) != null;
        }

        private static byte ToByte(float v)
        {
            if (v <= 0) return 0;
            if (v >= 1) return 255;
            return (byte)(v * 255f + 0.5f);
        }
    }

    /// <summary>
    /// Saves / loads <see cref="LightingSettings"/> as json.
    /// </summary>
    public static class LightingSettingsFile
    {
        public static string ToJson(LightingSettings s)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["effect"] = s.Effect;
            d["le"] = s.Le;
            d["speed"] = s.Speed;
            d["brightness"] = s.Brightness;
            d["color1"] = Colors.ToHex(s.Color1);
            d["color2"] = Colors.ToHex(s.Color2);
            d["direction"] = s.Direction;
            Dictionary<string, object> paint = new Dictionary<string, object>();
            foreach (KeyValuePair<int, uint> kv in s.Paint)
            {
                paint[kv.Key.ToString()] = Colors.ToHex(kv.Value);
            }
            d["paint"] = paint;
            return Json.Serialize(d);
        }

        public static LightingSettings FromJson(string json, LightingSettings baseSettings = null)
        {
            LightingSettings s = baseSettings != null ? baseSettings.Clone() : new LightingSettings();
            Dictionary<string, object> d = Json.Deserialize(json) as Dictionary<string, object>;
            if (d != null)
            {
                Apply(s, d);
            }
            return s;
        }

        /// <summary>Applies the keys which are present in <paramref name="d"/> (partial updates are fine).</summary>
        public static void Apply(LightingSettings s, Dictionary<string, object> d)
        {
            string str;
            long num;
            double dbl;
            if (Json.TryGetValue(d, "effect", out str))
            {
                s.Effect = str.ToLowerInvariant();
            }
            if (Json.TryGetValue(d, "le", out str)) s.Le = str.Length > 100 ? str.Substring(0, 100) : str;
            if (TryGetNumber(d, "speed", out dbl)) s.Speed = (int)dbl;
            if (TryGetNumber(d, "brightness", out dbl)) s.Brightness = (int)dbl;
            if (TryGetNumber(d, "direction", out dbl)) s.Direction = (int)dbl;
            if (Json.TryGetValue(d, "color1", out str)) s.Color1 = Colors.Parse(str);
            if (Json.TryGetValue(d, "color2", out str)) s.Color2 = Colors.Parse(str);
            Dictionary<string, object> paint;
            if (Json.TryGetValue(d, "paint", out paint))
            {
                s.Paint.Clear();
                foreach (KeyValuePair<string, object> kv in paint)
                {
                    int loc;
                    if (int.TryParse(kv.Key, out loc) && kv.Value is string)
                    {
                        s.Paint[loc] = Colors.Parse((string)kv.Value);
                    }
                }
            }
        }

        public static bool TryGetNumber(Dictionary<string, object> d, string key, out double value)
        {
            value = 0;
            object o;
            if (!d.TryGetValue(key, out o) || o == null)
            {
                return false;
            }
            if (o is long) { value = (long)o; return true; }
            if (o is double) { value = (double)o; return true; }
            if (o is string) { return double.TryParse((string)o, NumberStyles.Float, CultureInfo.InvariantCulture, out value); }
            return false;
        }
    }
}

namespace GK6X
{
    /// <summary>
    /// Software player for the official .le lighting files, used to preview them on the keyboard picture and to play them
    /// through the realtime driver layer. The firmware's exact timing is not documented, so this is an approximation:
    /// one "tick" is 1/20 s at speed 50.
    /// </summary>
    public class LePlayer
    {
        private readonly UserDataFile.LightingEffect effect;
        private readonly int totalTicks;
        private readonly List<int> frameStart = new List<int>();
        private readonly Dictionary<int, UserDataFile.LightingEffect.Param> paramByKey = new Dictionary<int, UserDataFile.LightingEffect.Param>();
        private readonly float[] buffer = new float[KeyboardDevice.RealtimeLedCount * 3];

        public LePlayer(UserDataFile.LightingEffect effect)
        {
            this.effect = effect;
            int tick = 0;
            foreach (UserDataFile.LightingEffect.Frame frame in effect.Frames)
            {
                frameStart.Add(tick);
                tick += Math.Max(1, frame.Count);
            }
            totalTicks = Math.Max(1, tick);
            foreach (UserDataFile.LightingEffect.Param p in effect.Params)
            {
                foreach (int key in p.Keys)
                {
                    if (!paramByKey.ContainsKey(key)) paramByKey[key] = p;
                }
            }
        }

        private static void Unpack(uint packed, out float r, out float g, out float b)
        {
            r = (packed & 0xFF) / 255f;
            g = ((packed >> 8) & 0xFF) / 255f;
            b = ((packed >> 16) & 0xFF) / 255f;
        }

        private static double StartHue(uint packed)
        {
            float r, g, b;
            Unpack(packed, out r, out g, out b);
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            if (d <= 0) return 0;
            double h;
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            return h < 0 ? h + 1 : h;
        }

        public float[] Render(double seconds, int speed)
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (effect.Type == LightingEffectType.Static)
            {
                foreach (KeyValuePair<int, uint> kv in effect.KeyColors)
                {
                    if (kv.Key >= 0 && kv.Key < KeyboardDevice.RealtimeLedCount)
                    {
                        Unpack(kv.Value, out buffer[kv.Key * 3], out buffer[kv.Key * 3 + 1], out buffer[kv.Key * 3 + 2]);
                    }
                }
                return buffer;
            }
            if (effect.Frames.Count == 0)
            {
                return buffer;
            }
            double ticks = seconds * 20.0 * (Math.Max(1, speed) / 50.0);
            int tick = (int)(ticks % totalTicks);
            int index = 0;
            for (int i = 0; i < frameStart.Count; i++)
            {
                if (frameStart[i] <= tick) index = i;
            }
            foreach (int key in effect.Frames[index].KeyCodes)
            {
                if (key < 0 || key >= KeyboardDevice.RealtimeLedCount) continue;
                float r = 1, g = 1, b = 1;
                UserDataFile.LightingEffect.Param p;
                if (paramByKey.TryGetValue(key, out p))
                {
                    bool hasColor = (p.Color & 0xFFFFFF) != 0;
                    switch (p.ColorType)
                    {
                        case LightingEffectColorType.RGB:
                            // The param color is where on the color wheel this group of keys starts
                            Colors.Hsv((float)(StartHue(p.Color) + ticks / Math.Max(1, p.Val1)), 1, 1, out r, out g, out b);
                            break;
                        case LightingEffectColorType.Breathing:
                            {
                                if (hasColor) Unpack(p.Color, out r, out g, out b);
                                else Colors.Hsv((float)(ticks / 90.0), 1, 1, out r, out g, out b);
                                double period = Math.Max(4, p.Val1 + p.Val2);
                                float k = (float)((Math.Sin(2 * Math.PI * ticks / period - Math.PI / 2) + 1) / 2);
                                r *= k; g *= k; b *= k;
                            }
                            break;
                        default:
                            Unpack(p.Color, out r, out g, out b);
                            break;
                    }
                }
                buffer[key * 3] = r;
                buffer[key * 3 + 1] = g;
                buffer[key * 3 + 2] = b;
            }
            return buffer;
        }
    }
}
