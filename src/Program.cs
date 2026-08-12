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
            }
            return result;
        }
    }

    internal sealed class TriggerGate
    {
        private DateTime lastTriggerUtc = DateTime.MinValue;

        public bool TryEnter(float confidence, DateTime utcNow, AppSettings settings)
        {
            if (confidence < settings.Confidence) return false;
            if (utcNow - lastTriggerUtc < TimeSpan.FromSeconds(5)) return false;
            lastTriggerUtc = utcNow;
            return true;
        }
    }

    internal static class RecognizerSupport
    {
        public static RecognizerInfo GetZhCnRecognizer()
        {
            return SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(x => x.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase));
        }

        public static bool HasZhCnRecognizer()
        {
            return GetZhCnRecognizer() != null;
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
        private const int ObjIdWindow = 0;
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
                    SwpNoSize | SwpNoZOrder | SwpNoActivate);
            });
        }
    }

#if !TEST
    internal static class Program
    {
        private static AppSettings settings;
        private static TriggerGate gate;
        private static string helperPath;

        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\SapiXiaoai.SingleInstance", out created))
            {
                if (!created) return;
                try { RunListener(); }
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

            RecognizerInfo info = RecognizerSupport.GetZhCnRecognizer();
            if (info == null) throw new InvalidOperationException("找不到 Windows 简体中文语音识别器 (zh-CN)。");

            settings = AppSettings.Load(Path.Combine(basePath, "set.ini"));
            gate = new TriggerGate();
            using (SpeechRecognitionEngine engine = new SpeechRecognitionEngine(info))
            {
                GrammarBuilder builder = new GrammarBuilder("你好小爱");
                builder.Culture = info.Culture;
                engine.LoadGrammar(new Grammar(builder));
                engine.SetInputToDefaultAudioDevice();
                engine.SpeechRecognized += OnSpeechRecognized;
                WindowAnchor.Start();
                engine.RecognizeAsync(RecognizeMode.Multiple);
                Application.Run();
            }
        }

        private static void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            if (!gate.TryEnter(e.Result.Confidence, DateTime.UtcNow, settings)) return;
            try
            {
                Process.Start(new ProcessStartInfo(helperPath) { UseShellExecute = true });
                WindowAnchor.AttachWhenAvailable();
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法启动小爱同学：" + ex.Message, "SAPI 小爱唤醒器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
#endif
}
