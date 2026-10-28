// OBS HUD: an always-on-top status pill and toasts for OBS recording and the replay buffer,
// with a distinct sound per event. It listens to obs-websocket events (built into OBS 28+),
// so it never polls OBS and sits at 0% CPU between events.
//
// Build:    build.cmd   (uses the C# compiler that ships with Windows, nothing to install)
// Settings: obs-hud.ini next to the exe (also reachable from the tray icon)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

// Releases are tagged v + this version (v1.1), which the updater compares against.
[assembly: AssemblyVersion("1.1")]
[assembly: AssemblyFileVersion("1.1")]

namespace ObsHud
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--preview") { Preview.Render(args[1]); return; }
            if (args.Length == 2 && args[0] == "--docs") { Preview.Docs(args[1]); return; }

            bool first;
            using (var mutex = new Mutex(true, @"Local\ObsHud.SingleInstance", out first))
            {
                // After an update the old instance starts this one and exits, so wait for it to let go
                if (!first && !(args.Length > 0 && args[0] == "--restart" && Acquire(mutex))) return;
                Application.ThreadException += (s, e) => Log.Write("error: " + e.Exception);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Application.Run(new HudApp(args.Length > 0 && args[0] == "--demo"));
            }
        }

        static bool Acquire(Mutex mutex)
        {
            try { return mutex.WaitOne(10000); }
            catch (AbandonedMutexException) { return true; }   // the old instance exited without releasing it, which still hands it over
        }
    }

    // ---------------------------------------------------------------- settings

    class Config
    {
        public string Position = "TopRight";
        public bool Pill = true;
        public double ToastSeconds = 2.5;
        public string PeekHotkey = "Alt+F16";
        public double Scale = 1.0;
        public bool HideFromCapture = true;
        public bool Rtss = false;
        public bool AutoUpdate = true;
        public bool Sounds = true;
        public int Volume = 70;
        public string Device = "";
        public string Host = "127.0.0.1";
        public int Port = 0;
        public string Password = "";

        public static readonly string[] Positions = { "TopLeft", "TopCenter", "TopRight", "BottomLeft", "BottomCenter", "BottomRight" };
        public static string Dir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        public static string FilePath { get { return Path.Combine(Dir, "obs-hud.ini"); } }

        public static Config Load()
        {
            var c = new Config();
            if (!File.Exists(FilePath)) { c.Save(); return c; }
            string[] lines;
            try { lines = File.ReadAllLines(FilePath); } catch (IOException) { return c; }
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "position": foreach (var p in Positions) if (string.Equals(p, v, StringComparison.OrdinalIgnoreCase)) c.Position = p; break;
                    case "pill": c.Pill = Bool(v, c.Pill); break;
                    case "toastseconds": c.ToastSeconds = Math.Max(0.5, Num(v, c.ToastSeconds)); break;
                    case "peekhotkey": c.PeekHotkey = v; break;
                    case "scale": c.Scale = Math.Max(0.5, Math.Min(3, Num(v, c.Scale))); break;
                    case "hidefromcapture": c.HideFromCapture = Bool(v, c.HideFromCapture); break;
                    case "rtss": c.Rtss = Bool(v, c.Rtss); break;
                    case "autoupdate": c.AutoUpdate = Bool(v, c.AutoUpdate); break;
                    case "sounds": c.Sounds = Bool(v, c.Sounds); break;
                    case "volume": c.Volume = (int)Math.Max(0, Math.Min(100, Num(v, c.Volume))); break;
                    case "device": c.Device = v; break;
                    case "host": if (v.Length > 0) c.Host = v; break;
                    case "port": c.Port = (int)Num(v, 0); break;
                    case "password": c.Password = v; break;
                }
            }
            return c;
        }

        public void Save()
        {
            var ic = CultureInfo.InvariantCulture;
            File.WriteAllLines(FilePath, new[] {
                "; OBS HUD settings. Saving this file applies the changes right away.",
                "[HUD]",
                "; Where the status pill and toasts appear: TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight",
                "Position=" + Position,
                "; 1 = keep a small pill on screen while recording or the replay buffer is on. 0 = toasts only.",
                "Pill=" + (Pill ? 1 : 0),
                "; Seconds a toast stays up",
                "ToastSeconds=" + ToastSeconds.ToString(ic),
                "; Hotkey that flashes the current status, e.g. Ctrl+Alt+S. Leave empty to turn it off.",
                "PeekHotkey=" + PeekHotkey,
                "; Size multiplier on top of Windows display scaling",
                "Scale=" + Scale.ToString(ic),
                "; 1 = keep the HUD out of OBS display captures and screenshots",
                "HideFromCapture=" + (HideFromCapture ? 1 : 0),
                "; 1 = in exclusive fullscreen games, where no window can show, have RivaTuner Statistics Server",
                "; draw the HUD instead (needs RTSS running). It hides along with the rest of the RTSS overlay.",
                "; RTSS draws it into the game's frames, so display captures record it.",
                "Rtss=" + (Rtss ? 1 : 0),
                "; 1 = download new versions from GitHub on their own; the HUD asks you to restart to use one",
                "AutoUpdate=" + (AutoUpdate ? 1 : 0),
                "",
                "[Sound]",
                "Sounds=" + (Sounds ? 1 : 0),
                "Volume=" + Volume,
                "; Part of a playback device name to play the sounds on. Empty = Windows default.",
                "; Use a device OBS doesn't capture, or the beeps end up in your clips.",
                "Device=" + Device,
                "; To replace a sound, put clip.wav, rec_start.wav, rec_stop.wav, replay_on.wav, replay_off.wav,",
                "; pause.wav, resume.wav or warn.wav in a \"sounds\" folder next to obs-hud.exe.",
                "",
                "[OBS]",
                "; Leave Port and Password empty to read them from OBS's own WebSocket settings.",
                "Host=" + Host,
                "Port=" + (Port > 0 ? Port.ToString(ic) : ""),
                "Password=" + Password,
            }, new UTF8Encoding(false));
        }

        static bool Bool(string v, bool fallback)
        {
            switch (v.ToLowerInvariant()) { case "1": case "true": case "yes": case "on": return true; case "0": case "false": case "no": case "off": return false; }
            return fallback;
        }

        static double Num(string v, double fallback)
        {
            double d;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        static string FilePath { get { return Path.Combine(Config.Dir, "obs-hud.log"); } }

        public static void Init()
        {
            try { var fi = new FileInfo(FilePath); if (fi.Exists && fi.Length > 256 * 1024) fi.Delete(); } catch (IOException) { }
        }

        public static void Write(string message)
        {
            lock (gate)
            {
                try { File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + message + Environment.NewLine); }
                catch (IOException) { }
            }
        }
    }

    // Small helpers for the Dictionary<string, object> trees JavaScriptSerializer produces.
    static class J
    {
        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object o;
            return d != null && d.TryGetValue(key, out o) ? o as Dictionary<string, object> : null;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object o;
            return d != null && d.TryGetValue(key, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : null;
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object o;
            return d != null && d.TryGetValue(key, out o) && o is bool && (bool)o;
        }

        public static long Num(Dictionary<string, object> d, string key, long fallback)
        {
            object o;
            if (d == null || !d.TryGetValue(key, out o) || o == null) return fallback;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch (FormatException) { return fallback; } catch (InvalidCastException) { return fallback; }
        }
    }

    // ---------------------------------------------------------------- obs-websocket v5 client

    class ObsError : Exception
    {
        public ObsError(int code, string comment) : base(Describe(code, comment)) { }

        static string Describe(int code, string comment)
        {
            switch (code)
            {
                case 500: return "It's already running.";
                case 501: return "It isn't running.";
                case 504: return "It's turned off in OBS (Settings > Output > Replay Buffer).";
            }
            return string.IsNullOrEmpty(comment) ? "OBS returned error " + code : comment;
        }
    }

    class ObsClient
    {
        public event Action Connected;
        public event Action<string, bool, bool> Disconnected;          // reason, was connected, is a setup problem
        public event Action<string, Dictionary<string, object>> EventReceived;

        // Scenes (CurrentProgramSceneChanged, SceneNameChanged) and Outputs (RecordStateChanged, ReplayBufferSaved, ...)
        const int Subscriptions = (1 << 2) | (1 << 6);

        readonly SynchronizationContext ui;
        readonly Func<Config> config;
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, TaskCompletionSource<Dictionary<string, object>>> pending = new Dictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
        volatile ClientWebSocket ws;
        volatile bool identified;
        int nextId;

        public ObsClient(SynchronizationContext ui, Func<Config> config) { this.ui = ui; this.config = config; }

        public void Start() { Task.Run(new Func<Task>(Loop)); }

        async Task Loop()
        {
            string lastReason = null, lastError = null;
            while (true)
            {
                string problem = null;
                try { problem = await Session(); }
                catch (Exception ex)
                {
                    if (ex.Message != lastError) Log.Write("obs: " + ex.Message);
                    lastError = ex.Message;
                }
                bool was = identified;
                identified = false;
                ws = null;
                FailPending();
                string reason = problem ?? (was ? "OBS closed, waiting for it" : "Waiting for OBS to start");
                if (was || reason != lastReason)
                {
                    bool isProblem = problem != null;
                    if (isProblem) Log.Write("obs: " + problem);
                    ui.Post(delegate { if (Disconnected != null) Disconnected(reason, was, isProblem); }, null);
                }
                lastReason = reason;
                await Task.Delay(was ? 1000 : 3000);
            }
        }

        // Runs one connection until it closes. Returns a setup problem to show the user, or null.
        async Task<string> Session()
        {
            var cfg = config();
            int port = cfg.Port;
            string password = cfg.Password ?? "";
            var obsCfg = ReadObsWebSocketConfig();
            if (obsCfg != null)
            {
                if (obsCfg.ContainsKey("server_enabled") && !J.Bool(obsCfg, "server_enabled"))
                    return "The WebSocket server is off in OBS (Tools > WebSocket Server Settings)";
                if (port <= 0) port = (int)J.Num(obsCfg, "server_port", 4455);
                if (password.Length == 0 && J.Bool(obsCfg, "auth_required")) password = J.Str(obsCfg, "server_password") ?? "";
            }
            if (port <= 0) port = 4455;

            using (var sock = new ClientWebSocket())
            {
                sock.Options.AddSubProtocol("obswebsocket.json");
                using (var timeout = new CancellationTokenSource(4000))
                    await sock.ConnectAsync(new Uri("ws://" + cfg.Host + ":" + port), timeout.Token);
                ws = sock;
                var buffer = new byte[16 * 1024];

                var hello = await Receive(sock, buffer);
                if (hello == null) return null;
                var identify = new Dictionary<string, object> { { "rpcVersion", 1 }, { "eventSubscriptions", Subscriptions } };
                var auth = J.Obj(J.Obj(hello, "d"), "authentication");
                if (auth != null)
                {
                    if (password.Length == 0) return "OBS wants a WebSocket password: set Password= in obs-hud.ini";
                    identify["authentication"] = Authenticate(password, J.Str(auth, "salt"), J.Str(auth, "challenge"));
                }
                await Send(new Dictionary<string, object> { { "op", 1 }, { "d", identify } });

                while (true)
                {
                    var msg = await Receive(sock, buffer);
                    if (msg == null)
                        return sock.CloseStatus.HasValue && (int)sock.CloseStatus.Value == 4009
                            ? "Wrong WebSocket password (check OBS Tools > WebSocket Server Settings)" : null;
                    var d = J.Obj(msg, "d");
                    switch (J.Num(msg, "op", -1))
                    {
                        case 2:
                            identified = true;
                            ui.Post(delegate { if (Connected != null) Connected(); }, null);
                            break;
                        case 5:
                        {
                            string type = J.Str(d, "eventType");
                            var data = J.Obj(d, "eventData") ?? new Dictionary<string, object>();
                            ui.Post(delegate { if (EventReceived != null) EventReceived(type, data); }, null);
                            break;
                        }
                        case 7:
                            Complete(d);
                            break;
                    }
                }
            }
        }

        public async Task<Dictionary<string, object>> Request(string type, Dictionary<string, object> data = null)
        {
            string id = Interlocked.Increment(ref nextId).ToString(CultureInfo.InvariantCulture);
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            lock (pending) pending[id] = tcs;
            var d = new Dictionary<string, object> { { "requestType", type }, { "requestId", id } };
            if (data != null) d["requestData"] = data;
            try { await Send(new Dictionary<string, object> { { "op", 6 }, { "d", d } }); }
            catch { lock (pending) pending.Remove(id); throw; }
            var timeout = Task.Delay(5000);
            if (await Task.WhenAny(tcs.Task, timeout) == timeout)
            {
                lock (pending) pending.Remove(id);
                throw new TimeoutException("OBS didn't answer " + type);
            }
            return await tcs.Task;
        }

        async Task Send(object message)
        {
            var sock = ws;
            if (sock == null || sock.State != WebSocketState.Open) throw new InvalidOperationException("Not connected to OBS.");
            var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(message));
            await sendLock.WaitAsync();
            try { await sock.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { sendLock.Release(); }
        }

        static async Task<Dictionary<string, object>> Receive(ClientWebSocket sock, byte[] buffer)
        {
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    var r = await sock.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    ms.Write(buffer, 0, r.Count);
                    if (r.EndOfMessage) break;
                }
                return new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(ms.ToArray())) as Dictionary<string, object>;
            }
        }

        void Complete(Dictionary<string, object> d)
        {
            string id = J.Str(d, "requestId");
            TaskCompletionSource<Dictionary<string, object>> tcs;
            lock (pending)
            {
                if (id == null || !pending.TryGetValue(id, out tcs)) return;
                pending.Remove(id);
            }
            var status = J.Obj(d, "requestStatus");
            if (J.Bool(status, "result")) tcs.TrySetResult(J.Obj(d, "responseData") ?? new Dictionary<string, object>());
            else tcs.TrySetException(new ObsError((int)J.Num(status, "code", 0), J.Str(status, "comment")));
        }

        void FailPending()
        {
            lock (pending)
            {
                foreach (var tcs in pending.Values) tcs.TrySetException(new InvalidOperationException("Lost connection to OBS."));
                pending.Clear();
            }
        }

        static string Authenticate(string password, string salt, string challenge)
        {
            using (var sha = SHA256.Create())
            {
                string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }

        static Dictionary<string, object> ReadObsWebSocketConfig()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\plugin_config\obs-websocket\config.json");
                return File.Exists(path) ? new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object> : null;
            }
            catch (Exception) { return null; }
        }
    }

    // ---------------------------------------------------------------- sound

    // Plays through waveOut so a specific device can be picked (one OBS doesn't capture).
    static class WavePlayer
    {
        public class Clip { public WaveFormat Format; public byte[] Data; public int Ms; }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WaveFormat { public short FormatTag, Channels; public int SamplesPerSec, AvgBytesPerSec; public short BlockAlign, BitsPerSample, Size; }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr { public IntPtr Data; public int BufferLength, BytesRecorded; public IntPtr User; public int Flags, Loops; public IntPtr Next, Reserved; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WaveOutCaps { public short Mid, Pid; public int DriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public int Formats; public short Channels, Reserved; public int Support; }

        [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)] static extern int waveOutGetDevCaps(IntPtr id, ref WaveOutCaps caps, int size);
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, IntPtr id, ref WaveFormat fmt, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);

        static readonly IntPtr WaveMapper = IntPtr.Size == 8 ? new IntPtr(0xFFFFFFFFL) : new IntPtr(-1);
        static readonly int HdrSize = Marshal.SizeOf(typeof(WaveHdr));
        static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(WaveHdr), "Flags");
        const int WHDR_DONE = 1;

        public static void Play(Clip clip, string deviceName)
        {
            var fmt = clip.Format;
            IntPtr hwo, device = FindDevice(deviceName);
            int err = waveOutOpen(out hwo, device, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (err != 0 && device != WaveMapper)
            {
                LogOnce("sound: couldn't open \"" + deviceName + "\" (error " + err + "), using the default device");
                err = waveOutOpen(out hwo, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            }
            if (err != 0) { LogOnce("sound: couldn't open the default device (error " + err + ")"); return; }

            IntPtr data = Marshal.AllocHGlobal(clip.Data.Length), hdr = Marshal.AllocHGlobal(HdrSize);
            try
            {
                Marshal.Copy(clip.Data, 0, data, clip.Data.Length);
                Marshal.StructureToPtr(new WaveHdr { Data = data, BufferLength = clip.Data.Length }, hdr, false);
                if (waveOutPrepareHeader(hwo, hdr, HdrSize) != 0) return;
                waveOutWrite(hwo, hdr, HdrSize);
                int deadline = Environment.TickCount + clip.Ms + 1500;
                while ((Marshal.ReadInt32(hdr, FlagsOffset) & WHDR_DONE) == 0 && Environment.TickCount - deadline < 0) Thread.Sleep(30);
                waveOutReset(hwo);
                waveOutUnprepareHeader(hwo, hdr, HdrSize);
            }
            finally
            {
                waveOutClose(hwo);
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(hdr);
            }
        }

        static string lastWarning;

        static void LogOnce(string message)
        {
            if (message == lastWarning) return;
            lastWarning = message;
            Log.Write(message);
        }

        static IntPtr FindDevice(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return WaveMapper;
            int n = waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new WaveOutCaps();
                if (waveOutGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(caps)) == 0 && caps.Name != null &&
                    caps.Name.IndexOf(name.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return new IntPtr(i);
            }
            return WaveMapper;
        }

        // notes = pairs of (frequency Hz, length ms); 0 Hz is a rest. Each note rings like a small bell.
        public static Clip Synth(double[] notes, int volume)
        {
            const int rate = 44100;
            const double tail = 0.22;
            double total = 0;
            for (int i = 1; i < notes.Length; i += 2) total += notes[i] / 1000.0;
            int n = (int)((total + tail) * rate);
            var mix = new double[n];
            double t0 = 0;
            for (int i = 0; i + 1 < notes.Length; i += 2)
            {
                double f = notes[i], len = notes[i + 1] / 1000.0;
                if (f > 0)
                {
                    int start = (int)(t0 * rate), count = Math.Min(n - start, (int)((len + tail) * rate));
                    for (int k = 0; k < count; k++)
                    {
                        double t = (double)k / rate, w = 2 * Math.PI * f * t;
                        double env = Math.Min(1.0, t / 0.004) * Math.Exp(-t / 0.09);
                        mix[start + k] += env * (Math.Sin(w) + 0.25 * Math.Sin(2 * w) + 0.06 * Math.Sin(3 * w));
                    }
                }
                t0 += len;
            }
            double gain = 0.32 * volume / 100.0;
            var data = new byte[n * 2];
            for (int k = 0; k < n; k++)
            {
                double fade = Math.Min(1.0, (n - k) / (0.01 * rate));
                short v = (short)(Math.Max(-1.0, Math.Min(1.0, mix[k] * gain * fade)) * 32767);
                data[2 * k] = (byte)v;
                data[2 * k + 1] = (byte)(v >> 8);
            }
            return new Clip { Format = Pcm(rate, 1, 16), Data = data, Ms = (int)((total + tail) * 1000) };
        }

        // Reads a PCM (or float) WAV file. 16-bit audio is scaled by the volume setting.
        public static Clip LoadWav(string path, int volume)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return null;
            WaveFormat fmt = new WaveFormat();
            bool haveFmt = false;
            byte[] data = null;
            int pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4), body = pos + 8;
                if (len < 0 || body + len > b.Length) len = b.Length - body;
                if (id == "fmt " && len >= 16)
                {
                    fmt.FormatTag = BitConverter.ToInt16(b, body);
                    fmt.Channels = BitConverter.ToInt16(b, body + 2);
                    fmt.SamplesPerSec = BitConverter.ToInt32(b, body + 4);
                    fmt.AvgBytesPerSec = BitConverter.ToInt32(b, body + 8);
                    fmt.BlockAlign = BitConverter.ToInt16(b, body + 12);
                    fmt.BitsPerSample = BitConverter.ToInt16(b, body + 14);
                    if (fmt.FormatTag == unchecked((short)0xFFFE)) fmt.FormatTag = (short)(fmt.BitsPerSample == 32 ? 3 : 1);   // WAVE_FORMAT_EXTENSIBLE
                    haveFmt = true;
                }
                else if (id == "data")
                {
                    data = new byte[len];
                    Buffer.BlockCopy(b, body, data, 0, len);
                }
                pos = body + len + (len & 1);
            }
            if (!haveFmt || data == null || (fmt.FormatTag != 1 && fmt.FormatTag != 3)) return null;
            if (fmt.FormatTag == 1 && fmt.BitsPerSample == 16 && volume < 100)
                for (int i = 0; i + 1 < data.Length; i += 2)
                {
                    short v = (short)(BitConverter.ToInt16(data, i) * volume / 100);
                    data[i] = (byte)v;
                    data[i + 1] = (byte)(v >> 8);
                }
            return new Clip { Format = fmt, Data = data, Ms = (int)(1000L * data.Length / Math.Max(1, fmt.AvgBytesPerSec)) };
        }

        static WaveFormat Pcm(int rate, int channels, int bits)
        {
            short align = (short)(channels * bits / 8);
            return new WaveFormat { FormatTag = 1, Channels = (short)channels, SamplesPerSec = rate, AvgBytesPerSec = rate * align, BlockAlign = align, BitsPerSample = (short)bits };
        }
    }

    class Sounds
    {
        static readonly Dictionary<string, double[]> Melodies = new Dictionary<string, double[]>
        {
            { "clip",       new double[] { 1046.5, 70, 0, 20, 1568, 160 } },          // bright double ding
            { "rec_start",  new double[] { 523.3, 85, 659.3, 85, 784, 170 } },        // rising C-E-G
            { "rec_stop",   new double[] { 784, 85, 659.3, 85, 523.3, 230 } },        // falling G-E-C
            { "replay_on",  new double[] { 587.3, 80, 880, 160 } },                   // rising fifth
            { "replay_off", new double[] { 880, 80, 587.3, 180 } },                   // falling fifth
            { "pause",      new double[] { 659.3, 150 } },
            { "resume",     new double[] { 659.3, 55, 0, 35, 659.3, 70 } },
            { "warn",       new double[] { 233, 140, 0, 40, 196, 260 } },             // low, unmistakably "not good"
        };

        readonly Dictionary<string, WavePlayer.Clip> clips = new Dictionary<string, WavePlayer.Clip>();
        Config cfg;

        public void Load(Config cfg)
        {
            this.cfg = cfg;
            clips.Clear();
            string dir = Path.Combine(Config.Dir, "sounds");
            foreach (var kv in Melodies)
            {
                WavePlayer.Clip clip = null;
                string custom = Path.Combine(dir, kv.Key + ".wav");
                if (File.Exists(custom))
                {
                    try { clip = WavePlayer.LoadWav(custom, cfg.Volume); } catch (Exception ex) { Log.Write("sound: " + custom + ": " + ex.Message); }
                    if (clip == null) Log.Write("sound: " + custom + " isn't a PCM WAV, using the built-in sound");
                }
                clips[kv.Key] = clip ?? WavePlayer.Synth(kv.Value, cfg.Volume);
            }
        }

        public void Play(string name)
        {
            WavePlayer.Clip clip;
            if (name == null || !cfg.Sounds || cfg.Volume <= 0 || !clips.TryGetValue(name, out clip)) return;
            string device = cfg.Device;
            ThreadPool.QueueUserWorkItem(delegate { WavePlayer.Play(clip, device); });
        }
    }

    // ---------------------------------------------------------------- drawing

    enum Glyph { Rec, Stop, Pause, Replay, Check, Warn, Info }

    static class Gfx
    {
        public static readonly Color Red = Color.FromArgb(240, 74, 74);
        public static readonly Color Teal = Color.FromArgb(32, 190, 172);
        public static readonly Color Green = Color.FromArgb(52, 196, 116);
        public static readonly Color Amber = Color.FromArgb(240, 164, 34);
        public static readonly Color Gray = Color.FromArgb(120, 127, 140);

        // tag (the current scene) is drawn as a small chip at the right end of the title line.
        public static Bitmap Toast(Glyph glyph, Color accent, string title, string detail, string tag, float s)
        {
            if (string.IsNullOrEmpty(detail)) detail = null;
            if (string.IsNullOrEmpty(tag)) tag = null;
            using (var fTitle = new Font("Segoe UI Semibold", 15f * s, GraphicsUnit.Pixel))
            using (var fDetail = new Font("Segoe UI", 12.5f * s, GraphicsUnit.Pixel))
            using (var fTag = new Font("Segoe UI Semibold", 11.5f * s, GraphicsUnit.Pixel))
            using (var fmt = new StringFormat(StringFormat.GenericTypographic))
            {
                fmt.FormatFlags |= StringFormatFlags.NoWrap;
                fmt.Trimming = StringTrimming.EllipsisCharacter;
                float pad = 14 * s, icon = 30 * s, gap = 12 * s, lineGap = 2 * s, chipPad = 7 * s, chipGap = 12 * s;
                SizeF t = Measure(title, fTitle, fmt), d = detail == null ? SizeF.Empty : Measure(detail, fDetail, fmt);
                SizeF g0 = tag == null ? SizeF.Empty : Measure(tag, fTag, fmt);
                float tagTextW = Math.Min(170 * s, g0.Width), chipW = tag == null ? 0 : tagTextW + chipPad * 2 + 1, chipH = g0.Height + 4 * s;
                float row1W = t.Width + (tag == null ? 0 : chipGap + chipW);
                float textW = Math.Min(420 * s, Math.Max(row1W, d.Width)) + 2 * s;
                float textH = t.Height + (detail == null ? 0 : lineGap + d.Height);
                int W = (int)Math.Ceiling(pad + icon + gap + textW + pad);
                int H = (int)Math.Ceiling(Math.Max(icon, textH) + pad * 2);

                var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
                using (var g = Prepare(bmp))
                {
                    using (var path = Rounded(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 12 * s))
                    using (var fill = new SolidBrush(Color.FromArgb(238, 22, 23, 28)))
                    using (var edge = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
                    {
                        g.FillPath(fill, path);
                        g.DrawPath(edge, path);
                    }
                    Badge(g, new RectangleF(pad, (H - icon) / 2f, icon, icon), glyph, accent);
                    float x = pad + icon + gap, y = (H - textH) / 2f;
                    using (var white = new SolidBrush(Color.FromArgb(246, 247, 249)))
                    using (var dim = new SolidBrush(Color.FromArgb(166, 172, 183)))
                    {
                        float titleW = textW - (tag == null ? 0 : chipGap + chipW);
                        g.DrawString(title, fTitle, white, new RectangleF(x, y, titleW, t.Height + 2 * s), fmt);
                        if (detail != null) g.DrawString(detail, fDetail, dim, new RectangleF(x, y + t.Height + lineGap, textW, d.Height + 2 * s), fmt);
                        if (tag != null)
                        {
                            var chip = new RectangleF(x + textW - chipW, y + (t.Height - chipH) / 2 + s, chipW, chipH);
                            using (var path = Rounded(chip, 5 * s))
                            using (var fill = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
                                g.FillPath(fill, path);
                            g.DrawString(tag, fTag, dim, new RectangleF(chip.X + chipPad, chip.Y + (chipH - g0.Height) / 2, tagTextW + 1, g0.Height + s), fmt);
                        }
                    }
                }
                return bmp;
            }
        }

        public static Bitmap Pill(IList<KeyValuePair<Color, string>> parts, float s)
        {
            using (var font = new Font("Segoe UI Semibold", 12.5f * s, GraphicsUnit.Pixel))
            using (var fmt = new StringFormat(StringFormat.GenericTypographic))
            {
                fmt.FormatFlags |= StringFormatFlags.NoWrap;
                float h = 26 * s, padX = 11 * s, dot = 8 * s, dotGap = 6 * s, partGap = 14 * s, textH = 0, W = padX * 2;
                var widths = new float[parts.Count];
                for (int i = 0; i < parts.Count; i++)
                {
                    // measure with every digit as 0 so a running clock doesn't make the pill wobble
                    var size = Measure(System.Text.RegularExpressions.Regex.Replace(parts[i].Value, "[0-9]", "0"), font, fmt);
                    widths[i] = size.Width;
                    textH = Math.Max(textH, size.Height);
                    W += dot + dotGap + size.Width + (i > 0 ? partGap : 0);
                }
                var bmp = new Bitmap((int)Math.Ceiling(W), (int)Math.Ceiling(h), PixelFormat.Format32bppArgb);
                using (var g = Prepare(bmp))
                {
                    using (var path = Rounded(new RectangleF(0.5f, 0.5f, bmp.Width - 1, bmp.Height - 1), (bmp.Height - 1) / 2f))
                    using (var fill = new SolidBrush(Color.FromArgb(200, 18, 19, 23)))
                    using (var edge = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
                    {
                        g.FillPath(fill, path);
                        g.DrawPath(edge, path);
                    }
                    float x = padX;
                    using (var white = new SolidBrush(Color.FromArgb(240, 242, 245)))
                        for (int i = 0; i < parts.Count; i++)
                        {
                            using (var b = new SolidBrush(parts[i].Key)) g.FillEllipse(b, x, (h - dot) / 2, dot, dot);
                            x += dot + dotGap;
                            g.DrawString(parts[i].Value, font, white, x, (h - textH) / 2, fmt);
                            x += widths[i] + partGap;
                        }
                }
                return bmp;
            }
        }

        public static Bitmap TrayBitmap(bool connected, bool rec, bool paused, bool replay, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            float u = size / 16f;
            using (var g = Prepare(bmp))
            {
                if (!connected)
                    using (var p = new Pen(Gray, 2 * u)) g.DrawEllipse(p, 2.5f * u, 2.5f * u, 11 * u, 11 * u);
                else if (rec)
                {
                    using (var b = new SolidBrush(paused ? Amber : Red))
                        if (replay) g.FillEllipse(b, 4 * u, 4 * u, 8 * u, 8 * u);
                        else g.FillEllipse(b, 1.5f * u, 1.5f * u, 13 * u, 13 * u);
                    if (replay) using (var p = new Pen(Teal, 2.2f * u)) g.DrawEllipse(p, 1.4f * u, 1.4f * u, 13.2f * u, 13.2f * u);
                }
                else if (replay)
                    using (var b = new SolidBrush(Teal)) g.FillEllipse(b, 1.5f * u, 1.5f * u, 13 * u, 13 * u);
                else
                    using (var b = new SolidBrush(Gray)) g.FillEllipse(b, 4 * u, 4 * u, 8 * u, 8 * u);
            }
            return bmp;
        }

        static void Badge(Graphics g, RectangleF c, Glyph glyph, Color accent)
        {
            using (var b = new SolidBrush(accent)) g.FillEllipse(b, c);
            float cx = c.X + c.Width / 2, cy = c.Y + c.Height / 2, R = c.Width / 2;
            using (var w = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.White, R * 0.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (glyph)
                {
                    case Glyph.Rec:
                        g.FillEllipse(w, cx - R * 0.42f, cy - R * 0.42f, R * 0.84f, R * 0.84f);
                        break;
                    case Glyph.Stop:
                        using (var p = Rounded(new RectangleF(cx - R * 0.36f, cy - R * 0.36f, R * 0.72f, R * 0.72f), R * 0.12f)) g.FillPath(w, p);
                        break;
                    case Glyph.Pause:
                        g.FillRectangle(w, cx - R * 0.36f, cy - R * 0.4f, R * 0.25f, R * 0.8f);
                        g.FillRectangle(w, cx + R * 0.11f, cy - R * 0.4f, R * 0.25f, R * 0.8f);
                        break;
                    case Glyph.Check:
                        g.DrawLines(pen, new[] { new PointF(cx - R * 0.42f, cy + R * 0.02f), new PointF(cx - R * 0.12f, cy + R * 0.32f), new PointF(cx + R * 0.44f, cy - R * 0.32f) });
                        break;
                    case Glyph.Replay:
                        pen.Width = R * 0.17f;
                        pen.EndCap = LineCap.Flat;
                        using (var cap = new AdjustableArrowCap(2.6f, 2.2f, true)) pen.CustomEndCap = cap;
                        g.DrawArc(pen, cx - R * 0.46f, cy - R * 0.46f, R * 0.92f, R * 0.92f, 250, 280);
                        break;
                    case Glyph.Warn:
                        g.DrawLine(pen, cx, cy - R * 0.44f, cx, cy + R * 0.06f);
                        g.FillEllipse(w, cx - R * 0.13f, cy + R * 0.24f, R * 0.26f, R * 0.26f);
                        break;
                    case Glyph.Info:
                        g.FillEllipse(w, cx - R * 0.13f, cy - R * 0.5f, R * 0.26f, R * 0.26f);
                        g.DrawLine(pen, cx, cy - R * 0.06f, cx, cy + R * 0.44f);
                        break;
                }
            }
        }

        static SizeF Measure(string text, Font font, StringFormat fmt)
        {
            using (var tmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(tmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                return g.MeasureString(text, font, PointF.Empty, fmt);
            }
        }

        static Graphics Prepare(Bitmap bmp)
        {
            var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;   // ClearType can't blend onto a transparent window
            return g;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ---------------------------------------------------------------- overlay windows

    static class Native
    {
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, WM_HOTKEY = 0x312, ULW_ALPHA = 2, GWL_EXSTYLE = -20;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        public const uint FILE_MAP_WRITE = 0x2, FILE_MAP_READ = 0x4;
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int CX, CY; public SIZE(int cx, int cy) { CX = cx; CY = cy; } }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32.dll")] public static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
        [DllImport("kernel32.dll")] public static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    }

    // A click-through, never-focused, topmost window that shows a per-pixel-alpha bitmap.
    // It is only redrawn when its content changes, so a visible overlay costs nothing per frame.
    class Overlay : Form
    {
        IntPtr memDc, hBitmap, oldBitmap;
        Size size;
        bool shown, layered;   // WS_EX_LAYERED is added after the capture setting, which Windows refuses on layered windows

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "OBS HUD";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST | (layered ? Native.WS_EX_LAYERED : 0);
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public bool IsShown { get { return shown; } }
        public Size ContentSize { get { return size; } }

        public void HideFromCapture(bool hide)
        {
            Conceal();
            SetLayered(false);
            if (!Native.SetWindowDisplayAffinity(Handle, hide ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE))
                Log.Write("overlay: SetWindowDisplayAffinity failed (" + Marshal.GetLastWin32Error() + ")");
            SetLayered(true);
        }

        void SetLayered(bool on)
        {
            layered = on;
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, on ? ex | Native.WS_EX_LAYERED : ex & ~Native.WS_EX_LAYERED);
        }

        public void SetContent(Bitmap bmp)
        {
            ReleaseContent();
            size = bmp.Size;
            memDc = Native.CreateCompatibleDC(IntPtr.Zero);
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = Native.SelectObject(memDc, hBitmap);
            bmp.Dispose();
        }

        public void Present(Point pos, byte alpha)
        {
            if (memDc == IntPtr.Zero) return;
            if (!layered) SetLayered(true);
            var dst = new Native.POINT(pos.X, pos.Y);
            var src = new Native.POINT(0, 0);
            var sz = new Native.SIZE(size.Width, size.Height);
            var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = alpha, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref sz, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            if (!shown)
            {
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                shown = true;
                RaiseToTop();
            }
        }

        // Games that make themselves topmost can end up above us; this puts the overlay back on top.
        public void RaiseToTop()
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void Conceal()
        {
            if (shown) { Native.ShowWindow(Handle, Native.SW_HIDE); shown = false; }
            ReleaseContent();
        }

        void ReleaseContent()
        {
            if (memDc == IntPtr.Zero) return;
            Native.SelectObject(memDc, oldBitmap);
            Native.DeleteObject(hBitmap);
            Native.DeleteDC(memDc);
            memDc = hBitmap = oldBitmap = IntPtr.Zero;
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseContent();
            base.Dispose(disposing);
        }
    }

    // Fades a toast in, holds it, fades it out. The timer only runs while a toast is on screen.
    class Toasts
    {
        const int FadeInMs = 110, FadeOutMs = 240;
        enum Phase { Hidden, In, Hold, Out }

        readonly Overlay win;
        readonly Timer timer = new Timer();
        Phase phase;
        int phaseStart, holdMs;
        Point pos;

        public event Action Hidden;

        public Toasts(Overlay win)
        {
            this.win = win;
            timer.Tick += delegate { Step(); };
        }

        public void Show(Bitmap bmp, Point pos, int holdMs)
        {
            bool visible = phase != Phase.Hidden;
            win.SetContent(bmp);
            this.pos = pos;
            this.holdMs = holdMs;
            Enter(visible ? Phase.Hold : Phase.In);   // already on screen: swap the content without fading again
            Step();
        }

        void Enter(Phase p)
        {
            phase = p;
            phaseStart = Environment.TickCount;
            timer.Stop();
            timer.Interval = p == Phase.Hold ? holdMs : 15;
            timer.Start();
        }

        void Step()
        {
            int t = Environment.TickCount - phaseStart;
            switch (phase)
            {
                case Phase.In:
                    if (t >= FadeInMs) { win.Present(pos, 255); Enter(Phase.Hold); }
                    else win.Present(pos, (byte)(255 * t / FadeInMs));
                    break;
                case Phase.Hold:
                    win.Present(pos, 255);
                    if (t >= holdMs) Enter(Phase.Out);
                    break;
                case Phase.Out:
                    if (t >= FadeOutMs) { timer.Stop(); phase = Phase.Hidden; win.Conceal(); if (Hidden != null) Hidden(); }
                    else win.Present(pos, (byte)(255 - 255 * t / FadeOutMs));
                    break;
            }
        }
    }

    class HotkeyWindow : NativeWindow, IDisposable
    {
        readonly Action onPress;
        bool registered;

        public HotkeyWindow(Action onPress)
        {
            this.onPress = onPress;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });   // message-only window
        }

        // Returns null on success, or why the hotkey couldn't be registered.
        public string Register(string spec)
        {
            if (registered) { Native.UnregisterHotKey(Handle, 1); registered = false; }
            if (string.IsNullOrWhiteSpace(spec)) return null;
            uint mods = 0x4000;   // MOD_NOREPEAT
            Keys key = Keys.None;
            foreach (var raw in spec.Split('+'))
            {
                string t = raw.Trim();
                switch (t.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= 2; break;
                    case "alt": mods |= 1; break;
                    case "shift": mods |= 4; break;
                    case "win": mods |= 8; break;
                    default:
                        if (t.Length == 1 && char.IsDigit(t[0])) t = "D" + t;
                        if (!Enum.TryParse(t, true, out key)) return "unknown key \"" + t + "\"";
                        break;
                }
            }
            if (key == Keys.None) return "no key in \"" + spec + "\"";
            registered = Native.RegisterHotKey(Handle, 1, mods, (uint)key);
            return registered ? null : spec + " is already used by another app";
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) onPress();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (registered) Native.UnregisterHotKey(Handle, 1);
            DestroyHandle();
        }
    }

    // ---------------------------------------------------------------- RTSS on-screen display

    // Feeds RivaTuner Statistics Server's on-screen display, which RTSS draws inside the game's own
    // frames, so it shows even in exclusive fullscreen. This app injects nothing: like HWiNFO and
    // AIDA64, it only fills one of the text slots in RTSS's shared memory, following
    // SDK\Include\RTSSSharedMemory.h and the SDK's RTSSSharedMemorySample.
    class RtssOsd
    {
        const string Owner = "OBS HUD";
        const int Signature = 0x52545353;   // 'RTSS'
        const int MinVersion = 0x0002000E;  // v2.14: text tags and the busy lock
        // Header fields, offsets inside an OSD slot, and inside an app entry
        const int AppEntrySize = 8, AppArrOffset = 12, AppArrSize = 16;
        const int OsdEntrySize = 20, OsdArrOffset = 24, OsdArrSize = 28, OsdFrame = 32, Busy = 36;
        const int OwnerAt = 256, TextAt = 512, TextSize = 4096;
        const int OsdPixelAt = 324, ResolutionAt = 9224;   // ResolutionAt needs v2.20
        bool claimed = true;   // so the first Clear also frees a slot left behind if the app crashed

        // How RTSS draws in the given process: its OSD zoom. False if RTSS isn't running or isn't drawing in it.
        public bool Target(int pid, out int zoom)
        {
            int z = 0;
            bool found = Use(false, mem =>
            {
                if (Marshal.ReadInt32(mem, 4) < 0x00020014) return false;
                int size = Marshal.ReadInt32(mem, AppEntrySize), offset = Marshal.ReadInt32(mem, AppArrOffset), count = Marshal.ReadInt32(mem, AppArrSize);
                for (int i = 0; i < count; i++)
                {
                    IntPtr app = mem + offset + i * size;
                    if (Marshal.ReadInt32(app) != pid) continue;
                    z = Math.Max(1, Marshal.ReadInt32(app, OsdPixelAt));
                    return Marshal.ReadInt32(app, ResolutionAt) > 0;   // RTSS has seen it render a frame
                }
                return false;
            });
            zoom = z;
            return found;
        }

        // Shows text in our slot, claiming a free one first. False if RTSS isn't running or every slot is taken.
        public bool Show(string text)
        {
            return Use(false, mem =>
            {
                IntPtr slot = Slot(mem, true);
                if (slot == IntPtr.Zero) return false;
                claimed = true;
                byte[] bytes = Encoding.Default.GetBytes(text);   // RTSS reads ANSI text
                int n = Math.Min(bytes.Length, TextSize - 1);
                if (Marshal.PtrToStringAnsi(slot + TextAt) == Encoding.Default.GetString(bytes, 0, n)) return true;
                if (!Lock(mem)) return false;
                try
                {
                    Marshal.Copy(bytes, 0, slot + TextAt, n);
                    Marshal.WriteByte(slot + TextAt, n, 0);
                }
                finally { Unlock(mem); }
                Redraw(mem);
                return true;
            });
        }

        // Frees our slot so RTSS stops drawing it.
        public void Clear()
        {
            if (!claimed) return;
            claimed = false;
            Use(true, mem =>
            {
                IntPtr slot = Slot(mem, false);
                if (slot == IntPtr.Zero) return true;
                Marshal.Copy(new byte[TextAt + TextSize], 0, slot, TextAt + TextSize);   // owner and text; we never touch the rest
                Redraw(mem);
                return true;
            });
        }

        static T Use<T>(T fallback, Func<IntPtr, T> action)
        {
            IntPtr map = Native.OpenFileMapping(Native.FILE_MAP_READ | Native.FILE_MAP_WRITE, false, "RTSSSharedMemoryV2");
            if (map == IntPtr.Zero) return fallback;
            IntPtr mem = Native.MapViewOfFile(map, Native.FILE_MAP_WRITE, 0, 0, UIntPtr.Zero);
            try
            {
                if (mem == IntPtr.Zero || Marshal.ReadInt32(mem) != Signature) return fallback;
                int version = Marshal.ReadInt32(mem, 4);
                return version >= MinVersion && version >> 16 == 2 ? action(mem) : fallback;
            }
            finally
            {
                if (mem != IntPtr.Zero) Native.UnmapViewOfFile(mem);
                Native.CloseHandle(map);
            }
        }

        // Our slot, or with claim the first free one. Slot 0 is reserved for Afterburner, as the SDK asks.
        static IntPtr Slot(IntPtr mem, bool claim)
        {
            int size = Marshal.ReadInt32(mem, OsdEntrySize), offset = Marshal.ReadInt32(mem, OsdArrOffset), count = Marshal.ReadInt32(mem, OsdArrSize);
            IntPtr free = IntPtr.Zero;
            for (int i = 1; i < count; i++)
            {
                IntPtr slot = mem + offset + i * size;
                string owner = Marshal.PtrToStringAnsi(slot + OwnerAt);
                if (owner == Owner) return slot;
                if (owner.Length == 0 && free == IntPtr.Zero) free = slot;
            }
            if (!claim || free == IntPtr.Zero) return IntPtr.Zero;
            byte[] name = Encoding.ASCII.GetBytes(Owner + "\0");
            Marshal.Copy(name, 0, free + OwnerAt, name.Length);
            return free;
        }

        // Bit 0 of the busy field is a lock shared with RTSS's renderer, which only holds it while copying text.
        static unsafe bool Lock(IntPtr mem)
        {
            int* busy = (int*)(mem + Busy).ToPointer();
            var spin = new SpinWait();
            while (true)
            {
                int old = Thread.VolatileRead(ref *busy);
                if ((old & 1) == 0 && Interlocked.CompareExchange(ref *busy, old | 1, old) == old) return true;
                if (spin.Count >= 100) return false;
                spin.SpinOnce();
            }
        }

        static unsafe void Unlock(IntPtr mem)
        {
            Interlocked.Exchange(ref *(int*)(mem + Busy).ToPointer(), 0);
        }

        static void Redraw(IntPtr mem)
        {
            Marshal.WriteInt32(mem, OsdFrame, Marshal.ReadInt32(mem, OsdFrame) + 1);
        }
    }

    // ---------------------------------------------------------------- updates

    // Finds the latest GitHub release and puts its obs-hud.exe in place of this one. Windows lets a
    // running exe be renamed but not overwritten, so this one moves aside to obs-hud.exe.old (deleted
    // on the next start) and the download takes its name. It runs from the next start.
    static class Updater
    {
        const string Repo = "Lancito01/obs-hud", Asset = "obs-hud.exe";

        public class Release { public Version Version; public string Tag, Url, Sha256; }

        public static Version Current { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

        // The latest release if it's newer than this build, else null.
        public static async Task<Release> Newer()
        {
            string json;
            using (var web = Client())
            {
                web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = await web.DownloadStringTaskAsync("https://api.github.com/repos/" + Repo + "/releases/latest");
            }
            var r = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            string tag = J.Str(r, "tag_name");
            Version v;
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out v) || Normalize(v) <= Normalize(Current)) return null;
            object assets;
            r.TryGetValue("assets", out assets);
            foreach (var o in assets as System.Collections.IEnumerable ?? new object[0])
            {
                var a = o as Dictionary<string, object>;
                if (J.Str(a, "name") != Asset) continue;
                string digest = J.Str(a, "digest");
                return new Release
                {
                    Version = v, Tag = tag, Url = J.Str(a, "browser_download_url"),
                    Sha256 = digest != null && digest.StartsWith("sha256:") ? digest.Substring(7) : null,
                };
            }
            return null;
        }

        // Downloads the release and swaps it in for exe. Throws if the download doesn't match GitHub's
        // checksum or the exe's folder can't be written to.
        public static async Task Install(Release r, string exe)
        {
            string fresh = exe + ".new", old = exe + ".old";
            try
            {
                using (var web = Client()) await web.DownloadFileTaskAsync(r.Url, fresh);
                byte[] bytes = File.ReadAllBytes(fresh);
                if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("the download isn't a program");
                if (r.Sha256 != null)
                    using (var sha = SHA256.Create())
                        if (!string.Equals(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""), r.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("the download doesn't match its checksum");
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Move(fresh, exe); }
                catch { File.Move(old, exe); throw; }
            }
            finally { TryDelete(fresh); }
        }

        // Deletes the exe the last update moved aside, now that it isn't running.
        public static void CleanUp() { TryDelete(Application.ExecutablePath + ".old"); }

        static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Treats 1.1 and 1.1.0.0 as the same version.
        static Version Normalize(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)); }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;   // GitHub requires TLS 1.2
            var web = new WebClient();
            web.Headers[HttpRequestHeader.UserAgent] = "obs-hud/" + Current;
            return web;
        }
    }

    // ---------------------------------------------------------------- the app

    class HudApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "OBS HUD";

        Config cfg;
        readonly float dpiScale;
        readonly ObsClient obs;
        readonly Overlay toastWin = new Overlay(), pillWin = new Overlay();
        readonly Toasts toasts;
        readonly RtssOsd rtss = new RtssOsd();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Sounds sounds = new Sounds();
        readonly HotkeyWindow hotkey;
        readonly Timer pillTimer = new Timer { Interval = 1000 };
        readonly Timer reloadTimer = new Timer { Interval = 400 };
        readonly Timer updateTimer = new Timer();
        Updater.Release updateReady;   // installed, runs after a restart
        bool checkingUpdates;
        readonly FileSystemWatcher watcher;
        int ignoreConfigUntil;

        // OBS state, as last reported by OBS
        bool connected, rec, paused, replay;
        readonly Stopwatch recClock = new Stopwatch();
        long recBaseMs;
        int replaySeconds, clipCount;
        string scene, lastSaved, status = "Waiting for OBS to start";

        string pillKey;
        Point pillPos, toastPos;
        const byte PillAlpha = 240;
        List<KeyValuePair<Color, string>> demoPill, pillShown;   // pillShown is null while the pill is hidden
        bool demoRunning;
        Func<Bitmap> rtssToast;   // draws the toast on screen again, for RTSS
        int toastSerial, rtssImageSerial;
        string rtssKey, rtssText, rtssState;

        public HudApp(bool demo)
        {
            Log.Init();
            cfg = Config.Load();
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f;

            toastWin.HideFromCapture(cfg.HideFromCapture);
            pillWin.HideFromCapture(cfg.HideFromCapture);
            toasts = new Toasts(toastWin);
            toasts.Hidden += delegate { rtssToast = null; UpdateRtss(); };
            sounds.Load(cfg);

            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Peek(); };
            tray.MouseMove += delegate { tray.Text = Trim63("OBS HUD: " + StatusLine()); };
            UpdateTray();
            tray.Visible = true;

            hotkey = new HotkeyWindow(Peek);
            string hotkeyError = hotkey.Register(cfg.PeekHotkey);

            pillTimer.Tick += delegate { RefreshPill(); };
            reloadTimer.Tick += delegate { reloadTimer.Stop(); Reload(); };
            updateTimer.Tick += delegate { updateTimer.Interval = 12 * 3600 * 1000; CheckForUpdates(false); };
            Updater.CleanUp();
            ScheduleUpdates();
            watcher = new FileSystemWatcher(Config.Dir, "obs-hud.ini") { SynchronizingObject = toastWin, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            FileSystemEventHandler onChange = delegate { if (Environment.TickCount - ignoreConfigUntil > 0) { reloadTimer.Stop(); reloadTimer.Start(); } };
            watcher.Changed += onChange;
            watcher.Created += onChange;
            watcher.Renamed += (s, e) => onChange(s, e);
            watcher.EnableRaisingEvents = true;

            obs = new ObsClient(SynchronizationContext.Current, () => cfg);
            obs.Connected += OnConnected;
            obs.Disconnected += OnDisconnected;
            obs.EventReceived += OnEvent;
            obs.Start();

            Log.Write("started (scale " + S.ToString("0.##", CultureInfo.InvariantCulture) + ", sound device \"" + cfg.Device + "\")");
            if (hotkeyError != null)
            {
                Log.Write("hotkey: " + hotkeyError);
                Toast(Glyph.Warn, Gfx.Amber, "Status hotkey not set", hotkeyError, null);
            }
            if (demo) RunDemo();
        }

        float S { get { return dpiScale * (float)cfg.Scale; } }
        long RecElapsedMs { get { return recBaseMs + recClock.ElapsedMilliseconds; } }

        // ------------------------------------------------ OBS events

        async void OnConnected()
        {
            connected = true;
            status = "Connected";
            Log.Write("connected to OBS");
            bool recActive = false, recPaused = false, rbActive = false;
            long duration = 0;
            try
            {
                var r = await obs.Request("GetRecordStatus");
                recActive = J.Bool(r, "outputActive");
                recPaused = J.Bool(r, "outputPaused");
                duration = J.Num(r, "outputDuration", 0);
            }
            catch (Exception ex) { Log.Write("GetRecordStatus: " + ex.Message); }
            try { rbActive = J.Bool(await obs.Request("GetReplayBufferStatus"), "outputActive"); }
            catch (Exception ex) { Log.Write("GetReplayBufferStatus: " + ex.Message); }   // fails when the replay buffer is off in OBS settings
            try
            {
                var r = await obs.Request("GetCurrentProgramScene");
                scene = J.Str(r, "currentProgramSceneName") ?? J.Str(r, "sceneName");
            }
            catch (Exception ex) { Log.Write("GetCurrentProgramScene: " + ex.Message); }
            await LoadReplayLength();
            if (!connected) return;

            rec = recActive;
            paused = recPaused;
            replay = rbActive;
            recBaseMs = duration;
            if (rec && !paused) recClock.Restart(); else recClock.Reset();
            Changed();
            Toast(Glyph.Info, Gfx.Gray, "Connected to OBS", Summary(), null);
        }

        void OnDisconnected(string reason, bool wasConnected, bool isProblem)
        {
            bool wasActive = rec || replay;
            connected = rec = paused = replay = false;
            recClock.Reset();
            recBaseMs = 0;
            scene = null;
            status = reason;
            Changed();
            if (wasConnected) Log.Write("disconnected: " + reason);
            if (wasConnected && wasActive)
                Toast(Glyph.Warn, Gfx.Amber, "Lost connection to OBS", "It was still recording or buffering", "warn", cfg.ToastSeconds + 2);
            else if (isProblem)
                Toast(Glyph.Warn, Gfx.Amber, "Can't connect to OBS", reason, "warn", cfg.ToastSeconds + 3);
        }

        void OnEvent(string type, Dictionary<string, object> d)
        {
            string state = J.Str(d, "outputState");
            if (type == "CurrentProgramSceneChanged")
                scene = J.Str(d, "sceneName");
            else if (type == "SceneNameChanged" && J.Str(d, "oldSceneName") == scene)
                scene = J.Str(d, "sceneName");
            else if (type == "RecordStateChanged")
            {
                switch (state)
                {
                    case "OBS_WEBSOCKET_OUTPUT_STARTED":
                        rec = true; paused = false; recBaseMs = 0; recClock.Restart();
                        Changed();
                        Toast(Glyph.Rec, Gfx.Red, "Recording started", replay ? "Replay buffer is on too" : null, "rec_start");
                        break;
                    case "OBS_WEBSOCKET_OUTPUT_PAUSED":
                        paused = true; recBaseMs += recClock.ElapsedMilliseconds; recClock.Reset();
                        Changed();
                        Toast(Glyph.Pause, Gfx.Amber, "Recording paused", "at " + Clock(RecElapsedMs), "pause");
                        break;
                    case "OBS_WEBSOCKET_OUTPUT_RESUMED":
                        paused = false; recClock.Restart();
                        Changed();
                        Toast(Glyph.Rec, Gfx.Red, "Recording resumed", "from " + Clock(RecElapsedMs), "resume");
                        break;
                    case "OBS_WEBSOCKET_OUTPUT_STOPPED":
                    {
                        bool was = rec;
                        long ms = RecElapsedMs;
                        rec = paused = false; recBaseMs = 0; recClock.Reset();
                        string path = J.Str(d, "outputPath");
                        if (path != null) lastSaved = path;
                        Changed();
                        if (was) Toast(Glyph.Check, Gfx.Green, "Recording saved", Join(Clock(ms), FileLabel(path)), "rec_stop", cfg.ToastSeconds + 1);
                        else Toast(Glyph.Warn, Gfx.Amber, "Recording didn't start", "Check OBS for the error", "warn");
                        break;
                    }
                }
            }
            else if (type == "ReplayBufferStateChanged")
            {
                if (state == "OBS_WEBSOCKET_OUTPUT_STARTED")
                {
                    replay = true;
                    Changed();
                    Toast(Glyph.Replay, Gfx.Teal, "Replay buffer on", replaySeconds > 0 ? "Clips save the last " + Seconds(replaySeconds) : null, "replay_on");
                    Task refresh = LoadReplayLength();   // in case the profile changed
                }
                else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED")
                {
                    bool was = replay;
                    replay = false;
                    Changed();
                    if (was) Toast(Glyph.Stop, Gfx.Gray, "Replay buffer off", "Nothing is being buffered", "replay_off");
                    else Toast(Glyph.Warn, Gfx.Amber, "Replay buffer didn't start", "Check OBS for the error", "warn");
                }
            }
            else if (type == "ReplayBufferSaved")
            {
                clipCount++;
                string path = J.Str(d, "savedReplayPath");
                if (path != null) lastSaved = path;
                Toast(Glyph.Check, Gfx.Green, "Clip saved", Join("#" + clipCount, FileLabel(path)), "clip", cfg.ToastSeconds + 0.5);
            }
        }

        async Task LoadReplayLength()
        {
            try
            {
                var mode = await obs.Request("GetProfileParameter", Param("Output", "Mode"));
                string section = ParamValue(mode) == "Advanced" ? "AdvOut" : "SimpleOutput";
                int seconds;
                replaySeconds = int.TryParse(ParamValue(await obs.Request("GetProfileParameter", Param(section, "RecRBTime"))), out seconds) ? seconds : 0;
            }
            catch (Exception ex) { Log.Write("replay length: " + ex.Message); }
        }

        static Dictionary<string, object> Param(string category, string name)
        {
            return new Dictionary<string, object> { { "parameterCategory", category }, { "parameterName", name } };
        }

        static string ParamValue(Dictionary<string, object> r)
        {
            return J.Str(r, "parameterValue") ?? J.Str(r, "defaultParameterValue");
        }

        // ------------------------------------------------ display

        void Changed()
        {
            UpdateTray();
            RefreshPill();
        }

        void Toast(Glyph glyph, Color accent, string title, string detail, string sound, double seconds = 0)
        {
            sounds.Play(sound);
            string tag = connected ? scene : null;
            var bmp = Gfx.Toast(glyph, accent, title, detail, tag, S);
            int offset = pillWin.IsShown ? pillWin.ContentSize.Height + (int)(8 * S) : 0;
            Point pos = Place(bmp.Size, TargetArea(), offset);
            toasts.Show(bmp, pos, (int)((seconds > 0 ? seconds : cfg.ToastSeconds) * 1000));
            rtssToast = () => Gfx.Toast(glyph, accent, title, detail, tag, S);
            toastPos = pos;
            toastSerial++;
            UpdateRtss();
        }

        void Peek()
        {
            if (!connected) Toast(Glyph.Warn, Gfx.Gray, "OBS not connected", status, null);
            else if (rec) Toast(paused ? Glyph.Pause : Glyph.Rec, paused ? Gfx.Amber : Gfx.Red, (paused ? "Paused at " : "Recording ") + Clock(RecElapsedMs), replay ? "Replay buffer on" : "Replay buffer off", null);
            else if (replay) Toast(Glyph.Replay, Gfx.Teal, "Replay buffer on", Join("Not recording", replaySeconds > 0 ? "clips save the last " + Seconds(replaySeconds) : null), null);
            else Toast(Glyph.Stop, Gfx.Gray, "Nothing running", "Recording and replay buffer are off", null);
        }

        // Shows the pill while something is running, on the primary display.
        void RefreshPill()
        {
            var parts = demoPill ?? CurrentPillParts();
            if (parts.Count == 0 || !(cfg.Pill || demoPill != null))
            {
                pillWin.Conceal();
                pillTimer.Stop();
                pillKey = null;
                pillShown = null;
                UpdateRtss();
                return;
            }
            var key = new StringBuilder();
            foreach (var p in parts) key.Append(p.Key.ToArgb()).Append(p.Value).Append('|');
            bool redraw = key.ToString() != pillKey;
            if (redraw)
            {
                pillWin.SetContent(Gfx.Pill(parts, S));
                pillKey = key.ToString();
            }
            Point pos = Place(pillWin.ContentSize, TargetArea(), 0);
            if (redraw || pos != pillPos || !pillWin.IsShown) pillWin.Present(pos, PillAlpha);
            else pillWin.RaiseToTop();
            pillPos = pos;
            pillShown = parts;
            if (!pillTimer.Enabled) pillTimer.Start();
            UpdateRtss();
        }

        // No window can show over a game in exclusive fullscreen, so there (with Rtss=1) RTSS draws the
        // HUD instead, from a copy of what the windows show. Borderless games still show the windows, so
        // RTSS is left alone there. RTSS's own show/hide switch (which Afterburner's overlay hotkey flips)
        // hides it with everything else RTSS draws. RTSS announces nothing, so this rereads its shared
        // memory on every pill tick and toast. Without RTSS it finds no shared memory and does nothing.
        void UpdateRtss()
        {
            string state = "off";
            if (cfg.Rtss && (pillShown != null || rtssToast != null) && ExclusiveFullscreen())
            {
                int pid, zoom;
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
                if (!rtss.Target(pid, out zoom)) state = "RTSS isn't running or isn't drawing in this game";
                else
                {
                    string key = pillKey + "|" + pillPos + "|" + (rtssToast != null ? toastSerial : -1) + "|" + zoom + "|" + cfg.Position;
                    try
                    {
                        if (key != rtssKey) { rtssText = RtssImage(zoom); rtssKey = key; }
                        state = rtss.Show(rtssText) ? "drawing the HUD through RTSS" : "all of RTSS's OSD slots are taken";
                    }
                    catch (Exception ex) { state = "couldn't save the HUD picture for RTSS: " + ex.Message; }
                }
            }
            if (state != "drawing the HUD through RTSS") { rtss.Clear(); rtssKey = null; }
            if (state != rtssState && state != "off") Log.Write("rtss: " + state);
            rtssState = state;
        }

        static bool ExclusiveFullscreen()
        {
            int state;
            return Native.SHQueryUserNotificationState(out state) == 0 && state == Native.QUNS_RUNNING_D3D_FULL_SCREEN;
        }

        static string RtssDir { get { return Path.Combine(Path.GetTempPath(), "obs-hud"); } }

        // Copies what the overlay windows show into one picture: the same pill and toast bitmaps, at the
        // windows' own screen spots and opacity, so RTSS shows the HUD exactly as the windows would. The
        // picture runs from the HUD's screen corner to the far side of the HUD, so pinned to that corner
        // everything lands where the windows are. It's padded to whole zoomed pixels, which RTSS sizes
        // images in, and each picture gets a new file name so RTSS loads it again.
        string RtssImage(int zoom)
        {
            var shown = new List<Tuple<Bitmap, Point, byte>>();
            try
            {
                if (pillShown != null) shown.Add(Tuple.Create(Gfx.Pill(pillShown, S), pillPos, PillAlpha));
                if (rtssToast != null) shown.Add(Tuple.Create(rtssToast(), toastPos, (byte)255));
                return RtssImage(zoom, shown);
            }
            finally { foreach (var s in shown) s.Item1.Dispose(); }
        }

        string RtssImage(int zoom, List<Tuple<Bitmap, Point, byte>> shown)
        {
            Rectangle box = Rectangle.Empty;
            foreach (var s in shown)
            {
                var r = new Rectangle(s.Item2, s.Item1.Size);
                box = box.IsEmpty ? r : Rectangle.Union(box, r);
            }

            string where = cfg.Position;
            bool bottom = where.StartsWith("Bottom"), left = where.EndsWith("Left"), right = where.EndsWith("Right");
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            int x0 = left ? screen.Left : box.Left, x1 = right ? screen.Right : box.Right;
            int y0 = bottom ? box.Top : screen.Top, y1 = bottom ? screen.Bottom : box.Bottom;
            int w = (x1 - x0 + zoom - 1) / zoom * zoom, h = (y1 - y0 + zoom - 1) / zoom * zoom;
            if (!left) x0 = x1 - w;   // grow away from the pinned corner
            if (bottom) y0 = y1 - h;

            string file = Path.Combine(RtssDir, "rtss-" + ++rtssImageSerial + ".png");
            using (var img = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(img))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    foreach (var s in shown)
                        using (var fade = new ImageAttributes())
                        {
                            fade.SetColorMatrix(new ColorMatrix { Matrix33 = s.Item3 / 255f });
                            var dst = new Rectangle(s.Item2.X - x0, s.Item2.Y - y0, s.Item1.Width, s.Item1.Height);
                            g.DrawImage(s.Item1, dst, 0, 0, dst.Width, dst.Height, GraphicsUnit.Pixel, fade);
                        }
                }
                Directory.CreateDirectory(RtssDir);
                img.Save(file, ImageFormat.Png);
            }
            try { File.Delete(Path.Combine(RtssDir, "rtss-" + (rtssImageSerial - 2) + ".png")); }   // RTSS may still be reading the last one
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // <LI> loads the picture, <Pn> pins its own layer <L> to one of 9 screen spots (numbered left to right,
            // top to bottom), and <I> draws all of it. White is set so RTSS doesn't tint it with its text color.
            int spot = (bottom ? 6 : 0) + (left ? 0 : right ? 2 : 1);
            return string.Format("<LI=\"{0}\"><P{1}><L{2}><C=FFFFFF><I={3},{4},0,0,{5},{6}><C>",
                file, spot, RtssLayer, w / zoom, h / zoom, w, h);
        }

        const int RtssLayer = 32;   // high enough to stay clear of overlay layouts, which number layers from 0

        List<KeyValuePair<Color, string>> CurrentPillParts()
        {
            var parts = new List<KeyValuePair<Color, string>>();
            if (!connected) return parts;
            if (rec) parts.Add(new KeyValuePair<Color, string>(paused ? Gfx.Amber : Gfx.Red, (paused ? "PAUSED " : "REC ") + Clock(RecElapsedMs)));
            if (replay) parts.Add(new KeyValuePair<Color, string>(Gfx.Teal, "REPLAY"));
            return parts;
        }

        // Overlays always go on the primary display.
        Rectangle TargetArea()
        {
            Screen screen = Screen.PrimaryScreen;
            IntPtr fg = Native.GetForegroundWindow();
            Native.RECT r;
            if (fg != IntPtr.Zero && !IsShellWindow(fg) && Native.GetWindowRect(fg, out r) && r.Left <= screen.Bounds.Left && r.Top <= screen.Bounds.Top && r.Right >= screen.Bounds.Right && r.Bottom >= screen.Bounds.Bottom)
                return screen.Bounds;   // a fullscreen app covers the taskbar, so use the whole screen
            return screen.WorkingArea;
        }

        // The desktop and the taskbar span the whole screen and get focus when an app closes,
        // but they aren't fullscreen apps, so the taskbar is still there to avoid.
        static bool IsShellWindow(IntPtr hwnd)
        {
            if (hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow()) return true;
            var cls = new StringBuilder(64);
            Native.GetClassName(hwnd, cls, cls.Capacity);
            switch (cls.ToString())
            {
                case "Progman": case "WorkerW": case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd": return true;
                default: return false;
            }
        }

        Point Place(Size size, Rectangle area, int offset)
        {
            int m = (int)(18 * S);
            string p = cfg.Position;
            // Center on the monitor, not the work area: a side taskbar would pull it off-center. Clamp so it never covers the taskbar.
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            int x = p.EndsWith("Left") ? area.Left + m : p.EndsWith("Right") ? area.Right - m - size.Width
                : Math.Max(area.Left + m, Math.Min(area.Right - m - size.Width, screen.Left + (screen.Width - size.Width) / 2));
            int y = p.StartsWith("Bottom") ? area.Bottom - m - offset - size.Height : area.Top + m + offset;
            return new Point(x, y);
        }

        void UpdateTray()
        {
            using (var bmp = Gfx.TrayBitmap(connected, rec, paused, replay, SystemInformation.SmallIconSize.Width))
            {
                var old = tray.Icon;
                tray.Icon = Icon.FromHandle(bmp.GetHicon());
                if (old != null) { Native.DestroyIcon(old.Handle); old.Dispose(); }
            }
            tray.Text = Trim63("OBS HUD: " + StatusLine());
        }

        string StatusLine()
        {
            if (!connected) return status;
            if (rec) return (paused ? "Paused at " : "Recording ") + Clock(RecElapsedMs) + ", replay buffer " + (replay ? "on" : "off");
            return replay ? "Replay buffer on, not recording" : "Idle, nothing running";
        }

        string Summary()
        {
            if (rec) return (paused ? "Recording paused" : "Recording " + Clock(RecElapsedMs)) + (replay ? " · replay buffer on" : "");
            return replay ? "Replay buffer on · not recording" : "Recording and replay buffer are off";
        }

        // ------------------------------------------------ tray menu

        void BuildMenu()
        {
            var items = tray.ContextMenuStrip.Items;
            items.Clear();
            var head = new ToolStripMenuItem(StatusLine()) { Enabled = false };
            items.Add(head);
            if (updateReady != null) Item(items, "Restart to update to " + updateReady.Tag, true, RestartToUpdate);
            items.Add(new ToolStripSeparator());
            Item(items, "Save replay", connected && replay, delegate { Do("SaveReplayBuffer", "Couldn't save the clip"); });
            Item(items, replay ? "Stop replay buffer" : "Start replay buffer", connected, delegate { Do("ToggleReplayBuffer", "Replay buffer"); });
            Item(items, rec ? "Stop recording" : "Start recording", connected, delegate { Do("ToggleRecord", "Recording"); });
            if (rec) Item(items, paused ? "Resume recording" : "Pause recording", connected, delegate { Do("ToggleRecordPause", "Pause"); });
            items.Add(new ToolStripSeparator());
            Item(items, "Show last saved file", lastSaved != null, delegate { Process.Start("explorer.exe", "/select,\"" + lastSaved.Replace('/', '\\') + "\""); });
            Item(items, "Open recordings folder", connected, OpenRecordFolder);
            items.Add(new ToolStripSeparator());
            Toggle(items, "Status pill while active", cfg.Pill, delegate { cfg.Pill = !cfg.Pill; SaveConfig(); RefreshPill(); });
            Toggle(items, "Sounds", cfg.Sounds, delegate { cfg.Sounds = !cfg.Sounds; SaveConfig(); });
            Toggle(items, "Draw through RTSS in exclusive fullscreen", cfg.Rtss, delegate { cfg.Rtss = !cfg.Rtss; SaveConfig(); RefreshPill(); });
            Toggle(items, "Update automatically", cfg.AutoUpdate, delegate { cfg.AutoUpdate = !cfg.AutoUpdate; SaveConfig(); ScheduleUpdates(); });
            Item(items, checkingUpdates ? "Checking for updates…" : "Check for updates (v" + VersionText(Updater.Current) + ")", !checkingUpdates && updateReady == null, delegate { CheckForUpdates(true); });
            var position = new ToolStripMenuItem("Position");
            foreach (var p in Config.Positions)
            {
                string chosen = p;
                var it = new ToolStripMenuItem(System.Text.RegularExpressions.Regex.Replace(p, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant()) { Checked = cfg.Position == p };
                it.Click += delegate { cfg.Position = chosen; SaveConfig(); RefreshPill(); Peek(); };
                position.DropDownItems.Add(it);
            }
            items.Add(position);
            Item(items, "Test notifications", !demoRunning, RunDemo);
            Toggle(items, "Start with Windows", StartupEnabled, delegate { SetStartup(!StartupEnabled); });
            Item(items, "Edit settings…", true, delegate { Process.Start("notepad.exe", "\"" + Config.FilePath + "\""); });
            items.Add(new ToolStripSeparator());
            Item(items, "Exit", true, ExitThread);
        }

        static void Item(ToolStripItemCollection items, string text, bool enabled, Action click)
        {
            var it = new ToolStripMenuItem(text) { Enabled = enabled };
            it.Click += delegate { click(); };
            items.Add(it);
        }

        static void Toggle(ToolStripItemCollection items, string text, bool on, Action click)
        {
            var it = new ToolStripMenuItem(text) { Checked = on };
            it.Click += delegate { click(); };
            items.Add(it);
        }

        async void Do(string request, string failTitle)
        {
            Exception error = null;
            try { await obs.Request(request); }
            catch (Exception ex) { error = ex; }
            if (error != null) Toast(Glyph.Warn, Gfx.Amber, failTitle, error.Message, "warn");
        }

        async void OpenRecordFolder()
        {
            string dir = null;
            try { dir = J.Str(await obs.Request("GetRecordDirectory"), "recordDirectory"); }
            catch (Exception ex) { Log.Write("GetRecordDirectory: " + ex.Message); }
            if (!string.IsNullOrEmpty(dir)) Process.Start("explorer.exe", "\"" + dir.Replace('/', '\\') + "\"");
        }

        bool StartupEnabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && string.Equals(k.GetValue(RunValue) as string, "\"" + Application.ExecutablePath + "\"", StringComparison.OrdinalIgnoreCase);
            }
        }

        void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValue, false);
            }
        }

        void SaveConfig()
        {
            ignoreConfigUntil = Environment.TickCount + 1500;
            try { cfg.Save(); } catch (IOException ex) { Log.Write("config: " + ex.Message); }
        }

        void Reload()
        {
            cfg = Config.Load();
            sounds.Load(cfg);
            toastWin.HideFromCapture(cfg.HideFromCapture);
            pillWin.HideFromCapture(cfg.HideFromCapture);
            string hotkeyError = hotkey.Register(cfg.PeekHotkey);
            pillKey = null;
            RefreshPill();
            ScheduleUpdates();
            Log.Write("settings reloaded");
            if (hotkeyError != null) Toast(Glyph.Warn, Gfx.Amber, "Status hotkey not set", hotkeyError, "warn");
            else Toast(Glyph.Info, Gfx.Gray, "Settings reloaded", null, null);
        }

        // With AutoUpdate, checks a minute after starting (to stay out of the way of startup), then twice a day.
        void ScheduleUpdates()
        {
            updateTimer.Stop();
            if (!cfg.AutoUpdate) return;
            updateTimer.Interval = 60 * 1000;
            updateTimer.Start();
        }

        // Looks for a newer release and installs it. A found update is always announced, and the menu
        // offers the restart; "up to date" and errors are only shown for a check asked for from the menu.
        // It never restarts on its own or pops up a window, so it can't interrupt a game.
        async void CheckForUpdates(bool asked)
        {
            if (checkingUpdates || updateReady != null) return;
            checkingUpdates = true;
            try
            {
                var release = await Updater.Newer();
                if (release == null)
                {
                    if (asked) Toast(Glyph.Check, Gfx.Green, "OBS HUD is up to date", "Version " + VersionText(Updater.Current), null);
                    return;
                }
                await Updater.Install(release, Application.ExecutablePath);
                updateReady = release;
                Log.Write("update: installed " + release.Tag + ", runs after a restart");
                Toast(Glyph.Info, Gfx.Teal, "OBS HUD " + release.Tag + " is ready", "Restart it from the tray icon to start using it", null, 6);
            }
            catch (Exception ex)
            {
                Log.Write("update: " + ex.Message);
                if (asked) Toast(Glyph.Warn, Gfx.Amber, "Couldn't update OBS HUD", ex.Message, "warn");
            }
            finally { checkingUpdates = false; }
        }

        void RestartToUpdate()
        {
            Log.Write("update: restarting into " + updateReady.Tag);
            Process.Start(Application.ExecutablePath, "--restart");
            ExitThread();
        }

        static string VersionText(Version v)
        {
            return v.ToString(v.Build > 0 ? 3 : 2);
        }

        // Plays every toast and sound once, with a fake pill, without touching OBS.
        void RunDemo()
        {
            if (demoRunning) return;
            demoRunning = true;
            var teal = new KeyValuePair<Color, string>(Gfx.Teal, "REPLAY");
            var steps = new List<Action>
            {
                () => DemoStep(new[] { teal }, Glyph.Replay, Gfx.Teal, "Replay buffer on", "Clips save the last 50 s", "replay_on"),
                () => DemoStep(new[] { teal }, Glyph.Check, Gfx.Green, "Clip saved", "#1 · Replay 2026-10-27 21-04-11.mp4 · 62 MB", "clip"),
                () => DemoStep(new[] { new KeyValuePair<Color, string>(Gfx.Red, "REC 0:00"), teal }, Glyph.Rec, Gfx.Red, "Recording started", "Replay buffer is on too", "rec_start"),
                () => DemoStep(new[] { new KeyValuePair<Color, string>(Gfx.Amber, "PAUSED 0:02"), teal }, Glyph.Pause, Gfx.Amber, "Recording paused", "at 0:02", "pause"),
                () => DemoStep(new[] { new KeyValuePair<Color, string>(Gfx.Red, "REC 0:02"), teal }, Glyph.Rec, Gfx.Red, "Recording resumed", "from 0:02", "resume"),
                () => DemoStep(new[] { teal }, Glyph.Check, Gfx.Green, "Recording saved", "0:04 · 2026-10-27 21-04-15.mp4 · 38 MB", "rec_stop"),
                () => DemoStep(new KeyValuePair<Color, string>[0], Glyph.Stop, Gfx.Gray, "Replay buffer off", "Nothing is being buffered", "replay_off"),
                () => DemoStep(new KeyValuePair<Color, string>[0], Glyph.Warn, Gfx.Amber, "Lost connection to OBS", "It was still recording or buffering", "warn"),
            };
            int i = 0;
            var timer = new Timer { Interval = 2000 };
            timer.Tick += delegate
            {
                if (i < steps.Count) { steps[i++](); return; }
                timer.Stop();
                timer.Dispose();
                demoPill = null;
                demoRunning = false;
                pillKey = null;
                RefreshPill();
            };
            steps[i++]();
            timer.Start();
        }

        void DemoStep(KeyValuePair<Color, string>[] pill, Glyph glyph, Color accent, string title, string detail, string sound)
        {
            demoPill = new List<KeyValuePair<Color, string>>(pill);
            RefreshPill();
            Toast(glyph, accent, title, detail, sound, 1.6);
        }

        protected override void ExitThreadCore()
        {
            watcher.Dispose();
            tray.Visible = false;
            if (tray.Icon != null) Native.DestroyIcon(tray.Icon.Handle);
            tray.Dispose();
            hotkey.Dispose();
            toastWin.Dispose();
            pillWin.Dispose();
            rtss.Clear();
            try { Directory.Delete(RtssDir, true); }
            catch (IOException) { }   // includes the folder never having been made
            catch (UnauthorizedAccessException) { }
            Log.Write("exit");
            base.ExitThreadCore();
        }

        // ------------------------------------------------ formatting

        static string Clock(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
        }

        static string Seconds(int s)
        {
            if (s < 60) return s + " s";
            return s % 60 == 0 ? s / 60 + " min" : s / 60 + " min " + s % 60 + " s";
        }

        static string FileLabel(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Name + " · " + FileSize(fi.Length) : fi.Name;
            }
            catch (Exception) { return null; }
        }

        static string FileSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes >> 20) + " MB";
            return Math.Max(1, bytes >> 10) + " KB";
        }

        static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            return string.IsNullOrEmpty(b) ? a : a + " · " + b;
        }

        static string Trim63(string s) { return s.Length <= 63 ? s : s.Substring(0, 62) + "…"; }
    }

    // ---------------------------------------------------------------- dev aid: obs-hud.exe --preview <dir>

    static class Preview
    {
        public static void Render(string dir)
        {
            float s = 1.25f;
            Directory.CreateDirectory(dir);
            var red = new KeyValuePair<Color, string>(Gfx.Red, "REC 12:34");
            var teal = new KeyValuePair<Color, string>(Gfx.Teal, "REPLAY");
            var items = new List<Bitmap>
            {
                Gfx.Pill(new[] { red, teal }, s),
                Gfx.Pill(new[] { teal }, s),
                Gfx.Pill(new[] { new KeyValuePair<Color, string>(Gfx.Amber, "PAUSED 1:03:07") }, s),
                Gfx.Toast(Glyph.Replay, Gfx.Teal, "Replay buffer on", "Clips save the last 50 s", "Recording", s),
                Gfx.Toast(Glyph.Check, Gfx.Green, "Clip saved", "#3 · Replay 2026-10-27 21-04-11.mp4 · 62 MB", "Recording", s),
                Gfx.Toast(Glyph.Rec, Gfx.Red, "Recording started", "Replay buffer is on too", "Streaming - full webcam", s),
                Gfx.Toast(Glyph.Pause, Gfx.Amber, "Recording paused", "at 3:07", "Capture", s),
                Gfx.Toast(Glyph.Check, Gfx.Green, "Recording saved", "12:34 · 2026-10-27 21-04-15.mp4 · 1.4 GB", "Recording", s),
                Gfx.Toast(Glyph.Stop, Gfx.Gray, "Replay buffer off", "Nothing is being buffered", "A very long scene name that keeps going and going", s),
                Gfx.Toast(Glyph.Warn, Gfx.Amber, "Lost connection to OBS", "It was still recording or buffering", null, s),
                Gfx.Toast(Glyph.Info, Gfx.Gray, "Connected to OBS", null, "Streaming", s),
            };
            int W = 1100, pad = 20, H = pad * 2 + 40;
            foreach (var b in items) H += b.Height + 12;
            using (var sheet = new Bitmap(W, H))
            using (var g = Graphics.FromImage(sheet))
            {
                // bright busy background on the left, dark on the right, to check contrast on both
                using (var lg = new LinearGradientBrush(new Rectangle(0, 0, W / 2, H), Color.FromArgb(160, 210, 245), Color.FromArgb(90, 170, 70), 90f))
                    g.FillRectangle(lg, 0, 0, W / 2, H);
                using (var dark = new SolidBrush(Color.FromArgb(28, 30, 36))) g.FillRectangle(dark, W / 2, 0, W / 2, H);
                int y = pad;
                foreach (var b in items)
                {
                    g.DrawImage(b, new Rectangle(pad, y, b.Width, b.Height));
                    g.DrawImage(b, new Rectangle(W / 2 + pad, y, b.Width, b.Height));
                    y += b.Height + 12;
                }
                int x = pad;
                var states = new[] { new[] { false, false, false, false }, new[] { true, false, false, false }, new[] { true, false, false, true }, new[] { true, true, false, false }, new[] { true, true, false, true }, new[] { true, true, true, true } };
                foreach (var st in states)
                {
                    using (var icon = Gfx.TrayBitmap(st[0], st[1], st[2], st[3], 32)) g.DrawImage(icon, new Rectangle(W / 2 + x, y, 32, 32));
                    x += 44;
                }
                sheet.Save(Path.Combine(dir, "preview.png"), ImageFormat.Png);
            }
            foreach (var b in items) b.Dispose();
        }

        // obs-hud.exe --docs <dir>: the README images, drawn at 2x so they stay sharp on high-DPI screens
        public static void Docs(string dir)
        {
            const float s = 2f;
            Directory.CreateDirectory(dir);
            var red = new KeyValuePair<Color, string>(Gfx.Red, "REC 12:34");
            var teal = new KeyValuePair<Color, string>(Gfx.Teal, "REPLAY");

            // hero: the top-right corner of a screen mid-game, pill and toast where the app puts them
            using (var pill = Gfx.Pill(new[] { red, teal }, s))
            using (var toast = Gfx.Toast(Glyph.Check, Gfx.Green, "Clip saved", "#3 · Replay 2026-10-27 21-04-11.mp4 · 62 MB", "Gameplay", s))
            using (var hero = new Bitmap(1600, 420, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(hero))
            {
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, hero.Width, hero.Height), Color.FromArgb(18, 24, 48), Color.FromArgb(58, 34, 78), 25f))
                    g.FillRectangle(bg, 0, 0, hero.Width, hero.Height);   // before anti-aliasing, which would leave the edge pixels half transparent
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Glow(g, new RectangleF(-260, 160, 900, 620), Color.FromArgb(150, 32, 190, 172));
                Glow(g, new RectangleF(720, -340, 1100, 760), Color.FromArgb(120, 240, 120, 70));
                Glow(g, new RectangleF(380, 300, 760, 480), Color.FromArgb(90, 110, 90, 240));
                int m = (int)(18 * s);
                g.DrawImage(pill, hero.Width - m - pill.Width, m, pill.Width, pill.Height);
                g.DrawImage(toast, hero.Width - m - toast.Width, m + pill.Height + (int)(8 * s), toast.Width, toast.Height);
                hero.Save(Path.Combine(dir, "hero.png"), ImageFormat.Png);
            }

            // every pill state, side by side on a transparent background
            var pills = new List<Bitmap>
            {
                Gfx.Pill(new[] { red, teal }, s),
                Gfx.Pill(new[] { new KeyValuePair<Color, string>(Gfx.Amber, "PAUSED 3:07") }, s),
                Gfx.Pill(new[] { teal }, s),
            };
            Sheet(pills, pills.Count, 24, Path.Combine(dir, "pills.png"));

            // the notifications, two columns on a transparent background
            var toasts = new List<Bitmap>
            {
                Gfx.Toast(Glyph.Rec, Gfx.Red, "Recording started", "Replay buffer is on too", "Gameplay", s),
                Gfx.Toast(Glyph.Check, Gfx.Green, "Clip saved", "#3 · Replay 21-04-11.mp4 · 62 MB", "Gameplay", s),
                Gfx.Toast(Glyph.Pause, Gfx.Amber, "Recording paused", "at 3:07", "Gameplay", s),
                Gfx.Toast(Glyph.Check, Gfx.Green, "Recording saved", "12:34 · 21-04-15.mp4 · 1.4 GB", "Gameplay", s),
                Gfx.Toast(Glyph.Replay, Gfx.Teal, "Replay buffer on", "Clips save the last 50 s", null, s),
                Gfx.Toast(Glyph.Warn, Gfx.Amber, "Lost connection to OBS", "It was still recording or buffering", null, s),
            };
            Sheet(toasts, 2, 20, Path.Combine(dir, "notifications.png"));
        }

        static void Glow(Graphics g, RectangleF r, Color c)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using (var b = new PathGradientBrush(path) { CenterColor = c, SurroundColors = new[] { Color.FromArgb(0, c) } })
                    g.FillPath(b, path);
            }
        }

        // Lays bitmaps out in a grid (each column as wide as its widest item), saves it and disposes them.
        static void Sheet(List<Bitmap> items, int cols, int gap, string file)
        {
            int rows = (items.Count + cols - 1) / cols;
            var colW = new int[cols];
            var rowH = new int[rows];
            for (int i = 0; i < items.Count; i++)
            {
                colW[i % cols] = Math.Max(colW[i % cols], items[i].Width);
                rowH[i / cols] = Math.Max(rowH[i / cols], items[i].Height);
            }
            int W = gap * (cols - 1), H = gap * (rows - 1);
            foreach (int w in colW) W += w;
            foreach (int h in rowH) H += h;
            using (var sheet = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(sheet))
            {
                int y = 0;
                for (int r = 0; r < rows; r++)
                {
                    int x = 0;
                    for (int c = 0; c < cols && r * cols + c < items.Count; c++)
                    {
                        var b = items[r * cols + c];
                        g.DrawImage(b, x, y + (rowH[r] - b.Height) / 2, b.Width, b.Height);
                        x += colW[c] + gap;
                    }
                    y += rowH[r] + gap;
                }
                sheet.Save(file, ImageFormat.Png);
            }
            foreach (var b in items) b.Dispose();
        }
    }
}
