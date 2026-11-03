// Common.cs — shared by HorizonOffline.exe (the player) and HorizonOfflineSetup.exe (the installer).
// Plain C# 5 + WinForms so everything builds with the in-box compiler on any Windows 10/11.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace HorizonOffline
{
    /// <summary>
    /// Fixed locations the Horizon launcher and game use. Mirror of horizon-rust/horizon
    /// launcher/src/backend.rs: data_dir() = %APPDATA%\horizon-launcher, game_home() = %APPDATA%\horizon.
    /// </summary>
    static class Paths
    {
        public static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static readonly string LauncherHome = Path.Combine(Roaming, "horizon-launcher");
        public static readonly string Versions = Path.Combine(LauncherHome, "versions");
        public static readonly string ServerJars = Path.Combine(LauncherHome, "server-jars");
        public static readonly string GameHome = Path.Combine(Roaming, "horizon");
        public static readonly string Saves = Path.Combine(GameHome, "saves");

        public static readonly string DefaultInstallDir = Path.Combine(LocalAppData, "HorizonOffline");

        public static string ExeDir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        public static string CfgPath { get { return Path.Combine(ExeDir, "horizon-offline.ini"); } }
    }

    /// <summary>Colour palettes: AMOLED (true black) and Light, both with the Horizon violet accent.</summary>
    class Theme
    {
        public bool Amoled;
        public Color Back, Surface, SurfaceAlt, Border, Text, TextDim, Accent, AccentHover, Success, Danger, Hover;

        public static Theme Make(bool amoled)
        {
            Theme t = new Theme();
            t.Amoled = amoled;
            if (amoled)
            {
                t.Back = Color.FromArgb(0, 0, 0);
                t.Surface = Color.FromArgb(12, 12, 14);
                t.SurfaceAlt = Color.FromArgb(24, 24, 29);
                t.Border = Color.FromArgb(44, 44, 54);
                t.Text = Color.FromArgb(242, 242, 245);
                t.TextDim = Color.FromArgb(150, 150, 160);
                t.Accent = Color.FromArgb(139, 92, 246);
                t.AccentHover = Color.FromArgb(167, 139, 250);
                t.Success = Color.FromArgb(72, 213, 110);
                t.Danger = Color.FromArgb(255, 106, 106);
                t.Hover = Color.FromArgb(34, 34, 41);
            }
            else
            {
                t.Back = Color.FromArgb(244, 244, 248);
                t.Surface = Color.FromArgb(255, 255, 255);
                t.SurfaceAlt = Color.FromArgb(234, 234, 241);
                t.Border = Color.FromArgb(212, 212, 222);
                t.Text = Color.FromArgb(24, 24, 30);
                t.TextDim = Color.FromArgb(104, 104, 116);
                t.Accent = Color.FromArgb(109, 40, 217);
                t.AccentHover = Color.FromArgb(124, 58, 237);
                t.Success = Color.FromArgb(20, 140, 63);
                t.Danger = Color.FromArgb(198, 40, 40);
                t.Hover = Color.FromArgb(226, 226, 234);
            }
            return t;
        }
    }

    static class Skins
    {
        public static void Primary(Button b, Theme t)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = t.Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.MouseOverBackColor = t.AccentHover;
            b.FlatAppearance.MouseDownBackColor = t.Accent;
            b.UseVisualStyleBackColor = false;
        }

        public static void Ghost(Button b, Theme t)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = t.Border;
            b.BackColor = t.SurfaceAlt;
            b.ForeColor = t.Text;
            b.FlatAppearance.MouseOverBackColor = t.Hover;
            b.FlatAppearance.MouseDownBackColor = t.SurfaceAlt;
            b.UseVisualStyleBackColor = false;
        }

        /// <summary>
        /// Walks the tree and styles by control type. Tags steer it:
        /// panels: "surface" | "alt" | "transparent" | default back; labels: "dim" | "accent";
        /// buttons: "primary" | default ghost; checkboxes: "transparent" (default transparent).
        /// </summary>
        public static void Apply(Control root, Theme t)
        {
            string tag = root.Tag as string;
            if (root is Form)
            {
                root.BackColor = t.Back;
                root.ForeColor = t.Text;
            }
            else if (root is Button)
            {
                if (tag == "primary") Primary((Button)root, t);
                else Ghost((Button)root, t);
            }
            else if (root is TextBox || root is ListBox)
            {
                root.BackColor = t.Surface;
                root.ForeColor = t.Text;
            }
            else if (root is ComboBox || root is NumericUpDown)
            {
                root.BackColor = t.Surface;
                root.ForeColor = t.Text;
                ComboBox cb = root as ComboBox;
                if (cb != null) cb.FlatStyle = FlatStyle.Flat;
            }
            else if (root is CheckBox || root is RadioButton)
            {
                root.ForeColor = t.Text;
                root.BackColor = Color.Transparent;
            }
            else if (root is LinkLabel)
            {
                LinkLabel ll = (LinkLabel)root;
                ll.LinkColor = t.Accent;
                ll.ActiveLinkColor = t.AccentHover;
                ll.VisitedLinkColor = t.Accent;
                ll.ForeColor = t.Text;
                ll.BackColor = Color.Transparent;
            }
            else if (root is Label)
            {
                Label l = (Label)root;
                if (tag == "dim") l.ForeColor = t.TextDim;
                else if (tag == "accent") l.ForeColor = t.Accent;
                else if (tag == "success") l.ForeColor = t.Success;
                else if (tag == "danger") l.ForeColor = t.Danger;
                else l.ForeColor = t.Text;
                l.BackColor = Color.Transparent;
            }
            else if (root is Panel || root is GroupBox || root is TabControl || root is TabPage)
            {
                if (tag == "surface") root.BackColor = t.Surface;
                else if (tag == "alt") root.BackColor = t.SurfaceAlt;
                else if (tag == "transparent") root.BackColor = Color.Transparent;
                else root.BackColor = t.Back;
                if (root is GroupBox) root.ForeColor = t.Text;
            }
            foreach (Control c in root.Controls) Apply(c, t);
        }
    }

    static class Win32
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Immersive dark title bar (Windows 10 1809+); best effort, no-op elsewhere.</summary>
        public static void DarkTitleBar(Form f, bool dark)
        {
            if (Environment.OSVersion.Version.Major < 10) return;
            try
            {
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, sizeof(int)) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref v, sizeof(int));
            }
            catch { }
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        public static void Focus(Form f)
        {
            try { SetForegroundWindow(f.Handle); } catch { }
        }
    }

    static class Util
    {
        /// <summary>The same job as launcher/src/backend.rs split_args: quotes group a word.</summary>
        public static List<string> SplitArgs(string text)
        {
            List<string> words = new List<string>();
            if (text == null) return words;
            StringBuilder word = new StringBuilder();
            bool started = false;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0'; else word.Append(ch);
                }
                else if (ch == '\'' || ch == '"') { quote = ch; started = true; }
                else if (ch == '\\' && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '\'')) { i++; word.Append(text[i]); started = true; }
                else if (char.IsWhiteSpace(ch))
                {
                    if (started) { words.Add(word.ToString()); word.Length = 0; started = false; }
                }
                else { word.Append(ch); started = true; }
            }
            if (started) words.Add(word.ToString());
            return words;
        }

        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return arg;
            StringBuilder b = new StringBuilder();
            b.Append('"');
            int slashes = 0;
            foreach (char ch in arg)
            {
                if (ch == '\\') slashes++;
                else if (ch == '"') { b.Append('\\', slashes * 2 + 1); b.Append('"'); slashes = 0; }
                else { b.Append('\\', slashes); b.Append(ch); slashes = 0; }
            }
            b.Append('\\', slashes * 2);
            b.Append('"');
            return b.ToString();
        }

        public static string FirstMatch(string text, string pattern)
        {
            Match m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static string Sha1File(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder b = new StringBuilder();
                foreach (byte x in h) b.Append(x.ToString("x2"));
                return b.ToString();
            }
        }

        public static bool ContainsSha1(string[] list, string value)
        {
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>A playable game version folder: nightly-… or v0.2.x / v2026.10.13.
        /// Skips the launcher's native-… native-mods dev builds.</summary>
        public static bool IsGameTag(string tag)
        {
            if (tag == null) return false;
            if (tag.StartsWith("nightly-")) return true;
            return tag.Length > 1 && tag[0] == 'v' && char.IsDigit(tag[1]);
        }

        public static void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); Process.Start("explorer.exe", Quote(path)); }
            catch { }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(url); }
            catch { }
        }

        public static string Human(long bytes)
        {
            double b = bytes;
            if (b < 1024) return ((long)b).ToString() + " B";
            if (b < 1024 * 1024) return (b / 1024).ToString("0.0") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / (1024.0 * 1024)).ToString("0.0") + " MB";
            return (b / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
        }
    }

    /// <summary>One Minecraft folder the installer can use, with its check result.</summary>
    class McCandidate
    {
        public string Dir;
        public string Status = "found, not checked yet";
        public bool Ok;

        public override string ToString()
        {
            return Dir + "   —   " + Status;
        }
    }

    /// <summary>
    /// Finds Minecraft 26.3 folders and checks them against the exact hashes Horizon's
    /// research-lock.json pins (client jar e877…, one of the four published asset indexes).
    /// </summary>
    static class McFind
    {
        public const string McVersion = "26.3";
        public const string ClientSha1 = "e877b6a07acd633fb3bb475002175cec036e7b87";
        public const string ServerSha1 = "33680f5f2ac32864d6d7cf5e56a705fdb3e05f4c";
        public const string ServerUrl = "https://piston-data.mojang.com/v1/objects/33680f5f2ac32864d6d7cf5e56a705fdb3e05f4c/server.jar";

        public static readonly string[] AcceptedIndexSha1 = new string[]
        {
            "abfaa525f923f807df8b4e4d29c1b5e3a104adbe",
            "1e4e4a68dece1c0f51b8f93b45da8fe8da51bcd3",
            "0c8f43b28f837869c1e54a6aeb22c396a1548946",
            "917ff6d74b9f635c46bac0d28857206bbcc1ffe4"
        };

        public static List<string> Roots()
        {
            List<string> list = new List<string>();
            Add(list, Path.Combine(Paths.Roaming, ".minecraft"));
            Add(list, Path.Combine(Paths.Roaming, ".fastclient"));
            AddInstances(list, Path.Combine(Paths.Roaming, "PrismLauncher", "instances"), true);
            AddInstances(list, Path.Combine(Paths.Roaming, "MultiMC", "instances"), true);
            AddInstances(list, Path.Combine(Paths.Roaming, "ATLauncher", "instances"), false);
            AddInstances(list, Path.Combine(Paths.Roaming, "com.modrinth.theseus", "profiles"), false);
            AddInstances(list, Path.Combine(Paths.Roaming, "ModrinthApp", "profiles"), false);
            AddInstances(list, Path.Combine(Paths.UserProfile, "curseforge", "minecraft", "Instances"), false);
            AddInstances(list, Path.Combine(Paths.Roaming, "gdlauncher_next", "instances"), false);
            AddInstances(list, Path.Combine(Paths.Roaming, ".fastclient", "profiles"), false);
            return list;
        }

        public static void Add(List<string> list, string dir)
        {
            string full;
            try { full = Path.GetFullPath(dir); } catch { return; }
            if (!Directory.Exists(full)) return;
            foreach (string s in list)
                if (string.Equals(s, full, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(full);
        }

        static void AddInstances(List<string> list, string instancesDir, bool dotMinecraft)
        {
            if (!Directory.Exists(instancesDir)) return;
            try
            {
                foreach (string d in Directory.GetDirectories(instancesDir))
                    Add(list, dotMinecraft ? Path.Combine(d, ".minecraft") : d);
            }
            catch { }
        }

        /// <summary>Cheap structural peek used to list candidates before hashing.</summary>
        public static bool LooksLike(string root)
        {
            return File.Exists(Path.Combine(root, "versions", McVersion, McVersion + ".json"))
                || File.Exists(Path.Combine(root, "versions", McVersion, McVersion + ".jar"))
                || File.Exists(Path.Combine(root, "assets", "indexes", "34.json"));
        }

        /// <summary>Full check. Returns null when the folder can feed Horizon, else the reason.</summary>
        public static string Validate(string root)
        {
            string jar = Path.Combine(root, "versions", McVersion, McVersion + ".jar");
            if (!File.Exists(jar)) return "no versions\\" + McVersion + "\\" + McVersion + ".jar";
            string sha;
            try { sha = Util.Sha1File(jar); }
            catch (Exception e) { return "cannot read the client jar (" + e.Message + ")"; }
            if (!string.Equals(sha, ClientSha1, StringComparison.OrdinalIgnoreCase))
                return "client jar is not the vanilla " + McVersion + " build (hash mismatch)";

            string dir = Path.Combine(root, "assets", "indexes");
            if (!Directory.Exists(dir)) return "no assets\\indexes folder";
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.json"))
                {
                    try { if (Util.ContainsSha1(AcceptedIndexSha1, Util.Sha1File(f))) return null; }
                    catch { }
                }
            }
            catch { }
            return "no matching " + McVersion + " asset index";
        }
    }

    static class Shortcuts
    {
        public static bool Create(string lnkPath, string target, string workDir, string iconPath, string description)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;
                object sh = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                if (iconPath != null && iconPath.Length > 0)
                    lt.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { iconPath });
                if (description != null)
                    lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { description });
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, new object[] { });
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>The player's settings; an INI next to HorizonOffline.exe.</summary>
    class Config
    {
        public string PlayerName = "";
        public string VersionTag = "";
        public string Theme = "amoled";
        public string MinecraftDir = "";
        public string LauncherExe = "";
        public bool Fullscreen;
        public bool UseDefaultRenderDistance = true;
        public int RenderDistance = 12;
        public bool CustomSize;
        public int Width = 1280;
        public int Height = 720;
        public string Server = "";
        public string ExtraArgs = "";
        public bool QuickWorld;
        public string QuickWorldFolder = "";
        public bool NewWorld;
        public string NewWorldName = "";
        public string NewWorldSeed = "";
        public bool NewWorldCreative;

        static string Get(Dictionary<string, string> d, string key, string fallback)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : fallback;
        }

        static bool GetBool(Dictionary<string, string> d, string key, bool fallback)
        {
            string v;
            if (!d.TryGetValue(key, out v)) return fallback;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        static int GetInt(Dictionary<string, string> d, string key, int fallback)
        {
            string s;
            int v;
            if (d.TryGetValue(key, out s) && int.TryParse(s, out v)) return v;
            return fallback;
        }

        public static Config Load(string path)
        {
            Config c = new Config();
            if (!File.Exists(path)) return c;
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    d[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
                }
            }
            catch { return c; }
            c.PlayerName = Get(d, "PlayerName", c.PlayerName);
            c.VersionTag = Get(d, "VersionTag", c.VersionTag);
            c.Theme = Get(d, "Theme", c.Theme);
            c.MinecraftDir = Get(d, "MinecraftDir", c.MinecraftDir);
            c.LauncherExe = Get(d, "LauncherExe", c.LauncherExe);
            c.Fullscreen = GetBool(d, "Fullscreen", c.Fullscreen);
            c.UseDefaultRenderDistance = GetBool(d, "UseDefaultRenderDistance", c.UseDefaultRenderDistance);
            c.RenderDistance = GetInt(d, "RenderDistance", c.RenderDistance);
            c.CustomSize = GetBool(d, "CustomSize", c.CustomSize);
            c.Width = GetInt(d, "Width", c.Width);
            c.Height = GetInt(d, "Height", c.Height);
            c.Server = Get(d, "Server", c.Server);
            c.ExtraArgs = Get(d, "ExtraArgs", c.ExtraArgs);
            c.QuickWorld = GetBool(d, "QuickWorld", c.QuickWorld);
            c.QuickWorldFolder = Get(d, "QuickWorldFolder", c.QuickWorldFolder);
            c.NewWorld = GetBool(d, "NewWorld", c.NewWorld);
            c.NewWorldName = Get(d, "NewWorldName", c.NewWorldName);
            c.NewWorldSeed = Get(d, "NewWorldSeed", c.NewWorldSeed);
            c.NewWorldCreative = GetBool(d, "NewWorldCreative", c.NewWorldCreative);
            return c;
        }

        public void Save(string path)
        {
            try
            {
                StringBuilder b = new StringBuilder();
                b.AppendLine("# HorizonOffline settings - one key=value per line");
                b.AppendLine("PlayerName=" + PlayerName);
                b.AppendLine("VersionTag=" + VersionTag);
                b.AppendLine("Theme=" + Theme);
                b.AppendLine("MinecraftDir=" + MinecraftDir);
                b.AppendLine("LauncherExe=" + LauncherExe);
                b.AppendLine("Fullscreen=" + (Fullscreen ? "1" : "0"));
                b.AppendLine("UseDefaultRenderDistance=" + (UseDefaultRenderDistance ? "1" : "0"));
                b.AppendLine("RenderDistance=" + RenderDistance);
                b.AppendLine("CustomSize=" + (CustomSize ? "1" : "0"));
                b.AppendLine("Width=" + Width);
                b.AppendLine("Height=" + Height);
                b.AppendLine("Server=" + Server);
                b.AppendLine("ExtraArgs=" + ExtraArgs);
                b.AppendLine("QuickWorld=" + (QuickWorld ? "1" : "0"));
                b.AppendLine("QuickWorldFolder=" + QuickWorldFolder);
                b.AppendLine("NewWorld=" + (NewWorld ? "1" : "0"));
                b.AppendLine("NewWorldName=" + NewWorldName);
                b.AppendLine("NewWorldSeed=" + NewWorldSeed);
                b.AppendLine("NewWorldCreative=" + (NewWorldCreative ? "1" : "0"));
                File.WriteAllText(path, b.ToString());
            }
            catch { }
        }
    }
}
