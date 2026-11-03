// HorizonOffline.cs — the player. Starts Horizon (Minecraft 26.3 in Rust) without a
// Microsoft account by replicating the launcher's own command (backend.rs::client_command)
// and skipping only the owner gate:
//
//   cwd   = %APPDATA%\horizon
//   exe   = %APPDATA%\horizon-launcher\versions\<tag>\horizon-client.exe
//   args  = --resources <versions>\<tag>\.local\horizon-client\26.3 [...]
//
// UI: sidebar navigation + AMOLED(black)/Light theme toggle in the header.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace HorizonOffline
{
    class VersionInfo
    {
        public string Tag;
        public string Dir;
        public string ClientExe;
        public string McVersion;
        public string ResourcesDir;
        public bool Prepared;
        public bool Sounds;
        public DateTime Sort;

        public override string ToString()
        {
            string s = Tag;
            if (!Prepared) s += "   (not prepared)";
            return s;
        }
    }

    class WorldItem
    {
        public string Folder;
        public DateTime Modified;

        public override string ToString()
        {
            return Folder + "   " + Modified.ToString("yyyy-MM-dd HH:mm");
        }
    }

    class MainForm : Form
    {
        public bool AutoPlay;

        Config cfg;
        Theme theme;

        Panel header, nav, host, accentLine;
        Button themeBtn;
        Label titleLbl, subLbl;
        List<Panel> pages = new List<Panel>();
        List<Button> navBtns = new List<Button>();
        List<Panel> navBars = new List<Panel>();
        int pageIdx;

        ComboBox versionBox;
        Label statusLabel;
        TextBox nameBox;

        CheckBox quickWorldCheck;
        ListBox worldList;
        CheckBox newWorldCheck;
        TextBox newWorldName;
        TextBox newWorldSeed;
        CheckBox newWorldCreative;

        CheckBox defaultRdCheck;
        NumericUpDown renderDistance;
        CheckBox fullscreenCheck;
        CheckBox customSizeCheck;
        NumericUpDown widthBox;
        NumericUpDown heightBox;
        TextBox serverBox;
        TextBox extraArgsBox;

        Label mcDirLabel;
        TextBox launcherExeBox;
        ToolTip tips = new ToolTip();

        List<VersionInfo> versions = new List<VersionInfo>();
        bool loading;
        bool playing;

        public MainForm(string forceTheme)
        {
            cfg = Config.Load(Paths.CfgPath);
            bool amoled = !string.Equals(cfg.Theme, "light", StringComparison.OrdinalIgnoreCase);
            if (forceTheme != null) amoled = !string.Equals(forceTheme, "light", StringComparison.OrdinalIgnoreCase);
            theme = Theme.Make(amoled);

            if (cfg.MinecraftDir.Length == 0 || !Directory.Exists(cfg.MinecraftDir))
            {
                foreach (string root in McFind.Roots())
                    if (McFind.Validate(root) == null) { cfg.MinecraftDir = root; break; }
            }
            if (cfg.LauncherExe.Length == 0 || !File.Exists(cfg.LauncherExe))
                cfg.LauncherExe = GuessLauncherExe();

            Text = "Horizon Offline";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(800, 560);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildHeader();
            BuildNav();
            host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Tag = "back";
            // Dock order: the control added LAST is docked first (outermost). host fills last.
            Controls.Add(host);
            Controls.Add(nav);
            Controls.Add(header);
            BuildPages();

            ApplyThemeAll();
            loading = true;
            ApplyConfigToUi();
            loading = false;
            RefreshVersions();
            RefreshWorlds();
            FormClosing += delegate { SaveConfigFromUi(); };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Win32.DarkTitleBar(this, theme.Amoled);
            if (AutoPlay) BeginInvoke(new Action(delegate { PlayClicked(); }));
        }

        // ---------- chrome ----------

        void BuildHeader()
        {
            header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 74;
            header.Tag = "surface";

            titleLbl = new Label();
            titleLbl.Text = "Horizon Offline";
            titleLbl.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            titleLbl.Location = new Point(22, 12);
            titleLbl.AutoSize = true;
            header.Controls.Add(titleLbl);

            subLbl = new Label();
            subLbl.Text = "Minecraft 26.3 in Rust \u2014 no Microsoft account";
            subLbl.Tag = "dim";
            subLbl.Location = new Point(24, 44);
            subLbl.AutoSize = true;
            header.Controls.Add(subLbl);

            themeBtn = new Button();
            themeBtn.Text = "Theme";
            themeBtn.Size = new Size(140, 32);
            themeBtn.Location = new Point(636, 20);
            themeBtn.Click += delegate { ToggleTheme(); };
            header.Controls.Add(themeBtn);
            header.Resize += delegate { themeBtn.Left = header.Width - themeBtn.Width - 22; };

            accentLine = new Panel();
            accentLine.Height = 2;
            accentLine.Dock = DockStyle.Bottom;
            header.Controls.Add(accentLine);
            accentLine.BringToFront();
        }

        void BuildNav()
        {
            nav = new Panel();
            nav.Dock = DockStyle.Left;
            nav.Width = 190;
            nav.Tag = "surface";

            string[] names = new string[] { "Play", "Worlds", "Options", "Launcher", "About" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                Button b = new Button();
                b.Text = "      " + names[i];
                b.TextAlign = ContentAlignment.MiddleLeft;
                b.Size = new Size(190, 46);
                b.Location = new Point(0, 14 + i * 46);
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.UseVisualStyleBackColor = false;
                b.Click += delegate { SwitchPage(index); };
                nav.Controls.Add(b);
                b.BringToFront();

                Panel bar = new Panel();
                bar.Size = new Size(3, 46);
                bar.Location = new Point(0, 14 + i * 46);
                nav.Controls.Add(bar);
                bar.BringToFront();

                navBtns.Add(b);
                navBars.Add(bar);
            }
        }

        void BuildPages()
        {
            pages.Add(BuildPlayPage());
            pages.Add(BuildWorldsPage());
            pages.Add(BuildOptionsPage());
            pages.Add(BuildLauncherPage());
            pages.Add(BuildAboutPage());
            foreach (Panel p in pages)
            {
                p.Dock = DockStyle.Fill;
                p.Tag = "back";
                p.Visible = false;
                host.Controls.Add(p);
            }
            SwitchPage(0);
        }

        Panel NewPage()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            return p;
        }

        Button Ghost(Control parent, string text, Point at, Size size)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = at;
            b.Size = size;
            parent.Controls.Add(b);
            return b;
        }

        Label Lab(Control parent, string text, Point at, string tag)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = at;
            l.AutoSize = true;
            if (tag != null) l.Tag = tag;
            parent.Controls.Add(l);
            return l;
        }

        Panel BuildPlayPage()
        {
            Panel p = NewPage();

            Lab(p, "Horizon version", new Point(26, 20), "dim");
            versionBox = new ComboBox();
            versionBox.DropDownStyle = ComboBoxStyle.DropDownList;
            versionBox.Location = new Point(26, 42);
            versionBox.Width = 400;
            versionBox.SelectedIndexChanged += delegate { VersionChanged(); };
            p.Controls.Add(versionBox);
            Ghost(p, "Refresh", new Point(436, 41), new Size(110, 28)).Click += delegate { RefreshVersions(); RefreshWorlds(); };

            Lab(p, "Offline player name", new Point(26, 90), "dim");
            nameBox = new TextBox();
            nameBox.Location = new Point(26, 112);
            nameBox.Width = 280;
            p.Controls.Add(nameBox);

            statusLabel = new Label();
            statusLabel.Location = new Point(26, 156);
            statusLabel.Size = new Size(540, 62);
            statusLabel.AutoSize = false;
            p.Controls.Add(statusLabel);

            Button play = new Button();
            play.Text = "\u25B6  Play";
            play.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            play.Size = new Size(190, 56);
            play.Location = new Point(24, 236);
            play.Tag = "primary";
            play.Click += delegate { PlayClicked(); };
            p.Controls.Add(play);

            Ghost(p, "Game folder", new Point(232, 248), new Size(130, 36)).Click += delegate { Util.OpenFolder(Paths.GameHome); };
            Ghost(p, "Logs", new Point(374, 248), new Size(90, 36)).Click += delegate { Util.OpenFolder(Path.Combine(Paths.GameHome, "logs")); };

            Lab(p, "The game runs fully offline. Multiplayer only works on offline-mode servers\r\n" +
                   "(Options) or a friend's LAN world \u2014 same as any offline name.", new Point(26, 320), "dim");

            return p;
        }

        Panel BuildWorldsPage()
        {
            Panel p = NewPage();

            quickWorldCheck = new CheckBox();
            quickWorldCheck.Text = "Start in this world (resume)";
            quickWorldCheck.Location = new Point(26, 20);
            quickWorldCheck.AutoSize = true;
            quickWorldCheck.CheckedChanged += delegate { WorldModeChanged(); };
            p.Controls.Add(quickWorldCheck);

            worldList = new ListBox();
            worldList.Location = new Point(26, 48);
            worldList.Size = new Size(380, 250);
            worldList.DoubleClick += delegate { SaveConfigFromUi(); PlayClicked(); };
            p.Controls.Add(worldList);

            Ghost(p, "saves folder", new Point(420, 48), new Size(120, 30)).Click += delegate { Util.OpenFolder(Paths.Saves); };

            newWorldCheck = new CheckBox();
            newWorldCheck.Text = "New world instead";
            newWorldCheck.Location = new Point(26, 316);
            newWorldCheck.AutoSize = true;
            newWorldCheck.CheckedChanged += delegate { WorldModeChanged(); };
            p.Controls.Add(newWorldCheck);

            Lab(p, "World name", new Point(46, 348), "dim");
            newWorldName = new TextBox();
            newWorldName.Location = new Point(140, 345);
            newWorldName.Width = 220;
            p.Controls.Add(newWorldName);

            Lab(p, "Seed (blank = 0)", new Point(46, 378), "dim");
            newWorldSeed = new TextBox();
            newWorldSeed.Location = new Point(160, 375);
            newWorldSeed.Width = 200;
            p.Controls.Add(newWorldSeed);

            newWorldCreative = new CheckBox();
            newWorldCreative.Text = "Creative mode";
            newWorldCreative.Location = new Point(46, 406);
            newWorldCreative.AutoSize = true;
            p.Controls.Add(newWorldCreative);

            return p;
        }

        Panel BuildOptionsPage()
        {
            Panel p = NewPage();

            defaultRdCheck = new CheckBox();
            defaultRdCheck.Text = "Default render distance (12 chunks)";
            defaultRdCheck.Location = new Point(26, 20);
            defaultRdCheck.AutoSize = true;
            defaultRdCheck.CheckedChanged += delegate { RenderDistanceChanged(); };
            p.Controls.Add(defaultRdCheck);

            Lab(p, "Render distance (2\u2013128)", new Point(46, 54), "dim");
            renderDistance = new NumericUpDown();
            renderDistance.Minimum = 2;
            renderDistance.Maximum = 128;
            renderDistance.Value = 12;
            renderDistance.Location = new Point(200, 51);
            renderDistance.Width = 80;
            p.Controls.Add(renderDistance);

            fullscreenCheck = new CheckBox();
            fullscreenCheck.Text = "Fullscreen";
            fullscreenCheck.Location = new Point(26, 92);
            fullscreenCheck.AutoSize = true;
            p.Controls.Add(fullscreenCheck);

            customSizeCheck = new CheckBox();
            customSizeCheck.Text = "Custom window size";
            customSizeCheck.Location = new Point(26, 124);
            customSizeCheck.AutoSize = true;
            customSizeCheck.CheckedChanged += delegate { RenderDistanceChanged(); };
            p.Controls.Add(customSizeCheck);

            widthBox = new NumericUpDown();
            widthBox.Minimum = 320;
            widthBox.Maximum = 16384;
            widthBox.Value = 1280;
            widthBox.Location = new Point(200, 122);
            widthBox.Width = 90;
            p.Controls.Add(widthBox);
            Lab(p, "x", new Point(296, 126), "dim");
            heightBox = new NumericUpDown();
            heightBox.Minimum = 240;
            heightBox.Maximum = 16384;
            heightBox.Value = 720;
            heightBox.Location = new Point(312, 122);
            heightBox.Width = 90;
            p.Controls.Add(heightBox);

            Lab(p, "Join server at start (host[:port], offline-mode only)", new Point(26, 164), "dim");
            serverBox = new TextBox();
            serverBox.Location = new Point(26, 186);
            serverBox.Width = 340;
            p.Controls.Add(serverBox);

            Lab(p, "Extra game arguments", new Point(26, 224), "dim");
            extraArgsBox = new TextBox();
            extraArgsBox.Location = new Point(26, 246);
            extraArgsBox.Width = 540;
            p.Controls.Add(extraArgsBox);

            Lab(p, "horizon-client --help lists every option (--pregen, --world-frames, \u2026).", new Point(26, 282), "dim");
            return p;
        }

        Panel BuildLauncherPage()
        {
            Panel p = NewPage();

            Lab(p, "Minecraft 26.3 folder (the source prepare imports from)", new Point(26, 20), "dim");
            mcDirLabel = new Label();
            mcDirLabel.Location = new Point(26, 42);
            mcDirLabel.Size = new Size(280, 20);
            mcDirLabel.AutoEllipsis = true;
            p.Controls.Add(mcDirLabel);
            tips.SetToolTip(mcDirLabel, "");
            Ghost(p, "Auto-detect", new Point(316, 38), new Size(120, 28)).Click += delegate { DetectMinecraft(); };
            Ghost(p, "Change\u2026", new Point(446, 38), new Size(110, 28)).Click += delegate { BrowseMinecraft(); };

            Lab(p, "horizon-launcher.exe (updates and preparing new versions)", new Point(26, 92), "dim");
            launcherExeBox = new TextBox();
            launcherExeBox.Location = new Point(26, 114);
            launcherExeBox.Width = 420;
            p.Controls.Add(launcherExeBox);
            Ghost(p, "Browse\u2026", new Point(456, 112), new Size(100, 28)).Click += delegate { BrowseLauncher(); };

            Ghost(p, "Update game + launcher", new Point(26, 164), new Size(210, 38)).Click += delegate { RunLauncher("update", true); };
            Ghost(p, "Prepare selected version", new Point(248, 164), new Size(210, 38)).Click += delegate { PrepareSelected(); };
            Ghost(p, "Open the real launcher", new Point(26, 214), new Size(210, 38)).Click += delegate { RunLauncher("", false); };

            Lab(p, "New versions install themselves when you update and normally reuse the prepared\r\n" +
                   "files of the version before, so Play keeps working. If one shows \"(not prepared)\",\r\n" +
                   "hit Prepare \u2014 it imports from the folder above, no account is involved.", new Point(26, 272), "dim");

            return p;
        }

        Panel BuildAboutPage()
        {
            Panel p = NewPage();

            Label t = Lab(p, "Horizon Offline", new Point(26, 20), null);
            t.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            Lab(p, "Account-free launcher for Horizon \u2014 Minecraft: Java Edition 26.3 rewritten in Rust.", new Point(26, 54), "dim");

            Lab(p, "How it works", new Point(26, 94), "accent");
            Lab(p, "Horizon's official launcher starts the game only while a signed-in Microsoft\r\n" +
                   "account owns Java Edition (its \"owner gate\"). The game itself does not care:\r\n" +
                   "without a session file it runs offline with your own player name. This tool uses\r\n" +
                   "the launcher's ungated prepare step to import the game files, then starts the\r\n" +
                   "game directly \u2014 the same command the launcher builds, minus the account check.",
                new Point(26, 116), "dim");

            Lab(p, "Links", new Point(26, 232), "accent");
            LinkLabel l1 = new LinkLabel();
            l1.Text = "github.com/horizon-rust/horizon";
            l1.Location = new Point(26, 254);
            l1.AutoSize = true;
            l1.Click += delegate { Util.OpenUrl("https://github.com/horizon-rust/horizon"); };
            p.Controls.Add(l1);
            LinkLabel l2 = new LinkLabel();
            l2.Text = "playhorizon.dev";
            l2.Location = new Point(26, 276);
            l2.AutoSize = true;
            l2.Click += delegate { Util.OpenUrl("https://playhorizon.dev"); };
            p.Controls.Add(l2);

            Lab(p, "Unofficial fan tool \u2014 not affiliated with Mojang, Microsoft or the Horizon project.\r\n" +
                   "Minecraft is a trademark of Mojang and Microsoft. Worlds, options and logs live\r\n" +
                   "in %APPDATA%\\horizon.", new Point(26, 320), "dim");

            return p;
        }

        // ---------- theme ----------

        void ToggleTheme()
        {
            theme = Theme.Make(!theme.Amoled);
            cfg.Theme = theme.Amoled ? "amoled" : "light";
            ApplyThemeAll();
            SaveConfigFromUi();
        }

        void ApplyThemeAll()
        {
            Skins.Apply(this, theme);
            accentLine.BackColor = theme.Accent;
            header.BackColor = theme.Surface;
            nav.BackColor = theme.Surface;
            themeBtn.Text = theme.Amoled ? "\u2600  Light mode" : "\u263E  Dark mode";
            StyleNav();
            UpdateMinecraftLabel();
            VersionChanged();
            Win32.DarkTitleBar(this, theme.Amoled);
            Invalidate(true);
        }

        void StyleNav()
        {
            for (int i = 0; i < navBtns.Count; i++)
            {
                bool active = i == pageIdx;
                navBtns[i].BackColor = active ? theme.SurfaceAlt : theme.Surface;
                navBtns[i].ForeColor = active ? theme.Accent : theme.TextDim;
                navBtns[i].Font = new Font("Segoe UI", 9.5f, active ? FontStyle.Bold : FontStyle.Regular);
                navBtns[i].FlatAppearance.MouseOverBackColor = active ? theme.SurfaceAlt : theme.Hover;
                navBars[i].BackColor = theme.Accent;
                navBars[i].Visible = active;
            }
        }

        void SwitchPage(int index)
        {
            pageIdx = index;
            for (int i = 0; i < pages.Count; i++)
                pages[i].Visible = i == index;
            StyleNav();
        }

        // ---------- state ----------

        string GuessLauncherExe()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Paths.ExeDir, "horizon-launcher", "horizon-launcher.exe"),
                Path.Combine(Paths.LocalAppData, "Programs", "horizon-launcher", "horizon-launcher.exe"),
                Path.Combine(Paths.LocalAppData, "horizon-launcher", "horizon-launcher.exe"),
                Path.Combine(Paths.Roaming, "horizon-launcher", "horizon-launcher.exe")
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return "";
        }

        void RefreshVersions()
        {
            versions = ScanVersions();
            string selected = null;
            try
            {
                if (File.Exists(Path.Combine(Paths.LauncherHome, "settings.json")))
                    selected = Util.FirstMatch(File.ReadAllText(Path.Combine(Paths.LauncherHome, "settings.json")),
                                               "\"selected\"\\s*:\\s*\"([^\"]+)\"");
            }
            catch { }

            versionBox.Items.Clear();
            foreach (VersionInfo v in versions) versionBox.Items.Add(v);

            int pick = -1;
            if (cfg.VersionTag.Length > 0)
                for (int i = 0; i < versions.Count; i++)
                    if (versions[i].Tag == cfg.VersionTag && versions[i].Prepared) { pick = i; break; }
            if (pick < 0 && cfg.VersionTag.Length > 0)
                for (int i = 0; i < versions.Count; i++)
                    if (versions[i].Tag == cfg.VersionTag) { pick = i; break; }
            if (pick < 0 && selected != null)
                for (int i = 0; i < versions.Count; i++)
                    if (versions[i].Tag == selected && versions[i].Prepared) { pick = i; break; }
            if (pick < 0)
                for (int i = 0; i < versions.Count; i++)
                    if (versions[i].Prepared) { pick = i; break; }
            if (pick < 0 && versions.Count > 0) pick = 0;
            if (pick >= 0) versionBox.SelectedIndex = pick;

            VersionChanged();
        }

        static List<VersionInfo> ScanVersions()
        {
            List<VersionInfo> list = new List<VersionInfo>();
            try
            {
                if (!Directory.Exists(Paths.Versions)) return list;
                foreach (string dir in Directory.GetDirectories(Paths.Versions))
                {
                    if (!Util.IsGameTag(Path.GetFileName(dir))) continue;
                    string exe = Path.Combine(dir, "horizon-client.exe");
                    if (!File.Exists(exe)) continue;

                    VersionInfo v = new VersionInfo();
                    v.Tag = Path.GetFileName(dir);
                    v.Dir = dir;
                    v.ClientExe = exe;
                    v.McVersion = McFind.McVersion;
                    try
                    {
                        string lockPath = Path.Combine(dir, "research-lock.json");
                        if (File.Exists(lockPath))
                        {
                            string mc = Util.FirstMatch(File.ReadAllText(lockPath),
                                "\"minecraft\"\\s*:\\s*\\{[^}]*?\"version\"\\s*:\\s*\"([^\"]+)\"");
                            if (mc != null) v.McVersion = mc;
                        }
                    }
                    catch { }
                    v.ResourcesDir = Path.Combine(dir, ".local", "horizon-client", v.McVersion);
                    v.Prepared = File.Exists(Path.Combine(v.ResourcesDir, "horizon-resources.json"));
                    v.Sounds = File.Exists(Path.Combine(v.ResourcesDir, "assets", "minecraft", "sounds.json"));
                    try { v.Sort = Directory.GetLastWriteTimeUtc(dir); }
                    catch { v.Sort = DateTime.MinValue; }
                    list.Add(v);
                }
            }
            catch { }
            list.Sort(delegate(VersionInfo a, VersionInfo b) { return b.Sort.CompareTo(a.Sort); });
            return list;
        }

        void RefreshWorlds()
        {
            worldList.Items.Clear();
            try
            {
                if (Directory.Exists(Paths.Saves))
                {
                    List<WorldItem> worlds = new List<WorldItem>();
                    foreach (string dir in Directory.GetDirectories(Paths.Saves))
                    {
                        WorldItem w = new WorldItem();
                        w.Folder = Path.GetFileName(dir);
                        try { w.Modified = Directory.GetLastWriteTime(dir); }
                        catch { w.Modified = DateTime.MinValue; }
                        worlds.Add(w);
                    }
                    worlds.Sort(delegate(WorldItem a, WorldItem b) { return b.Modified.CompareTo(a.Modified); });
                    foreach (WorldItem w in worlds)
                    {
                        worldList.Items.Add(w);
                        if (w.Folder == cfg.QuickWorldFolder) worldList.SelectedItem = w;
                    }
                }
            }
            catch { }
        }

        void ApplyConfigToUi()
        {
            nameBox.Text = cfg.PlayerName;
            fullscreenCheck.Checked = cfg.Fullscreen;
            defaultRdCheck.Checked = cfg.UseDefaultRenderDistance;
            renderDistance.Value = Math.Min(128, Math.Max(2, cfg.RenderDistance));
            customSizeCheck.Checked = cfg.CustomSize;
            widthBox.Value = Math.Min(16384, Math.Max(320, cfg.Width));
            heightBox.Value = Math.Min(16384, Math.Max(240, cfg.Height));
            serverBox.Text = cfg.Server;
            extraArgsBox.Text = cfg.ExtraArgs;
            launcherExeBox.Text = cfg.LauncherExe;
            quickWorldCheck.Checked = cfg.QuickWorld;
            newWorldCheck.Checked = cfg.NewWorld;
            newWorldName.Text = cfg.NewWorldName;
            newWorldSeed.Text = cfg.NewWorldSeed;
            newWorldCreative.Checked = cfg.NewWorldCreative;
            UpdateMinecraftLabel();
            RenderDistanceChanged();
            WorldModeChanged();
        }

        void SaveConfigFromUi()
        {
            cfg.PlayerName = nameBox.Text.Trim();
            VersionInfo v = versionBox.SelectedItem as VersionInfo;
            cfg.VersionTag = v != null ? v.Tag : "";
            cfg.Theme = theme.Amoled ? "amoled" : "light";
            cfg.LauncherExe = launcherExeBox.Text.Trim();
            cfg.Fullscreen = fullscreenCheck.Checked;
            cfg.UseDefaultRenderDistance = defaultRdCheck.Checked;
            cfg.RenderDistance = (int)renderDistance.Value;
            cfg.CustomSize = customSizeCheck.Checked;
            cfg.Width = (int)widthBox.Value;
            cfg.Height = (int)heightBox.Value;
            cfg.Server = serverBox.Text.Trim();
            cfg.ExtraArgs = extraArgsBox.Text.Trim();
            cfg.QuickWorld = quickWorldCheck.Checked;
            WorldItem w = worldList.SelectedItem as WorldItem;
            cfg.QuickWorldFolder = w != null ? w.Folder : cfg.QuickWorldFolder;
            cfg.NewWorld = newWorldCheck.Checked;
            cfg.NewWorldName = newWorldName.Text.Trim();
            cfg.NewWorldSeed = newWorldSeed.Text.Trim();
            cfg.NewWorldCreative = newWorldCreative.Checked;
            cfg.Save(Paths.CfgPath);
        }

        void UpdateMinecraftLabel()
        {
            if (mcDirLabel == null) return;
            string text = cfg.MinecraftDir.Length > 0 ? cfg.MinecraftDir : "not set \u2014 choose your Minecraft 26.3 folder";
            mcDirLabel.Text = text;
            mcDirLabel.ForeColor = cfg.MinecraftDir.Length > 0 ? theme.Text : theme.TextDim;
            tips.SetToolTip(mcDirLabel, text);
        }

        // ---------- events ----------

        void VersionChanged()
        {
            if (loading || statusLabel == null) return;
            VersionInfo v = versionBox.SelectedItem as VersionInfo;
            if (v == null)
            {
                statusLabel.ForeColor = theme.Danger;
                statusLabel.Text = "No Horizon version installed. Open the real launcher, install a\n" +
                                   "Nightly on its Versions page, then hit Refresh.";
                return;
            }
            if (!v.Prepared)
            {
                statusLabel.ForeColor = theme.Danger;
                statusLabel.Text = v.Tag + " is not prepared yet.\n" +
                                   "Launcher tab \u2192 \"Prepare selected version\" imports it from your\n" +
                                   "Minecraft " + v.McVersion + " folder. No account is involved.";
                return;
            }
            statusLabel.ForeColor = theme.Success;
            statusLabel.Text = "Prepared for Minecraft " + v.McVersion +
                               (v.Sounds ? ", with sound." : ", WITHOUT sound (prepare again to copy sounds).") +
                               "\n" + v.ResourcesDir;
        }

        void WorldModeChanged()
        {
            if (loading) return;
            worldList.Enabled = quickWorldCheck.Checked;
            newWorldName.Enabled = newWorldCheck.Checked;
            newWorldSeed.Enabled = newWorldCheck.Checked;
            newWorldCreative.Enabled = newWorldCheck.Checked;
            if (newWorldCheck.Checked && quickWorldCheck.Checked && newWorldCheck.Focused)
                quickWorldCheck.Checked = false;
            if (quickWorldCheck.Checked && newWorldCheck.Checked && quickWorldCheck.Focused)
                newWorldCheck.Checked = false;
        }

        void RenderDistanceChanged()
        {
            renderDistance.Enabled = !defaultRdCheck.Checked;
            widthBox.Enabled = customSizeCheck.Checked;
            heightBox.Enabled = customSizeCheck.Checked;
        }

        void BrowseMinecraft()
        {
            FolderBrowserDialog d = new FolderBrowserDialog();
            d.Description = "Pick the folder that holds versions\\26.3 (any launcher's .minecraft)";
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                cfg.MinecraftDir = d.SelectedPath;
                UpdateMinecraftLabel();
            }
        }

        void DetectMinecraft()
        {
            foreach (string root in McFind.Roots())
            {
                if (McFind.Validate(root) == null)
                {
                    cfg.MinecraftDir = root;
                    UpdateMinecraftLabel();
                    return;
                }
            }
            MessageBox.Show(this, "No Minecraft 26.3 files found in the usual places.\nPick the folder manually.", "Horizon Offline");
        }

        void BrowseLauncher()
        {
            OpenFileDialog d = new OpenFileDialog();
            d.Title = "Pick horizon-launcher.exe";
            d.Filter = "horizon-launcher.exe|horizon-launcher.exe|Programs (*.exe)|*.exe";
            if (d.ShowDialog(this) == DialogResult.OK)
                launcherExeBox.Text = d.FileName;
        }

        void RunLauncher(string subcommand, bool console)
        {
            string exe = launcherExeBox.Text.Trim();
            if (exe.Length == 0 || !File.Exists(exe))
            {
                MessageBox.Show(this, "Pick horizon-launcher.exe on this tab first.", "Horizon Offline");
                return;
            }
            try
            {
                if (console)
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/k \"" + Util.Quote(exe) + (subcommand.Length > 0 ? " " + subcommand : "") + "\"";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                else
                {
                    Process.Start(exe);
                }
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Horizon Offline"); }
        }

        void PrepareSelected()
        {
            VersionInfo v = versionBox.SelectedItem as VersionInfo;
            if (v == null) { MessageBox.Show(this, "No version selected.", "Horizon Offline"); return; }
            string exe = launcherExeBox.Text.Trim();
            if (exe.Length == 0 || !File.Exists(exe))
            {
                MessageBox.Show(this, "Pick horizon-launcher.exe on this tab first.", "Horizon Offline");
                return;
            }
            string mc = cfg.MinecraftDir;
            if (mc.Length == 0 || !Directory.Exists(mc) || McFind.Validate(mc) != null)
            {
                mc = null;
                foreach (string root in McFind.Roots())
                    if (McFind.Validate(root) == null) { mc = root; break; }
                if (mc == null)
                {
                    MessageBox.Show(this, "No valid Minecraft " + McFind.McVersion + " folder found.\nSet one above (\"Change\u2026\").", "Horizon Offline");
                    return;
                }
                cfg.MinecraftDir = mc;
                UpdateMinecraftLabel();
            }
            string cmd = Util.Quote(exe) + " prepare --version " + v.Tag + " --minecraft " + Util.Quote(mc);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/k \"" + cmd + "\"";
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Horizon Offline"); }
        }

        void PlayClicked()
        {
            if (playing) return;
            playing = true;
            SaveConfigFromUi();
            VersionInfo v = versionBox.SelectedItem as VersionInfo;
            if (v == null)
            {
                playing = false;
                MessageBox.Show(this, "No Horizon version installed (or none selected).", "Horizon Offline");
                return;
            }
            if (!v.Prepared)
            {
                playing = false;
                DialogResult r = MessageBox.Show(this,
                    v.Tag + " is not prepared yet.\n\nRun the launcher's prepare step now?",
                    "Horizon Offline", MessageBoxButtons.YesNo);
                if (r == DialogResult.Yes) PrepareSelected();
                return;
            }

            List<string> args = new List<string>();
            args.Add("--resources"); args.Add(v.ResourcesDir);
            if (cfg.PlayerName.Length > 0) { args.Add("--name"); args.Add(cfg.PlayerName); }

            if (cfg.QuickWorld && cfg.QuickWorldFolder.Length > 0)
            {
                if (Directory.Exists(Path.Combine(Paths.Saves, cfg.QuickWorldFolder)))
                {
                    args.Add("--quickPlaySingleplayer"); args.Add(cfg.QuickWorldFolder);
                }
            }
            else if (cfg.NewWorld && cfg.NewWorldName.Length > 0)
            {
                args.Add("--world"); args.Add(cfg.NewWorldName);
                if (cfg.NewWorldSeed.Length > 0) { args.Add("--seed"); args.Add(cfg.NewWorldSeed); }
                if (cfg.NewWorldCreative) args.Add("--creative");
            }

            if (cfg.Fullscreen) args.Add("--fullscreen");
            if (!cfg.UseDefaultRenderDistance)
            {
                args.Add("--render-distance");
                args.Add(cfg.RenderDistance.ToString());
            }
            if (cfg.CustomSize)
            {
                args.Add("--width"); args.Add(cfg.Width.ToString());
                args.Add("--height"); args.Add(cfg.Height.ToString());
            }
            if (cfg.Server.Length > 0) { args.Add("--server"); args.Add(cfg.Server); }
            if (cfg.ExtraArgs.Length > 0) args.AddRange(Util.SplitArgs(cfg.ExtraArgs));

            StringBuilder cmdline = new StringBuilder();
            foreach (string a in args)
            {
                if (cmdline.Length > 0) cmdline.Append(' ');
                cmdline.Append(Util.Quote(a));
            }

            try
            {
                Directory.CreateDirectory(Paths.GameHome);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = v.ClientExe;
                psi.Arguments = cmdline.ToString();
                psi.WorkingDirectory = Paths.GameHome;
                psi.UseShellExecute = false;
                Process.Start(psi);
                Close();
            }
            catch (Exception e)
            {
                playing = false;
                MessageBox.Show(this, "Could not start the game:\n\n" + e.Message, "Horizon Offline");
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool auto = false;
            string theme = null;
            string[] a = Environment.GetCommandLineArgs();
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i] == "--play" || a[i] == "/play") auto = true;
                else if ((a[i] == "--theme" || a[i] == "/theme") && i + 1 < a.Length) theme = a[++i];
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm f = new MainForm(theme);
            f.AutoPlay = auto;
            Application.Run(f);
        }
    }
}
