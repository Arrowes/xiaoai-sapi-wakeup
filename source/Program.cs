using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SapiXiaoai
{
    internal sealed class AppSettings
    {
        public float Confidence = 0.75f;
        public float CooldownSeconds = 1f;

        public static AppSettings Load(string path)
        {
            AppSettings result = new AppSettings();
            if (!File.Exists(path)) return result;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int separator = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '[' || separator < 1) continue;
                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                float number;
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) continue;
                if (key.Equals("sensitivities", StringComparison.OrdinalIgnoreCase) && number >= 0f && number <= 1f)
                    result.Confidence = number;
                else if (key.Equals("cooldown_seconds", StringComparison.OrdinalIgnoreCase) &&
                    number > 0f && !float.IsInfinity(number))
                    result.CooldownSeconds = number;
            }
            return result;
        }
    }

    internal sealed class TriggerGate
    {
        private bool hasTriggered;
        private long lastTriggerTimestamp;

        public bool TryEnter(float confidence, long timestamp, long frequency, AppSettings settings)
        {
            if (confidence < settings.Confidence) return false;
            if (hasTriggered && timestamp - lastTriggerTimestamp < settings.CooldownSeconds * frequency) return false;
            hasTriggered = true;
            lastTriggerTimestamp = timestamp;
            return true;
        }
    }

    internal sealed class RecognitionCompletionPolicy
    {
        private int shutdownStarted;

        public bool TryBeginUnexpectedFailure(Exception error, bool inputStreamEnded)
        {
            return (error != null || inputStreamEnded) &&
                Interlocked.CompareExchange(ref shutdownStarted, 1, 0) == 0;
        }

        public void BeginShutdown()
        {
            Interlocked.Exchange(ref shutdownStarted, 1);
        }
    }

    internal sealed class AsyncErrorNotifier
    {
        private readonly Func<Action, bool> post;
        private readonly Action<string> show;
        private readonly Action exit;
        private int launchFailureOutstanding;
        private int fatalFailureOutstanding;

        public AsyncErrorNotifier(Func<Action, bool> post, Action<string> show, Action exit)
        {
            this.post = post;
            this.show = show;
            this.exit = exit;
        }

        public bool PostLaunchFailure(string message)
        {
            if (Interlocked.CompareExchange(ref launchFailureOutstanding, 1, 0) != 0)
                return false;
            return TryPost(delegate
            {
                try { show(message); }
                finally { Interlocked.Exchange(ref launchFailureOutstanding, 0); }
            }, ref launchFailureOutstanding);
        }

        public bool PostFatalFailure(string message)
        {
            if (Interlocked.CompareExchange(ref fatalFailureOutstanding, 1, 0) != 0)
                return false;
            return TryPost(delegate
            {
                try { show(message); }
                finally { exit(); }
            }, ref fatalFailureOutstanding);
        }

        private bool TryPost(Action action, ref int outstanding)
        {
            try
            {
                if (post(action)) return true;
            }
            catch (InvalidOperationException) { }
            Interlocked.Exchange(ref outstanding, 0);
            return false;
        }
    }

    internal static class RecognizerSupport
    {
        public static bool IsRequiredRecognizer(string id, string culture, string description)
        {
            return id == "MS-2052-80-DESK" &&
                culture != null && culture.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) &&
                description != null && description.IndexOf("Microsoft Speech Recognizer 8.0",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static RecognizerInfo GetRequiredRecognizer()
        {
            return SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(x =>
                IsRequiredRecognizer(x.Id, x.Culture.Name, x.Description));
        }
    }

    internal static class ProcessLauncher
    {
        public static void StartAndDispose(Func<Process> start)
        {
            using (Process process = start()) { }
        }
    }

    internal static class DpiAwareness
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public static void Enable()
        {
            if (!SetProcessDPIAware())
                throw new InvalidOperationException("无法启用屏幕 DPI 感知。");
        }
    }

    internal sealed class WindowAnchorState
    {
        private readonly object sync = new object();
        private int generation;
        private IntPtr targetWindow;
        private uint targetThreadId;
        private uint targetProcessId;

        public int BeginDiscovery()
        {
            lock (sync)
            {
                ClearTarget();
                return ++generation;
            }
        }

        public bool TryAttach(int discoveryGeneration, IntPtr hwnd,
            uint threadId, uint processId)
        {
            lock (sync)
            {
                if (discoveryGeneration != generation || hwnd == IntPtr.Zero ||
                    threadId == 0 || processId == 0) return false;
                targetWindow = hwnd;
                targetThreadId = threadId;
                targetProcessId = processId;
                return true;
            }
        }

        public bool TryGetTarget(IntPtr hwnd, out int discoveryGeneration,
            out uint threadId, out uint processId)
        {
            lock (sync)
            {
                if (hwnd != IntPtr.Zero && hwnd == targetWindow)
                {
                    discoveryGeneration = generation;
                    threadId = targetThreadId;
                    processId = targetProcessId;
                    return true;
                }
                discoveryGeneration = 0;
                threadId = 0;
                processId = 0;
                return false;
            }
        }

        public bool TryUseTarget(int discoveryGeneration, IntPtr hwnd,
            uint threadId, uint processId, Func<bool> action)
        {
            lock (sync)
            {
                if (!IsTarget(discoveryGeneration, hwnd, threadId, processId)) return false;
                bool succeeded = action();
                if (!succeeded && IsTarget(discoveryGeneration, hwnd, threadId, processId))
                    ClearTarget();
                return succeeded;
            }
        }

        private bool IsTarget(int discoveryGeneration, IntPtr hwnd,
            uint threadId, uint processId)
        {
            return discoveryGeneration == generation && hwnd != IntPtr.Zero &&
                hwnd == targetWindow && threadId == targetThreadId && processId == targetProcessId;
        }

        private void ClearTarget()
        {
            targetWindow = IntPtr.Zero;
            targetThreadId = 0;
            targetProcessId = 0;
        }
    }

    internal static class WindowAnchor
    {
        private const uint EventObjectLocationChange = 0x800B;
        private const uint WineventOutOfContext = 0;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpAsyncWindowPos = 0x4000;
        private const int ObjIdWindow = 0;
        internal const uint ForeignMoveFlags =
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos;
        private static readonly WinEventDelegate callback = OnWindowEvent;
        private static readonly WindowAnchorState state = new WindowAnchorState();
        private static IntPtr hook;

        internal delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maximumCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder windowName, int maximumCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
            IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        public static Point CalculatePosition(Rectangle workArea, Size windowSize)
        {
            int x = Math.Max(workArea.Left, workArea.Right - windowSize.Width - 12);
            int y = Math.Max(workArea.Top, workArea.Bottom - windowSize.Height - 12);
            return new Point(x, y);
        }

        public static void Start()
        {
            hook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange,
                IntPtr.Zero, callback, 0, 0, WineventOutOfContext);
            if (hook == IntPtr.Zero) throw new InvalidOperationException("无法监听小爱窗口位置变化。");
            Application.ApplicationExit += delegate
            {
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                hook = IntPtr.Zero;
            };
        }

        public static void AttachWhenAvailable()
        {
            int generation = state.BeginDiscovery();
            ThreadPool.QueueUserWorkItem(delegate(object ignored) { Discover(generation); });
        }

        private static void Discover(int generation)
        {
            for (int i = 0; i < 50; i++)
            {
                IntPtr hwnd = FindWindow("ApplicationFrameWindow", "小爱同学");
                uint processId;
                uint threadId = GetWindowThreadProcessId(hwnd, out processId);
                if (threadId != 0 && IsExpectedWindow(hwnd))
                {
                    if (state.TryAttach(generation, hwnd, threadId, processId))
                        MoveIfNeeded(generation, hwnd, threadId, processId);
                    return;
                }
                Thread.Sleep(100);
            }
        }

        private static bool IsExpectedWindow(IntPtr hwnd)
        {
            StringBuilder className = new StringBuilder(256);
            StringBuilder windowName = new StringBuilder(256);
            return GetClassName(hwnd, className, className.Capacity) > 0 &&
                GetWindowText(hwnd, windowName, windowName.Capacity) > 0 &&
                className.ToString() == "ApplicationFrameWindow" &&
                windowName.ToString() == "小爱同学";
        }

        private static void OnWindowEvent(IntPtr ignored, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint eventThread, uint eventTime)
        {
            int generation;
            uint threadId;
            uint processId;
            if (idObject == ObjIdWindow &&
                state.TryGetTarget(hwnd, out generation, out threadId, out processId))
                MoveIfNeeded(generation, hwnd, threadId, processId);
        }

        private static void MoveIfNeeded(int generation, IntPtr hwnd,
            uint threadId, uint processId)
        {
            state.TryUseTarget(generation, hwnd, threadId, processId, delegate
            {
                uint currentProcessId;
                if (GetWindowThreadProcessId(hwnd, out currentProcessId) != threadId ||
                    currentProcessId != processId || !IsExpectedWindow(hwnd)) return false;
                NativeRect rect;
                if (!GetWindowRect(hwnd, out rect)) return false;
                Point target = CalculatePosition(Screen.PrimaryScreen.WorkingArea,
                    new Size(rect.Right - rect.Left, rect.Bottom - rect.Top));
                if (rect.Left == target.X && rect.Top == target.Y) return true;
                return SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, 0, 0,
                    ForeignMoveFlags);
            });
        }
    }

#if !TEST
    internal static class Program
    {
        private static AppSettings settings;
        private static TriggerGate gate;
        private static string helperPath;
        private static RecognitionCompletionPolicy completionPolicy;
        private static AsyncErrorNotifier errorNotifier;

        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\SapiXiaoai.SingleInstance", out created))
            {
                if (!created) return;
                try
                {
                    DpiAwareness.Enable();
                    RunListener();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "SAPI 小爱唤醒器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static void RunListener()
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            helperPath = Path.Combine(basePath, "xiaoai.exe");
            if (!File.Exists(helperPath)) throw new FileNotFoundException("找不到同目录下的 xiaoai.exe。", helperPath);

            RecognizerInfo info = RecognizerSupport.GetRequiredRecognizer();
            if (info == null) throw new InvalidOperationException(
                "找不到离线语音识别器 MS-2052-80-DESK (Microsoft Speech Recognizer 8.0, zh-CN)。");

            settings = AppSettings.Load(Path.Combine(basePath, "set.ini"));
            gate = new TriggerGate();
            completionPolicy = new RecognitionCompletionPolicy();
            using (Control dispatcher = new Control())
            using (SpeechRecognitionEngine engine = new SpeechRecognitionEngine(info))
            {
                dispatcher.CreateControl();
                errorNotifier = new AsyncErrorNotifier(
                    delegate(Action action) { return TryPost(dispatcher, action); },
                    delegate(string message)
                    {
                        MessageBox.Show(message, "SAPI 小爱唤醒器",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    },
                    Application.ExitThread);
                GrammarBuilder builder = new GrammarBuilder("你好小爱");
                builder.Culture = info.Culture;
                engine.LoadGrammar(new Grammar(builder));
                engine.SetInputToDefaultAudioDevice();
                engine.SpeechRecognized += OnSpeechRecognized;
                engine.RecognizeCompleted += OnRecognizeCompleted;
                WindowAnchor.Start();
                engine.RecognizeAsync(RecognizeMode.Multiple);
                try { Application.Run(); }
                finally { completionPolicy.BeginShutdown(); }
            }
        }

        private static bool TryPost(Control dispatcher, Action action)
        {
            if (dispatcher.IsDisposed || dispatcher.Disposing || !dispatcher.IsHandleCreated)
                return false;
            dispatcher.BeginInvoke(action);
            return true;
        }

        private static void OnRecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            if (!completionPolicy.TryBeginUnexpectedFailure(e.Error, e.InputStreamEnded)) return;
            string message = e.Error != null
                ? "语音识别意外停止：" + e.Error.Message
                : "语音识别输入已结束，请检查默认麦克风。";
            errorNotifier.PostFatalFailure(message);
        }

        private static void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            if (!gate.TryEnter(e.Result.Confidence, Stopwatch.GetTimestamp(),
                Stopwatch.Frequency, settings)) return;
            try
            {
                ProcessLauncher.StartAndDispose(delegate
                {
                    return Process.Start(new ProcessStartInfo(helperPath) { UseShellExecute = true });
                });
                WindowAnchor.AttachWhenAvailable();
            }
            catch (Exception ex)
            {
                errorNotifier.PostLaunchFailure("无法启动小爱同学：" + ex.Message);
            }
        }
    }
#endif
}
