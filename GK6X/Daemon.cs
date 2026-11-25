using HidSharp;
using MiniJSON;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace GK6X
{
    /// <summary>
    /// How the "driver" layer key table is handled when entering realtime mode.
    /// </summary>
    public enum DriverKeysMode
    {
        /// <summary>Don't send a key table (the firmware keeps whatever it has)</summary>
        None,
        /// <summary>Send a key table in which every key has its default (unmapped) value</summary>
        Default
    }

    /// <summary>
    /// Watches the keyboard's standard (boot) HID interface to know which keys are pressed so that
    /// the reactive / ripple effects work. Passive: it only reads, nothing is grabbed or blocked.
    /// </summary>
    public class KeyPressMonitor : IDisposable
    {
        private readonly KeyboardDevice device;
        private readonly LightingEngine engine;
        private readonly Dictionary<int, List<int>> locationsByUsage = new Dictionary<int, List<int>>();
        private readonly List<HidStream> streams = new List<HidStream>();
        private volatile bool disposed;

        public int Interfaces { get { return streams.Count; } }

        public KeyPressMonitor(KeyboardDevice device, LightingEngine engine)
        {
            this.device = device;
            this.engine = engine;
            foreach (KeyboardState.Key key in device.State.KeysByLocationCode.Values)
            {
                if (KeyValues.GetKeyType(key.DriverValue) != DriverValueType.Key)
                {
                    continue;
                }
                int usage;
                if (KeyValues.IsKeyModifier(key.DriverValue))
                {
                    // HID modifier bits (0xE0 + bit)
                    byte bits = KeyValues.GetKeyData2(key.DriverValue);
                    int bit = 0;
                    while (bit < 8 && (bits & (1 << bit)) == 0) bit++;
                    if (bit >= 8) continue;
                    usage = 0xE0 + bit;
                }
                else
                {
                    usage = KeyValues.GetKeyData1(key.DriverValue);
                    if (usage == 0) continue;
                }
                List<int> list;
                if (!locationsByUsage.TryGetValue(usage, out list))
                {
                    locationsByUsage[usage] = list = new List<int>();
                }
                list.Add(key.LocationCode);
            }
        }

        public void Start()
        {
            try
            {
                string ownPath = device.device != null ? device.device.DevicePath : null;
                foreach (HidDevice hid in DeviceList.Local.GetHidDevices(0x1EA7, 0x0907))
                {
                    if (hid.DevicePath == ownPath)
                    {
                        continue;
                    }
                    int len;
                    try { len = hid.GetMaxInputReportLength(); } catch { continue; }
                    // Boot keyboard report: 8 bytes (+1 for the report id which HidSharp counts)
                    if (len != 9)
                    {
                        continue;
                    }
                    HidStream stream;
                    if (!hid.TryOpen(out stream))
                    {
                        continue;
                    }
                    stream.ReadTimeout = Timeout.Infinite;
                    streams.Add(stream);
                    Thread t = new Thread(() => ReadLoop(stream, len));
                    t.IsBackground = true;
                    t.Name = "GK6X key monitor";
                    t.Start();
                }
            }
            catch (Exception e)
            {
                Program.Log("Key press monitor unavailable: " + e.Message);
            }
        }

        private void ReadLoop(HidStream stream, int len)
        {
            byte[] buffer = new byte[len];
            byte[] previous = new byte[8];
            try
            {
                while (!disposed)
                {
                    int read = stream.Read(buffer);
                    if (read < len)
                    {
                        continue;
                    }
                    // buffer[0] = report id (0), then modifiers, reserved, 6 keys
                    byte[] report = new byte[8];
                    Buffer.BlockCopy(buffer, 1, report, 0, 8);
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if ((report[0] & (1 << bit)) != 0 && (previous[0] & (1 << bit)) == 0)
                        {
                            Pressed(0xE0 + bit);
                        }
                    }
                    for (int i = 2; i < 8; i++)
                    {
                        if (report[i] > 3 && Array.IndexOf(previous, report[i], 2, 6) < 0)
                        {
                            Pressed(report[i]);
                        }
                    }
                    previous = report;
                }
            }
            catch
            {
            }
        }

        private void Pressed(int usage)
        {
            List<int> locations;
            if (locationsByUsage.TryGetValue(usage, out locations))
            {
                foreach (int loc in locations)
                {
                    engine.NotifyKeyPress(loc);
                }
            }
        }

        public void Dispose()
        {
            disposed = true;
            foreach (HidStream stream in streams)
            {
                try { stream.Close(); } catch { }
            }
            streams.Clear();
        }
    }

    /// <summary>
    /// Keeps a realtime lighting engine attached to whichever keyboard is connected, and persists the settings.
    /// </summary>
    public class RealtimeDaemon
    {
        public const int DefaultPort = 6465;

        private readonly object locker = new object();
        private readonly string settingsPath;
        private readonly bool simulate;
        private readonly uint simulateModel;
        private LightingSettings settings;
        private LightingEngine engine;
        private KeyboardDevice device;
        private KeyPressMonitor monitor;
        private Timer saveTimer;
        private bool settingsDirty;
        private readonly ManualResetEvent shutdown = new ManualResetEvent(false);
        private Timer watchdogTimer;
        private int missingTicks;

        /// <summary>Set when the daemon asked to be restarted (see Watchdog); the process exits with a failure code.</summary>
        public bool RestartRequested { get; private set; }

        public DriverKeysMode DriverKeys = DriverKeysMode.Default;
        public bool UseKeyMonitor = true;
        public int Fps = 30;
        public bool IsSimulated { get { return simulate; } }

        public RealtimeDaemon(string settingsPath, bool simulate, uint simulateModel)
        {
            this.settingsPath = settingsPath;
            this.simulate = simulate;
            this.simulateModel = simulateModel;
            settings = LoadSettings();
            saveTimer = new Timer(SaveSettingsTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        private LightingSettings LoadSettings()
        {
            LightingSettings result = new LightingSettings();
            try
            {
                if (File.Exists(settingsPath))
                {
                    result = LightingSettingsFile.FromJson(File.ReadAllText(settingsPath), result);
                }
            }
            catch (Exception e)
            {
                Program.Log("Failed to load " + settingsPath + ": " + e.Message);
            }
            return result;
        }

        private void QueueSave()
        {
            settingsDirty = true;
            saveTimer.Change(500, Timeout.Infinite);
        }

        private void SaveSettingsTick(object state)
        {
            if (!settingsDirty)
            {
                return;
            }
            settingsDirty = false;
            try
            {
                LightingSettings s;
                lock (locker)
                {
                    s = engine != null ? engine.GetSettings() : settings;
                    settings = s;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllText(settingsPath, LightingSettingsFile.ToJson(s));
            }
            catch (Exception e)
            {
                Program.Log("Failed to save settings: " + e.Message);
            }
        }

        public LightingEngine Engine { get { lock (locker) { return engine; } } }
        public KeyboardDevice Device { get { lock (locker) { return device; } } }
        public bool HasMonitor { get { lock (locker) { return monitor != null && monitor.Interfaces > 0; } } }

        public LightingSettings Settings
        {
            get
            {
                lock (locker)
                {
                    return engine != null ? engine.GetSettings() : settings.Clone();
                }
            }
        }

        /// <summary>Applies a change even when no keyboard is connected (it will be used once one is).</summary>
        public void Update(Action<LightingSettings> change)
        {
            LightingEngine e;
            lock (locker)
            {
                e = engine;
                if (e == null)
                {
                    change(settings);
                    QueueSave();
                    return;
                }
            }
            e.Update(change);
        }

        public void Start()
        {
            if (simulate)
            {
                KeyboardState keyboardState = KeyboardState.GetKeyboardState(simulateModel);
                if (keyboardState == null)
                {
                    throw new InvalidOperationException("Unknown model id " + simulateModel);
                }
                lock (locker)
                {
                    AttachEngine(keyboardState, new NullFrameSink());
                }
                return;
            }
            KeyboardDeviceManager.Connected += OnConnected;
            KeyboardDeviceManager.Disconnected += OnDisconnected;
            KeyboardDeviceManager.StartListener();
            watchdogTimer = new Timer(Watchdog, null, 2000, 2000);
        }

        /// <summary>
        /// True when the kernel exposes a GK6X keyboard (USB id 1ea7:0907) as a hidraw device.
        /// Looked up straight in sysfs so it doesn't depend on HidSharp's hotplug thread.
        /// </summary>
        private static bool KeyboardPresentInSysfs()
        {
            try
            {
                const string root = "/sys/class/hidraw";
                if (!Directory.Exists(root))
                {
                    return false;
                }
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string uevent = Path.Combine(dir, "device", "uevent");
                    if (!File.Exists(uevent))
                    {
                        continue;
                    }
                    foreach (string line in File.ReadAllLines(uevent))
                    {
                        if (line.StartsWith("HID_ID=", StringComparison.Ordinal) &&
                            line.EndsWith(":00001EA7:00000907", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// HidSharp's hotplug thread can wedge on Linux (spins at 100% CPU and never reports devices again), so after
        /// unplugging and replugging the keyboard it was never picked up. Every 2s: if the keyboard is there but not
        /// attached, rescan by hand; if that doesn't help for ~20s, restart the daemon (systemd brings it back).
        /// </summary>
        private void Watchdog(object state)
        {
            if (shutdown.WaitOne(0))
            {
                return;
            }
            lock (locker)
            {
                if (device != null && !device.IsClosed)
                {
                    missingTicks = 0;
                    return;
                }
            }
            if (!KeyboardPresentInSysfs())
            {
                missingTicks = 0;
                return;
            }
            try
            {
                KeyboardDeviceManager.RefreshConnectedDevices();
            }
            catch (Exception e)
            {
                Program.Log("Realtime: rescan failed: " + e.Message);
            }
            Thread.Sleep(1000);
            lock (locker)
            {
                if (device != null && !device.IsClosed)
                {
                    missingTicks = 0;
                    return;
                }
            }
            if (++missingTicks >= 10)
            {
                Program.Log("Realtime: keyboard is plugged in but wasn't picked up, restarting the daemon");
                RestartRequested = true;
                shutdown.Set();
            }
        }

        private void AttachEngine(KeyboardState keyboardState, IFrameSink sink)
        {
            engine = new LightingEngine(keyboardState, sink);
            engine.Fps = Fps;
            engine.SetSettings(settings);
            engine.SettingsChanged += QueueSave;
            engine.Start();
        }

        private void OnConnected(KeyboardDevice dev)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (locker)
                {
                    if (device != null && !device.IsClosed)
                    {
                        return;
                    }
                    try
                    {
                        Program.Log("Realtime: entering driver layer on '" + dev.State.ModelName + "'");
                        dev.SetLayer(KeyboardLayer.Driver);
                        if (DriverKeys == DriverKeysMode.Default)
                        {
                            dev.SetDriverKeyTable(BuildDefaultDriverKeyTable(dev.State));
                        }
                        device = dev;
                        AttachEngine(dev.State, new DeviceFrameSink(dev));
                        engine.Failed += OnEngineFailed;
                        if (UseKeyMonitor)
                        {
                            monitor = new KeyPressMonitor(dev, engine);
                            monitor.Start();
                        }
                    }
                    catch (Exception e)
                    {
                        Program.Log("Realtime: failed to start on the keyboard: " + e);
                    }
                }
            });
        }

        private void OnEngineFailed(LightingEngine failed)
        {
            ThreadPool.QueueUserWorkItem(delegate { Detach(false); });
        }

        private void OnDisconnected(KeyboardDevice dev)
        {
            lock (locker)
            {
                if (device != dev)
                {
                    return;
                }
            }
            Program.Log("Realtime: keyboard disconnected");
            Detach(false);
        }

        private void Detach(bool restoreLayer)
        {
            LightingEngine e;
            KeyboardDevice d;
            KeyPressMonitor m;
            lock (locker)
            {
                e = engine; d = device; m = monitor;
                engine = null; device = null; monitor = null;
                if (e != null)
                {
                    settings = e.GetSettings();
                }
            }
            if (m != null) m.Dispose();
            if (e != null) e.Stop();
            if (d != null && restoreLayer && !d.IsClosed)
            {
                try { d.SetLayer(KeyboardLayer.Base); } catch { }
            }
            else if (d != null && !restoreLayer)
            {
                // Drop the stale handle: the device manager keeps it registered (and ignores the keyboard when it
                // comes back) until it's closed
                try { d.Close(); } catch { }
            }
        }

        /// <summary>
        /// Every key set to its default (unmapped) value, so typing keeps working in the driver layer.
        /// </summary>
        public static uint[] BuildDefaultDriverKeyTable(KeyboardState state)
        {
            uint[] table = new uint[Math.Max(state.MaxLogicCode, 255)];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = KeyValues.UnusedKeyValue;
            }
            foreach (KeyboardState.Key key in state.KeysByLogicCode.Values)
            {
                if (key.LogicCode >= 0 && key.LogicCode < table.Length && key.DriverValue != KeyValues.UnusedKeyValue)
                {
                    table[key.LogicCode] = key.DriverValue;
                }
            }
            return table;
        }

        /// <summary>
        /// Writes the saved profile to the keyboard (key maps, macros, lighting of the given layers) and goes back to the
        /// driver layer so realtime lighting keeps working. Returns an error message or null on success.
        /// </summary>
        public string ApplyProfile(IEnumerable<KeyboardLayer> layers, out int failedPackets)
        {
            failedPackets = 0;
            LightingEngine e;
            KeyboardDevice d;
            lock (locker)
            {
                e = engine;
                d = device;
            }
            if (e == null)
            {
                return "No keyboard connected.";
            }
            if (d == null)
            {
                return "Simulated keyboard: nothing was written.";
            }
            e.Paused = true;
            try
            {
                string error;
                int failed = ConfigService.Apply(d, layers, out error);
                if (error != null)
                {
                    return error;
                }
                failedPackets = failed;
                // The keyboard reloads the layer data when the layer changes
                d.SetLayer(KeyboardLayer.Base);
                d.SetLayer(KeyboardLayer.Driver);
                if (DriverKeys == DriverKeysMode.Default)
                {
                    d.SetDriverKeyTable(BuildDefaultDriverKeyTable(d.State));
                }
                return failed > 0 ? "The keyboard did not acknowledge " + failed + " packet(s)." : null;
            }
            finally
            {
                e.Paused = false;
            }
        }

        public void RequestShutdown()
        {
            shutdown.Set();
        }

        public void WaitForShutdown()
        {
            shutdown.WaitOne();
        }

        public void Stop()
        {
            if (watchdogTimer != null) watchdogTimer.Dispose();
            KeyboardDeviceManager.Connected -= OnConnected;
            KeyboardDeviceManager.Disconnected -= OnDisconnected;
            settingsDirty = true;
            SaveSettingsTick(null);
            Detach(true);
            KeyboardDeviceManager.StopListener();
        }
    }

    /// <summary>
    /// Tiny local web server: serves the control page and a json api (127.0.0.1 only).
    /// </summary>
    public class ControlServer
    {
        private readonly RealtimeDaemon daemon;
        private readonly int port;
        private HttpListener listener;
        private Thread thread;
        private string webRoot;

        public ControlServer(RealtimeDaemon daemon, int port, string webRoot)
        {
            this.daemon = daemon;
            this.port = port;
            this.webRoot = webRoot;
        }

        public string Url { get { return "http://127.0.0.1:" + port + "/"; } }

        public void Start()
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.Prefixes.Add("http://localhost:" + port + "/");
            listener.Start();
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "GK6X control server";
            thread.Start();
        }

        public void Stop()
        {
            try { listener.Stop(); } catch { }
        }

        private void Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch
                {
                    return;
                }
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
        }

        private static bool IsLocalHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            int colon = host.LastIndexOf(':');
            if (colon > 0) host = host.Substring(0, colon);
            return host == "127.0.0.1" || host == "localhost";
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath;
                // Guard against DNS rebinding / cross site requests from web pages
                if (!IsLocalHost(ctx.Request.Headers["Host"]))
                {
                    Respond(ctx, 403, "text/plain", "Forbidden");
                    return;
                }
                if (ctx.Request.HttpMethod == "POST")
                {
                    string type = ctx.Request.ContentType ?? string.Empty;
                    if (!type.StartsWith("application/json"))
                    {
                        Respond(ctx, 415, "text/plain", "Content-Type must be application/json");
                        return;
                    }
                }

                if (ctx.Request.HttpMethod == "GET" && (path == "/" || path == "/index.html"))
                {
                    string file = Path.Combine(webRoot, "index.html");
                    if (File.Exists(file))
                    {
                        Respond(ctx, 200, "text/html; charset=utf-8", File.ReadAllText(file));
                    }
                    else
                    {
                        Respond(ctx, 404, "text/plain", "Missing " + file);
                    }
                    return;
                }
                if (ctx.Request.HttpMethod == "GET" && path == "/api/state")
                {
                    Respond(ctx, 200, "application/json", Json.Serialize(BuildState()));
                    return;
                }
                if (path == "/favicon.ico")
                {
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                    return;
                }
                if (HandleConfig(ctx, path))
                {
                    return;
                }
                if (ctx.Request.HttpMethod == "POST" && path == "/api/settings")
                {
                    Dictionary<string, object> body = ReadBody(ctx);
                    LightingEngine e = daemon.Engine;
                    string leName;
                    if (e != null && Json.TryGetValue(body, "effect", out leName) && leName == "le")
                    {
                        string requested;
                        if (!Json.TryGetValue(body, "le", out requested)) requested = daemon.Settings.Le;
                        if (!e.CanPlay(requested))
                        {
                            throw new FormatException("Lighting file '" + requested + "' can't be loaded for this keyboard");
                        }
                    }
                    daemon.Update(s => LightingSettingsFile.Apply(s, body));
                    Respond(ctx, 200, "application/json", Json.Serialize(BuildState()));
                    return;
                }
                if (ctx.Request.HttpMethod == "POST" && path == "/api/paint")
                {
                    Dictionary<string, object> body = ReadBody(ctx);
                    ApplyPaint(body);
                    Respond(ctx, 200, "application/json", Json.Serialize(BuildState()));
                    return;
                }
                if (ctx.Request.HttpMethod == "POST" && path == "/api/shutdown")
                {
                    Respond(ctx, 200, "application/json", "{\"ok\":true}");
                    daemon.RequestShutdown();
                    return;
                }
                Respond(ctx, 404, "text/plain", "Not found");
            }
            catch (FormatException e)
            {
                Respond(ctx, 400, "application/json", Json.Serialize(new Dictionary<string, object> { { "error", e.Message } }));
            }
            catch (Exception e)
            {
                Program.Log("Control server error: " + e);
                Respond(ctx, 500, "application/json", Json.Serialize(new Dictionary<string, object> { { "error", e.Message } }));
            }
        }

        private static string Json400(string message)
        {
            return Json.Serialize(new Dictionary<string, object> { { "error", message } });
        }

        /// <summary>Profile / palette / lighting-file endpoints. Returns true when the request was handled.</summary>
        private bool HandleConfig(HttpListenerContext ctx, string path)
        {
            string method = ctx.Request.HttpMethod;
            if (!path.StartsWith("/api/keyset") && !path.StartsWith("/api/usage") && !path.StartsWith("/api/profile") &&
                !path.StartsWith("/api/apply") && !path.StartsWith("/api/le"))
            {
                return false;
            }
            LightingEngine engine = daemon.Engine;
            KeyboardState keyboard = engine != null ? engine.State : null;

            if (method == "GET" && path == "/api/keyset")
            {
                Respond(ctx, 200, "application/json", Json.Serialize(ConfigService.BuildKeyset()));
                return true;
            }
            if (method == "GET" && path == "/api/usage")
            {
                Respond(ctx, 200, "application/json", Json.Serialize(ConfigService.BuildUsageNames()));
                return true;
            }
            if (method == "GET" && path == "/api/le")
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["items"] = ConfigService.ListLightingFiles();
                Respond(ctx, 200, "application/json", Json.Serialize(d));
                return true;
            }
            if (keyboard == null)
            {
                Respond(ctx, 409, "application/json", Json400("No keyboard connected."));
                return true;
            }

            if (method == "GET" && path == "/api/profile")
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["path"] = ConfigService.ProfilePath(keyboard);
                d["exists"] = File.Exists(ConfigService.ProfilePath(keyboard));
                d["profile"] = ConfigService.LoadProfile(keyboard).ToJson();
                Respond(ctx, 200, "application/json", Json.Serialize(d));
                return true;
            }
            if (method == "POST" && path == "/api/profile")
            {
                ProfileModel model = ProfileModel.FromJson(ReadBody(ctx));
                List<string> errors = model.Validate(keyboard);
                if (errors.Count > 0)
                {
                    Respond(ctx, 400, "application/json", Json400(string.Join("; ", errors.Take(5))));
                    return true;
                }
                ConfigService.SaveProfile(keyboard, model);
                Respond(ctx, 200, "application/json", "{\"ok\":true}");
                return true;
            }
            if (method == "GET" && path == "/api/profile/export")
            {
                string file = ConfigService.ProfilePath(keyboard);
                ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"gk6x-" + keyboard.ModelId + ".txt\"";
                Respond(ctx, 200, "text/plain; charset=utf-8", File.Exists(file) ? File.ReadAllText(file) : string.Empty);
                return true;
            }
            if (method == "POST" && path == "/api/profile/import")
            {
                string text;
                if (!Json.TryGetValue(ReadBody(ctx), "text", out text))
                {
                    throw new FormatException("Missing 'text'");
                }
                ProfileModel model = ProfileModel.Parse(text);
                List<string> errors = model.Validate(keyboard);
                if (errors.Count > 0)
                {
                    Respond(ctx, 400, "application/json", Json400("Import rejected: " + string.Join("; ", errors.Take(5))));
                    return true;
                }
                ConfigService.SaveProfile(keyboard, model);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["profile"] = model.ToJson();
                Respond(ctx, 200, "application/json", Json.Serialize(d));
                return true;
            }
            if (method == "POST" && path == "/api/apply")
            {
                Dictionary<string, object> body = ReadBody(ctx);
                List<object> names;
                List<KeyboardLayer> layers = new List<KeyboardLayer>();
                if (Json.TryGetValue(body, "layers", out names))
                {
                    foreach (string n in names.OfType<string>())
                    {
                        KeyboardLayer layer;
                        if (Enum.TryParse(n, true, out layer) && layer != KeyboardLayer.Driver) layers.Add(layer);
                    }
                }
                if (layers.Count == 0)
                {
                    throw new FormatException("No layers selected");
                }
                int failed;
                string error = daemon.ApplyProfile(layers, out failed);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = error == null;
                d["failed"] = failed;
                if (error != null) d["error"] = error;
                Respond(ctx, error == null ? 200 : 500, "application/json", Json.Serialize(d));
                return true;
            }
            if (method == "POST" && path == "/api/le/save")
            {
                Dictionary<string, object> body = ReadBody(ctx);
                string name;
                if (!Json.TryGetValue(body, "name", out name))
                {
                    throw new FormatException("Missing 'name'");
                }
                Dictionary<int, uint> colors = new Dictionary<int, uint>();
                Dictionary<string, object> given;
                if (Json.TryGetValue(body, "colors", out given))
                {
                    foreach (KeyValuePair<string, object> kv in given)
                    {
                        int loc;
                        if (int.TryParse(kv.Key, out loc) && kv.Value is string) colors[loc] = Colors.Parse((string)kv.Value);
                    }
                }
                else
                {
                    colors = new Dictionary<int, uint>(daemon.Settings.Paint);
                }
                if (colors.Count == 0)
                {
                    throw new FormatException("No colored keys to save");
                }
                ConfigService.SaveStaticLighting(keyboard, name.Trim(), colors);
                Respond(ctx, 200, "application/json", Json.Serialize(new Dictionary<string, object> { { "ok", true }, { "name", name.Trim() } }));
                return true;
            }
            if (method == "POST" && path == "/api/le/delete")
            {
                string name;
                Json.TryGetValue(ReadBody(ctx), "name", out name);
                bool ok = ConfigService.DeleteUserLighting(name);
                Respond(ctx, ok ? 200 : 404, "application/json", ok ? "{\"ok\":true}" : Json400("Only your own lighting files can be deleted"));
                return true;
            }
            return false;
        }

        private void ApplyPaint(Dictionary<string, object> body)
        {
            bool clear = false;
            Json.TryGetValue(body, "clear", out clear);
            Dictionary<string, object> keys;
            Json.TryGetValue(body, "keys", out keys);
            LightingEngine engine = daemon.Engine;
            // Resolve names -> locations first (this needs the keyboard layout)
            Dictionary<int, uint?> changes = new Dictionary<int, uint?>();
            if (keys != null)
            {
                foreach (KeyValuePair<string, object> kv in keys)
                {
                    List<int> locations = engine != null ? engine.FindLocations(kv.Key) : new List<int>();
                    if (locations.Count == 0)
                    {
                        throw new FormatException("Unknown key '" + kv.Key + "'");
                    }
                    uint? color = null;
                    if (kv.Value is string && !string.Equals((string)kv.Value, "off", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals((string)kv.Value, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        color = Colors.Parse((string)kv.Value);
                    }
                    foreach (int loc in locations)
                    {
                        changes[loc] = color;
                    }
                }
            }
            daemon.Update(s =>
            {
                if (clear)
                {
                    s.Paint.Clear();
                }
                foreach (KeyValuePair<int, uint?> change in changes)
                {
                    if (change.Value.HasValue)
                    {
                        s.Paint[change.Key] = change.Value.Value;
                    }
                    else
                    {
                        s.Paint.Remove(change.Key);
                    }
                }
                // Painting keys only makes sense when something shows them
                if (changes.Count > 0 && s.Effect == "off")
                {
                    s.Effect = "custom";
                }
            });
        }

        private Dictionary<string, object> BuildState()
        {
            LightingEngine engine = daemon.Engine;
            LightingSettings s = daemon.Settings;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["connected"] = engine != null;
            d["simulated"] = daemon.IsSimulated;
            d["effects"] = LightingEngine.EffectNames.Cast<object>().ToList();
            d["settings"] = Json.Deserialize(LightingSettingsFile.ToJson(s));
            d["keyMonitor"] = daemon.HasMonitor;
            if (engine != null)
            {
                d["model"] = engine.State.ModelName;
                d["modelId"] = engine.State.ModelId;
                d["firmware"] = engine.State.FirmwareVersion;
                d["fps"] = Math.Round(engine.MeasuredFps, 1);
                d["framesSent"] = engine.FramesSent;
                d["framesFailed"] = engine.FramesFailed;
                d["keys"] = engine.DescribeKeys().Cast<object>().ToList();
            }
            else
            {
                d["keys"] = new List<object>();
            }
            return d;
        }

        private static Dictionary<string, object> ReadBody(HttpListenerContext ctx)
        {
            using (StreamReader reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                string text = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new Dictionary<string, object>();
                }
                Dictionary<string, object> result = Json.Deserialize(text) as Dictionary<string, object>;
                if (result == null)
                {
                    throw new FormatException("Expected a json object");
                }
                return result;
            }
        }

        private static void Respond(HttpListenerContext ctx, int status, string contentType, string body)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.Headers["Cache-Control"] = "no-store";
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
            }
        }
    }
}
