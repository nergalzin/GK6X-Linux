using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace GK6X
{
    class Program
    {
        public static string BasePath;
        public static string DataBasePath = "Data";
        public static string UserDataPath = "UserData";
        public static string UserFileNamePrefix;
        public static string UserFileName;
        public static string AutoRunCommand;

        static void Main(string[] args)
        {
            if (Cli.IsCommand(args))
            {
                Environment.Exit(Cli.Run(args));
                return;
            }
            if (args.Length > 0) {
                string flag = args[0].ToLower();
                
                switch(flag)
                {
                    case "/p":  //filename prefix
                        if (args.Length>1)
                        {
                            UserFileNamePrefix = args[1];
                            Console.WriteLine("Filename Prefix: " + UserFileNamePrefix);
                        }
                        break;
                    case "/map":
                    case "/unmap":
                    case "/dumpkeys":
                    case "/update_data":
                        AutoRunCommand = flag.Substring(1);
                        if (args.Length > 1)
                        {
                            UserFileName = args[1];
                            Console.WriteLine("Filename: " + UserFileName);
                        }
                        break;
                    case "/f":
                        if (args.Length > 1)
                        {
                            UserFileName = args[1];
                            Console.WriteLine("Filename: " + UserFileName);
                        }
                        break;
                }

            }
            Run();
            Stop();
        }

        static void Stop()
        {
            KeyboardDeviceManager.StopListener();
            Environment.Exit(0);// Ensure any loose threads die...
        }

        private static bool initialized;

        /// <summary>
        /// Resolves the data / user data / log folders and loads the static data files.
        /// Layout on Linux (when installed): data next to the program, user files under ~/.config/gk6x,
        /// logs under ~/.local/state/gk6x. Put a file called "portable.txt" next to the program to keep
        /// everything in the program folder instead.
        /// </summary>
        internal static bool Initialize()
        {
            if (initialized)
            {
                return true;
            }
            BasePath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string dataOverride = Environment.GetEnvironmentVariable("GK6X_DATA");
            DataBasePath = !string.IsNullOrEmpty(dataOverride) ? dataOverride : Path.Combine(BasePath, DataBasePath);
            bool portable = File.Exists(Path.Combine(BasePath, "portable.txt")) ||
                !(Environment.OSVersion.Platform == PlatformID.Unix);
            if (portable)
            {
                UserDataPath = Path.Combine(BasePath, UserDataPath);
                LogDirectory = BasePath;
            }
            else
            {
                string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(config)) config = Path.Combine(home, ".config");
                string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
                if (string.IsNullOrEmpty(state)) state = Path.Combine(home, ".local", "state");
                ConfigPath = Path.Combine(config, "gk6x");
                UserDataPath = Path.Combine(ConfigPath, "UserData");
                LogDirectory = Path.Combine(state, "gk6x");
                try
                {
                    Directory.CreateDirectory(UserDataPath);
                    Directory.CreateDirectory(LogDirectory);
                    // First run: provide the sample configs next to the user's own files
                    string sampleDir = Path.Combine(BasePath, "UserData");
                    if (Directory.Exists(sampleDir))
                    {
                        foreach (string file in Directory.GetFiles(sampleDir))
                        {
                            string target = Path.Combine(UserDataPath, Path.GetFileName(file));
                            if (!File.Exists(target) && Path.GetFileName(file).Contains("Sample"))
                            {
                                File.Copy(file, target);
                            }
                        }
                    }
                }
                catch
                {
                }
            }
            if (ConfigPath == null)
            {
                ConfigPath = BasePath;
            }

            if (!Localization.Load())
            {
                LogFatalError("Failed to load localization data");
                return false;
            }
            if (!KeyValues.Load())
            {
                LogFatalError("Failed to load the key data");
                return false;
            }
            if (!KeyboardState.Load())
            {
                LogFatalError("Failed to load keyboard data");
                return false;
            }
            initialized = true;
            return true;
        }

        public static string ConfigPath;
        public static string LogDirectory;

        /// <summary>Lighting effects (.le) created by the user live in UserData/lighting.</summary>
        public static string UserLightingPath
        {
            get { return Path.Combine(UserDataPath, "lighting"); }
        }

        /// <summary>Finds a lighting effect file by name (user files win over the bundled ones).</summary>
        public static string FindLightingFile(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains(".."))
            {
                return null;
            }
            string user = Path.Combine(UserLightingPath, name + ".le");
            if (File.Exists(user))
            {
                return user;
            }
            string bundled = Path.Combine(DataBasePath, "lighting", name + ".le");
            return File.Exists(bundled) ? bundled : null;
        }

        static void Run()
        {
            if (!Initialize())
            {
                return;
            }

#if COMMAND_LOGGER_ENABLED
            if (args.Length > 0 && args[0].ToLower() == "clog")
            {
                CommandLogger.Run();
                return;
            }
#endif

            KeyboardDeviceManager.Connected += (KeyboardDevice device) =>
            {
                Log("Connected to device '" + device.State.ModelName + "' model:" + device.State.ModelId +
                    " fw:" + device.State.FirmwareVersion);

                string file = GetUserDataFile(device);

                if (!string.IsNullOrEmpty(file))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(file);
                        if (!Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        if (!File.Exists(file))
                        {
                            File.WriteAllText(file, string.Empty, Encoding.UTF8);
                        }
                    }
                    catch
                    {
                    }
                }
            };
            KeyboardDeviceManager.Disconnected += (KeyboardDevice device) =>
            {
                Log("Disconnected from device '" + device.State.ModelName + "'");
            };
            KeyboardDeviceManager.StartListener();
            
            bool running = true;
            bool hasNullInput = false;

            while (running)
            {
                string line;
                if (!string.IsNullOrEmpty(AutoRunCommand))
                    line = AutoRunCommand;
                else
                    line = Console.ReadLine();
                if (line == null)
                {
                    // Handler for potential issue where ReadLine() returns null - see https://github.com/pixeltris/GK6X/issues/8
                    if (hasNullInput)
                    {
                        Console.WriteLine("Cannot read from command line. Exiting.");
                        break;
                    }
                    hasNullInput = true;
                    continue;
                }
                hasNullInput = false;
                string[] splitted = line.Split();
                switch (splitted[0].ToLower())
                {
                    case "close":
                    case "exit":
                    case "quit":
                        running = false;
                        break;
                    case "cls":
                    case "clear":
                        Console.Clear();
                        break;
                    case "update_data":
                        if (splitted.Length > 1)
                        {
                            string path = line.TrimStart();
                            int spaceChar = path.IndexOf(' ');
                            if (spaceChar > 0)
                            {
                                path = path.Substring(spaceChar).Trim();
                            }
                            bool isValidPath = false;
                            try
                            {
                                if (Directory.Exists(path))
                                {
                                    isValidPath = true;
                                }
                            }
                            catch
                            {
                            }
                            if (isValidPath)
                            {
                                UpdateDataFiles(path);
                                Log("done");
                            }
                            else
                            {
                                Log("Couldn't find path '" + path + "'");
                            }
                        }
                        else
                        {
                            Log("Bad input. Expected folder name.");
                        }
                        break;
                    case "findkeys":
                        {
                            Log(string.Empty);
                            Log("This is used to identify keys. Press keys to see their values. Missing keys will generally show up as '(null)' and they need to be mapped in the data files Data/devuces/YOUR_MODEL_ID/");
                            Log("The 'S' values are what you want to use to map keys in your UserData file.");
                            Log(string.Empty);
                            Log("Entering 'driver' mode and mapping all keys to callbacks.");
                            Log(string.Empty);
                            KeyboardDevice[] devices;
                            if (TryGetDevices(out devices))
                            {
                                foreach (KeyboardDevice device in devices)
                                {
                                    device.SetLayer(KeyboardLayer.Driver);
                                    device.SetIdentifyDriverMacros();
                                }
                            }
                        }
                        break;
                    case "map":
                    case "unmap":
                        {
                            bool map = splitted[0].ToLower() == "map";
                            KeyboardDevice[] devices;
                            KeyboardLayer targetLayer;
                            bool targetLayerIsFn;
                            TryParseLayer(splitted, 1, out targetLayer, out targetLayerIsFn);
                            bool hasTargetLayer = targetLayer != KeyboardLayer.Invalid;
                            if (TryGetDevices(out devices))
                            {
                                foreach (KeyboardDevice device in devices)
                                {
                                    UserDataFile userData = UserDataFile.Load(device.State, GetUserDataFile(device));
                                    if (userData == null)
                                    {
                                        Log("Couldn't find user data file '" + GetUserDataFile(device) + "'");
                                        continue;
                                    }

                                    foreach (KeyValuePair<KeyboardLayer, KeyboardStateLayer> layer in device.State.Layers)
                                    {
                                        if (layer.Key == KeyboardLayer.Driver)
                                        {
                                            continue;
                                        }

                                        if (hasTargetLayer && layer.Key != targetLayer)
                                        {
                                            continue;
                                        }

                                        device.SetLighting(layer.Key, userData);
                                        device.SetMacros(layer.Key, userData);

                                        for (int i = 0; i < 2; i++)
                                        {
                                            bool fn = i == 1;
                                            if (targetLayer != KeyboardLayer.Invalid && fn != targetLayerIsFn)
                                            {
                                                continue;
                                            }

                                            // Setting keys to 0xFFFFFFFF is preferable compared to using what is defined in 
                                            // files as this will use what is defined in the firmware.
                                            uint[] driverValues = new uint[device.State.MaxLogicCode];
                                            for (int j = 0; j < driverValues.Length; j++)
                                            {
                                                driverValues[j] = KeyValues.UnusedKeyValue;
                                            }

                                            if (map)
                                            {
                                                UserDataFile.Layer userDataLayer;
                                                if (fn)
                                                {
                                                    userData.FnLayers.TryGetValue(layer.Key, out userDataLayer);
                                                }
                                                else
                                                {
                                                    userData.Layers.TryGetValue(layer.Key, out userDataLayer);
                                                }
                                                if (userDataLayer != null)
                                                {
                                                    uint allValue;
                                                    if (userDataLayer.Keys.TryGetValue("all", out allValue))
                                                    {
                                                        for (int j = 0; j < driverValues.Length; j++)
                                                        {
                                                            driverValues[j] = allValue;
                                                        }
                                                    }
                                                    for (int j = 0; j < driverValues.Length; j++)
                                                    {
                                                        KeyboardState.Key key = device.State.GetKeyByLogicCode(j);
                                                        if (key != null)
                                                        {
                                                            uint keyValue = userDataLayer.GetKey(key);
                                                            if (keyValue != KeyValues.UnusedKeyValue)
                                                            {
                                                                driverValues[j] = keyValue;
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                            device.SetKeys(layer.Key, driverValues, fn);
                                        }
                                    }

                                    // This is required to "refresh" the keyboard with the updated key info
                                    if (hasTargetLayer)
                                    {
                                        device.SetLayer(targetLayer);
                                    }
                                    else
                                    {
                                        device.SetLayer(KeyboardLayer.Base);
                                    }
                                    Log("Done");
                                }
                            }
                        }
                        break;
                    case "dumpkeys":
                        {
                            int targetRow = -1;
                            if (splitted.Length > 1)
                            {
                                if (!int.TryParse(splitted[1], out targetRow))
                                {
                                    targetRow = -1;
                                }
                            }
                            bool showLocationCodeInfo = false;
                            if (splitted.Length > 2)
                            {
                                showLocationCodeInfo = splitted[2] == "ex";
                            }

                            KeyboardDevice[] devices;
                            if (TryGetDevices(out devices))
                            {
                                foreach (KeyboardDevice device in devices)
                                {
                                    Log("====== " + device.State.ModelId + " ======");
                                    bool foundKey = false;
                                    int lastLeft = int.MinValue;
                                    int row = 1;
                                    foreach (KeyboardState.Key key in device.State.KeysByLocationCode.Values.OrderBy(
                                        x => x.Position.Top).ThenBy(x => x.Position.Left))
                                    {
                                        if (key.Position.Left >= 0)
                                        {
                                            if (lastLeft > key.Position.Left && foundKey)
                                            {
                                                if (targetRow == -1)
                                                {
                                                    Log("--------");
                                                }
                                                foundKey = false;
                                                row++;
                                            }
                                            lastLeft = key.Position.Left;
                                        }

                                        if (string.IsNullOrEmpty(key.KeyName) || !key.KeyName.StartsWith("LED-"))
                                        {
                                            if (targetRow == -1 || row == targetRow)
                                            {
                                                Log(key.KeyName + " = " + key.DriverValueName +
                                                    (showLocationCodeInfo ? " (" + key.LocationCode + ")" : string.Empty));
                                            }
                                            foundKey = true;
                                        }
                                    }
                                }
                            }
                        }
                        break;
                }
                if (!string.IsNullOrEmpty(AutoRunCommand))
                    running = false;
            }
        }

        private static string GetUserDataFile(KeyboardDevice device)
        {
            if (!string.IsNullOrEmpty(UserFileName))
                return Path.Combine(UserDataPath, UserFileName.Trim() + ".txt");
            else if (!string.IsNullOrEmpty(UserFileNamePrefix))
              return Path.Combine(UserDataPath, UserFileNamePrefix.Trim() + " - " + device.State.ModelId + ".txt");
            else
              return Path.Combine(UserDataPath, device.State.ModelId + ".txt");
        }

        private static bool TryGetDevices(out KeyboardDevice[] devices)
        {
            devices = KeyboardDeviceManager.GetConnectedDevices();
            if (devices.Length > 0)
            {
                return true;
            }
            else
            {
                Log("No devices connected!");
                return false;
            }
        }

        private static bool TryParseLayer(string[] args, int index, out KeyboardLayer layer, out bool fn)
        {
            layer = KeyboardLayer.Invalid;
            fn = false;
            if (args.Length < index)
            {
                string arg = args[index];
                if (arg.ToLower().StartsWith("fn"))
                {
                    arg = arg.Substring(2);
                    fn = true;
                }
                int layerVal;
                if (int.TryParse(args[index], out layerVal))
                {
                    switch (layerVal)
                    {
                        case 1: layer = KeyboardLayer.Layer1; break;
                        case 2: layer = KeyboardLayer.Layer2; break;
                        case 3: layer = KeyboardLayer.Layer3; break;
                    }
                }
                else
                {
                    Enum.TryParse(args[index], true, out layer);
                }
            }
            switch (layer)
            {
                case KeyboardLayer.Driver:
                    layer = KeyboardLayer.Invalid;
                    break;
            }
            return layer != KeyboardLayer.Invalid;
        }

        static object logLocker = new object();
        internal static void Log(string str)
        {
            lock (logLocker)
            {
                try
                {
                    File.AppendAllText(Path.Combine(LogDirectory ?? BasePath ?? ".", "KbLog.txt"), "[" + DateTime.Now.TimeOfDay + "] " + str + Environment.NewLine);
                }
                catch
                {
                    // Never fail because the log can't be written (read only install folder, etc)
                }
                Console.WriteLine(str);
            }
        }

        private static void LogFatalError(string str)
        {
            Log(str);
            Console.ReadLine();
            Environment.Exit(1);
        }

        internal static string GetDriverDir(string dir)
        {
            string rootDir = Path.Combine(dir, "GK6XPlus Driver");
            if (Directory.Exists(rootDir))
            {
                dir = rootDir;
            }
            string engineDir = Path.Combine(dir, "CMSEngine");
            if (Directory.Exists(engineDir))
            {
                dir = engineDir;
            }
            string driverDir = Path.Combine(dir, "driver");
            if (Directory.Exists(driverDir))
            {
                dir = driverDir;
            }
            string deviceDir = Path.Combine(dir, "device");
            if (Directory.Exists(deviceDir) && File.Exists(Path.Combine(deviceDir, "modellist.json")))
            {
                return dir;
            }
            return null;
        }

        private static void ReadModelList(string file, Dictionary<string, object> models)
        {
            if (File.Exists(file))
            {
                List<object> objs = MiniJSON.Json.Deserialize(File.ReadAllText(file)) as List<object>;
                if (objs != null)
                {
                    foreach (object obj in objs)
                    {
                        Dictionary<string, object> dict = obj as Dictionary<string, object>;
                        if (dict != null && dict.ContainsKey("ModelID"))
                        {
                            models[dict["ModelID"].ToString()] = dict;
                        }
                    }
                }
            }
        }

        private static void UpdateDataFiles(string srcDir)
        {
            List<string> additionalDirs = new List<string>();
            additionalDirs.Add("Tronsmart Radiant");
            additionalDirs.Add("Kemove Driver");
            for (int i = 0; i < additionalDirs.Count; )
            {
                string fullPath = Path.Combine(srcDir, additionalDirs[i]);
                if (Directory.Exists(fullPath))
                {
                    additionalDirs[i++] = GetDriverDir(fullPath);
                }
                else
                {
                    additionalDirs.RemoveAt(i);
                }
            }

            // Create a merged 'driver' directory containing all the data from all distributors
            string combinedDriverDir = Path.Combine(srcDir, "driver_combined");

            srcDir = GetDriverDir(srcDir);
            if (string.IsNullOrEmpty(srcDir))
            {
                return;
            }

            if (!Directory.Exists(combinedDriverDir))
            {
                Directory.CreateDirectory(combinedDriverDir);
            }

            string dstDir = Path.Combine(Program.BasePath, "Data");
            string leDir = Path.Combine(srcDir, "res", "data", "le");
            string deviceDir = Path.Combine(srcDir, "device");
            string modelListFile = Path.Combine(deviceDir, "modellist.json");

            // Format these files manually (https://beautifier.io/)
            string indexJsFile = Path.Combine(srcDir, "index.formatted.js");
            string zeroJsFile = Path.Combine(srcDir, "0.formatted.js");
            if (!File.Exists(indexJsFile) || !File.Exists(zeroJsFile))
            {
                Log("Couldn't find formatted js files to process!");
                return;
            }

            if (File.Exists(indexJsFile) && File.Exists(zeroJsFile) && Directory.Exists(leDir) && Directory.Exists(deviceDir) && File.Exists(modelListFile))
            {
                Dictionary<string, object> models = new Dictionary<string, object>();

                foreach (string additionalDir in additionalDirs)
                {
                    string additionalLeDir = Path.Combine(additionalDir, "res", "data", "le");
                    if (Directory.Exists(additionalLeDir))
                    {
                        CMFile.DumpLighting(additionalLeDir, Path.Combine(dstDir, "lighting"));
                    }

                    string additionalDeviceDir = Path.Combine(additionalDir, "device");
                    if (Directory.Exists(additionalDeviceDir))
                    {
                        CopyFilesRecursively(new DirectoryInfo(additionalDeviceDir), new DirectoryInfo(Path.Combine(dstDir, "device")), false);
                        string additionalModelListFile = Path.Combine(additionalDeviceDir, "modellist.json");
                        if (File.Exists(additionalModelListFile))
                        {
                            ReadModelList(additionalModelListFile, models);
                        }
                    }

                    //CopyFilesRecursively(new DirectoryInfo(additionalDir), new DirectoryInfo(combinedDriverDir), true);
                    if (Directory.Exists(additionalDeviceDir))
                    {
                        CopyFilesRecursively(new DirectoryInfo(additionalDeviceDir), new DirectoryInfo(Path.Combine(combinedDriverDir, "device")), true);
                    }
                }
                CMFile.DumpLighting(leDir, Path.Combine(dstDir, "lighting"));
                CopyFilesRecursively(new DirectoryInfo(deviceDir), new DirectoryInfo(Path.Combine(dstDir, "device")), false);

                // TODO: Merge json files in /res/data/le/ and /res/data/macro/
                CopyFilesRecursively(new DirectoryInfo(srcDir), new DirectoryInfo(combinedDriverDir), true);

                // Combine modellist.json files
                ReadModelList(modelListFile, models);
                File.WriteAllText(Path.Combine(dstDir, "device", "modellist.json"), CMFile.FormatJson(MiniJSON.Json.Serialize(models.Values.ToList())));

                string langDir = Path.Combine(dstDir, "i18n", "langs");
                Directory.CreateDirectory(langDir);

                string indexJs = File.ReadAllText(indexJsFile);
                int commonIndex = 0;
                for (int i = 0; i < 2; i++)
                {
                    string langStr = FindContent(indexJs, "common: {", '{', '}', ref commonIndex);
                    if (!string.IsNullOrEmpty(langStr))
                    {
                        File.WriteAllText(Path.Combine(langDir, (i == 0 ? "en" : "zh") + ".json"), langStr);
                    }
                }

                string zeroJs = File.ReadAllText(zeroJsFile);
                string keysStr = FindContent(zeroJs, "el-icon-kb-keyboard", '[', ']');
                if (!string.IsNullOrEmpty(keysStr))
                {
                    File.WriteAllText(Path.Combine(dstDir, "keys.json"), keysStr);
                }
            }
            else
            {
                Log("Missing directory / file!");
            }
        }

        static string FindContent(string str, string header, char openBraceChar, char closeBraceChar)
        {
            int index = 0;
            return FindContent(str, header, openBraceChar, closeBraceChar, ref index);
        }

        static string FindContent(string str, string header, char openBraceChar, char closeBraceChar, ref int index)
        {
            int braceCount = 0;
            index = str.IndexOf(header, index);
            if (index > 0)
            {
                while (str[index] != openBraceChar)
                {
                    index--;
                }
                int commonEndIndex = -1;
                for (int j = index; j < str.Length; j++)
                {
                    if (str[j] == openBraceChar)
                    {
                        braceCount++;
                    }
                    else if (str[j] == closeBraceChar)
                    {
                        braceCount--;
                        if (braceCount == 0)
                        {
                            commonEndIndex = j + 1;
                            break;
                        }
                    }
                }
                if (commonEndIndex > 0)
                {
                    string result = CleanJson(str.Substring(index, commonEndIndex - index));
                    index = commonEndIndex;
                    return result;
                }
            }
            return null;
        }

        private static string CleanJson(string json)
        {
            string[] lines = json.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder result = new StringBuilder();
            string indentString = "    ";
            int indent = 0;
            char[] braceChars = { '{', '}', '[', ']' };
            for (int j = 0; j < lines.Length; j++)
            {
                string line = lines[j].TrimStart();
                line = line.Replace("!0", "true");
                if (line.Length > 0 && !braceChars.Contains(line[0]))
                {
                    line = "\"" + line;
                    line = line.Insert(line.IndexOf(':'), "\"");
                }
                if (line.Contains("}"))
                {
                    indent--;
                }
                line = String.Concat(Enumerable.Repeat(indentString, indent)) + line;
                if (line.Contains("{"))
                {
                    indent++;
                }
                result.AppendLine(line);
            }
            return result.ToString();
        }

        private static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target, bool all)
        {
            string[] extensions = { "json", "js" };
            foreach (DirectoryInfo dir in source.GetDirectories())
            {
                // "res" folder contains some data we don't want
                if (all || dir.Name != "res")
                {
                    if (dir.Name.Contains("新建文件夹"))// "New Folder" (img\新建文件夹\)
                    {
                        continue;
                    }
                    // Remove the special case folder (TODO: Make this more generic)
                    CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name.Replace("(风控)", string.Empty)), all);
                }
            }
            foreach (FileInfo file in source.GetFiles())
            {
                if (all || extensions.Contains(file.Extension.ToLower().TrimStart(new char[] { '.' })))
                {
                    if (!file.Name.Contains("剑灵") && !file.Name.Contains("逆战") && !file.Name.Contains("问号") &&
                        !file.Name.Contains("下灯序") &&// Data\device\656801861\data\keymap下灯序.js
                        !file.Name.Contains("新建文件夹"))// driver\res\img\新建文件夹.rar
                    {
                        file.CopyTo(Path.Combine(target.FullName, file.Name), true);
                    }
                }
            }
            if (!all && target.GetFiles().Length == 0)
            {
                target.Delete();
            }
        }
    }
}
