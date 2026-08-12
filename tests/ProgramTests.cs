using System;
using System.Drawing;
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

        Point position = WindowAnchor.CalculatePosition(
            new Rectangle(0, 0, 1920, 1040), new Size(400, 300));
        Check(position == new Point(1508, 728), "bottom-right with 12px margin");

        Point clamped = WindowAnchor.CalculatePosition(
            new Rectangle(100, 50, 800, 600), new Size(1000, 700));
        Check(clamped == new Point(100, 50), "oversized window clamped");

        WindowAnchorState anchorState = new WindowAnchorState();
        int staleDiscovery = anchorState.BeginDiscovery();
        int currentDiscovery = anchorState.BeginDiscovery();
        Check(!anchorState.TryAttach(staleDiscovery, new IntPtr(1), 11, 101),
            "superseded discovery cannot attach stale result");
        Check(anchorState.TryAttach(currentDiscovery, new IntPtr(2), 22, 202),
            "current discovery attaches result");

        WindowAnchorState coordinatedState = new WindowAnchorState();
        int useGeneration = coordinatedState.BeginDiscovery();
        coordinatedState.TryAttach(useGeneration, new IntPtr(10), 110, 1010);
        int actionCount = 0;
        Check(coordinatedState.TryUseTarget(useGeneration, new IntPtr(10), 110, 1010, delegate
        {
            actionCount++;
            return true;
        }) && actionCount == 1, "current generation action executes");

        int replacementGeneration = coordinatedState.BeginDiscovery();
        coordinatedState.TryAttach(replacementGeneration, new IntPtr(20), 220, 2020);
        actionCount = 0;
        Check(!coordinatedState.TryUseTarget(useGeneration, new IntPtr(10), 110, 1010, delegate
        {
            actionCount++;
            return true;
        }) && actionCount == 0, "superseded generation action does not execute");

        Check(!coordinatedState.TryUseTarget(replacementGeneration, new IntPtr(20), 220, 2020,
            delegate { return false; }) &&
            !coordinatedState.TryUseTarget(replacementGeneration, new IntPtr(20), 220, 2020,
                delegate { actionCount++; return true; }) && actionCount == 0,
            "failed current action clears its target");

        int oldGeneration = coordinatedState.BeginDiscovery();
        coordinatedState.TryAttach(oldGeneration, new IntPtr(30), 330, 3030);
        int newerGeneration = 0;
        Check(!coordinatedState.TryUseTarget(oldGeneration, new IntPtr(30), 330, 3030, delegate
        {
            newerGeneration = coordinatedState.BeginDiscovery();
            coordinatedState.TryAttach(newerGeneration, new IntPtr(40), 440, 4040);
            return false;
        }) && coordinatedState.TryUseTarget(newerGeneration, new IntPtr(40), 440, 4040,
            delegate { return true; }),
            "failed old action preserves newer target");
        return 0;
    }
}
