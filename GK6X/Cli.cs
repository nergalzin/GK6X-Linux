using HidSharp;
using MiniJSON;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GK6X
{
    /// <summary>
    /// Command line front end for the Linux friendly features (realtime lighting daemon + client commands).
    /// The original interactive prompt (map / unmap / ...) is untouched and still starts when no command is given.
    /// </summary>
    public static class Cli
    {
        private static readonly HashSet<string> commands = new HashSet<string>
        {
            "daemon", "status", "ui", "color", "colour", "effect", "effects", "brightness", "key", "clear", "off", "stop",
            "list", "probe", "help", "version", "speed", "selftest"
        };

        public static bool IsCommand(string[] args)
        {
            if (args.Length == 0)
            {
                return false;
            }
            return commands.Contains(Normalize(args[0]));
        }

        private static string Normalize(string arg)
        {
            return arg.TrimStart('-').ToLowerInvariant();
        }

        private static string GetOption(List<string> args, string name, string defaultValue)
        {
            int i = args.IndexOf("--" + name);
            if (i >= 0 && i + 1 < args.Count)
            {
                string value = args[i + 1];
                args.RemoveRange(i, 2);
                return value;
            }
            return defaultValue;
        }

        private static bool GetFlag(List<string> args, string name)
        {
            return args.Remove("--" + name);
        }

        public static int Run(string[] argv)
        {
            string command = Normalize(argv[0]);
            List<string> args = argv.Skip(1).ToList();
            try
            {
                switch (command)
                {
                    case "help":
                        PrintHelp();
                        return 0;
                    case "version":
                        Console.WriteLine("GK6X (linux build) " + typeof(Cli).Assembly.GetName().Version);
                        return 0;
                    case "daemon":
                        return RunDaemon(args);
                    case "list":
                        return RunList();
                    case "probe":
                        return RunProbe(args);
                    case "selftest":
                        return RunSelfTest();
                    default:
                        return RunClient(command, args);
                }
            }
            catch (FormatException e)
            {
                Console.Error.WriteLine("Error: " + e.Message);
                return 2;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"GK6X for Linux - realtime lighting

Start the lighting service (keeps running; use the systemd user service for autostart):
  gk6x daemon [--port 6465] [--fps 30] [--driver-keys default|none] [--no-key-monitor]
  gk6x daemon --simulate [MODEL_ID]     no keyboard needed (UI testing)

Change the lighting while the service is running (instant):
  gk6x color ff0000                     solid color
  gk6x effect rainbow [--speed 60] [--color ff0000] [--color2 0000ff] [--dir 0-3]
  gk6x effects                          list the effects
  gk6x brightness 70                    0 - 100
  gk6x speed 40                         1 - 100
  gk6x key W 00ff00                     paint one key (name or #locationCode); 'off' removes it
  gk6x clear                            remove all painted keys
  gk6x off                              all lights off
  gk6x status
  gk6x ui [--tab]                       open the control page in its own window (--tab: normal browser tab)
  gk6x stop                             stop the service (keyboard returns to its own layer)

Diagnostics:
  gk6x list                             show the keyboard HID interfaces and whether they can be opened
  gk6x probe [--color ff0000] [--seconds 5] [--driver-keys default|none]
                                        enter the driver layer, light all keys, go back

Other (original) commands: run 'gk6x' without arguments for the interactive prompt (map, unmap, ...).");
        }

        // ------------------------------------------------------------------ daemon

        private static int RunDaemon(List<string> args)
        {
            int port = int.Parse(GetOption(args, "port", RealtimeDaemon.DefaultPort.ToString()));
            int fps = int.Parse(GetOption(args, "fps", "30"));
            string keys = GetOption(args, "driver-keys", "default");
            bool noMonitor = GetFlag(args, "no-key-monitor");
            bool simulate = false;
            uint model = 655491200;
            int simIndex = args.IndexOf("--simulate");
            if (simIndex >= 0)
            {
                simulate = true;
                args.RemoveAt(simIndex);
                uint parsed;
                if (simIndex < args.Count && uint.TryParse(args[simIndex], out parsed))
                {
                    model = parsed;
                }
            }

            if (!Program.Initialize())
            {
                return 1;
            }

            string settingsPath = Path.Combine(Program.ConfigPath, "realtime.json");
            RealtimeDaemon daemon = new RealtimeDaemon(settingsPath, simulate, model);
            daemon.Fps = Math.Max(1, Math.Min(120, fps));
            daemon.UseKeyMonitor = !noMonitor;
            daemon.DriverKeys = keys == "none" ? DriverKeysMode.None : DriverKeysMode.Default;

            ControlServer server = new ControlServer(daemon, port, Path.Combine(Program.DataBasePath, "web"));
            try
            {
                server.Start();
            }
            catch (Exception e)
            {
                Program.Log("Couldn't start the control server on port " + port + " (already running?): " + e.Message);
                return 1;
            }

            List<IDisposable> signals = new List<IDisposable>();
            foreach (PosixSignal signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP })
            {
                signals.Add(PosixSignalRegistration.Create(signal, ctx =>
                {
                    ctx.Cancel = true;
                    daemon.RequestShutdown();
                }));
            }

            daemon.Start();
            Program.Log("GK6X realtime lighting running. Control page: " + server.Url +
                (simulate ? " (simulated keyboard " + model + ")" : string.Empty));
            daemon.WaitForShutdown();
            Program.Log("Shutting down...");
            server.Stop();
            daemon.Stop();
            // Non-zero so that systemd (Restart=on-failure) starts a fresh process
            return daemon.RestartRequested ? 75 : 0;
        }

        // ------------------------------------------------------------------ self test

        private static int selfTestFailures;

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + name);
            if (!ok) selfTestFailures++;
        }

        /// <summary>Offline checks of the protocol encoding and the effects (no keyboard needed).</summary>
        private static int RunSelfTest()
        {
            if (!Program.Initialize())
            {
                return 1;
            }
            selfTestFailures = 0;
            Console.WriteLine("Protocol");
            // CRC-16/CCITT-FALSE check value
            Check("crc16 of '123456789' is 0x29B1", Crc16.GetCrc(Encoding.ASCII.GetBytes("123456789")) == 0x29B1);

            byte[] frame = new byte[KeyboardDevice.RealtimeLedCount * 4];
            for (int i = 0; i < frame.Length; i++) frame[i] = (byte)(i * 7 + 1);
            byte[] padded = new byte[560];
            Buffer.BlockCopy(frame, 0, padded, 0, frame.Length);
            int numReports = (padded.Length + 0x37) / 0x38;
            Check("realtime frame is 10 reports", numReports == 10);
            bool layoutOk = true, crcOk = true, dataOk = true;
            for (int i = 0; i < numReports; i++)
            {
                byte[] r = KeyboardDevice.BuildReport(OpCodes.DriverLayerUpdateRealtimeLighting, 1, 0, padded, numReports, i * 0x38);
                layoutOk &= r.Length == 65 && r[0] == 0 && r[1] == 0x1A && r[2] == 1 &&
                    (r[3] | r[4] << 8 | r[5] << 16) == i * 0x38 && r[6] == 0x38;
                crcOk &= Crc16.ValidateCrc(r, 1, 7);
                byte[] chunk = new byte[0x38];
                Buffer.BlockCopy(r, 9, chunk, 0, 0x38);
                dataOk &= chunk.SequenceEqual(padded.Skip(i * 0x38).Take(0x38));
            }
            Check("report header: op, op2, 24 bit offset, length 0x38", layoutOk);
            Check("report crc is valid", crcOk);
            Check("report data matches the frame", dataOk);

            byte[] simple = KeyboardDevice.BuildReport(OpCodes.DriverLayerUpdateRealtimeLighting, 2, 0, null, 1, 0);
            Check("update-complete report (1A 02) has a valid crc", simple[1] == 0x1A && simple[2] == 2 && Crc16.ValidateCrc(simple, 1, 7));

            Console.WriteLine("Effects");
            KeyboardState keyboard = KeyboardState.GetKeyboardState(655491200);
            Check("model 655491200 (GK84S RGB) data loads", keyboard != null && keyboard.KeysByLocationCode.Count > 80);
            if (keyboard != null)
            {
                LightingEngine engine = new LightingEngine(keyboard, new NullFrameSink());
                foreach (string effect in LightingEngine.EffectNames)
                {
                    LightingSettings s = new LightingSettings { Effect = effect };
                    byte[] f = engine.Render(s, 1.25);
                    Check("effect '" + effect + "' renders " + (KeyboardDevice.RealtimeLedCount * 4) + " bytes",
                        f.Length == KeyboardDevice.RealtimeLedCount * 4);
                }
                LightingSettings st = new LightingSettings { Effect = "static", Color1 = 0x102030, Brightness = 100 };
                byte[] sf = engine.Render(st, 0);
                int loc = engine.FindLocations("Escape").FirstOrDefault();
                Check("static color lands in R,G,B order", sf[loc * 4] == 0x10 && sf[loc * 4 + 1] == 0x20 && sf[loc * 4 + 2] == 0x30);
                st.Brightness = 50;
                sf = engine.Render(st, 0);
                Check("brightness scales the color", sf[loc * 4] == 8 && sf[loc * 4 + 2] == 24);
                Check("lit key has alpha 255", sf[loc * 4 + 3] == 255);
                st.Paint[loc] = 0xFF0000;
                st.Brightness = 100;
                sf = engine.Render(st, 0);
                Check("painted key overrides the effect", sf[loc * 4] == 255 && sf[loc * 4 + 1] == 0);
                Check("key lookup by name and #location", engine.FindLocations("esc").Count == 1 && engine.FindLocations("#" + loc).Count == 1);
                LightingSettings round = LightingSettingsFile.FromJson(LightingSettingsFile.ToJson(st));
                Check("settings survive a json round trip", round.Color1 == st.Color1 && round.Paint.Count == 1 && round.Paint[loc] == 0xFF0000);
            }
            Console.WriteLine("Profile / lighting files");
            {
                KeyboardState sk61 = KeyboardState.GetKeyboardState(656801885);
                string sample = Path.Combine(Program.BasePath, "UserData", "Sample.txt");
                ProfileModel m = ProfileModel.Parse(File.Exists(sample) ? File.ReadAllText(sample) : string.Empty);
                Check("Sample.txt parses (layers, macros, lighting)", m.Layers.ContainsKey("FnLayer3") && m.Macros.Count == 2 && m.Lighting.ContainsKey("Base"));
                ProfileModel again = ProfileModel.Parse(m.Serialize());
                Check("profile text round trip is stable", again.Serialize() == m.Serialize());
                ProfileModel viaJson = ProfileModel.FromJson((Dictionary<string, object>)Json.Deserialize(Json.Serialize(m.ToJson())));
                Check("profile json round trip is stable", viaJson.Serialize() == m.Serialize());
                ProfileModel bad = ProfileModel.Parse("[Layer1]\nQ:NotAKey\nNoSuchKey:A\n");
                List<string> errors = bad.Validate(sk61);
                Check("validation rejects unknown keys and values", errors.Count == 2);
                ProfileModel good = ProfileModel.Parse("[Macro(m1,0,RepeatXTimes,2)]\nPress:A+B\n[Layer1]\nQ:LCtrl+A\nW:Macro(m1)\nE:NextLightingEffect\n");
                Check("valid profile passes validation", good.Validate(sk61).Count == 0);
                Check("English key names load", Localization.GetValue("key_esc") == "ESC" && Localization.GetValue("key_disable") == "Disabled");
                Check("palette has the official groups", ConfigService.BuildKeyset().Count >= 8);
                List<object> files = ConfigService.ListLightingFiles();
                Check("lighting library lists the bundled .le files", files.Count > 100);
                LightingEngine engine = new LightingEngine(sk61, new NullFrameSink());
                bool played = engine.CanPlay("Windmill 61") || engine.CanPlay("Windmill");
                Check("a dynamic .le file loads and renders", played);
                LightingSettings le = new LightingSettings { Effect = "le", Le = engine.CanPlay("Windmill 61") ? "Windmill 61" : "Windmill" };
                int lit = 0;
                for (double t = 0; t < 6; t += 0.1)
                {
                    byte[] f = engine.Render(le, t);
                    for (int i = 0; i < f.Length; i += 4) if (f[i] + f[i + 1] + f[i + 2] > 0) lit++;
                }
                Check("the dynamic effect lights keys over time", lit > 0);
                LightingSettings st = new LightingSettings { Effect = "le", Le = "WASD static" };
                Check("a static .le file plays", engine.CanPlay("WASD static") && engine.Render(st, 0).Where((v, i) => i % 4 < 3).Any(v => v > 0));
            }
            Console.WriteLine(selfTestFailures == 0 ? "All checks passed." : selfTestFailures + " check(s) FAILED.");
            return selfTestFailures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ diagnostics

        private static int RunList()
        {
            if (!Program.Initialize())
            {
                return 1;
            }
            int found = 0;
            int unreadable = 0;
            foreach (HidDevice hid in DeviceList.Local.GetHidDevices(0x1EA7, 0x0907))
            {
                found++;
                string info;
                try
                {
                    info = "in=" + hid.GetMaxInputReportLength() + " out=" + hid.GetMaxOutputReportLength();
                }
                catch (Exception e)
                {
                    info = "(can't read report info: " + e.Message + ")";
                }
                HidStream stream;
                bool canOpen = hid.TryOpen(out stream);
                if (canOpen)
                {
                    stream.Close();
                }
                else
                {
                    unreadable++;
                }
                Console.WriteLine(hid.DevicePath + "  " + info + "  open=" + (canOpen ? "yes" : "NO"));
            }
            if (found == 0)
            {
                Console.WriteLine("No GK6X keyboard (1ea7:0907) found.");
                return 1;
            }
            if (unreadable > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Some interfaces can't be opened: install the udev rule (linux/install.sh) and replug the keyboard.");
            }
            KeyboardDeviceManager.StartListener();
            Thread.Sleep(500);
            foreach (KeyboardDevice device in KeyboardDeviceManager.GetConnectedDevices())
            {
                Console.WriteLine();
                Console.WriteLine("Keyboard: '" + device.State.ModelName + "' model:" + device.State.ModelId +
                    " fw:" + device.State.FirmwareVersion + " keys:" + device.State.KeysByLocationCode.Count +
                    " maxLogicCode:" + device.State.MaxLogicCode);
            }
            KeyboardDeviceManager.StopListener();
            return 0;
        }

        private static string Describe(Packet p)
        {
            return p != null ? "ok" : "NO ANSWER";
        }

        private static int RunProbe(List<string> args)
        {
            uint color = Colors.Parse(GetOption(args, "color", "ff0000"));
            int seconds = int.Parse(GetOption(args, "seconds", "5"));
            string keys = GetOption(args, "driver-keys", "default");
            byte alpha = byte.Parse(GetOption(args, "alpha", "255"));
            bool config = GetFlag(args, "le-config");
            int interval = int.Parse(GetOption(args, "interval", "50"));
            int pingEvery = int.Parse(GetOption(args, "ping", "0"));
            bool diag = GetFlag(args, "diag");

            if (!Program.Initialize())
            {
                return 1;
            }
            KeyboardDeviceManager.StartListener();
            KeyboardDevice[] devices = new KeyboardDevice[0];
            for (int i = 0; i < 30 && devices.Length == 0; i++)
            {
                devices = KeyboardDeviceManager.GetConnectedDevices();
                if (devices.Length == 0) Thread.Sleep(100);
            }
            if (devices.Length == 0)
            {
                Console.WriteLine("No keyboard found. Run 'gk6x list' for details.");
                KeyboardDeviceManager.StopListener();
                return 1;
            }
            KeyboardDevice device = devices[0];
            Console.WriteLine("Keyboard: '" + device.State.ModelName + "' model:" + device.State.ModelId + " fw:" + device.State.FirmwareVersion);
            try
            {
                using (Packet r = device.WritePacket(OpCodes.SetLayer, (byte)KeyboardLayer.Driver, null))
                {
                    Console.WriteLine("SetLayer(Driver):          " + Describe(r));
                }
                if (keys != "none")
                {
                    bool ok = device.SetDriverKeyTable(RealtimeDaemon.BuildDefaultDriverKeyTable(device.State));
                    Console.WriteLine("Driver key table:          " + (ok ? "ok" : "NO ANSWER"));
                }
                if (config)
                {
                    bool ok = device.SetDriverLayerConfig(0, 1, 18, 3, 0, 0, 0, 0, false);
                    Console.WriteLine("Driver layer config:       " + (ok ? "ok" : "NO ANSWER"));
                }

                byte[] frame = new byte[KeyboardDevice.RealtimeLedCount * 4];
                foreach (KeyboardState.Key key in device.State.KeysByLocationCode.Values)
                {
                    if (key.LocationCode >= 0 && key.LocationCode < KeyboardDevice.RealtimeLedCount)
                    {
                        frame[key.LocationCode * 4 + 0] = (byte)(color >> 16);
                        frame[key.LocationCode * 4 + 1] = (byte)(color >> 8);
                        frame[key.LocationCode * 4 + 2] = (byte)color;
                        frame[key.LocationCode * 4 + 3] = alpha;
                    }
                }
                Stopwatch sw = Stopwatch.StartNew();
                int ok2 = 0, failed = 0;
                long maxMs = 0;
                int n = 0;
                long lastPing = 0;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    long t0 = sw.ElapsedMilliseconds;
                    if (pingEvery > 0 && t0 - lastPing >= pingEvery)
                    {
                        lastPing = t0;
                        using (Packet pr = device.WritePacket(OpCodes.Ping, 0, null))
                        {
                            if (pr == null) Console.WriteLine("  ping at " + t0 + " ms: NO ANSWER");
                        }
                        t0 = sw.ElapsedMilliseconds;
                    }
                    bool sent = device.SendRealtimeFrame(frame);
                    if (sent) ok2++; else failed++;
                    long took = sw.ElapsedMilliseconds - t0;
                    if (!sent || took > 200) Console.WriteLine("  frame " + n + " at " + t0 + " ms " + (sent ? "ok" : "FAILED") + " took " + took + " ms");
                    maxMs = Math.Max(maxMs, took);
                    if (!sent && diag)
                    {
                        using (Packet pr = device.WritePacket(OpCodes.Ping, 0, null)) Console.WriteLine("  diag ping: " + Describe(pr));
                        using (Packet pr = device.WritePacket(OpCodes.Info, 1, null)) Console.WriteLine("  diag info: " + Describe(pr));
                        using (Packet pr = device.WritePacket(OpCodes.SetLayer, (byte)KeyboardLayer.Driver, null)) Console.WriteLine("  diag SetLayer(Driver): " + Describe(pr));
                    }
                    n++;
                    Thread.Sleep(interval);
                }
                Console.WriteLine("Frames sent: " + ok2 + " (avg " + (n > 0 ? sw.ElapsedMilliseconds / n : 0) + " ms incl. 50 ms pause)"+ " / " + ok2 + " ok, " + failed + " failed (slowest frame " + maxMs + " ms)");
            }
            finally
            {
                using (Packet r = device.WritePacket(OpCodes.SetLayer, (byte)KeyboardLayer.Base, null))
                {
                    Console.WriteLine("SetLayer(Base) (restore):  " + Describe(r));
                }
                KeyboardDeviceManager.StopListener();
            }
            return 0;
        }


        // ------------------------------------------------------------------ desktop window

        private static readonly string[] chromiumBrowsers =
        {
            "brave", "brave-browser", "helium-browser", "chromium", "chromium-browser", "google-chrome-stable", "google-chrome",
            "vivaldi", "vivaldi-stable", "microsoft-edge-stable", "thorium-browser", "opera"
        };

        private static string FindOnPath(string name)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':'))
            {
                if (dir.Length == 0) continue;
                string path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static string RunAndRead(string file, string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false };
                using (Process p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    return text.Trim();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Finds a Chromium based browser (they can show a page as a plain app window with --app=).</summary>
        private static string FindAppBrowser()
        {
            string forced = Environment.GetEnvironmentVariable("GK6X_BROWSER");
            if (!string.IsNullOrEmpty(forced))
            {
                return FindOnPath(forced) ?? (File.Exists(forced) ? forced : null);
            }
            // Prefer the user's default browser when it is Chromium based
            string defaultDesktop = RunAndRead("xdg-settings", "get default-web-browser").ToLowerInvariant();
            foreach (string name in chromiumBrowsers)
            {
                if (defaultDesktop.StartsWith(name) && FindOnPath(name) != null)
                {
                    return FindOnPath(name);
                }
            }
            foreach (string name in chromiumBrowsers)
            {
                string path = FindOnPath(name);
                if (path != null) return path;
            }
            return null;
        }

        private static bool IsServiceUp(string baseUrl)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(1);
                    return client.GetAsync(baseUrl + "/api/state").Result.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Opens the control page in its own desktop window (or a browser tab with --tab / no Chromium browser).</summary>
        private static int OpenUi(string baseUrl, bool asTab)
        {
            if (!IsServiceUp(baseUrl))
            {
                // Start the user service if it is installed, then wait for it
                try { Process.Start(new ProcessStartInfo("systemctl", "--user start gk6x.service") { UseShellExecute = false })?.WaitForExit(3000); } catch { }
                for (int i = 0; i < 30 && !IsServiceUp(baseUrl); i++) Thread.Sleep(200);
                if (!IsServiceUp(baseUrl))
                {
                    Console.Error.WriteLine("The lighting service is not running. Start it with 'gk6x daemon' or 'systemctl --user start gk6x'.");
                    return 1;
                }
            }
            string url = baseUrl + "/";
            string browser = asTab ? null : FindAppBrowser();
            if (browser == null)
            {
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
                return 0;
            }
            // A separate profile keeps this window independent from the user's normal browser windows
            string home = Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
            string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config)) config = Path.Combine(home, ".config");
            string profile = Path.Combine(config, "gk6x", "browser");
            Directory.CreateDirectory(profile);
            ProcessStartInfo psi = new ProcessStartInfo(browser) { UseShellExecute = false };
            psi.ArgumentList.Add("--app=" + url);
            psi.ArgumentList.Add("--user-data-dir=" + profile);
            psi.ArgumentList.Add("--class=GK6X");
            psi.ArgumentList.Add("--window-size=1220,860");
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");
            psi.ArgumentList.Add("--disable-features=Translate");
            Process.Start(psi);
            return 0;
        }

        // ------------------------------------------------------------------ client

        private static string BaseUrl(List<string> args)
        {
            return "http://127.0.0.1:" + GetOption(args, "port", RealtimeDaemon.DefaultPort.ToString());
        }

        private static int RunClient(string command, List<string> args)
        {
            string baseUrl = BaseUrl(args);
            Dictionary<string, object> body = new Dictionary<string, object>();
            string path = "/api/settings";

            switch (command)
            {
                case "status":
                    return Print(Request(baseUrl, "GET", "/api/state", null), true);
                case "effects":
                    Console.WriteLine(string.Join(Environment.NewLine, LightingEngine.EffectNames));
                    return 0;
                case "ui":
                    return OpenUi(baseUrl, GetFlag(args, "tab"));
                case "stop":
                    if (Request(baseUrl, "POST", "/api/shutdown", new Dictionary<string, object>()) == null)
                    {
                        return 1;
                    }
                    Console.WriteLine("Stopping.");
                    return 0;
                case "off":
                    body["effect"] = "off";
                    break;
                case "clear":
                    path = "/api/paint";
                    body["clear"] = true;
                    break;
                case "color":
                case "colour":
                    Require(args, 1, "gk6x color RRGGBB");
                    body["effect"] = "static";
                    body["color1"] = Colors.ToHex(Colors.Parse(args[0]));
                    body["paint"] = new Dictionary<string, object>();
                    break;
                case "brightness":
                    Require(args, 1, "gk6x brightness 0-100");
                    body["brightness"] = int.Parse(args[0]);
                    break;
                case "speed":
                    Require(args, 1, "gk6x speed 1-100");
                    body["speed"] = int.Parse(args[0]);
                    break;
                case "key":
                    Require(args, 2, "gk6x key NAME RRGGBB|off   (e.g. gk6x key W 00ff00)");
                    path = "/api/paint";
                    body["keys"] = new Dictionary<string, object> { { args[0], args[1] } };
                    break;
                case "effect":
                    {
                        Require(args, 1, "gk6x effect NAME [--speed N] [--color RRGGBB] [--color2 RRGGBB] [--dir N]");
                        string name = args[0];
                        args.RemoveAt(0);
                        body["effect"] = name;
                        string v;
                        if ((v = GetOption(args, "speed", null)) != null) body["speed"] = int.Parse(v);
                        if ((v = GetOption(args, "brightness", null)) != null) body["brightness"] = int.Parse(v);
                        if ((v = GetOption(args, "dir", null)) != null) body["direction"] = int.Parse(v);
                        if ((v = GetOption(args, "color", null)) != null) body["color1"] = Colors.ToHex(Colors.Parse(v));
                        if ((v = GetOption(args, "color2", null)) != null) body["color2"] = Colors.ToHex(Colors.Parse(v));
                    }
                    break;
            }
            return Print(Request(baseUrl, "POST", path, body), false);
        }

        private static void Require(List<string> args, int count, string usage)
        {
            if (args.Count < count)
            {
                throw new FormatException("usage: " + usage);
            }
        }

        private static int Print(Dictionary<string, object> state, bool verbose)
        {
            if (state == null)
            {
                return 1;
            }
            object err;
            if (state.TryGetValue("error", out err))
            {
                Console.Error.WriteLine("Error: " + err);
                return 1;
            }
            bool connected = state.ContainsKey("connected") && (bool)state["connected"];
            Dictionary<string, object> s = state["settings"] as Dictionary<string, object>;
            Console.WriteLine(connected
                ? "Keyboard: " + state["model"] + " (fw " + state["firmware"] + ")" + ((bool)state["simulated"] ? " [simulated]" : string.Empty)
                : "Keyboard: not connected (settings are kept and applied when it is)");
            if (s != null)
            {
                Console.WriteLine("Effect: " + s["effect"] + "  speed: " + s["speed"] + "  brightness: " + s["brightness"] +
                    "  color: " + s["color1"] + " / " + s["color2"] + "  direction: " + s["direction"]);
                Dictionary<string, object> paint = s["paint"] as Dictionary<string, object>;
                if (paint != null && paint.Count > 0)
                {
                    Console.WriteLine("Painted keys: " + paint.Count);
                }
            }
            if (verbose && connected)
            {
                Console.WriteLine("Frames: " + state["framesSent"] + " sent, " + state["framesFailed"] + " failed, " + state["fps"] + " fps" +
                    ", reactive key monitor: " + ((bool)state["keyMonitor"] ? "on" : "off"));
            }
            return 0;
        }

        private static Dictionary<string, object> Request(string baseUrl, string method, string path, Dictionary<string, object> body)
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                try
                {
                    HttpResponseMessage response;
                    if (method == "GET")
                    {
                        response = client.GetAsync(baseUrl + path).Result;
                    }
                    else
                    {
                        StringContent content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
                        response = client.PostAsync(baseUrl + path, content).Result;
                    }
                    string text = response.Content.ReadAsStringAsync().Result;
                    return Json.Deserialize(text) as Dictionary<string, object> ?? new Dictionary<string, object>();
                }
                catch (Exception)
                {
                    Console.Error.WriteLine("Can't reach the lighting service at " + baseUrl + ". Start it with 'gk6x daemon' " +
                        "(or 'systemctl --user start gk6x').");
                    return null;
                }
            }
        }
    }
}
