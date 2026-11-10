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
using System.Drawing.Text;
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
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

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

        public static string Clock(DateTime utc)
        {
            return utc.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
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
        public readonly TimeSpan? RetryAfter;
        public HttpError(int status, TimeSpan? retryAfter) : base("HTTP " + status) { Status = status; RetryAfter = retryAfter; }
    }

    static class Http
    {
        public static string Get(string url, IDictionary<string, string> headers)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.UserAgent = "UsageTracker/1.1";
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
                if (r != null)
                {
                    int code = (int)r.StatusCode;
                    var retry = ParseRetryAfter(r.Headers["Retry-After"]);
                    r.Close();
                    throw new HttpError(code, retry);
                }
                if (ex.Status == WebExceptionStatus.Timeout) throw new FetchException("Timed out reaching " + new Uri(url).Host);
                throw new FetchException("Offline: can't reach " + new Uri(url).Host);
            }
        }

        static TimeSpan? ParseRetryAfter(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            int secs;
            if (int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out secs) && secs >= 0)
                return TimeSpan.FromSeconds(secs);
            DateTime when;
            if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
            {
                var d = when - DateTime.UtcNow;
                if (d > TimeSpan.Zero) return d;
            }
            return null;
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

    class FetchException : Exception
    {
        public bool RateLimited;
        public TimeSpan? RetryAfter;
        public FetchException(string m) : base(m) { }
    }

    abstract class Provider
    {
        public string Name;
        public string Key;
        public Color Accent = Color.FromArgb(154, 160, 166);
        // Some sources rate-limit hard; this is the shortest polling interval they get.
        public virtual double MinIntervalMinutes { get { return 1; } }
        public abstract Snapshot Fetch();   // throws FetchException with a short, human message
    }

    class ProviderState
    {
        // Data older than this is drawn dimmed when refreshing keeps failing.
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

        public Provider P;
        public Snapshot Last;       // last good data, kept when a later refresh fails
        public string Error;        // last refresh error, null when the last refresh worked
        public bool Busy;
        public int Failures;        // refreshes failed in a row
        public DateTime LastAttempt = DateTime.MinValue;
        public DateTime NextDue = DateTime.MinValue;

        public bool HasData { get { return Last != null && Last.Limits.Count > 0; } }
        public bool Warning { get { return HasData && Error != null; } }
        public bool Faded(DateTime now) { return Warning && now - Last.FetchedAt > StaleAfter; }
    }

    class FetchResult
    {
        public Snapshot Snap;
        public string Error;
        public bool RateLimited;
        public TimeSpan? RetryAfter;
    }

    static class Fetcher
    {
        public static FetchResult Run(Provider p)
        {
            var res = new FetchResult();
            try
            {
                res.Snap = p.Fetch();
                res.Snap.FetchedAt = DateTime.UtcNow;
                res.Snap.Limits.Sort((a, b) => a.WindowMinutes.CompareTo(b.WindowMinutes));
            }
            catch (FetchException fe) { res.Error = fe.Message; res.RateLimited = fe.RateLimited; res.RetryAfter = fe.RetryAfter; }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; Paths.Log(p.Name + ": " + ex); }
            return res;
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

        // Anthropic rate-limits this endpoint per account, and Claude Code polls it too.
        public override double MinIntervalMinutes { get { return 5; } }

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
                if (e.Status == 429)
                {
                    var fe = new FetchException("Rate limited by Anthropic, retrying later");
                    fe.RateLimited = true;
                    fe.RetryAfter = e.RetryAfter;
                    throw fe;
                }
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
                    w.WriteLine("{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"usage_tracker\",\"title\":\"Usage Tracker\",\"version\":\"1.1.0\"}}}");
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
        public string Layout { get; set; }        // "wide", "narrow" or "taskbar"
        public int X { get; set; }                // wide window position
        public int Y { get; set; }
        public int NX { get; set; }               // narrow window position
        public int NY { get; set; }
        public string DockScreen { get; set; }    // taskbar mode: display device name...
        public string DockSide { get; set; }      // ...and its side ("main", "right", ...) as a fallback
        public int DockGap { get; set; }          // taskbar mode: pixels from the taskbar's right end, -1 = default
        public bool OnTop { get; set; }
        public int Opacity { get; set; }
        public double UiScale { get; set; }
        public bool ShowResets { get; set; }

        public AppState()
        {
            Layout = "wide";
            X = Y = NX = NY = int.MinValue;
            DockGap = -1;
            OnTop = true;
            Opacity = 100;
            UiScale = 1.0;
            ShowResets = true;
        }

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
                        if (s.Layout != "narrow" && s.Layout != "taskbar") s.Layout = "wide";
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

    enum ViewMode { Wide, Narrow, Strip }

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(27, 29, 34);
        public static readonly Color Border = Color.FromArgb(58, 63, 71);

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Parse(string hex, Color fallback)
        {
            try { if (!string.IsNullOrEmpty(hex)) return ColorTranslator.FromHtml(hex.Trim()); }
            catch { }
            return fallback;
        }
    }

    class Palette
    {
        public Color Bg, Text, Dim, Track, Line, Ok, Warn, High;
        public bool Transparent;   // drawn with per-pixel alpha (taskbar mode), so GDI+ text

        public static readonly Palette Dark = new Palette
        {
            Bg = Theme.Bg, Text = Color.FromArgb(230, 232, 235), Dim = Color.FromArgb(138, 144, 153),
            Track = Color.FromArgb(46, 50, 59), Line = Color.FromArgb(44, 48, 56),
            Ok = Color.FromArgb(90, 169, 230), Warn = Color.FromArgb(232, 176, 74), High = Color.FromArgb(229, 83, 75),
        };

        public static readonly Palette DarkTaskbar = new Palette
        {
            Transparent = true, Bg = Color.Transparent,
            Text = Color.FromArgb(255, 255, 255), Dim = Color.FromArgb(196, 200, 206),
            Track = Color.FromArgb(64, 255, 255, 255), Line = Color.FromArgb(56, 255, 255, 255),
            Ok = Color.FromArgb(98, 180, 242), Warn = Color.FromArgb(242, 186, 84), High = Color.FromArgb(242, 100, 92),
        };

        public static readonly Palette LightTaskbar = new Palette
        {
            Transparent = true, Bg = Color.Transparent,
            Text = Color.FromArgb(28, 28, 28), Dim = Color.FromArgb(88, 92, 98),
            Track = Color.FromArgb(44, 0, 0, 0), Line = Color.FromArgb(48, 0, 0, 0),
            Ok = Color.FromArgb(0, 108, 196), Warn = Color.FromArgb(184, 116, 0), High = Color.FromArgb(196, 43, 33),
        };

        public Color For(double p) { return p >= 90 ? High : p >= 70 ? Warn : Ok; }

        public Color Fade(Color c, bool faded)
        {
            if (!faded) return c;
            if (Transparent) return Color.FromArgb(Math.Min((int)c.A, 105), c);
            return Theme.Mix(c, Bg, 0.55);
        }
    }

    // Lays out and draws the providers in one of three shapes:
    //   Wide:   providers stacked, each a name column plus one row per limit (the original look)
    //   Narrow: providers stacked in a thin column, each limit a text line over a full-width bar
    //   Strip:  providers side by side, two rows high, sized to fit inside the taskbar
    class Renderer : IDisposable
    {
        const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                                   TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        readonly float S;
        readonly ViewMode mode;
        readonly bool showResets;
        readonly Palette pal;
        readonly IList<ProviderState> items;
        readonly Font fName, fText, fSmall;
        readonly Bitmap measureBmp = new Bitmap(1, 1);
        readonly Graphics measure;   // measuring through a DC is tight; the DC-less overload pads ~8 px
        readonly List<Rectangle> blocks = new List<Rectangle>();
        readonly int accentW, barH, labelW, pctW, resetW;
        int pad, gap, rowH, barW, width, height;
        int xName, nameColW, xLabel, xBar, xPct, xReset;       // wide
        int nLeft, nContentW, nHeadH, nLineH, nBarRowH;         // narrow
        int sTop;                                               // strip
        readonly List<int> sNameW = new List<int>();

        public Renderer(float scale, ViewMode mode, bool showResets, Palette pal, int stripHeight, IList<ProviderState> items)
        {
            S = scale;
            this.mode = mode;
            this.showResets = showResets;
            this.pal = pal;
            this.items = items;
            fName = new Font("Segoe UI Semibold", 12f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            fText = new Font("Segoe UI", 11.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            fSmall = new Font("Segoe UI", 10.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            measure = Graphics.FromImage(measureBmp);

            accentW = Math.Max(2, P(3));
            barH = Math.Max(4, P(mode == ViewMode.Wide ? 6 : 5));
            // Countdown column: as wide as the longest countdown each window can show ("4h 59m", "6d 23h").
            Font fr = mode == ViewMode.Narrow ? fSmall : fText;
            int lw = W("wk", fText), rw = 0;
            foreach (var it in items)
                if (it.Last != null)
                    foreach (var l in it.Last.Limits)
                    {
                        lw = Math.Max(lw, W(l.Label, fText));
                        rw = Math.Max(rw, W(LongestCountdown(l.WindowMinutes), fr));
                    }
            labelW = lw;
            pctW = W("100%", fText);
            resetW = rw > 0 ? rw : W("6d 23h", fr);

            if (mode == ViewMode.Narrow) LayoutNarrow();
            else if (mode == ViewMode.Strip) LayoutStrip(stripHeight);
            else LayoutWide();
        }

        public Size Size { get { return new Size(width, height); } }

        int P(float v) { return (int)Math.Round(v * S); }

        // Measured with the same engine that draws: GDI for normal windows, GDI+ for the transparent strip.
        int W(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (pal.Transparent)
            {
                measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                return (int)Math.Ceiling(measure.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width) + 2;
            }
            return TextRenderer.MeasureText(measure, s, f, new Size(10000, 1000), TF).Width;
        }

        static string LongestCountdown(double windowMinutes)
        {
            if (windowMinutes <= 0) return "23h 59m";
            return Fmt.Short(TimeSpan.FromMinutes(Math.Max(1, windowMinutes - 1)));
        }

        static int LimitCount(ProviderState it) { return it.HasData ? it.Last.Limits.Count : 0; }

        // ---- layout

        void LayoutWide()
        {
            pad = P(9); gap = P(11); rowH = P(17);
            int nw = P(44);
            foreach (var it in items)
            {
                nw = Math.Max(nw, W(it.P.Name, fName) + P(11));
                if (it.Last != null && it.Last.Plan != null) nw = Math.Max(nw, W(it.Last.Plan, fSmall));
            }
            nameColW = Math.Min(nw, P(130));
            xName = pad + accentW + P(7);
            xLabel = xName + nameColW + P(8);
            xBar = xLabel + labelW + P(7);
            barW = P(104);
            xPct = xBar + barW + P(7);
            xReset = xPct + pctW + P(10);
            width = (showResets ? xReset + resetW : xPct + pctW) + pad;
            int y = pad - P(1);
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) y += gap;
                int h = Math.Max(2, LimitCount(items[i])) * rowH;
                blocks.Add(new Rectangle(0, y, width, h));
                y += h;
            }
            height = y + pad - P(1);
        }

        void LayoutNarrow()
        {
            pad = P(7); gap = P(10);
            nHeadH = P(18); nLineH = P(15); nBarRowH = P(9);
            nLeft = P(10);
            int need = P(80);
            foreach (var it in items) need = Math.Max(need, W(it.P.Name, fName) + P(10));
            need = Math.Max(need, labelW + P(5) + (showResets ? resetW + P(6) : 0) + pctW);
            nContentW = need;
            barW = nContentW;
            width = nLeft + nContentW + pad;
            int y = pad - P(1);
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) y += gap;
                int n = LimitCount(items[i]);
                int h = nHeadH + (n > 0 ? n * (nLineH + nBarRowH) : P(36));
                blocks.Add(new Rectangle(0, y, width, h));
                y += h;
            }
            height = y + pad - P(2);
        }

        void LayoutStrip(int stripHeight)
        {
            pad = P(6); gap = P(16);
            int H = stripHeight > 0 ? stripHeight : P(44);
            rowH = Math.Min(P(17), (H - P(4)) / 2);
            sTop = (H - 2 * rowH) / 2;
            barW = P(64);
            int dataW = labelW + P(5) + barW + P(6) + pctW + (showResets ? P(8) + resetW : 0);
            int x = pad;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (i > 0) x += gap;
                int nw = W(it.P.Name, fName) + P(10);
                if (it.Last != null && it.Last.Plan != null) nw = Math.Max(nw, W(it.Last.Plan, fSmall));
                nw = Math.Min(nw, P(110));
                sNameW.Add(nw);
                int bw = accentW + P(6) + nw + P(8) + dataW;
                blocks.Add(new Rectangle(x, 0, bw, H));
                x += bw;
            }
            width = x + pad;
            height = H;
        }

        public int HitTest(Point p)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (mode == ViewMode.Strip ? p.X < b.Right + gap / 2 : p.Y < b.Bottom + gap / 2) return i;
            }
            return blocks.Count - 1;
        }

        // ---- drawing

        public void Draw(Graphics g, DateTime now)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (pal.Transparent) g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            for (int i = 0; i < items.Count && i < blocks.Count; i++)
            {
                var it = items[i];
                var b = blocks[i];
                bool faded = it.Faded(now);
                if (mode == ViewMode.Strip) DrawStrip(g, it, b, sNameW[i], faded, now);
                else if (mode == ViewMode.Narrow) DrawNarrow(g, it, b, faded, now);
                else DrawWide(g, it, b, faded, now);

                if (i < items.Count - 1)
                {
                    var old = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.None;
                    using (var pen = new Pen(pal.Line, Math.Max(1, P(1))))
                    {
                        if (mode == ViewMode.Strip)
                        {
                            int x = b.Right + gap / 2;
                            g.DrawLine(pen, x, sTop + P(3), x, sTop + 2 * rowH - P(3));
                        }
                        else
                        {
                            int y = b.Bottom + gap / 2;
                            g.DrawLine(pen, pad, y, width - pad, y);
                        }
                    }
                    g.SmoothingMode = old;
                }
            }
        }

        void DrawWide(Graphics g, ProviderState it, Rectangle b, bool faded, DateTime now)
        {
            Accent(g, it, pad, b.Top + P(2), b.Height - P(4), faded);
            int nameMax = nameColW - P(11);
            Txt(g, it.P.Name, fName, new Rectangle(xName, b.Top, nameMax, rowH), pal.Fade(pal.Text, faded), false, true);
            Dot(g, it, xName + Math.Min(W(it.P.Name, fName), nameMax) + P(5), b.Top + rowH / 2f);
            if (it.Last != null && !string.IsNullOrEmpty(it.Last.Plan))
                Txt(g, it.Last.Plan, fSmall, new Rectangle(xName, b.Top + rowH, nameColW, rowH), pal.Dim, false, true);

            if (!it.HasData) { Message(g, it, new Rectangle(xLabel, b.Top, width - pad - xLabel, b.Height)); return; }
            for (int j = 0; j < it.Last.Limits.Count; j++)
            {
                var l = it.Last.Limits[j];
                int y = b.Top + j * rowH;
                Txt(g, l.Label, fText, new Rectangle(xLabel, y, xBar - xLabel, rowH), pal.Fade(pal.Dim, faded), false, false);
                Bar(g, l, xBar, y + (rowH - barH) / 2f, barW, faded, now);
                Txt(g, Fmt.Pct(l.Percent), fText, new Rectangle(xPct, y, pctW, rowH), PctColor(l, faded), true, false);
                if (showResets && l.ResetsAt.HasValue)
                    Txt(g, Fmt.Short(l.ResetsAt.Value - now), fText, new Rectangle(xReset, y, resetW, rowH), pal.Fade(pal.Dim, faded), false, false);
            }
        }

        void DrawNarrow(Graphics g, ProviderState it, Rectangle b, bool faded, DateTime now)
        {
            Accent(g, it, P(3), b.Top + P(2), b.Height - P(4), faded);
            int x = nLeft;
            int nameW = W(it.P.Name, fName);
            string plan = it.Last != null ? it.Last.Plan : null;
            int planW = string.IsNullOrEmpty(plan) ? 0 : W(plan, fSmall);
            bool showPlan = planW > 0 && nameW + P(14) + planW <= nContentW;
            int nameMax = showPlan ? nContentW - planW - P(14) : nContentW - P(10);
            Txt(g, it.P.Name, fName, new Rectangle(x, b.Top, nameMax, nHeadH), pal.Fade(pal.Text, faded), false, true);
            Dot(g, it, x + Math.Min(nameW, nameMax) + P(5), b.Top + nHeadH / 2f);
            if (showPlan) Txt(g, plan, fSmall, new Rectangle(x, b.Top, nContentW, nHeadH), pal.Dim, true, false);

            if (!it.HasData) { Message(g, it, new Rectangle(x, b.Top + nHeadH, nContentW, b.Height - nHeadH)); return; }
            int y = b.Top + nHeadH;
            foreach (var l in it.Last.Limits)
            {
                Txt(g, l.Label, fText, new Rectangle(x, y, labelW, nLineH), pal.Fade(pal.Dim, faded), false, false);
                if (showResets && l.ResetsAt.HasValue)
                    Txt(g, Fmt.Short(l.ResetsAt.Value - now), fSmall, new Rectangle(x + labelW + P(5), y, resetW, nLineH), pal.Fade(pal.Dim, faded), false, false);
                Txt(g, Fmt.Pct(l.Percent), fText, new Rectangle(x + nContentW - pctW, y, pctW, nLineH), PctColor(l, faded), true, false);
                Bar(g, l, x, y + nLineH + P(1), nContentW, faded, now);
                y += nLineH + nBarRowH;
            }
        }

        void DrawStrip(Graphics g, ProviderState it, Rectangle b, int nw, bool faded, DateTime now)
        {
            Accent(g, it, b.Left, sTop + P(1), 2 * rowH - P(2), faded);
            int xn = b.Left + accentW + P(6);
            int nameMax = nw - P(10);
            Txt(g, it.P.Name, fName, new Rectangle(xn, sTop, nameMax, rowH), pal.Fade(pal.Text, faded), false, true);
            Dot(g, it, xn + Math.Min(W(it.P.Name, fName), nameMax) + P(5), sTop + rowH / 2f);
            if (it.Last != null && !string.IsNullOrEmpty(it.Last.Plan))
                Txt(g, it.Last.Plan, fSmall, new Rectangle(xn, sTop + rowH, nw, rowH), pal.Dim, false, true);

            int xl = xn + nw + P(8);
            if (!it.HasData) { Message(g, it, new Rectangle(xl, sTop, b.Right - xl, 2 * rowH)); return; }
            int xb = xl + labelW + P(5), xp = xb + barW + P(6), xr = xp + pctW + P(8);
            for (int j = 0; j < it.Last.Limits.Count && j < 2; j++)
            {
                var l = it.Last.Limits[j];
                int y = sTop + j * rowH;
                Txt(g, l.Label, fText, new Rectangle(xl, y, labelW + P(5), rowH), pal.Fade(pal.Dim, faded), false, false);
                Bar(g, l, xb, y + (rowH - barH) / 2f, barW, faded, now);
                Txt(g, Fmt.Pct(l.Percent), fText, new Rectangle(xp, y, pctW, rowH), PctColor(l, faded), true, false);
                if (showResets && l.ResetsAt.HasValue)
                    Txt(g, Fmt.Short(l.ResetsAt.Value - now), fText, new Rectangle(xr, y, resetW, rowH), pal.Fade(pal.Dim, faded), false, false);
            }
        }

        // ---- pieces

        Color PctColor(Limit l, bool faded) { return pal.Fade(l.Percent >= 100 ? pal.High : pal.Text, faded); }

        void Accent(Graphics g, ProviderState it, float x, float y, float h, bool faded)
        {
            using (var b = new SolidBrush(pal.Fade(it.P.Accent, faded))) FillRound(g, b, x, y, accentW, h, accentW / 2f);
        }

        // Blue while refreshing, amber when the last refresh failed and older numbers are showing.
        void Dot(Graphics g, ProviderState it, float cx, float cy)
        {
            if (!it.Busy && !it.Warning) return;
            float r = Math.Max(2f, 2.5f * S);
            using (var b = new SolidBrush(it.Busy ? pal.Ok : pal.Warn)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        }

        void Message(Graphics g, ProviderState it, Rectangle rc)
        {
            string msg = it.Error ?? (it.Busy ? "Loading…" : "Waiting…");
            Para(g, msg, fSmall, rc, it.Error != null ? pal.Warn : pal.Dim);
        }

        // Usage bar with a pace tick showing how much of the window's time has gone by.
        void Bar(Graphics g, Limit l, float x, float y, float w, bool faded, DateTime now)
        {
            double pct = Math.Max(0, Math.Min(100, l.Percent));
            using (var b = new SolidBrush(pal.Track)) FillRound(g, b, x, y, w, barH, barH / 2f);
            float fw = (float)(w * pct / 100.0);
            if (pct > 0 && fw < barH) fw = barH;
            if (fw > 0)
                using (var b = new SolidBrush(pal.Fade(pal.For(l.Percent), faded)))
                    FillRound(g, b, x, y, fw, barH, barH / 2f);
            if (l.ResetsAt.HasValue && l.WindowMinutes > 0)
            {
                double frac = 1 - (l.ResetsAt.Value - now).TotalMinutes / l.WindowMinutes;
                if (frac > 0 && frac < 1)
                {
                    float px = x + (float)(w * frac);
                    float tw = Math.Max(1f, 1.5f * S);
                    using (var b = new SolidBrush(Color.FromArgb(faded ? 90 : 190, pal.Text)))
                        g.FillRectangle(b, px - tw / 2f, y - P(2.5f), tw, barH + P(5));
                }
            }
        }

        void Txt(Graphics g, string s, Font f, Rectangle rc, Color c, bool right, bool ellipsis)
        {
            if (string.IsNullOrEmpty(s) || rc.Width <= 0) return;
            if (pal.Transparent)
            {
                using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
                using (var b = new SolidBrush(c))
                {
                    sf.FormatFlags = (sf.FormatFlags | StringFormatFlags.NoWrap) & ~StringFormatFlags.NoClip;
                    sf.Alignment = right ? StringAlignment.Far : StringAlignment.Near;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;
                    g.DrawString(s, f, b, rc, sf);
                }
                return;
            }
            var flags = TF;
            if (right) flags |= TextFormatFlags.Right;
            if (ellipsis) flags |= TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, s, f, rc, c, flags);
        }

        void Para(Graphics g, string s, Font f, Rectangle rc, Color c)
        {
            if (string.IsNullOrEmpty(s) || rc.Width <= 0) return;
            if (pal.Transparent)
            {
                // Grid-fitted typographic layout spaces wrapped small text oddly; plain anti-aliasing doesn't.
                var oldHint = g.TextRenderingHint;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (var sf = new StringFormat())
                using (var b = new SolidBrush(c))
                {
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = StringTrimming.EllipsisWord;
                    g.DrawString(s, f, b, rc, sf);
                }
                g.TextRenderingHint = oldHint;
                return;
            }
            TextRenderer.DrawText(g, s, f, rc, c,
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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

        public void Dispose()
        {
            measure.Dispose();
            measureBmp.Dispose();
            fName.Dispose();
            fText.Dispose();
            fSmall.Dispose();
        }
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

    // ------------------------------------------------------------------ taskbar helpers

    static class Taskbar
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        // The strip a display's taskbar occupies (bottom or top), or empty when it has none or it auto-hides.
        public static Rectangle Area(Screen s)
        {
            var b = s.Bounds;
            var wa = s.WorkingArea;
            if (wa.Bottom < b.Bottom) return new Rectangle(b.Left, wa.Bottom, b.Width, b.Bottom - wa.Bottom);
            if (wa.Top > b.Top) return new Rectangle(b.Left, b.Top, b.Width, wa.Top - b.Top);
            return Rectangle.Empty;
        }

        public static string Side(Screen s)
        {
            if (s.Primary) return "main";
            var p = Screen.PrimaryScreen.Bounds;
            var b = s.Bounds;
            if (b.Left >= p.Right) return "right";
            if (b.Right <= p.Left) return "left";
            if (b.Top >= p.Bottom) return "lower";
            if (b.Bottom <= p.Top) return "upper";
            return "other";
        }

        public static string Number(Screen s)
        {
            var m = Regex.Match(s.DeviceName ?? "", @"(\d+)\s*$");
            return m.Success ? m.Groups[1].Value : "?";
        }

        public static Screen Find(string device, string side)
        {
            foreach (var s in Screen.AllScreens) if (s.DeviceName == device) return s;
            foreach (var s in Screen.AllScreens) if (Side(s) == side) return s;
            return null;
        }

        public static Rectangle RectOf(IntPtr h)
        {
            RECT r;
            if (!GetWindowRect(h, out r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        public static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        // The taskbar window (main or secondary) that covers the given strip.
        public static IntPtr WindowFor(Rectangle area, IntPtr cached)
        {
            if (cached != IntPtr.Zero && IsWindow(cached) && Covers(RectOf(cached), area)) return cached;
            foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr h = IntPtr.Zero;
                while ((h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
                    if (IsWindowVisible(h) && Covers(RectOf(h), area)) return h;
            }
            return IntPtr.Zero;
        }

        static bool Covers(Rectangle win, Rectangle area)
        {
            var i = Rectangle.Intersect(win, area);
            return (long)i.Width * i.Height * 2 > (long)area.Width * area.Height;
        }

        // Where the tray or clock starts on that taskbar, if Windows exposes it as a window.
        public static int TrayLeft(IntPtr taskbar)
        {
            if (taskbar == IntPtr.Zero) return -1;
            foreach (var cls in new[] { "TrayNotifyWnd", "ClockButton" })
            {
                IntPtr c = FindWindowEx(taskbar, IntPtr.Zero, cls, null);
                if (c != IntPtr.Zero && IsWindowVisible(c))
                {
                    var r = RectOf(c);
                    if (r.Width > 0) return r.Left;
                }
            }
            return -1;
        }

        public static bool IsLightTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ the widget window

    class Widget : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT pptDst, ref SZ psize, IntPtr hdcSrc, ref PT pptSrc, int crKey, ref BLEND pblend, int flags);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; public PT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; public SZ(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "UsageTracker";
        const int DefaultDockGap = 110;   // px at 100% scale: clears the clock on a secondary taskbar

        readonly EventWaitHandle showSignal;
        AppConfig cfg;
        AppState st;
        List<ProviderState> items = new List<ProviderState>();
        Renderer r;
        float dpi = 1f;
        int hover = -1;

        bool wantLayered;      // the window is (or will be created as) a per-pixel-alpha layered window
        bool docked;           // taskbar mode is on and the chosen display has a visible taskbar
        bool placedOnce, userHidden, autoHidden;
        Screen dockScreen;
        Rectangle dockBar;
        IntPtr taskbarHwnd, winHook;
        WinEventProc hookProc;

        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 15000 };
        readonly System.Windows.Forms.Timer reload = new System.Windows.Forms.Timer { Interval = 700 };
        readonly System.Windows.Forms.Timer keeper = new System.Windows.Forms.Timer { Interval = 1500 };
        FileSystemWatcher watcher;
        NotifyIcon tray;
        ContextMenuStrip menu;
        ToolTip tip;
        ToolStripMenuItem miShow, miTop, miResets, miStartup, miSize, miOpacity, miLayout;

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
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX / 96f;   // no window handle yet on purpose
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

            ApplyLayout();

            tick.Tick += (s, e) => OnTick();
            keeper.Tick += (s, e) => KeepDocked();
            reload.Tick += (s, e) => { reload.Stop(); LoadConfig(); Relayout(true); OnTick(); };
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
            SystemEvents.DisplaySettingsChanged += OnDisplayChange;
            SystemEvents.UserPreferenceChanged += OnPreferenceChange;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;                      // WS_EX_TOOLWINDOW: no taskbar button, not in Alt+Tab
                if (wantLayered) cp.ExStyle |= 0x80000;  // WS_EX_LAYERED, fed by UpdateLayeredWindow
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (wantLayered) { PushLayered(); return; }
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
            Redraw();
            OnTick();   // first fetch for every provider
        }

        // ---- config and layout

        ViewMode Mode
        {
            get { return docked ? ViewMode.Strip : st.Layout == "narrow" ? ViewMode.Narrow : ViewMode.Wide; }
        }

        void LoadConfig()
        {
            cfg = AppConfig.Load(Paths.ConfigFile);
            var fresh = new List<ProviderState>();
            foreach (var p in cfg.Providers)
            {
                var ps = new ProviderState { P = p };
                var prev = items.FirstOrDefault(o => o.P.Key == p.Key);
                if (prev != null)
                {
                    ps.Last = prev.Last;
                    ps.Error = prev.Error;
                    ps.Failures = prev.Failures;
                    ps.LastAttempt = prev.LastAttempt;
                    ps.NextDue = prev.NextDue;
                }
                fresh.Add(ps);
            }
            items = fresh;
            hover = -1;
        }

        void ResolveDock()
        {
            docked = false;
            dockScreen = null;
            dockBar = Rectangle.Empty;
            if (st.Layout != "taskbar") return;
            var s = Taskbar.Find(st.DockScreen, st.DockSide);
            if (s == null) return;
            var bar = Taskbar.Area(s);
            if (bar.IsEmpty) return;
            dockScreen = s;
            dockBar = bar;
            docked = true;
        }

        // Re-applies the chosen layout: picks the display, switches the window between a normal
        // window and a transparent taskbar strip, sizes it and puts it in place.
        void ApplyLayout()
        {
            ResolveDock();
            if (docked != wantLayered)
            {
                wantLayered = docked;
                if (docked) Opacity = 1.0;   // taskbar mode does its own opacity; keep WinForms out of it
                if (IsHandleCreated) RecreateHandle();
            }
            ApplyState();
            Relayout(false);
            Place();
            placedOnce = true;
            Redraw();
            if (docked && winHook == IntPtr.Zero)
            {
                hookProc = OnForegroundChanged;
                winHook = SetWinEventHook(3, 3, IntPtr.Zero, hookProc, 0, 0, 0);   // EVENT_SYSTEM_FOREGROUND, out of context
            }
            else if (!docked && winHook != IntPtr.Zero)
            {
                UnhookWinEvent(winHook);
                winHook = IntPtr.Zero;
            }
            if (st.Layout == "taskbar") keeper.Start(); else keeper.Stop();
        }

        void Relayout(bool keepOnScreen)
        {
            if (r != null) r.Dispose();
            var pal = docked ? (Taskbar.IsLightTheme() ? Palette.LightTaskbar : Palette.DarkTaskbar) : Palette.Dark;
            r = new Renderer(dpi * (float)st.UiScale, Mode, st.ShowResets, pal, docked ? dockBar.Height : 0, items);
            var size = r.Size;
            if (size != ClientSize)
            {
                ClientSize = size;
                if (docked) PlaceDocked();
                else if (keepOnScreen && Visible) KeepOnScreen();
            }
            Redraw();
        }

        void Place()
        {
            if (docked) { PlaceDocked(); return; }
            bool narrow = Mode == ViewMode.Narrow;
            int x = narrow ? st.NX : st.X, y = narrow ? st.NY : st.Y;
            if (x != int.MinValue && OnScreen(new Rectangle(x, y, Width, Height))) { Location = new Point(x, y); return; }
            if (placedOnce && OnScreen(Bounds)) { KeepOnScreen(); return; }   // switching layouts: stay put
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        }

        static bool OnScreen(Rectangle rect)
        {
            return Screen.AllScreens.Any(s =>
            {
                var i = Rectangle.Intersect(s.WorkingArea, rect);
                return i.Width >= 40 && i.Height >= 20;
            });
        }

        void PlaceDocked()
        {
            int gap = st.DockGap;
            if (gap < 0)
            {
                int trayLeft = Taskbar.TrayLeft(TaskbarWindow());
                gap = trayLeft > dockBar.Left && trayLeft < dockBar.Right
                    ? dockBar.Right - trayLeft + (int)Math.Round(8 * dpi)
                    : (int)Math.Round(DefaultDockGap * dpi);
            }
            int x = dockBar.Right - gap - Width;
            x = Math.Max(dockBar.Left, Math.Min(x, dockBar.Right - Width));
            var p = new Point(x, dockBar.Top);
            if (Location != p) Location = p;
        }

        void KeepOnScreen()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
            int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
            if (x != Left || y != Top) Location = new Point(x, y);
        }

        void SavePosition()
        {
            if (docked) st.DockGap = dockBar.Right - Right;
            else if (Mode == ViewMode.Narrow) { st.NX = Left; st.NY = Top; }
            else { st.X = Left; st.Y = Top; }
            st.Save(Paths.StateFile);
        }

        void ApplyState()
        {
            TopMost = docked || st.OnTop;
            if (!wantLayered) Opacity = Math.Max(30, Math.Min(100, st.Opacity)) / 100.0;
        }

        void SetLayout(string layout, Screen screen)
        {
            st.Layout = layout;
            if (screen != null) { st.DockScreen = screen.DeviceName; st.DockSide = Taskbar.Side(screen); }
            st.Save(Paths.StateFile);
            autoHidden = false;
            ApplyLayout();
            if (!userHidden && !Visible) Show();
        }

        void ShowWidget()
        {
            userHidden = false;
            autoHidden = false;
            if (!Visible) Show();
            if (docked) { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); Redraw(); return; }
            if (!st.OnTop) { TopMost = true; TopMost = false; }   // pop above other windows once
            BringToFront();
        }

        // ---- taskbar mode upkeep

        IntPtr TaskbarWindow()
        {
            taskbarHwnd = Taskbar.WindowFor(dockBar, taskbarHwnd);
            return taskbarHwnd;
        }

        void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            try { BeginInvoke(new Action(KeepDocked)); } catch { }
        }

        // Runs every 1.5 s and on every focus change while in taskbar mode: follows display changes,
        // gets out of the way of full-screen apps, and climbs back above the taskbar after it's clicked.
        void KeepDocked()
        {
            if (st.Layout != "taskbar" || IsDisposed) return;
            bool was = docked;
            var oldBar = dockBar;
            ResolveDock();
            if (docked != was) { ApplyLayout(); return; }
            if (!docked) return;
            if (dockBar != oldBar) { Relayout(false); PlaceDocked(); }

            bool fullscreen = FullscreenOn(dockScreen);
            if (fullscreen && Visible) { autoHidden = true; Hide(); }
            else if (!fullscreen && autoHidden)
            {
                autoHidden = false;
                if (!userHidden) { Show(); Redraw(); }
            }

            if (Visible && TaskbarAbove()) SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        bool TaskbarAbove()
        {
            IntPtr tb = TaskbarWindow();
            if (tb == IntPtr.Zero) return false;
            int guard = 0;
            for (IntPtr h = GetWindow(Handle, 3); h != IntPtr.Zero && guard < 2000; h = GetWindow(h, 3), guard++)   // GW_HWNDPREV
                if (h == tb) return true;
            return false;
        }

        bool FullscreenOn(Screen s)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return false;
            string cls = Taskbar.ClassOf(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            var w = Taskbar.RectOf(fg);
            var b = s.Bounds;
            return w.Left <= b.Left && w.Top <= b.Top && w.Right >= b.Right && w.Bottom >= b.Bottom;
        }

        // ---- refreshing

        TimeSpan IntervalFor(ProviderState it)
        {
            return TimeSpan.FromMinutes(Math.Max(cfg.RefreshMinutes, it.P.MinIntervalMinutes));
        }

        void RefreshAll() { foreach (var it in items) StartFetch(it); }

        void StartFetch(ProviderState it)
        {
            if (it.Busy) return;
            it.Busy = true;
            it.LastAttempt = DateTime.UtcNow;
            Redraw();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var res = Fetcher.Run(it.P);
                try { BeginInvoke(new Action(() => OnFetched(it, res))); }
                catch { }
            });
        }

        void OnFetched(ProviderState it, FetchResult res)
        {
            it.Busy = false;
            var now = DateTime.UtcNow;
            var interval = IntervalFor(it);
            if (res.Snap != null)
            {
                it.Last = res.Snap;
                it.Error = null;
                it.Failures = 0;
                it.NextDue = now + interval;
            }
            else
            {
                it.Error = res.Error;
                it.Failures++;
                var wait = interval;
                if (res.RateLimited)   // back off: 5, 10, 20, then 30 minutes
                    wait = TimeSpan.FromMinutes(Math.Max(interval.TotalMinutes, Math.Min(30, 5 * Math.Pow(2, Math.Min(it.Failures, 4) - 1))));
                if (res.RetryAfter.HasValue && res.RetryAfter.Value > wait) wait = res.RetryAfter.Value;
                it.NextDue = now + wait;
            }
            UpdateTrayText();
            Relayout(true);
            if (hover >= 0 && hover < items.Count) tip.SetToolTip(this, TipText(items[hover]));
        }

        void OnTick()
        {
            var now = DateTime.UtcNow;
            foreach (var it in items)
            {
                if (it.Busy) continue;
                bool due = now >= it.NextDue;
                // A window just reset: check once for fresh numbers instead of waiting for the next poll.
                if (!due && it.Failures == 0 && it.Last != null && now - it.LastAttempt >= TimeSpan.FromSeconds(60))
                    foreach (var l in it.Last.Limits)
                        if (l.ResetsAt.HasValue && l.ResetsAt.Value < now && it.LastAttempt < l.ResetsAt.Value) due = true;
                if (due) StartFetch(it);
            }
            Redraw();   // countdowns and pace ticks move
        }

        void OnPowerChange(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    // check again about 20 s after waking, once the network is back
                    var soon = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                    foreach (var it in items) if (!it.Busy && it.NextDue > soon) it.NextDue = soon;
                }));
            }
            catch { }
        }

        void OnDisplayChange(object sender, EventArgs e)
        {
            try { BeginInvoke(new Action(() => { taskbarHwnd = IntPtr.Zero; ApplyLayout(); })); } catch { }
        }

        void OnPreferenceChange(object sender, UserPreferenceChangedEventArgs e)
        {
            if (!docked) return;
            try { BeginInvoke(new Action(() => Relayout(false))); } catch { }   // light/dark switch, taskbar size
        }

        // ---- painting

        void Redraw()
        {
            if (wantLayered) PushLayered();
            else Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (wantLayered) return;
            e.Graphics.Clear(Theme.Bg);
            if (r != null) r.Draw(e.Graphics, DateTime.UtcNow);
        }

        // Taskbar mode: draw onto a transparent bitmap and hand it to Windows with per-pixel alpha.
        void PushLayered()
        {
            if (!IsHandleCreated || r == null) return;
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(1, 0, 0, 0));   // invisible, but keeps the whole strip clickable
                    r.Draw(g, DateTime.UtcNow);
                }
                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hbmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                    old = SelectObject(memDc, hbmp);
                    var size = new SZ(w, h);
                    var src = new PT(0, 0);
                    var dst = new PT(Left, Top);
                    var blend = new BLEND { Op = 0, Flags = 0, Alpha = (byte)(Math.Max(30, Math.Min(100, st.Opacity)) * 255 / 100), Format = 1 };
                    UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);   // ULW_ALPHA
                }
                finally
                {
                    if (old != IntPtr.Zero) SelectObject(memDc, old);
                    if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                    DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        // ---- mouse

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
            if (m.Msg == 0x0216 && docked)   // WM_MOVING: in taskbar mode, slide along the taskbar only
            {
                var rc = (Taskbar.RECT)Marshal.PtrToStructure(m.LParam, typeof(Taskbar.RECT));
                int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
                int x = Math.Max(dockBar.Left, Math.Min(rc.Left, dockBar.Right - w));
                rc.Left = x; rc.Right = x + w; rc.Top = dockBar.Top; rc.Bottom = dockBar.Top + h;
                Marshal.StructureToPtr(rc, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == 0x0232) SavePosition();   // WM_EXITSIZEMOVE: drag finished
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (r == null || items.Count == 0) return;
            int h = r.HitTest(e.Location);
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
                sb.Append("Updated " + Fmt.Clock(it.Last.FetchedAt));
            }
            if (it.Error != null)
            {
                sb.AppendLine();
                sb.Append(it.Last != null ? "Last check failed: " + it.Error : it.Error);
                if (it.NextDue > now && !it.Busy) sb.Append(". Next try " + Fmt.Clock(it.NextDue));
            }
            if (st.Layout == "taskbar" && !docked)
            {
                sb.AppendLine();
                sb.Append("That display has no visible taskbar, so this is floating for now");
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
            miShow = Item(m, "Hide", () =>
            {
                if (Visible || autoHidden) { userHidden = true; autoHidden = false; Hide(); }
                else ShowWidget();
            });
            Item(m, "Refresh now", RefreshAll);
            m.Add(new ToolStripSeparator());
            miLayout = new ToolStripMenuItem("Layout");
            miLayout.DropDownItems.Add(new ToolStripMenuItem("(filled when opened)"));
            m.Add(miLayout);
            miTop = Item(m, "Always on top", () => { st.OnTop = !st.OnTop; ApplyState(); st.Save(Paths.StateFile); });
            miResets = Item(m, "Show reset times", () => { st.ShowResets = !st.ShowResets; Relayout(true); st.Save(Paths.StateFile); });

            miSize = new ToolStripMenuItem("Size");
            foreach (var pair in new[] { Tuple.Create("Small", 0.85), Tuple.Create("Normal", 1.0), Tuple.Create("Large", 1.25), Tuple.Create("Extra large", 1.5) })
            {
                var v = pair.Item2;
                var mi = Item(miSize.DropDownItems, pair.Item1, () => { st.UiScale = v; Relayout(true); if (docked) PlaceDocked(); st.Save(Paths.StateFile); });
                mi.Tag = v;
            }
            m.Add(miSize);

            miOpacity = new ToolStripMenuItem("Opacity");
            foreach (var pct in new[] { 100, 90, 80, 70, 60, 50 })
            {
                var v = pct;
                var mi = Item(miOpacity.DropDownItems, pct + "%", () => { st.Opacity = v; ApplyState(); Redraw(); st.Save(Paths.StateFile); });
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
                miShow.Text = Visible || autoHidden ? "Hide" : "Show";
                FillLayoutMenu();
                miTop.Checked = docked || st.OnTop;
                miTop.Enabled = !docked;
                miResets.Checked = st.ShowResets;
                miStartup.Checked = StartupEnabled();
                foreach (ToolStripMenuItem mi in miSize.DropDownItems) mi.Checked = Math.Abs((double)mi.Tag - st.UiScale) < 0.01;
                foreach (ToolStripMenuItem mi in miOpacity.DropDownItems) mi.Checked = (int)mi.Tag == st.Opacity;
            };
        }

        void FillLayoutMenu()
        {
            var dd = miLayout.DropDownItems;
            dd.Clear();
            Item(dd, "Wide", () => SetLayout("wide", null)).Checked = st.Layout == "wide";
            Item(dd, "Narrow (vertical)", () => SetLayout("narrow", null)).Checked = st.Layout == "narrow";
            dd.Add(new ToolStripSeparator());
            var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
            foreach (var s in screens)
            {
                var screen = s;
                string side = Taskbar.Side(s);
                string label = screens.Count == 1 ? "In the taskbar"
                    : side == "main" ? "In the taskbar on the main display"
                    : "In the taskbar on the " + side + " display";
                if (screens.Count(o => Taskbar.Side(o) == side) > 1) label += " (" + Taskbar.Number(s) + ")";
                bool hasBar = !Taskbar.Area(s).IsEmpty;
                if (!hasBar) label += " (no taskbar)";
                var mi = Item(dd, label, () => SetLayout("taskbar", screen));
                mi.Enabled = hasBar;
                mi.Checked = st.Layout == "taskbar" && (st.DockScreen == s.DeviceName || (docked && dockScreen != null && dockScreen.DeviceName == s.DeviceName));
            }
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
            SystemEvents.DisplaySettingsChanged -= OnDisplayChange;
            SystemEvents.UserPreferenceChanged -= OnPreferenceChange;
            if (winHook != IntPtr.Zero) { UnhookWinEvent(winHook); winHook = IntPtr.Zero; }
            tick.Stop();
            keeper.Stop();
            watcher.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ --render (testing / screenshots)

    // UsageTracker.exe --render out.png [--demo] [--scale 2] [--layout wide|narrow|strip]
    //                  [--taskbar light|dark] [--height 48] [--no-resets]
    static class RenderCli
    {
        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public static int ToFile(string outPath, string[] args)
        {
            float scale = 1f;
            if (Arg(args, "--scale") != null) float.TryParse(Arg(args, "--scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
            bool showResets = Array.IndexOf(args, "--no-resets") < 0;
            string layout = Arg(args, "--layout") ?? "wide";
            var mode = layout == "narrow" ? ViewMode.Narrow : layout == "strip" ? ViewMode.Strip : ViewMode.Wide;
            bool light = Arg(args, "--taskbar") == "light";
            int height = 0;
            if (Arg(args, "--height") != null) int.TryParse(Arg(args, "--height"), out height);
            if (mode == ViewMode.Strip && height == 0) height = (int)Math.Round(48 * scale);

            List<ProviderState> items;
            if (Array.IndexOf(args, "--demo") >= 0) items = Demo();
            else
            {
                items = AppConfig.Load(Paths.ConfigFile).Providers.Select(p => new ProviderState { P = p }).ToList();
                var threads = items.Select(it =>
                {
                    var t = new Thread(() =>
                    {
                        var res = Fetcher.Run(it.P);
                        it.Last = res.Snap;
                        it.Error = res.Error;
                    });
                    t.Start();
                    return t;
                }).ToList();
                threads.ForEach(t => t.Join());
            }

            var pal = mode == ViewMode.Strip ? (light ? Palette.LightTaskbar : Palette.DarkTaskbar) : Palette.Dark;
            using (var r = new Renderer(scale, mode, showResets, pal, height, items))
            {
                var size = r.Size;
                // A strip is drawn over a flat stand-in for the taskbar colour.
                var bg = mode != ViewMode.Strip ? Theme.Bg : light ? Color.FromArgb(238, 240, 243) : Color.FromArgb(32, 32, 32);
                using (var bmp = new Bitmap(size.Width, size.Height, mode == ViewMode.Strip ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(bg);
                        r.Draw(g, DateTime.UtcNow);
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
            var b = mk("Codex", Color.FromArgb(16, 163, 127), "Team");
            b.Last.Limits.Add(Limit.Make(300, 76, now.AddMinutes(41), null));
            b.Last.Limits.Add(Limit.Make(10080, 93, now.AddHours(30), null));
            var c = mk("Gemini", Color.FromArgb(120, 140, 240), "Pro");
            c.Last.Limits.Add(Limit.Make(300, 12, now.AddMinutes(250), null));
            c.Last.Limits.Add(Limit.Make(10080, 40, now.AddHours(80), null));
            c.Error = "Offline: can't reach example.com";
            c.Last.FetchedAt = now.AddMinutes(-40);   // old enough to show dimmed
            var d = mk("Cursor", Color.FromArgb(154, 160, 166), null);
            d.Error = "Not signed in to Codex";
            return new List<ProviderState> { a, b, c, d };
        }
    }
}
