// Usage Tracker: a tiny desktop widget that shows 5-hour and weekly usage limits
// for AI agents (Claude, Codex/ChatGPT, or anything a command can report).
// Built by build.ps1 with the C# compiler that ships with .NET Framework 4.8.
// Language level is C# 5, so no string interpolation, ?. or expression-bodied members.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Usage Tracker")]
[assembly: AssemblyProduct("Usage Tracker")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace UsageTracker
{
    // ------------------------------------------------------------------ entry point

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; }
            catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }

            int ri = Array.IndexOf(args, "--render");
            if (ri >= 0 && ri + 1 < args.Length) return RenderCli.ToFile(args[ri + 1], args);

            bool created;
            using (var mutex = new Mutex(true, @"Local\UsageTracker.Instance", out created))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\UsageTracker.Show"))
            {
                if (!created) { showSignal.Set(); return 0; }   // already running: ask it to show itself
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Paths.Log(e.Exception.ToString());
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Paths.Log(Convert.ToString(e.ExceptionObject));
                Application.Run(new Widget(showSignal));
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------ helpers

    static class Paths
    {
        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string AppDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public static readonly string ConfigFile = Path.Combine(AppDir, "config.json");
        public static readonly string StateFile = Path.Combine(AppDir, "state.json");
        static readonly string LogFile = Path.Combine(AppDir, "error.log");
        static readonly object LogLock = new object();

        public static string Expand(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            p = Environment.ExpandEnvironmentVariables(p.Trim());
            if (p == "~" || p.StartsWith("~/") || p.StartsWith("~\\")) p = Home + p.Substring(1);
            return p;
        }

        public static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public static void Log(string msg)
        {
            try
            {
                lock (LogLock)
                {
                    var fi = new FileInfo(LogFile);
                    if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                    File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    static class Json
    {
        public static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static JavaScriptSerializer Ser() { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }; }

        public static object Parse(string s) { return Ser().DeserializeObject(s); }

        public static object Get(object o, params string[] path)
        {
            foreach (var k in path)
            {
                var d = o as IDictionary<string, object>;
                if (d == null) return null;
                object v;
                if (!d.TryGetValue(k, out v)) return null;
                o = v;
            }
            return o;
        }

        public static string Str(object o, params string[] path)
        {
            var v = Get(o, path);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double? ToNum(object v)
        {
            if (v == null) return null;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is decimal) return (double)(decimal)v;
            if (v is double) return (double)v;
            if (v is float) return (float)v;
            double d;
            var s = v as string;
            if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        public static double? Num(object o, params string[] path) { return ToNum(Get(o, path)); }

        public static bool? Bool(object o, params string[] path)
        {
            var v = Get(o, path);
            if (v is bool) return (bool)v;
            return null;
        }

        // Accepts an ISO 8601 string, unix seconds, or unix milliseconds. Returns UTC.
        public static DateTime? Time(object v)
        {
            if (v == null) return null;
            var s = v as string;
            if (s != null)
            {
                DateTimeOffset dto;
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
                    return dto.UtcDateTime;
            }
            double? n = ToNum(v);
            if (n.HasValue && n.Value > 0)
            {
                double x = n.Value;
                if (x > 1e11) x /= 1000.0;
                return Epoch.AddSeconds(x);
            }
            return null;
        }

        public static IList List(object o, params string[] path) { return Get(o, path) as IList; }
    }

    static class Fmt
    {
        public static string Title(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Replace('_', ' ').Replace('-', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
        }

        // Compact countdown for the widget: "43m", "2h 13m", "3d 4h".
        public static string Short(TimeSpan t)
        {
            if (t.TotalSeconds <= 0) return "now";
            if (t.TotalMinutes < 60) return Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)) + "m";
            if (t.TotalHours < 24) return (int)t.TotalHours + "h " + t.Minutes.ToString("00") + "m";
            return (int)t.TotalDays + "d " + t.Hours + "h";
        }

        public static string When(DateTime utc)
        {
            var local = utc.ToLocalTime();
            if (local.Date == DateTime.Now.Date) return local.ToString("h:mm tt", CultureInfo.CurrentCulture);
            return local.ToString("ddd h:mm tt", CultureInfo.CurrentCulture);
        }

        public static string Pct(double p) { return Math.Round(Math.Max(0, p)).ToString(CultureInfo.InvariantCulture) + "%"; }
    }

    class HttpError : Exception
    {
        public readonly int Status;
        public HttpError(int status) : base("HTTP " + status) { Status = status; }
    }

    static class Http
    {
        public static string Get(string url, IDictionary<string, string> headers)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.UserAgent = "UsageTracker/1.0";
            req.Accept = "application/json";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (headers != null) foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null) { int code = (int)r.StatusCode; r.Close(); throw new HttpError(code); }
                if (ex.Status == WebExceptionStatus.Timeout) throw new FetchException("Timed out reaching " + new Uri(url).Host);
                throw new FetchException("Offline: can't reach " + new Uri(url).Host);
            }
        }
    }

    // ------------------------------------------------------------------ data model

    class Limit
    {
        public string Label;        // short, shown in the widget: "5h", "wk"
        public string LongLabel;    // shown in the tooltip: "5-hour", "Weekly"
        public double Percent;      // 0-100, share of the limit used
        public DateTime? ResetsAt;  // UTC
        public double WindowMinutes;

        public static Limit Make(double minutes, double percent, DateTime? resetsAt, string label)
        {
            var l = new Limit();
            l.WindowMinutes = minutes;
            l.Percent = percent;
            l.ResetsAt = resetsAt;
            l.Label = string.IsNullOrEmpty(label) ? ShortLabel(minutes) : label;
            l.LongLabel = string.IsNullOrEmpty(label) ? LongLabelFor(minutes) : label;
            return l;
        }

        static string N(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        public static string ShortLabel(double m)
        {
            if (m <= 0) return "?";
            if (Math.Abs(m - 10080) < 1) return "wk";
            if (m >= 1440 && m % 1440 == 0) return N(m / 1440) + "d";
            if (m >= 60 && m % 60 == 0) return N(m / 60) + "h";
            return N(m) + "m";
        }

        public static string LongLabelFor(double m)
        {
            if (Math.Abs(m - 10080) < 1) return "Weekly";
            if (Math.Abs(m - 1440) < 1) return "Daily";
            if (m >= 1440 && m % 1440 == 0) return N(m / 1440) + "-day";
            if (m >= 60 && m % 60 == 0) return N(m / 60) + "-hour";
            if (m > 0) return N(m) + "-minute";
            return "Limit";
        }
    }

    class Snapshot
    {
        public List<Limit> Limits = new List<Limit>();
        public string Plan;
        public DateTime FetchedAt = DateTime.UtcNow;
    }

    class FetchException : Exception { public FetchException(string m) : base(m) { } }

    abstract class Provider
    {
        public string Name;
        public string Key;
        public Color Accent = Color.FromArgb(154, 160, 166);
        public abstract Snapshot Fetch();   // throws FetchException with a short, human message
    }

    class ProviderState
    {
        public Provider P;
        public Snapshot Last;       // last good data, kept when a later refresh fails
        public string Error;        // last refresh error, null when the last refresh worked
        public bool Busy;
        public DateTime LastAttempt = DateTime.MinValue;
    }

    static class Fetcher
    {
        public static void Run(Provider p, out Snapshot snap, out string err)
        {
            snap = null; err = null;
            try
            {
                snap = p.Fetch();
                snap.FetchedAt = DateTime.UtcNow;
                snap.Limits.Sort((a, b) => a.WindowMinutes.CompareTo(b.WindowMinutes));
            }
            catch (FetchException fe) { err = fe.Message; }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; Paths.Log(p.Name + ": " + ex); }
        }
    }

    // ------------------------------------------------------------------ providers

    // Claude (Pro/Max): reads Claude Code's OAuth login and asks the same usage endpoint /usage uses.
    // Read-only: it never refreshes or rewrites the token, so it can't sign Claude Code out.
    class ClaudeProvider : Provider
    {
        const string Url = "https://api.anthropic.com/api/oauth/usage";
        const string Expired = "Login expired: open Claude Code to renew it";
        readonly string credPath;

        public ClaudeProvider(string name, string credentials)
        {
            Name = name;
            credPath = credentials;
            Accent = Color.FromArgb(217, 119, 87);
        }

        string CredFile()
        {
            if (!string.IsNullOrEmpty(credPath)) return Paths.Expand(credPath);
            string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Paths.Home, ".claude");
            return Path.Combine(dir, ".credentials.json");
        }

        public override Snapshot Fetch()
        {
            string f = CredFile();
            if (!File.Exists(f)) throw new FetchException("Not signed in to Claude Code");
            object root = Json.Parse(Paths.ReadShared(f));
            string token = Json.Str(root, "claudeAiOauth", "accessToken");
            if (string.IsNullOrEmpty(token)) throw new FetchException("Not signed in to Claude Code");
            double? exp = Json.Num(root, "claudeAiOauth", "expiresAt");
            if (exp.HasValue && Json.Epoch.AddMilliseconds(exp.Value) < DateTime.UtcNow) throw new FetchException(Expired);

            string body;
            try
            {
                body = Http.Get(Url, new Dictionary<string, string> {
                    { "Authorization", "Bearer " + token },
                    { "anthropic-beta", "oauth-2025-04-20" } });
            }
            catch (HttpError e)
            {
                if (e.Status == 401 || e.Status == 403) throw new FetchException(Expired);
                if (e.Status == 429) throw new FetchException("Rate limited by Anthropic, retrying later");
                throw new FetchException("Anthropic API error " + e.Status);
            }

            object o = Json.Parse(body);
            var s = new Snapshot();
            s.Plan = PlanName(Json.Str(root, "claudeAiOauth", "subscriptionType"), Json.Str(root, "claudeAiOauth", "rateLimitTier"));
            Add(s, o, "five_hour", 300);
            Add(s, o, "seven_day", 10080);
            if (s.Limits.Count == 0) throw new FetchException("No usage data in Anthropic's reply");
            return s;
        }

        static void Add(Snapshot s, object o, string key, double minutes)
        {
            var w = Json.Get(o, key);
            if (w == null) return;
            double? used = Json.Num(w, "utilization");
            if (!used.HasValue) return;
            s.Limits.Add(Limit.Make(minutes, used.Value, Json.Time(Json.Get(w, "resets_at")), null));
        }

        static string PlanName(string sub, string tier)
        {
            var m = Regex.Match(tier ?? "", @"max_(\d+)x");
            if (m.Success) return "Max " + m.Groups[1].Value + "x";
            return Fmt.Title(sub);
        }
    }

    // Codex (ChatGPT plans): starts `codex app-server` and asks it for account/rateLimits/read.
    // Codex handles its own login and token refresh, wherever it stores them.
    class CodexProvider : Provider
    {
        readonly string exePath, codexHome;

        public CodexProvider(string name, string exe, string home)
        {
            Name = name;
            exePath = exe;
            codexHome = home;
            Accent = Color.FromArgb(16, 163, 127);
        }

        string FindCodex()
        {
            if (!string.IsNullOrEmpty(exePath))
            {
                string p = Paths.Expand(exePath);
                return File.Exists(p) ? p : null;
            }
            // Newest Codex bundled with the VS Code (or fork) ChatGPT/Codex extension.
            string best = null;
            Version bestV = null;
            foreach (var root in new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" })
            {
                string ext = Path.Combine(Paths.Home, root, "extensions");
                if (!Directory.Exists(ext)) continue;
                foreach (var dir in Directory.GetDirectories(ext, "openai.chatgpt-*"))
                {
                    string exe = null;
                    foreach (var arch in new[] { "windows-x86_64", "windows-aarch64" })
                    {
                        string c = Path.Combine(dir, "bin", arch, "codex.exe");
                        if (File.Exists(c)) { exe = c; break; }
                    }
                    if (exe == null) continue;
                    Version v = ParseVersion(Path.GetFileName(dir));
                    if (best == null || v > bestV) { best = exe; bestV = v; }
                }
            }
            if (best != null) return best;
            // Otherwise a standalone install on PATH.
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (d.Trim().Length == 0) continue;
                foreach (var n in new[] { "codex.exe", "codex.cmd" })
                {
                    try
                    {
                        string c = Path.Combine(d.Trim().Trim('"'), n);
                        if (File.Exists(c)) return c;
                    }
                    catch { }
                }
            }
            return null;
        }

        static Version ParseVersion(string dirName)
        {
            var m = Regex.Match(dirName, @"-(\d+(\.\d+){1,3})");
            Version v;
            if (m.Success && Version.TryParse(m.Groups[1].Value, out v)) return v;
            return new Version(0, 0);
        }

        public override Snapshot Fetch()
        {
            string exe = FindCodex();
            if (exe == null) throw new FetchException("Codex not found (install the Codex CLI or VS Code extension)");

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/d /s /c \"\"" + exe + "\" app-server\"";
            }
            else
            {
                psi.FileName = exe;
                psi.Arguments = "app-server";
            }
            if (!string.IsNullOrEmpty(codexHome)) psi.EnvironmentVariables["CODEX_HOME"] = Paths.Expand(codexHome);

            object result = null;
            string rpcError = null;
            Process p = Process.Start(psi);
            using (var killer = new System.Threading.Timer(_ => { try { if (!p.HasExited) p.Kill(); } catch { } }, null, 20000, Timeout.Infinite))
            {
                try
                {
                    p.ErrorDataReceived += delegate { };
                    p.BeginErrorReadLine();
                    var w = p.StandardInput;
                    w.NewLine = "\n";
                    w.AutoFlush = true;
                    w.WriteLine("{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"usage_tracker\",\"title\":\"Usage Tracker\",\"version\":\"1.0.0\"}}}");
                    w.WriteLine("{\"method\":\"initialized\"}");
                    w.WriteLine("{\"method\":\"account/rateLimits/read\",\"id\":2}");
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        if (line.IndexOf("\"id\"", StringComparison.Ordinal) < 0) continue;
                        object msg;
                        try { msg = Json.Parse(line); } catch { continue; }
                        double? id = Json.Num(msg, "id");
                        if (!id.HasValue || id.Value != 2) continue;
                        result = Json.Get(msg, "result");
                        rpcError = Json.Str(msg, "error", "message");
                        break;
                    }
                }
                finally
                {
                    try { p.StandardInput.Close(); } catch { }
                    try { if (!p.WaitForExit(1500)) p.Kill(); } catch { }
                }
            }
            p.Dispose();

            if (rpcError != null)
            {
                if (rpcError.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new FetchException("Not signed in to Codex");
                throw new FetchException("Codex: " + rpcError);
            }
            if (result == null) throw new FetchException("Codex didn't answer");

            var rl = Json.Get(result, "rateLimits");
            var s = new Snapshot();
            s.Plan = PlanName(Json.Str(rl, "planType"));
            Add(s, Json.Get(rl, "primary"), 300);
            Add(s, Json.Get(rl, "secondary"), 10080);
            if (s.Limits.Count == 0) throw new FetchException("Codex reported no limits yet");
            return s;
        }

        static void Add(Snapshot s, object w, double defaultMinutes)
        {
            if (w == null) return;
            double? used = Json.Num(w, "usedPercent");
            if (!used.HasValue) return;
            double minutes = Json.Num(w, "windowDurationMins") ?? defaultMinutes;
            s.Limits.Add(Limit.Make(minutes, used.Value, Json.Time(Json.Get(w, "resetsAt")), null));
        }

        static string PlanName(string plan)
        {
            if (string.IsNullOrEmpty(plan) || plan == "unknown") return null;
            if (plan == "prolite") return "Pro Lite";
            if (plan == "promax") return "Pro Max";
            return Fmt.Title(plan);
        }
    }

    // Any other agent: runs a command that prints JSON like
    // {"plan":"Pro","limits":[{"label":"5h","percent":42,"resetsAt":"2026-11-10T17:40:00Z","windowMinutes":300}]}
    class CommandProvider : Provider
    {
        readonly string command;

        public CommandProvider(string name, string cmd) { Name = name; command = cmd; }

        public override Snapshot Fetch()
        {
            if (string.IsNullOrEmpty(command)) throw new FetchException("No \"command\" set in config.json");
            var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Paths.AppDir,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            string output;
            using (var p = Process.Start(psi))
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(20000))
                {
                    try { p.Kill(); } catch { }
                    throw new FetchException("Command timed out");
                }
                output = outTask.Result;
                GC.KeepAlive(errTask);
            }
            object o;
            try { o = Json.Parse(output.Trim()); }
            catch { throw new FetchException("Command didn't print valid JSON"); }

            var s = new Snapshot { Plan = Json.Str(o, "plan") };
            var list = Json.List(o, "limits");
            if (list != null)
            {
                foreach (var w in list)
                {
                    double? pct = Json.Num(w, "percent");
                    if (!pct.HasValue) continue;
                    double minutes = Json.Num(w, "windowMinutes") ?? 0;
                    s.Limits.Add(Limit.Make(minutes, pct.Value, Json.Time(Json.Get(w, "resetsAt")), Json.Str(w, "label")));
                }
            }
            string err = Json.Str(o, "error");
            if (s.Limits.Count == 0) throw new FetchException(err ?? "Command reported no limits");
            return s;
        }
    }

    // Stands in for a provider that can't be built (bad config) so the problem shows in the widget.
    class BrokenProvider : Provider
    {
        readonly string message;
        public BrokenProvider(string name, string msg) { Name = name; message = msg; }
        public override Snapshot Fetch() { throw new FetchException(message); }
    }

    // ------------------------------------------------------------------ config + state

    class AppConfig
    {
        public double RefreshMinutes = 3;
        public List<Provider> Providers = new List<Provider>();

        public const string DefaultJson =
@"{
  ""refreshMinutes"": 3,
  ""providers"": [
    { ""type"": ""claude"", ""name"": ""Claude"" },
    { ""type"": ""codex"",  ""name"": ""Codex"" }
  ]
}
";

        public static AppConfig Load(string path)
        {
            var c = new AppConfig();
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, DefaultJson, new UTF8Encoding(false));
                object root = Json.Parse(Paths.ReadShared(path));
                double? rm = Json.Num(root, "refreshMinutes");
                if (rm.HasValue) c.RefreshMinutes = Math.Max(1, Math.Min(60, rm.Value));
                var arr = Json.List(root, "providers");
                if (arr != null)
                {
                    foreach (var o in arr)
                    {
                        if (Json.Bool(o, "enabled") == false) continue;
                        c.Providers.Add(Create(o));
                    }
                }
                if (c.Providers.Count == 0) c.Providers.Add(new BrokenProvider("Usage Tracker", "No providers in config.json"));
            }
            catch (Exception ex)
            {
                c.Providers.Clear();
                c.Providers.Add(new BrokenProvider("config.json", "Can't read config.json: " + ex.Message));
            }
            return c;
        }

        static Provider Create(object o)
        {
            string type = (Json.Str(o, "type") ?? "").Trim().ToLowerInvariant();
            string name = Json.Str(o, "name");
            Provider p;
            if (type == "claude") p = new ClaudeProvider(name ?? "Claude", Json.Str(o, "credentials"));
            else if (type == "codex" || type == "chatgpt") p = new CodexProvider(name ?? "Codex", Json.Str(o, "codexPath"), Json.Str(o, "codexHome"));
            else if (type == "command") p = new CommandProvider(name ?? "Agent", Json.Str(o, "command"));
            else p = new BrokenProvider(name ?? (type.Length > 0 ? type : "?"), "Unknown provider type \"" + type + "\"");
            p.Accent = Theme.Parse(Json.Str(o, "color"), p.Accent);
            p.Key = type + "|" + p.Name;
            return p;
        }
    }

    public class AppState
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool OnTop { get; set; }
        public int Opacity { get; set; }
        public double UiScale { get; set; }
        public bool ShowResets { get; set; }

        public AppState() { X = int.MinValue; Y = int.MinValue; OnTop = true; Opacity = 100; UiScale = 1.0; ShowResets = true; }

        public static AppState Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var s = new JavaScriptSerializer().Deserialize<AppState>(File.ReadAllText(path));
                    if (s != null)
                    {
                        if (s.UiScale < 0.5 || s.UiScale > 3) s.UiScale = 1.0;
                        if (s.Opacity < 30 || s.Opacity > 100) s.Opacity = 100;
                        return s;
                    }
                }
            }
            catch { }
            return new AppState();
        }

        public void Save(string path)
        {
            try { File.WriteAllText(path, new JavaScriptSerializer().Serialize(this)); } catch { }
        }
    }

    // ------------------------------------------------------------------ drawing

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(27, 29, 34);
        public static readonly Color Border = Color.FromArgb(58, 63, 71);
        public static readonly Color Line = Color.FromArgb(44, 48, 56);
        public static readonly Color Text = Color.FromArgb(230, 232, 235);
        public static readonly Color Dim = Color.FromArgb(138, 144, 153);
        public static readonly Color Track = Color.FromArgb(46, 50, 59);
        public static readonly Color Ok = Color.FromArgb(90, 169, 230);
        public static readonly Color Warn = Color.FromArgb(232, 176, 74);
        public static readonly Color High = Color.FromArgb(229, 83, 75);

        public static Color ForPercent(double p) { return p >= 90 ? High : p >= 70 ? Warn : Ok; }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Fade(Color c, bool faded) { return faded ? Mix(c, Bg, 0.55) : c; }

        public static Color Parse(string hex, Color fallback)
        {
            try { if (!string.IsNullOrEmpty(hex)) return ColorTranslator.FromHtml(hex.Trim()); }
            catch { }
            return fallback;
        }
    }

    class Renderer : IDisposable
    {
        const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                                   TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        readonly float S;
        readonly bool showResets;
        readonly Font fName, fText, fSmall;
        readonly int pad, rowH, blockGap, accentW, xName, nameW, xLabel, xBar, barW, barH, xPct, pctW, xReset, resetW, width;

        public Renderer(float scale, bool showResets, IList<ProviderState> items)
        {
            S = scale;
            this.showResets = showResets;
            fName = new Font("Segoe UI Semibold", 12f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            fText = new Font("Segoe UI", 11.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            fSmall = new Font("Segoe UI", 10.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);

            pad = P(9);
            rowH = P(17);
            blockGap = P(11);
            accentW = Math.Max(2, P(3));
            barH = Math.Max(4, P(6));
            xName = pad + accentW + P(7);

            int nw = P(44), lw = W("wk", fText);
            foreach (var it in items)
            {
                nw = Math.Max(nw, W(it.P.Name, fName) + P(11));
                if (it.Last != null)
                {
                    if (it.Last.Plan != null) nw = Math.Max(nw, W(it.Last.Plan, fSmall));
                    foreach (var l in it.Last.Limits) lw = Math.Max(lw, W(l.Label, fText));
                }
            }
            nameW = Math.Min(nw, P(130));
            xLabel = xName + nameW + P(8);
            xBar = xLabel + lw + P(7);
            barW = P(104);
            xPct = xBar + barW + P(7);
            pctW = W("100%", fText);
            xReset = xPct + pctW + P(10);
            resetW = Math.Max(W("23h 59m", fText), W("6d 23h", fText));
            width = (showResets ? xReset + resetW : xPct + pctW) + pad;
        }

        int P(float v) { return (int)Math.Round(v * S); }

        static int W(string s, Font f) { return TextRenderer.MeasureText(s ?? "", f, Size.Empty, TF).Width; }

        static int RowsFor(ProviderState it)
        {
            return it.Last != null ? Math.Max(2, it.Last.Limits.Count) : 2;
        }

        public Size Measure(IList<ProviderState> items)
        {
            int h = pad * 2 - P(2);
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) h += blockGap;
                h += RowsFor(items[i]) * rowH;
            }
            return new Size(width, h);
        }

        public int HitTest(IList<ProviderState> items, int y)
        {
            int top = pad - P(1);
            for (int i = 0; i < items.Count; i++)
            {
                int bottom = top + RowsFor(items[i]) * rowH;
                if (y < bottom + blockGap / 2) return i;
                top = bottom + blockGap;
            }
            return items.Count - 1;
        }

        public void Draw(Graphics g, IList<ProviderState> items, DateTime now)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int y = pad - P(1);
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                int rows = RowsFor(it), h = rows * rowH;
                bool hasData = it.Last != null && it.Last.Limits.Count > 0;
                bool stale = hasData && it.Error != null;

                // provider accent stripe, name, plan
                using (var b = new SolidBrush(Theme.Fade(it.P.Accent, stale)))
                    FillRound(g, b, pad, y + P(2), accentW, h - P(4), accentW / 2f);
                int nameTextW = Math.Min(W(it.P.Name, fName), nameW - P(11));
                Txt(g, it.P.Name, fName, xName, y, nameW - P(11), rowH, Theme.Fade(Theme.Text, stale), TextFormatFlags.EndEllipsis);
                if (it.Last != null && !string.IsNullOrEmpty(it.Last.Plan))
                    Txt(g, it.Last.Plan, fSmall, xName, y + rowH, nameW, rowH, Theme.Dim, TextFormatFlags.EndEllipsis);

                // status dot: blue while refreshing, amber when the last refresh failed
                if (it.Busy || stale)
                {
                    float r = Math.Max(2f, 2.5f * S);
                    using (var b = new SolidBrush(it.Busy ? Theme.Ok : Theme.Warn))
                        g.FillEllipse(b, xName + nameTextW + P(5) - r, y + rowH / 2f - r, r * 2, r * 2);
                }

                if (hasData)
                {
                    for (int j = 0; j < it.Last.Limits.Count; j++)
                        DrawLimit(g, it.Last.Limits[j], y + j * rowH, now, stale);
                }
                else
                {
                    string msg = it.Error ?? (it.Busy ? "Loading…" : "Waiting…");
                    var rect = new Rectangle(xLabel, y, width - pad - xLabel, h);
                    TextRenderer.DrawText(g, msg, fSmall, rect, it.Error != null ? Theme.Warn : Theme.Dim,
                        TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }

                y += h;
                if (i < items.Count - 1)
                {
                    using (var pen = new Pen(Theme.Line, Math.Max(1f, S)))
                        g.DrawLine(pen, pad, y + blockGap / 2f, width - pad, y + blockGap / 2f);
                    y += blockGap;
                }
            }
        }

        void DrawLimit(Graphics g, Limit l, int rowY, DateTime now, bool stale)
        {
            double pct = Math.Max(0, Math.Min(100, l.Percent));
            Txt(g, l.Label, fText, xLabel, rowY, xBar - xLabel, rowH, Theme.Dim, 0);

            float by = rowY + (rowH - barH) / 2f;
            using (var b = new SolidBrush(Theme.Track)) FillRound(g, b, xBar, by, barW, barH, barH / 2f);
            float fw = (float)(barW * pct / 100.0);
            if (pct > 0 && fw < barH) fw = barH;
            if (fw > 0)
                using (var b = new SolidBrush(Theme.Fade(Theme.ForPercent(l.Percent), stale)))
                    FillRound(g, b, xBar, by, fw, barH, barH / 2f);

            // pace tick: how much of this window's time has gone by
            if (l.ResetsAt.HasValue && l.WindowMinutes > 0)
            {
                double frac = 1 - (l.ResetsAt.Value - now).TotalMinutes / l.WindowMinutes;
                if (frac > 0 && frac < 1)
                {
                    float px = xBar + (float)(barW * frac);
                    float tw = Math.Max(1f, 1.5f * S);
                    using (var b = new SolidBrush(Color.FromArgb(stale ? 90 : 190, Theme.Text)))
                        g.FillRectangle(b, px - tw / 2f, by - P(2.5f), tw, barH + P(5));
                }
            }

            Color pc = l.Percent >= 100 ? Theme.High : Theme.Text;
            Txt(g, Fmt.Pct(l.Percent), fText, xPct, rowY, pctW, rowH, Theme.Fade(pc, stale), TextFormatFlags.Right);
            if (showResets && l.ResetsAt.HasValue)
                Txt(g, Fmt.Short(l.ResetsAt.Value - now), fText, xReset, rowY, resetW, rowH, Theme.Fade(Theme.Dim, stale), 0);
        }

        static void Txt(Graphics g, string s, Font f, int x, int y, int w, int h, Color c, TextFormatFlags extra)
        {
            TextRenderer.DrawText(g, s, f, new Rectangle(x, y, w, h), c, TF | extra);
        }

        static void FillRound(Graphics g, Brush b, float x, float y, float w, float h, float r)
        {
            if (w <= 0 || h <= 0) return;
            r = Math.Min(r, Math.Min(w, h) / 2f);
            if (r < 0.75f) { g.FillRectangle(b, x, y, w, h); return; }
            using (var p = new GraphicsPath())
            {
                float d = r * 2;
                p.AddArc(x, y, d, d, 180, 90);
                p.AddArc(x + w - d, y, d, d, 270, 90);
                p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                p.AddArc(x, y + h - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        public void Dispose() { fName.Dispose(); fText.Dispose(); fSmall.Dispose(); }
    }

    static class AppIcon
    {
        public static Icon Load(int size)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    return s == null ? null : new Icon(s, size, size);
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ the widget window

    class Widget : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "UsageTracker";

        readonly EventWaitHandle showSignal;
        AppConfig cfg;
        AppState st;
        List<ProviderState> items = new List<ProviderState>();
        Renderer r;
        float dpi = 1f;
        int hover = -1;

        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 15000 };
        readonly System.Windows.Forms.Timer reload = new System.Windows.Forms.Timer { Interval = 700 };
        FileSystemWatcher watcher;
        NotifyIcon tray;
        ContextMenuStrip menu;
        ToolTip tip;
        ToolStripMenuItem miShow, miTop, miResets, miStartup, miSize, miOpacity;

        public Widget(EventWaitHandle showSignal)
        {
            this.showSignal = showSignal;
            Text = "Usage Tracker";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            var ico = AppIcon.Load(32);
            if (ico != null) Icon = ico;

            st = AppState.Load(Paths.StateFile);
            using (var g = CreateGraphics()) dpi = g.DpiX / 96f;
            LoadConfig();
            BuildMenu();

            tray = new NotifyIcon
            {
                Icon = AppIcon.Load(SystemInformation.SmallIconSize.Width) ?? SystemIcons.Application,
                Text = "Usage Tracker",
                ContextMenuStrip = menu,
                Visible = true,
            };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWidget(); };

            tip = new ToolTip { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 30000, ShowAlways = true };
            ContextMenuStrip = menu;

            ApplyState();
            Relayout(false);
            PlaceWindow();

            tick.Tick += (s, e) => OnTick();
            reload.Tick += (s, e) => { reload.Stop(); LoadConfig(); Relayout(true); RefreshAll(); };
            watcher = new FileSystemWatcher(Paths.AppDir, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                SynchronizingObject = this,
            };
            watcher.Changed += (s, e) => { reload.Stop(); reload.Start(); };
            watcher.Created += (s, e) => { reload.Stop(); reload.Start(); };
            watcher.Renamed += (s, e) => { reload.Stop(); reload.Start(); };
            watcher.EnableRaisingEvents = true;
            SystemEvents.PowerModeChanged += OnPowerChange;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;   // WS_EX_TOOLWINDOW: no taskbar button, not in Alt+Tab
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2;   // DWMWCP_ROUND (Windows 11)
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
                int border = Theme.Border.R | (Theme.Border.G << 8) | (Theme.Border.B << 16);
                DwmSetWindowAttribute(Handle, 34, ref border, 4);   // DWMWA_BORDER_COLOR
            }
            catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ThreadPool.RegisterWaitForSingleObject(showSignal, (o, timedOut) =>
            {
                try { BeginInvoke(new Action(ShowWidget)); } catch { }
            }, null, -1, false);
            tick.Start();
            RefreshAll();
        }

        // ---- config, layout, placement

        void LoadConfig()
        {
            cfg = AppConfig.Load(Paths.ConfigFile);
            var fresh = new List<ProviderState>();
            foreach (var p in cfg.Providers)
            {
                var ps = new ProviderState { P = p };
                var prev = items.FirstOrDefault(o => o.P.Key == p.Key);
                if (prev != null) { ps.Last = prev.Last; ps.Error = prev.Error; }
                fresh.Add(ps);
            }
            items = fresh;
            hover = -1;
        }

        void Relayout(bool keepOnScreen)
        {
            if (r != null) r.Dispose();
            r = new Renderer(dpi * (float)st.UiScale, st.ShowResets, items);
            var size = r.Measure(items);
            if (size != ClientSize)
            {
                ClientSize = size;
                if (keepOnScreen && Visible) KeepOnScreen();
            }
            Invalidate();
        }

        void PlaceWindow()
        {
            var rect = new Rectangle(st.X, st.Y, Width, Height);
            bool ok = st.X != int.MinValue && Screen.AllScreens.Any(s =>
            {
                var i = Rectangle.Intersect(s.WorkingArea, rect);
                return i.Width >= 40 && i.Height >= 20;
            });
            if (ok) { Location = new Point(st.X, st.Y); return; }
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        }

        void KeepOnScreen()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
            int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
            if (x != Left || y != Top) { Location = new Point(x, y); SavePosition(); }
        }

        void SavePosition() { st.X = Left; st.Y = Top; st.Save(Paths.StateFile); }

        void ApplyState()
        {
            TopMost = st.OnTop;
            Opacity = Math.Max(30, Math.Min(100, st.Opacity)) / 100.0;
        }

        void ShowWidget()
        {
            if (!Visible) Show();
            if (!st.OnTop) { TopMost = true; TopMost = false; }   // pop above other windows once
            BringToFront();
        }

        // ---- refreshing

        void RefreshAll() { foreach (var it in items) StartFetch(it); }

        void StartFetch(ProviderState it)
        {
            if (it.Busy) return;
            it.Busy = true;
            it.LastAttempt = DateTime.UtcNow;
            Invalidate();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Snapshot snap; string err;
                Fetcher.Run(it.P, out snap, out err);
                try { BeginInvoke(new Action(() => OnFetched(it, snap, err))); }
                catch { }
            });
        }

        void OnFetched(ProviderState it, Snapshot snap, string err)
        {
            it.Busy = false;
            if (snap != null) { it.Last = snap; it.Error = null; }
            else it.Error = err;
            UpdateTrayText();
            Relayout(true);
            if (hover >= 0 && hover < items.Count) tip.SetToolTip(this, TipText(items[hover]));
        }

        void OnTick()
        {
            var now = DateTime.UtcNow;
            var interval = TimeSpan.FromMinutes(cfg.RefreshMinutes);
            foreach (var it in items)
            {
                if (it.Busy) continue;
                bool due = now - it.LastAttempt >= interval;
                // a window just reset: fetch fresh numbers instead of waiting for the next poll
                if (!due && it.Last != null && now - it.LastAttempt >= TimeSpan.FromSeconds(60))
                    foreach (var l in it.Last.Limits)
                        if (l.ResetsAt.HasValue && l.ResetsAt.Value < now && it.Last.FetchedAt < l.ResetsAt.Value) due = true;
                if (due) StartFetch(it);
            }
            Invalidate();   // countdowns and pace ticks move
        }

        void OnPowerChange(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    // refresh about 20 s after waking, once the network is back
                    var soon = DateTime.UtcNow - TimeSpan.FromMinutes(cfg.RefreshMinutes) + TimeSpan.FromSeconds(20);
                    foreach (var it in items) if (!it.Busy) it.LastAttempt = soon;
                }));
            }
            catch { }
        }

        // ---- painting and mouse

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Bg);
            if (r != null) r.Draw(e.Graphics, items, DateTime.UtcNow);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            tip.Hide(this);
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // drag the window from anywhere
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x0232) SavePosition();   // WM_EXITSIZEMOVE: drag finished
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (r == null || items.Count == 0) return;
            int h = r.HitTest(items, e.Y);
            if (h == hover) return;
            hover = h;
            tip.SetToolTip(this, TipText(items[h]));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
        }

        string TipText(ProviderState it)
        {
            var sb = new StringBuilder(it.P.Name);
            var now = DateTime.UtcNow;
            if (it.Last != null)
            {
                if (!string.IsNullOrEmpty(it.Last.Plan)) sb.Append(" (" + it.Last.Plan + ")");
                foreach (var l in it.Last.Limits)
                {
                    sb.AppendLine();
                    sb.Append(l.LongLabel + ": " + Fmt.Pct(l.Percent) + " used");
                    if (l.ResetsAt.HasValue)
                    {
                        sb.Append(", resets " + Fmt.When(l.ResetsAt.Value));
                        if (l.ResetsAt.Value > now) sb.Append(" (in " + Fmt.Short(l.ResetsAt.Value - now) + ")");
                        if (l.WindowMinutes > 0)
                        {
                            double frac = 1 - (l.ResetsAt.Value - now).TotalMinutes / l.WindowMinutes;
                            if (frac > 0 && frac < 1) sb.Append(". " + Fmt.Pct(frac * 100) + " of the window has passed");
                        }
                    }
                }
                sb.AppendLine();
                sb.Append("Updated " + it.Last.FetchedAt.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture));
            }
            if (it.Error != null)
            {
                sb.AppendLine();
                sb.Append(it.Last != null ? "Last refresh failed: " + it.Error : it.Error);
            }
            return sb.ToString();
        }

        void UpdateTrayText()
        {
            var parts = items.Where(i => i.Last != null)
                .Select(i => i.P.Name + " " + string.Join("/", i.Last.Limits.Select(l => Fmt.Pct(l.Percent))));
            string t = string.Join(" · ", parts);
            if (t.Length == 0) t = "Usage Tracker";
            if (t.Length > 63) t = t.Substring(0, 62) + "…";
            try { tray.Text = t; } catch { }
        }

        // ---- menu

        ToolStripMenuItem Item(ToolStripItemCollection into, string text, Action onClick)
        {
            var mi = new ToolStripMenuItem(text);
            mi.Click += (s, e) => onClick();
            into.Add(mi);
            return mi;
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            var m = menu.Items;
            miShow = Item(m, "Hide", () => { if (Visible) Hide(); else ShowWidget(); });
            Item(m, "Refresh now", RefreshAll);
            m.Add(new ToolStripSeparator());
            miTop = Item(m, "Always on top", () => { st.OnTop = !st.OnTop; ApplyState(); st.Save(Paths.StateFile); });
            miResets = Item(m, "Show reset times", () => { st.ShowResets = !st.ShowResets; Relayout(true); st.Save(Paths.StateFile); });

            miSize = new ToolStripMenuItem("Size");
            foreach (var pair in new[] { Tuple.Create("Small", 0.85), Tuple.Create("Normal", 1.0), Tuple.Create("Large", 1.25), Tuple.Create("Extra large", 1.5) })
            {
                var v = pair.Item2;
                var mi = Item(miSize.DropDownItems, pair.Item1, () => { st.UiScale = v; Relayout(true); st.Save(Paths.StateFile); });
                mi.Tag = v;
            }
            m.Add(miSize);

            miOpacity = new ToolStripMenuItem("Opacity");
            foreach (var pct in new[] { 100, 90, 80, 70, 60, 50 })
            {
                var v = pct;
                var mi = Item(miOpacity.DropDownItems, pct + "%", () => { st.Opacity = v; ApplyState(); st.Save(Paths.StateFile); });
                mi.Tag = v;
            }
            m.Add(miOpacity);

            m.Add(new ToolStripSeparator());
            miStartup = Item(m, "Start with Windows", () => SetStartup(!StartupEnabled()));
            Item(m, "Edit providers (config.json)", OpenConfig);
            m.Add(new ToolStripSeparator());
            Item(m, "Exit", Close);

            menu.Opening += (s, e) =>
            {
                miShow.Text = Visible ? "Hide" : "Show";
                miTop.Checked = st.OnTop;
                miResets.Checked = st.ShowResets;
                miStartup.Checked = StartupEnabled();
                foreach (ToolStripMenuItem mi in miSize.DropDownItems) mi.Checked = Math.Abs((double)mi.Tag - st.UiScale) < 0.01;
                foreach (ToolStripMenuItem mi in miOpacity.DropDownItems) mi.Checked = (int)mi.Tag == st.Opacity;
            };
        }

        static bool StartupEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = k == null ? null : k.GetValue(RunName) as string;
                    return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch (Exception ex) { Paths.Log("startup toggle: " + ex.Message); }
        }

        static void OpenConfig()
        {
            if (!File.Exists(Paths.ConfigFile)) AppConfig.Load(Paths.ConfigFile);
            try { Process.Start(new ProcessStartInfo(Paths.ConfigFile) { UseShellExecute = true }); }
            catch
            {
                try { Process.Start("notepad.exe", "\"" + Paths.ConfigFile + "\""); } catch { }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.PowerModeChanged -= OnPowerChange;
            tick.Stop();
            watcher.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ --render (testing / screenshots)

    static class RenderCli
    {
        public static int ToFile(string outPath, string[] args)
        {
            float scale = 1f;
            int si = Array.IndexOf(args, "--scale");
            if (si >= 0 && si + 1 < args.Length) float.TryParse(args[si + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
            bool showResets = Array.IndexOf(args, "--no-resets") < 0;

            List<ProviderState> items;
            if (Array.IndexOf(args, "--demo") >= 0) items = Demo();
            else
            {
                items = AppConfig.Load(Paths.ConfigFile).Providers.Select(p => new ProviderState { P = p }).ToList();
                var threads = items.Select(it =>
                {
                    var t = new Thread(() =>
                    {
                        Snapshot snap; string err;
                        Fetcher.Run(it.P, out snap, out err);
                        it.Last = snap; it.Error = err;
                    });
                    t.Start();
                    return t;
                }).ToList();
                threads.ForEach(t => t.Join());
            }

            using (var r = new Renderer(scale, showResets, items))
            {
                var size = r.Measure(items);
                using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Theme.Bg);
                        r.Draw(g, items, DateTime.UtcNow);
                    }
                    bmp.Save(outPath, ImageFormat.Png);
                }
            }
            var sb = new StringBuilder();
            foreach (var it in items)
            {
                sb.Append(it.P.Name + ": ");
                if (it.Last != null) sb.Append(string.Join(", ", it.Last.Limits.Select(l => l.Label + " " + Fmt.Pct(l.Percent) + (l.ResetsAt.HasValue ? " resets " + l.ResetsAt.Value.ToString("o") : ""))) + " plan=" + it.Last.Plan);
                if (it.Error != null) sb.Append(" error=" + it.Error);
                sb.AppendLine();
            }
            File.WriteAllText(outPath + ".txt", sb.ToString());
            return 0;
        }

        static List<ProviderState> Demo()
        {
            var now = DateTime.UtcNow;
            Func<string, Color, string, ProviderState> mk = (name, accent, plan) =>
            {
                var p = new BrokenProvider(name, "") { Accent = accent };
                return new ProviderState { P = p, Last = plan == null ? null : new Snapshot { Plan = plan } };
            };
            var a = mk("Claude", Color.FromArgb(217, 119, 87), "Max 5x");
            a.Last.Limits.Add(Limit.Make(300, 42, now.AddMinutes(133), null));
            a.Last.Limits.Add(Limit.Make(10080, 18, now.AddHours(100), null));
            var b = mk("Codex", Color.FromArgb(16, 163, 127), "Plus");
            b.Last.Limits.Add(Limit.Make(300, 76, now.AddMinutes(41), null));
            b.Last.Limits.Add(Limit.Make(10080, 93, now.AddHours(30), null));
            var c = mk("Gemini", Color.FromArgb(120, 140, 240), "Pro");
            c.Last.Limits.Add(Limit.Make(300, 12, now.AddMinutes(250), null));
            c.Last.Limits.Add(Limit.Make(10080, 40, now.AddHours(80), null));
            c.Error = "Offline: can't reach example.com";
            var d = mk("Cursor", Color.FromArgb(154, 160, 166), null);
            d.Error = "Not signed in to Codex";
            return new List<ProviderState> { a, b, c, d };
        }
    }
}
