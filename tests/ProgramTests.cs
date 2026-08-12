using System;
using System.IO;
using System.Speech.Recognition;
using SapiXiaoai;

internal static class ProgramTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "SapiXiaoaiTests");
        Directory.CreateDirectory(root);

        string missing = Path.Combine(root, "missing.ini");
        AppSettings defaults = AppSettings.Load(missing);
        Check(Math.Abs(defaults.Confidence - 0.75f) < 0.001f, "missing config confidence");

        string valid = Path.Combine(root, "valid.ini");
        File.WriteAllText(valid, "[settings]\r\nsensitivities = 0.82\r\n");
        AppSettings configured = AppSettings.Load(valid);
        Check(Math.Abs(configured.Confidence - 0.82f) < 0.001f, "valid confidence");

        string invalid = Path.Combine(root, "invalid.ini");
        File.WriteAllText(invalid, "[settings]\r\nsensitivities = 2\r\n");
        AppSettings fallback = AppSettings.Load(invalid);
        Check(Math.Abs(fallback.Confidence - 0.75f) < 0.001f, "invalid confidence fallback");

        TriggerGate gate = new TriggerGate();
        DateTime start = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc);
        Check(!gate.TryEnter(0.74f, start, defaults), "below confidence rejected");
        Check(gate.TryEnter(0.75f, start, defaults), "threshold accepted");
        Check(!gate.TryEnter(0.99f, start.AddSeconds(4), defaults), "cooldown rejected");
        Check(gate.TryEnter(0.99f, start.AddSeconds(5), defaults), "cooldown elapsed");
        Check(RecognizerSupport.HasZhCnRecognizer(), "zh-CN recognizer installed");
        RecognizerInfo info = RecognizerSupport.GetZhCnRecognizer();
        Check(info != null, "zh-CN recognizer selected");
        using (SpeechRecognitionEngine engine = new SpeechRecognitionEngine(info))
        {
            GrammarBuilder builder = new GrammarBuilder("你好小爱");
            builder.Culture = info.Culture;
            engine.LoadGrammar(new Grammar(builder));
        }
        Check(true, "wake grammar loaded");
        return 0;
    }
}
