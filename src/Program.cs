using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.Recognition;

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
        public static bool HasZhCnRecognizer()
        {
            return SpeechRecognitionEngine.InstalledRecognizers()
                .Any(x => x.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase));
        }
    }
}
