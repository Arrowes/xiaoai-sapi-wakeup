using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.Recognition;
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
