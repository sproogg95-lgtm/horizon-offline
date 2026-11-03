// Setup.cs — HorizonOfflineSetup.exe, a single-file installer.
//
// It: takes Minecraft 26.3 files (your own folder, or downloads from Mojang's public CDN),
// gets the official Horizon launcher (yours, or from GitHub), points it at those files,
// installs a version, caches the server jar, runs the launcher's ungated prepare step,
// then writes the config and shortcuts for HorizonOffline.exe (embedded as the "payload").
//
// Modes:  (no args)          the wizard
//         /S, --silent       unattended install with auto choices; writes install.log
//         --check-net        verifies the download endpoints; writes %TEMP%\horizon-offline-check.txt
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace HorizonOffline
{
    static class Net
    {
        public static void Init()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } // TLS 1.2
            catch { }
        }

        public static string GetString(string url)
        {
            Exception last = null;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "HorizonOfflineSetup/1.0";
                        wc.Encoding = Encoding.UTF8;
                        return wc.DownloadString(url);
                    }
                }
                catch (Exception e) { last = e; Thread.Sleep(1200); }
            }
            throw new Exception("GET " + url + " failed: " + last.Message);
        }

        public static void DownloadTo(string url, string path, Action<long, long> progress, Func<bool> cancelled)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string tmp = path + ".part";
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = "HorizonOfflineSetup/1.0";
                    req.Timeout = 60000;
                    req.ReadWriteTimeout = 60000;
                    using (WebResponse resp = req.GetResponse())
                    {
                        long total = resp.ContentLength;
                        using (Stream s = resp.GetResponseStream())
                        using (FileStream f = File.Create(tmp))
                        {
                            byte[] buf = new byte[262144];
                            long got = 0;
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                            {
                                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                                f.Write(buf, 0, n);
                                got += n;
                                if (progress != null) progress(got, total);
                            }
                        }
                    }
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { last = e; Thread.Sleep(1500); }
            }
            throw new Exception("download failed: " + url + " (" + last.Message + ")");
        }
    }

    /// <summary>The install steps, shared by the wizard and silent mode.</summary>
    class Engine
    {
        public string InstallDir = Paths.DefaultInstallDir;
        public string McDir;
        public bool McDownload;
        public string LauncherExe;
        public bool LauncherDownload;
        public bool DesktopLink = true;
        public bool StartMenuLink = true;

        public Action<string> Log = delegate { };
        public Action<string> Step = delegate { };
        public Action<double> Progress = delegate { }; // 0..1; < 0 = unknown
        public Func<bool> Cancelled = delegate { return false; };

        public string PreparedTag;
        public string Error;
        public bool Done;

        Process current;

        public void KillCurrent()
        {
            try { if (current != null) current.Kill(); }
            catch { }
        }

        public void Run()
        {
            try
            {
                Step("Preparing folders");
                Directory.CreateDirectory(InstallDir);
                string playerExe = Path.Combine(InstallDir, "HorizonOffline.exe");
                CopyPlayer(playerExe);
                Log("Player: " + playerExe);

                Step("Minecraft files");
                if (McDownload)
                {
                    McDir = Path.Combine(InstallDir, "minecraft");
                    DownloadMinecraft(McDir);
                }
                if (McDir == null || !Directory.Exists(McDir)) throw new Exception("No Minecraft folder to use.");
                Log("Minecraft " + McFind.McVersion + ": " + McDir);

                Step("Horizon launcher");
                if (LauncherDownload)
                    LauncherExe = DownloadLauncher(Path.Combine(InstallDir, "horizon-launcher"));
                if (LauncherExe == null || !File.Exists(LauncherExe)) throw new Exception("No horizon-launcher.exe to use.");
                Log("Launcher: " + LauncherExe);

                Step("Pointing the launcher at your Minecraft files");
                int code = Run(LauncherExe, "settings minecraft-dir " + Util.Quote(McDir), null);
                if (code != 0) Log("warning: settings exited with " + code);

                Step("Game version");
                string tag = NewestInstalled();
                if (tag == null)
                {
                    Log("No version installed yet \u2014 installing the latest Horizon release (downloads the game)");
                    code = Run(LauncherExe, "install latest", null);
                    tag = NewestInstalled();
                }
                if (tag == null) throw new Exception("No Horizon version could be installed (see the log).");
                Log("Version: " + tag);

                Step("Server jar");
                EnsureServerJar();

                Step("Preparing game resources (the long one)");
                if (IsPrepared(tag))
                {
                    Log("Already prepared \u2014 nothing to do");
                }
                else
                {
                    code = Run(LauncherExe, "prepare --version " + tag, null);
                    if (!IsPrepared(tag))
                        throw new Exception("prepare did not finish (exit " + code + ") \u2014 see the log above.");
                }

                Step("Settings and shortcuts");
                Config cfg = new Config();
                cfg.Theme = "amoled";
                cfg.MinecraftDir = McDir;
                cfg.LauncherExe = LauncherExe;
                cfg.VersionTag = tag;
                cfg.Save(Path.Combine(InstallDir, "horizon-offline.ini"));
                if (DesktopLink)
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    Shortcuts.Create(Path.Combine(desktop, "Horizon Offline.lnk"), playerExe, InstallDir, playerExe,
                        "Horizon (Minecraft 26.3 in Rust) without a Microsoft account");
                }
                if (StartMenuLink)
                {
                    string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    Shortcuts.Create(Path.Combine(programs, "Horizon Offline.lnk"), playerExe, InstallDir, playerExe,
                        "Horizon (Minecraft 26.3 in Rust) without a Microsoft account");
                }
                Log("Shortcuts written");

                PreparedTag = tag;
                Done = true;
            }
            catch (OperationCanceledException) { Error = "cancelled"; }
            catch (Exception e) { Error = e.Message; Log("ERROR: " + e.Message); }
        }

        // ----- steps -----

        static void CopyPlayer(string dest)
        {
            byte[] data = null;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload"))
            {
                if (s != null)
                {
                    MemoryStream ms = new MemoryStream();
                    s.CopyTo(ms);
                    data = ms.ToArray();
                }
            }
            if (data != null)
            {
                File.WriteAllBytes(dest, data);
                return;
            }
            string sibling = Path.Combine(Paths.ExeDir, "HorizonOffline.exe");
            if (File.Exists(sibling) && !string.Equals(sibling, dest, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sibling, dest, true);
                return;
            }
            throw new Exception("HorizonOffline.exe payload missing \u2014 build with build.ps1, or run this setup next to HorizonOffline.exe.");
        }

        public static string NewestInstalled()
        {
            try
            {
                string best = null;
                DateTime bestT = DateTime.MinValue;
                foreach (string d in Directory.GetDirectories(Paths.Versions))
                {
                    if (!Util.IsGameTag(Path.GetFileName(d))) continue;
                    if (!File.Exists(Path.Combine(d, "horizon-client.exe"))) continue;
                    DateTime t = Directory.GetLastWriteTimeUtc(d);
                    if (t > bestT) { bestT = t; best = Path.GetFileName(d); }
                }
                return best;
            }
            catch { return null; }
        }

        public static bool IsPrepared(string tag)
        {
            return File.Exists(Path.Combine(Paths.Versions, tag, ".local", "horizon-client", McFind.McVersion, "horizon-resources.json"));
        }

        void EnsureServerJar()
        {
            string jar = Path.Combine(Paths.ServerJars, McFind.McVersion + ".jar");
            try
            {
                if (File.Exists(jar) && new FileInfo(jar).Length > 1000000)
                {
                    if (string.Equals(Util.Sha1File(jar), McFind.ServerSha1, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Server jar already cached");
                        return;
                    }
                    File.Delete(jar);
                }
                Directory.CreateDirectory(Paths.ServerJars);
                Log("Downloading the " + McFind.McVersion + " server jar from Mojang (public, no account)");
                Net.DownloadTo(McFind.ServerUrl, jar, Pct, Cancelled);
                if (!string.Equals(Util.Sha1File(jar), McFind.ServerSha1, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("server jar hash mismatch");
                Log("Server jar cached (" + Util.Human(new FileInfo(jar).Length) + ")");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                Log("Could not cache the server jar (" + e.Message + ") \u2014 the launcher will try its own download.");
            }
        }

        void DownloadMinecraft(string root)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 256 * 1024 * 1024;

            Progress(-1);
            Log("Fetching Mojang's version manifest");
            Dictionary<string, object> manifest = (Dictionary<string, object>)js.DeserializeObject(
                Net.GetString("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"));
            string vurl = null;
            foreach (object o in (object[])manifest["versions"])
            {
                Dictionary<string, object> v = (Dictionary<string, object>)o;
                if ((string)v["id"] == McFind.McVersion) { vurl = (string)v["url"]; break; }
            }
            if (vurl == null) throw new Exception("Mojang's manifest has no " + McFind.McVersion);
            string versionJson = Net.GetString(vurl);
            Dictionary<string, object> vj = (Dictionary<string, object>)js.DeserializeObject(versionJson);

            string versionDir = Path.Combine(root, "versions", McFind.McVersion);
            Directory.CreateDirectory(versionDir);
            File.WriteAllText(Path.Combine(versionDir, McFind.McVersion + ".json"), versionJson);

            Dictionary<string, object> dls = (Dictionary<string, object>)vj["downloads"];
            Dictionary<string, object> clientDl = (Dictionary<string, object>)dls["client"];
            string jar = Path.Combine(versionDir, McFind.McVersion + ".jar");
            if (!(File.Exists(jar) && string.Equals(Util.Sha1File(jar), McFind.ClientSha1, StringComparison.OrdinalIgnoreCase)))
            {
                Log("Downloading the client jar (~41 MB)");
                Net.DownloadTo((string)clientDl["url"], jar, Pct, Cancelled);
                if (!string.Equals(Util.Sha1File(jar), McFind.ClientSha1, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("client jar hash mismatch");
            }
            Log("Client jar saved");

            // libraries
            int libDone = 0, libTotal = 0;
            object[] libs = vj.ContainsKey("libraries") ? (object[])vj["libraries"] : new object[0];
            foreach (object lo in libs)
            {
                if (LibraryAllowed((Dictionary<string, object>)lo)) libTotal++;
            }
            Progress(-1);
            Log("Downloading libraries (" + libTotal + " files)");
            foreach (object lo in libs)
            {
                Dictionary<string, object> lib = (Dictionary<string, object>)lo;
                if (!LibraryAllowed(lib)) continue;
                Dictionary<string, object> dd = lib.ContainsKey("downloads") ? (Dictionary<string, object>)lib["downloads"] : null;
                if (dd == null || !dd.ContainsKey("artifact") || dd["artifact"] == null) continue;
                Dictionary<string, object> art = (Dictionary<string, object>)dd["artifact"];
                if (!art.ContainsKey("url")) continue;
                string rel = ((string)art["path"]).Replace('/', Path.DirectorySeparatorChar);
                string file = Path.Combine(root, "libraries", rel);
                if (!(File.Exists(file) && new FileInfo(file).Length == Convert.ToInt64(art["size"])))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    Net.DownloadTo((string)art["url"], file, null, Cancelled);
                }
                libDone++;
                if (libDone % 10 == 0) { Progress((double)libDone / Math.Max(1, libTotal) * 0.25); }
            }

            // asset index + objects
            Dictionary<string, object> ai = (Dictionary<string, object>)vj["assetIndex"];
            Directory.CreateDirectory(Path.Combine(root, "assets", "indexes"));
            string idx = Path.Combine(root, "assets", "indexes", (string)ai["id"] + ".json");
            Log("Downloading the asset index");
            Net.DownloadTo((string)ai["url"], idx, Pct, Cancelled);
            Dictionary<string, object> index = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(idx));
            Dictionary<string, object> objects = (Dictionary<string, object>)index["objects"];

            int total = objects.Count, done = 0;
            Log("Downloading assets (" + total + " files, ~470 MB)");
            foreach (KeyValuePair<string, object> kv in objects)
            {
                if (Cancelled()) throw new OperationCanceledException();
                Dictionary<string, object> obj = (Dictionary<string, object>)kv.Value;
                string hash = (string)obj["hash"];
                long size = Convert.ToInt64(obj["size"]);
                string two = hash.Substring(0, 2);
                string dir2 = Path.Combine(root, "assets", "objects", two);
                string file = Path.Combine(dir2, hash);
                if (!(File.Exists(file) && new FileInfo(file).Length == size))
                {
                    Directory.CreateDirectory(dir2);
                    Net.DownloadTo("https://resources.download.minecraft.net/" + two + "/" + hash, file, null, Cancelled);
                }
                done++;
                if (done % 25 == 0 || done == total)
                {
                    Progress(0.25 + 0.75 * ((double)done / Math.Max(1, total)));
                    if (done % 250 == 0) Log("assets: " + done + " / " + total);
                }
            }
            Progress(1);
            Log("Minecraft " + McFind.McVersion + " downloaded (" + total + " asset files)");
        }

        static bool LibraryAllowed(Dictionary<string, object> lib)
        {
            if (!lib.ContainsKey("rules")) return true;
            bool allow = false;
            foreach (object ro in (object[])lib["rules"])
            {
                Dictionary<string, object> r = (Dictionary<string, object>)ro;
                bool applies = true;
                if (r.ContainsKey("os"))
                {
                    Dictionary<string, object> os = (Dictionary<string, object>)r["os"];
                    if (os.ContainsKey("name"))
                        applies = string.Equals((string)os["name"], "windows", StringComparison.OrdinalIgnoreCase);
                }
                if (applies) allow = (string)r["action"] == "allow";
            }
            return allow;
        }

        string DownloadLauncher(string destDir)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 256 * 1024 * 1024;
            Log("Looking up the newest Horizon launcher release on GitHub");
            string url = null, tag = null;
            for (int page = 1; page <= 3 && url == null; page++)
            {
                object[] releases = (object[])js.DeserializeObject(
                    Net.GetString("https://api.github.com/repos/horizon-rust/horizon/releases?per_page=100&page=" + page));
                foreach (object ro in releases)
                {
                    Dictionary<string, object> r = (Dictionary<string, object>)ro;
                    string t = r.ContainsKey("tag_name") ? (string)r["tag_name"] : null;
                    if (t == null || !t.StartsWith("launcher")) continue; // launcher@0.2.x
                    foreach (object ao in (object[])r["assets"])
                    {
                        Dictionary<string, object> a = (Dictionary<string, object>)ao;
                        if (string.Equals((string)a["name"], "horizon-launcher-windows-x64.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            url = (string)a["browser_download_url"];
                            tag = t;
                            break;
                        }
                    }
                    if (url != null) break;
                }
            }
            if (url == null)
                throw new Exception("No launcher download found on GitHub (API rate limit?). Download horizon-launcher-windows-x64.zip yourself from github.com/horizon-rust/horizon/releases, then run setup again and pick it with Browse.");
            Log("Downloading " + tag);
            string zip = Path.Combine(Path.GetTempPath(), "horizon-launcher-setup.zip");
            Net.DownloadTo(url, zip, Pct, Cancelled);
            Directory.CreateDirectory(destDir);
            string exe = Path.Combine(destDir, "horizon-launcher.exe");
            using (ZipArchive za = ZipFile.OpenRead(zip))
            {
                foreach (ZipArchiveEntry e in za.Entries)
                {
                    if (e.Name.Equals("horizon-launcher.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        e.ExtractToFile(exe, true);
                        break;
                    }
                }
            }
            try { File.Delete(zip); } catch { }
            if (!File.Exists(exe)) throw new Exception("The launcher zip had no horizon-launcher.exe.");
            Log("Launcher saved to " + exe);
            return exe;
        }

        void Pct(long got, long total)
        {
            Progress(total > 0 ? (double)got / total : -1);
        }

        int Run(string exe, string args, string cwd)
        {
            Log("> " + Path.GetFileName(exe) + " " + args);
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            if (cwd != null) psi.WorkingDirectory = cwd;
            Process p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Log(e.Data); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Log(e.Data); };
            current = p;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            while (!p.WaitForExit(500))
            {
                if (Cancelled())
                {
                    try { p.Kill(); } catch { }
                    current = null;
                    throw new OperationCanceledException();
                }
            }
            p.WaitForExit();
            current = null;
            return p.ExitCode;
        }
    }

    class SetupForm : Form
    {
        Theme theme = Theme.Make(true);
        Engine engine = new Engine();

        Panel body;
        Button backBtn, nextBtn, cancelBtn;
        Label stepHead, stepSub;

        Panel pWelcome, pFiles, pLauncher, pOptions, pProgress, pDone;
        Panel barWrap, barFill;
        Label barLbl;

        RadioButton rbLocal, rbDownload;
        ListBox candList;
        Label filesNote;
        List<McCandidate> candidates = new List<McCandidate>();

        RadioButton rbLauncherFound, rbLauncherDownload;
        Label launcherFoundLbl;
        string foundLauncher = "";

        TextBox dirBox;
        CheckBox deskChk, startChk;

        Label stepLbl;
        TextBox logBox;

        Label doneLbl;

        int page;
        bool running;
        bool cancelRequested;
        StringBuilder fullLog = new StringBuilder();

        public SetupForm()
        {
            Text = "Horizon Offline Setup";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(720, 520);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 70;
            header.Tag = "surface";

            Label t = new Label();
            t.Text = "Horizon Offline Setup";
            t.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            t.Location = new Point(22, 12);
            t.AutoSize = true;
            header.Controls.Add(t);

            Label s = new Label();
            s.Text = "Horizon \u2014 Minecraft 26.3 in Rust \u2014 without a Microsoft account";
            s.Tag = "dim";
            s.Location = new Point(24, 42);
            s.AutoSize = true;
            header.Controls.Add(s);

            Panel accent = new Panel();
            accent.Height = 2;
            accent.Dock = DockStyle.Bottom;
            accent.BackColor = theme.Accent;
            header.Controls.Add(accent);

            body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Tag = "back";
            body.Padding = new Padding(20, 14, 20, 10);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 58;
            footer.Tag = "surface";

            // Dock order: the control added LAST is docked first (outermost); body fills last.
            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(header);

            backBtn = MakeFooterButton(footer, "Back", 22);
            backBtn.Click += delegate { GoBack(); };
            nextBtn = MakeFooterButton(footer, "Continue", 596);
            nextBtn.Tag = "primary";
            nextBtn.Click += delegate { GoNext(); };
            cancelBtn = MakeFooterButton(footer, "Cancel", 596);
            cancelBtn.Visible = false;
            cancelBtn.Click += delegate { cancelRequested = true; cancelBtn.Text = "Cancelling\u2026"; };
            footer.Resize += delegate
            {
                nextBtn.Left = footer.Width - 140 - 22;
                cancelBtn.Left = footer.Width - 140 - 22;
                stepSub.Left = 24 + backBtn.Width + 16;
            };

            stepHead = new Label();
            stepHead.Text = "Horizon Offline Setup";
            stepHead.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            stepHead.Location = new Point(24, 14);
            stepHead.AutoSize = true;
            footer.Controls.Add(stepHead);
            stepHead.Visible = false;

            stepSub = new Label();
            stepSub.Tag = "dim";
            stepSub.Location = new Point(200, 19);
            stepSub.AutoSize = true;
            footer.Controls.Add(stepSub);
            stepSub.Visible = false;

            BuildWelcome();
            BuildFiles();
            BuildLauncherPage();
            BuildOptions();
            BuildProgress();
            BuildDone();

            ShowPage(0);
            Skins.Apply(this, theme);
            Win32.DarkTitleBar(this, true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Win32.DarkTitleBar(this, true);
        }

        Button MakeFooterButton(Control parent, string text, int x)
        {
            Button b = new Button();
            b.Text = text;
            b.Size = new Size(140, 34);
            b.Location = new Point(x, 12);
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            parent.Controls.Add(b);
            return b;
        }

        Panel NewPage()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.Tag = "back";
            p.Visible = false;
            body.Controls.Add(p);
            return p;
        }

        static Label Head(Control parent, string text, Point at)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            l.Location = at;
            l.AutoSize = true;
            parent.Controls.Add(l);
            return l;
        }

        static Label Para(Control parent, string text, Point at, int width)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = at;
            l.Size = new Size(width, 200);
            l.AutoSize = false;
            l.Tag = "dim";
            parent.Controls.Add(l);
            return l;
        }

        void BuildWelcome()
        {
            pWelcome = NewPage();
            Head(pWelcome, "Welcome", new Point(20, 16));
            Para(pWelcome,
                "This installer sets up everything needed to play Horizon \u2014 Minecraft: Java Edition 26.3\r\n" +
                "rewritten in Rust \u2014 without a Microsoft account:",
                new Point(20, 52), 690);
            Para(pWelcome,
                "\u2022  Minecraft 26.3 game files: from a folder already on this PC, or downloaded from\r\n" +
                "    Mojang's public file server (no account is used, none is asked for).\r\n" +
                "\r\n" +
                "\u2022  The official Horizon launcher, used only on the command line: it installs game\r\n" +
                "    updates and prepares the assets. Its Play button \u2014 the part that wants a\r\n" +
                "    Microsoft account \u2014 is never used.\r\n" +
                "\r\n" +
                "\u2022  HorizonOffline.exe, a small launcher that starts the game fully offline with your\r\n" +
                "    own player name, plus a desktop shortcut.\r\n" +
                "\r\n" +
                "Needs about 2 GB of disk with the download option. The prepare step at the end takes\r\n" +
                "5\u201315 minutes; everything before it is quick.",
                new Point(20, 92), 690);
        }

        void BuildFiles()
        {
            pFiles = NewPage();
            Head(pFiles, "Minecraft 26.3 files", new Point(20, 16));
            Para(pFiles, "Horizon builds its own assets from a vanilla Minecraft 26.3 copy. Pick one found below,\r\nor let the installer download it from Mojang.",
                new Point(20, 52), 690);

            rbLocal = new RadioButton();
            rbLocal.Text = "Use Minecraft 26.3 already on this PC";
            rbLocal.Location = new Point(20, 96);
            rbLocal.AutoSize = true;
            pFiles.Controls.Add(rbLocal);

            candList = new ListBox();
            candList.Location = new Point(40, 122);
            candList.Size = new Size(600, 120);
            candList.SelectedIndexChanged += delegate { ValidateSelection(); };
            pFiles.Controls.Add(candList);

            Button refresh = new Button();
            refresh.Text = "Refresh";
            refresh.Size = new Size(90, 28);
            refresh.Location = new Point(650, 122);
            refresh.Click += delegate { ScanCandidates(); };
            pFiles.Controls.Add(refresh);

            Button browse = new Button();
            browse.Text = "Browse\u2026";
            browse.Size = new Size(90, 28);
            browse.Location = new Point(650, 156);
            browse.Click += delegate { BrowseFolderInto(); };
            pFiles.Controls.Add(browse);

            rbDownload = new RadioButton();
            rbDownload.Text = "Download vanilla 26.3 from Mojang's public file server (no account, ~700 MB)";
            rbDownload.Location = new Point(20, 258);
            rbDownload.AutoSize = true;
            pFiles.Controls.Add(rbDownload);

            filesNote = new Label();
            filesNote.Location = new Point(20, 296);
            filesNote.Size = new Size(700, 80);
            filesNote.Tag = "dim";
            pFiles.Controls.Add(filesNote);
        }

        void BuildLauncherPage()
        {
            pLauncher = NewPage();
            Head(pLauncher, "Horizon launcher", new Point(20, 16));
            Para(pLauncher,
                "The official launcher is only used on the command line (install, prepare, update).\r\n" +
                "If you already have horizon-launcher.exe, point at it; otherwise the installer fetches the\r\n" +
                "newest release from GitHub (\u2248 40 MB).",
                new Point(20, 52), 690);

            rbLauncherFound = new RadioButton();
            rbLauncherFound.Text = "Use horizon-launcher.exe found at:";
            rbLauncherFound.Location = new Point(20, 122);
            rbLauncherFound.AutoSize = true;
            pLauncher.Controls.Add(rbLauncherFound);

            launcherFoundLbl = new Label();
            launcherFoundLbl.Location = new Point(40, 148);
            launcherFoundLbl.Size = new Size(560, 20);
            launcherFoundLbl.AutoEllipsis = true;
            launcherFoundLbl.Tag = "dim";
            pLauncher.Controls.Add(launcherFoundLbl);

            Button browse = new Button();
            browse.Text = "Browse\u2026";
            browse.Size = new Size(90, 28);
            browse.Location = new Point(610, 144);
            browse.Click += delegate
            {
                OpenFileDialog d = new OpenFileDialog();
                d.Filter = "horizon-launcher.exe|horizon-launcher.exe|Programs (*.exe)|*.exe";
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    foundLauncher = d.FileName;
                    launcherFoundLbl.Text = foundLauncher;
                    rbLauncherFound.Checked = true;
                }
            };
            pLauncher.Controls.Add(browse);

            rbLauncherDownload = new RadioButton();
            rbLauncherDownload.Text = "Download the newest Horizon launcher from GitHub";
            rbLauncherDownload.Location = new Point(20, 190);
            rbLauncherDownload.AutoSize = true;
            pLauncher.Controls.Add(rbLauncherDownload);
        }

        void BuildOptions()
        {
            pOptions = NewPage();
            Head(pOptions, "Install options", new Point(20, 16));

            Label l = new Label();
            l.Text = "Install folder";
            l.Tag = "dim";
            l.Location = new Point(20, 60);
            l.AutoSize = true;
            pOptions.Controls.Add(l);

            dirBox = new TextBox();
            dirBox.Text = Paths.DefaultInstallDir;
            dirBox.Location = new Point(20, 82);
            dirBox.Width = 580;
            pOptions.Controls.Add(dirBox);

            Button b = new Button();
            b.Text = "Browse\u2026";
            b.Size = new Size(90, 28);
            b.Location = new Point(610, 80);
            b.Click += delegate
            {
                FolderBrowserDialog d = new FolderBrowserDialog();
                d.Description = "Pick the install folder";
                if (d.ShowDialog(this) == DialogResult.OK) dirBox.Text = d.SelectedPath;
            };
            pOptions.Controls.Add(b);

            deskChk = new CheckBox();
            deskChk.Text = "Desktop shortcut";
            deskChk.Checked = true;
            deskChk.Location = new Point(20, 140);
            deskChk.AutoSize = true;
            pOptions.Controls.Add(deskChk);

            startChk = new CheckBox();
            startChk.Text = "Start menu shortcut";
            startChk.Checked = true;
            startChk.Location = new Point(220, 140);
            startChk.AutoSize = true;
            pOptions.Controls.Add(startChk);

            Para(pOptions,
                "Worlds, options and logs live in %APPDATA%\\horizon \u2014 shared with any other Horizon\r\ninstall you may have \u2014 so updating or reinstalling never touches your saves.",
                new Point(20, 190), 690);
        }

        void BuildProgress()
        {
            pProgress = NewPage();
            stepLbl = new Label();
            stepLbl.Text = "Starting\u2026";
            stepLbl.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            stepLbl.Location = new Point(20, 20);
            stepLbl.AutoSize = true;
            pProgress.Controls.Add(stepLbl);

            barWrap = new Panel();
            barWrap.Location = new Point(20, 60);
            barWrap.Size = new Size(700, 8);
            barWrap.Tag = "alt";
            pProgress.Controls.Add(barWrap);
            barFill = new Panel();
            barFill.Height = 8;
            barFill.Width = 0;
            barFill.Location = new Point(0, 0);
            barWrap.Controls.Add(barFill);

            barLbl = new Label();
            barLbl.Location = new Point(20, 76);
            barLbl.AutoSize = true;
            barLbl.Tag = "dim";
            barLbl.Text = "";
            pProgress.Controls.Add(barLbl);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.WordWrap = false;
            logBox.Location = new Point(20, 106);
            logBox.Size = new Size(700, 280);
            logBox.Font = new Font("Consolas", 8.25f);
            pProgress.Controls.Add(logBox);
        }

        void BuildDone()
        {
            pDone = NewPage();
            Head(pDone, "All set", new Point(20, 16));

            doneLbl = Para(pDone, "", new Point(20, 60), 690);

            Button play = new Button();
            play.Text = "\u25B6  Play Horizon now";
            play.Size = new Size(220, 44);
            play.Location = new Point(20, 220);
            play.Tag = "primary";
            play.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            play.Click += delegate
            {
                try { Process.Start(Path.Combine(engine.InstallDir, "HorizonOffline.exe")); }
                catch (Exception e) { MessageBox.Show(this, e.Message, "Horizon Offline"); }
            };
            pDone.Controls.Add(play);

            Button open = new Button();
            open.Text = "Open install folder";
            open.Size = new Size(170, 44);
            open.Location = new Point(252, 220);
            open.Click += delegate { Util.OpenFolder(engine.InstallDir); };
            pDone.Controls.Add(open);
        }

        // ---------- wizard flow ----------

        void ShowPage(int index)
        {
            page = index;
            Panel[] all = new Panel[] { pWelcome, pFiles, pLauncher, pOptions, pProgress, pDone };
            for (int i = 0; i < all.Length; i++) all[i].Visible = i == index;

            backBtn.Visible = index >= 1 && index <= 3 && !running;
            cancelBtn.Visible = index == 4;
            nextBtn.Visible = index != 4;
            nextBtn.Text = index == 3 ? "Install" : (index == 5 ? "Finish" : "Continue");
            stepHead.Visible = index == 4;
            stepSub.Visible = index == 4;
        }

        void GoBack()
        {
            if (running) return;
            if (page > 0) ShowPage(page - 1);
        }

        void GoNext()
        {
            if (page == 0) { ShowPage(1); if (candidates.Count == 0) ScanCandidates(); return; }
            if (page == 1)
            {
                if (rbLocal.Checked)
                {
                    McCandidate c = candList.SelectedItem as McCandidate;
                    if (c == null) { filesNote.Text = "Pick a folder from the list, or choose the download option."; filesNote.Tag = "danger"; Skins.Apply(this, theme); return; }
                    if (!c.Ok)
                    {
                        string why = McFind.Validate(c.Dir);
                        if (why != null) { c.Ok = false; c.Status = why; RefreshCandList(); filesNote.Text = "That folder can't be used: " + why; filesNote.Tag = "danger"; Skins.Apply(this, theme); return; }
                        c.Ok = true; c.Status = "vanilla 26.3 \u2713"; RefreshCandList();
                    }
                }
                ShowPage(2);
                DetectLauncher();
                return;
            }
            if (page == 2)
            {
                if (rbLauncherFound.Checked && (foundLauncher.Length == 0 || !File.Exists(foundLauncher)))
                {
                    MessageBox.Show(this, "Point at a horizon-launcher.exe first, or pick the download option.", "Horizon Offline Setup");
                    return;
                }
                ShowPage(3);
                return;
            }
            if (page == 3) { StartInstall(); return; }
            if (page == 5) { Close(); return; }
            if (page == 4 && !running) { Close(); return; } // failed install: Next became "Close"
        }

        // ---------- files page ----------

        void ScanCandidates()
        {
            candidates.Clear();
            List<string> roots = McFind.Roots();
            string instMc = Path.Combine(engine.InstallDir, "minecraft");
            if (Directory.Exists(instMc)) roots.Insert(0, instMc);
            foreach (string r in roots)
            {
                if (!McFind.LooksLike(r)) continue;
                McCandidate c = new McCandidate();
                c.Dir = r;
                c.Status = "checking\u2026";
                candidates.Add(c);
            }
            RefreshCandList();

            Thread t = new Thread(delegate()
            {
                foreach (McCandidate c in candidates)
                {
                    string why = McFind.Validate(c.Dir);
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            c.Ok = why == null;
                            c.Status = why == null ? "vanilla 26.3 \u2713" : why;
                            RefreshCandList();
                        }));
                    }
                    catch { }
                }
                try { BeginInvoke(new Action(delegate { PickDefaultCandidate(); })); }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void PickDefaultCandidate()
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Ok)
                {
                    candList.SelectedIndex = i;
                    rbLocal.Checked = true;
                    filesNote.Text = "Found a usable copy \u2014 the installer will use it.";
                    return;
                }
            }
            rbDownload.Checked = true;
            filesNote.Text = "No usable 26.3 copy found on this PC \u2014 the download option is selected instead.";
        }

        void RefreshCandList()
        {
            int sel = candList.SelectedIndex;
            candList.Items.Clear();
            foreach (McCandidate c in candidates) candList.Items.Add(c);
            if (sel >= 0 && sel < candList.Items.Count) candList.SelectedIndex = sel;
        }

        void ValidateSelection()
        {
            McCandidate c = candList.SelectedItem as McCandidate;
            if (c != null) rbLocal.Checked = true;
        }

        void BrowseFolderInto()
        {
            FolderBrowserDialog d = new FolderBrowserDialog();
            d.Description = "Pick the Minecraft folder that holds versions\\26.3";
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string why = McFind.Validate(d.SelectedPath);
            McCandidate c = new McCandidate();
            c.Dir = d.SelectedPath;
            c.Ok = why == null;
            c.Status = why == null ? "vanilla 26.3 \u2713" : why;
            candidates.Insert(0, c);
            RefreshCandList();
            candList.SelectedIndex = 0;
            rbLocal.Checked = true;
        }

        // ---------- launcher page ----------

        void DetectLauncher()
        {
            if (foundLauncher.Length > 0 && File.Exists(foundLauncher)) { rbLauncherFound.Checked = true; return; }
            string[] candidates = new string[]
            {
                Path.Combine(Paths.DefaultInstallDir, "horizon-launcher", "horizon-launcher.exe"),
                Path.Combine(Paths.LocalAppData, "Programs", "horizon-launcher", "horizon-launcher.exe"),
                Path.Combine(Paths.LocalAppData, "horizon-launcher", "horizon-launcher.exe"),
                Path.Combine(Paths.Roaming, "horizon-launcher", "horizon-launcher.exe")
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c)) { foundLauncher = c; break; }
            }
            if (foundLauncher.Length > 0)
            {
                launcherFoundLbl.Text = foundLauncher;
                rbLauncherFound.Checked = true;
            }
            else
            {
                launcherFoundLbl.Text = "none found";
                rbLauncherDownload.Checked = true;
            }
        }

        // ---------- install ----------

        void StartInstall()
        {
            running = true;
            engine.InstallDir = dirBox.Text.Trim();
            if (engine.InstallDir.Length == 0) engine.InstallDir = Paths.DefaultInstallDir;
            engine.McDownload = rbDownload.Checked;
            if (!engine.McDownload)
            {
                McCandidate c = candList.SelectedItem as McCandidate;
                if (c != null) engine.McDir = c.Dir;
            }
            engine.LauncherDownload = !rbLauncherFound.Checked;
            engine.LauncherExe = rbLauncherFound.Checked ? foundLauncher : null;
            engine.DesktopLink = deskChk.Checked;
            engine.StartMenuLink = startChk.Checked;

            engine.Log = delegate(string s) { Append(s); };
            engine.Step = delegate(string s) { SetStep(s); };
            engine.Progress = delegate(double p) { SetBar(p); };
            engine.Cancelled = delegate { return cancelRequested; };

            cancelRequested = false;
            ShowPage(4);
            barFill.BackColor = theme.Accent;
            Skins.Apply(this, theme);
            backBtn.Visible = false;

            Thread t = new Thread(delegate()
            {
                engine.Run();
                try { BeginInvoke(new Action(InstallFinished)); }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Append(string s)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(delegate { Append(s); })); }
                catch { }
                return;
            }
            fullLog.AppendLine(s);
            logBox.AppendText(s + "\r\n");
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        void SetStep(string s)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(delegate { SetStep(s); })); }
                catch { }
                return;
            }
            stepLbl.Text = s;
            stepHead.Text = s;
            SetBar(-1);
        }

        void SetBar(double p)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(delegate { SetBar(p); })); }
                catch { }
                return;
            }
            if (p < 0)
            {
                barFill.Width = barWrap.Width;
                barFill.BackColor = theme.Border;
                barLbl.Text = "working\u2026";
            }
            else
            {
                if (p > 1) p = 1;
                barFill.Width = (int)(barWrap.Width * p);
                barFill.BackColor = theme.Accent;
                barLbl.Text = ((int)(p * 100)) + "%";
            }
        }

        void InstallFinished()
        {
            running = false;
            cancelBtn.Visible = false;
            nextBtn.Visible = true;

            if (engine.Done)
            {
                doneLbl.Text =
                    "Horizon " + engine.PreparedTag + " is ready.\r\n\r\n" +
                    "Player:      " + Path.Combine(engine.InstallDir, "HorizonOffline.exe") + "\r\n" +
                    "Launcher:  " + engine.LauncherExe + "\r\n" +
                    "Minecraft: " + engine.McDir + "\r\n\r\n" +
                    "Start it from the desktop shortcut \u2014 pick a version, set your offline name, and Play.\r\n" +
                    "To update later: the Launcher tab inside the player, or run the real Horizon launcher.";
                ShowPage(5);
            }
            else
            {
                stepLbl.Text = engine.Error == "cancelled" ? "Cancelled" : "Install failed";
                nextBtn.Text = "Close";
                nextBtn.Visible = true;
                backBtn.Visible = false;
                MessageBox.Show(this,
                    (engine.Error == "cancelled" ? "Install cancelled." : "Install failed: " + engine.Error) +
                    "\r\n\r\nThe log above has the details. Fix the problem and run setup again \u2014 finished steps are skipped.",
                    "Horizon Offline Setup");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running)
            {
                DialogResult r = MessageBox.Show(this, "The install is still running. Quit anyway?", "Horizon Offline Setup", MessageBoxButtons.YesNo);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                cancelRequested = true;
                engine.KillCurrent();
            }
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Net.Init();
            string[] a = Environment.GetCommandLineArgs();
            bool silent = false, check = false;
            foreach (string s in a)
            {
                if (s == "/S" || s == "/silent" || s == "--silent") silent = true;
                if (s == "--check-net") check = true;
            }
            if (check) { Environment.Exit(CheckNet()); return; }
            if (silent) { Environment.Exit(Silent()); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }

        static string FindLauncherExe()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Paths.DefaultInstallDir, "horizon-launcher", "horizon-launcher.exe"),
                Path.Combine(Paths.LocalAppData, "Programs", "horizon-launcher", "horizon-launcher.exe"),
                Path.Combine(Paths.LocalAppData, "horizon-launcher", "horizon-launcher.exe"),
                Path.Combine(Paths.Roaming, "horizon-launcher", "horizon-launcher.exe")
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>Unattended install: auto-picks a Minecraft folder (or downloads), and a launcher (or downloads).</summary>
        static int Silent()
        {
            Engine e = new Engine();
            foreach (string r in McFind.Roots())
            {
                if (McFind.Validate(r) == null) { e.McDir = r; break; }
            }
            if (e.McDir == null)
            {
                string inst = Path.Combine(Paths.DefaultInstallDir, "minecraft");
                if (Directory.Exists(inst) && McFind.Validate(inst) == null) e.McDir = inst;
            }
            if (e.McDir == null) e.McDownload = true;

            string found = FindLauncherExe();
            if (found != null) e.LauncherExe = found;
            else e.LauncherDownload = true;

            StringBuilder log = new StringBuilder();
            object gate = new object();
            e.Log = delegate(string s) { lock (gate) { log.AppendLine(s); } };
            e.Run();
            Directory.CreateDirectory(e.InstallDir);
            lock (gate) { File.WriteAllText(Path.Combine(e.InstallDir, "install.log"), log.ToString()); }
            return e.Done ? 0 : 1;
        }

        /// <summary>Network self test; writes %TEMP%\horizon-offline-check.txt.</summary>
        static int CheckNet()
        {
            StringBuilder o = new StringBuilder();
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 256 * 1024 * 1024;
            try
            {
                Dictionary<string, object> manifest = (Dictionary<string, object>)js.DeserializeObject(
                    Net.GetString("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"));
                string vurl = null;
                foreach (object vv in (object[])manifest["versions"])
                {
                    Dictionary<string, object> v = (Dictionary<string, object>)vv;
                    if ((string)v["id"] == "26.3") { vurl = (string)v["url"]; break; }
                }
                o.AppendLine("manifest: ok, 26.3 at " + (vurl ?? "NOT FOUND"));
            }
            catch (Exception e) { o.AppendLine("manifest: ERROR " + e.Message); }
            try
            {
                object[] releases = (object[])js.DeserializeObject(
                    Net.GetString("https://api.github.com/repos/horizon-rust/horizon/releases?per_page=100&page=1"));
                string tag = null, url = null;
                foreach (object ro in releases)
                {
                    Dictionary<string, object> r = (Dictionary<string, object>)ro;
                    string t = r.ContainsKey("tag_name") ? (string)r["tag_name"] : null;
                    if (t == null || !t.StartsWith("launcher")) continue;
                    foreach (object ao in (object[])r["assets"])
                    {
                        Dictionary<string, object> a2 = (Dictionary<string, object>)ao;
                        if (string.Equals((string)a2["name"], "horizon-launcher-windows-x64.zip", StringComparison.OrdinalIgnoreCase)) { tag = t; url = (string)a2["browser_download_url"]; break; }
                    }
                    if (url != null) break;
                }
                o.AppendLine("launcher release: " + (tag ?? "NOT FOUND") + " " + (url ?? ""));
                if (url != null)
                {
                    string zip = Path.Combine(Path.GetTempPath(), "horizon-launcher-check.zip");
                    Net.DownloadTo(url, zip, null, null);
                    string exeDir = Path.Combine(Path.GetTempPath(), "horizon-launcher-check");
                    Directory.CreateDirectory(exeDir);
                    string exe = Path.Combine(exeDir, "horizon-launcher.exe");
                    using (ZipArchive za = ZipFile.OpenRead(zip))
                    {
                        foreach (ZipArchiveEntry en in za.Entries)
                        {
                            if (en.Name.Equals("horizon-launcher.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                en.ExtractToFile(exe, true);
                                break;
                            }
                        }
                    }
                    o.AppendLine("launcher zip: downloaded " + Util.Human(new FileInfo(zip).Length) + ", exe extracted: " + File.Exists(exe));
                    try { File.Delete(zip); } catch { }
                }
            }
            catch (Exception e) { o.AppendLine("launcher release: ERROR " + e.Message); }
            string file = Path.Combine(Path.GetTempPath(), "horizon-offline-check.txt");
            File.WriteAllText(file, o.ToString());
            return 0;
        }
    }
}
