using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Diagnostics;
using SapiXiaoai;

internal static class ProgramTests
{
    private sealed class DisposableProcess : Process
    {
        public bool WasDisposed;

        protected override void Dispose(bool disposing)
        {
            WasDisposed = disposing;
            base.Dispose(disposing);
        }
    }

    [DllImport("shcore.dll")]
    private static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    public static int Main()
    {
        DpiAwareness.Enable();
        int awareness;
        Check(GetProcessDpiAwareness(Process.GetCurrentProcess().Handle, out awareness) == 0 &&
            awareness == 1, "process uses physical screen coordinates");

        string root = Path.Combine(Path.GetTempPath(), "SapiXiaoaiTests");
        Directory.CreateDirectory(root);

        string missing = Path.Combine(root, "missing.ini");
        AppSettings defaults = AppSettings.Load(missing);
        Check(Math.Abs(defaults.Confidence - 0.75f) < 0.001f, "missing config confidence");
        Check(Math.Abs(defaults.CooldownSeconds - 1f) < 0.001f, "missing config one-second cooldown");

        string valid = Path.Combine(root, "valid.ini");
        File.WriteAllText(valid, "[settings]\r\nsensitivities = 0.82\r\ncooldown_seconds = 2.5\r\n");
        AppSettings configured = AppSettings.Load(valid);
        Check(Math.Abs(configured.Confidence - 0.82f) < 0.001f, "valid confidence");
        Check(Math.Abs(configured.CooldownSeconds - 2.5f) < 0.001f, "valid cooldown");

        string invalid = Path.Combine(root, "invalid.ini");
        File.WriteAllText(invalid, "[settings]\r\nsensitivities = 2\r\ncooldown_seconds = -1\r\n");
        AppSettings fallback = AppSettings.Load(invalid);
        Check(Math.Abs(fallback.Confidence - 0.75f) < 0.001f, "invalid confidence fallback");
        Check(Math.Abs(fallback.CooldownSeconds - 1f) < 0.001f, "invalid cooldown fallback");

        string fakeWindows = Path.Combine(root, "Windows");
        string fakeMedia = Path.Combine(fakeWindows, "Media");
        Directory.CreateDirectory(fakeMedia);
        string fakeSpeechOn = Path.Combine(fakeMedia, "Speech On.wav");
        if (File.Exists(fakeSpeechOn)) File.Delete(fakeSpeechOn);
        Check(WakeSound.FindCustomSound(fakeWindows) == null,
            "missing custom wake sound uses Windows fallback");
        File.WriteAllBytes(fakeSpeechOn, new byte[0]);
        Check(WakeSound.FindCustomSound(fakeWindows) == fakeSpeechOn,
            "localized speech-on sound resolves to its real file name");

        TriggerGate gate = new TriggerGate();
        const long frequency = 1000;
        const long start = 123456;
        Check(!gate.TryEnter(0.74f, start, frequency, defaults), "below confidence rejected");
        Check(gate.TryEnter(0.75f, start, frequency, defaults), "threshold accepted");
        Check(!gate.TryEnter(0.99f, start + 999, frequency, defaults), "cooldown rejected");
        Check(gate.TryEnter(0.99f, start + 1000, frequency, defaults),
            "cooldown exact monotonic boundary accepted");

        TriggerGate configuredGate = new TriggerGate();
        Check(configuredGate.TryEnter(0.99f, start, frequency, configured), "configured cooldown first trigger");
        Check(!configuredGate.TryEnter(0.99f, start + 2499, frequency, configured),
            "configured cooldown rejected");
        Check(configuredGate.TryEnter(0.99f, start + 2500, frequency, configured),
            "configured cooldown exact boundary accepted");

        RecognitionCompletionPolicy normalCompletion = new RecognitionCompletionPolicy();
        Check(normalCompletion.ShouldRestart(),
            "unexpected normal completion restarts recognition");

        RecognitionCompletionPolicy shutdownCompletion = new RecognitionCompletionPolicy();
        shutdownCompletion.BeginShutdown();
        Check(!shutdownCompletion.ShouldRestart(),
            "shutdown completion does not restart recognition");

        Queue<Action> uiQueue = new Queue<Action>();
        List<string> shownErrors = new List<string>();
        int exitCount = 0;
        AsyncErrorNotifier notifier = new AsyncErrorNotifier(
            delegate(Action action) { uiQueue.Enqueue(action); return true; },
            delegate(string message) { shownErrors.Add(message); },
            delegate { exitCount++; });
        Check(notifier.PostLaunchFailure("first") && shownErrors.Count == 0 && uiQueue.Count == 1,
            "launch error is posted off recognition callback");
        Check(!notifier.PostLaunchFailure("duplicate") && uiQueue.Count == 1,
            "outstanding launch error suppresses duplicate");
        uiQueue.Dequeue()();
        Check(shownErrors.Count == 1 && shownErrors[0] == "first",
            "posted launch error is shown once");
        Check(notifier.PostLaunchFailure("retry") && uiQueue.Count == 1,
            "launch error can report after dismissal");
        uiQueue.Dequeue()();

        Check(notifier.PostFatalFailure("fatal") && exitCount == 0 && uiQueue.Count == 1,
            "fatal recognition error is posted before exit");
        Check(!notifier.PostFatalFailure("fatal duplicate") && uiQueue.Count == 1,
            "fatal recognition error reports only once");
        uiQueue.Dequeue()();
        Check(shownErrors.Count == 3 && shownErrors[2] == "fatal" && exitCount == 1,
            "fatal recognition error shows then exits message loop");

        Check(RecognizerSupport.IsRequiredRecognizer("MS-2052-80-DESK", "zh-CN",
            "Microsoft Speech Recognizer 8.0 for Windows (Chinese Simplified - PRC)"),
            "required recognizer identity accepted");
        Check(!RecognizerSupport.IsRequiredRecognizer("other", "zh-CN",
            "Microsoft Speech Recognizer 8.0 for Windows (Chinese Simplified - PRC)") &&
            !RecognizerSupport.IsRequiredRecognizer("MS-2052-80-DESK", "en-US",
                "Microsoft Speech Recognizer 8.0 for Windows (Chinese Simplified - PRC)") &&
            !RecognizerSupport.IsRequiredRecognizer("MS-2052-80-DESK", "zh-CN",
                "Another Recognizer"), "recognizer identity mismatch rejected");
        RecognizerInfo info = RecognizerSupport.GetRequiredRecognizer();
        Check(info != null && info.Id == "MS-2052-80-DESK" && info.Culture.Name == "zh-CN" &&
            info.Description.IndexOf("Microsoft Speech Recognizer 8.0",
                StringComparison.OrdinalIgnoreCase) >= 0,
            "exact offline recognizer installed and selected");
        using (SpeechRecognitionEngine engine = new SpeechRecognitionEngine(info))
        {
            GrammarBuilder builder = new GrammarBuilder("你好小爱");
            builder.Culture = info.Culture;
            engine.LoadGrammar(new Grammar(builder));
        }
        Check(true, "wake grammar loaded");

        DisposableProcess launchedProcess = new DisposableProcess();
        ProcessLauncher.StartAndDispose(delegate { return launchedProcess; });
        Check(launchedProcess.WasDisposed, "launched process component disposed immediately");

        Point position = WindowAnchor.CalculatePosition(
            new Rectangle(0, 0, 1920, 1040), new Size(400, 300));
        Check(position == new Point(1508, 728), "bottom-right with 12px margin");

        Point clamped = WindowAnchor.CalculatePosition(
            new Rectangle(100, 50, 800, 600), new Size(1000, 700));
        Check(clamped == new Point(100, 50), "oversized window clamped");
        Check((WindowAnchor.ForeignMoveFlags & 0x4000) != 0,
            "foreign window move uses asynchronous positioning");

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

        XiaoaiClosePolicy silentSession = new XiaoaiClosePolicy();
        Check(silentSession.ProcessLine("PlaybackStateChanged, Playing ->Paused, BufferFinished: True") ==
            XiaoaiLogAction.None, "old log lines cannot close XiaoAI");
        Check(silentSession.ProcessLine("DialogManager: Session start: test") ==
            XiaoaiLogAction.CancelClose, "new XiaoAI session cancels an older close");
        Check(silentSession.ProcessLine("DialogManager, CurrentCard: NULL, Session stop: test") ==
            XiaoaiLogAction.ScheduleClose, "session without a command schedules close");

        XiaoaiClosePolicy spokenSession = new XiaoaiClosePolicy();
        spokenSession.ProcessLine("DialogManager: Session start: test");
        Check(spokenSession.ProcessLine("PlaybackStateChanged, Buffering ->Playing") ==
            XiaoaiLogAction.CancelClose, "answer playback cancels early close");
        Check(spokenSession.ProcessLine("DialogManager, Session stop: test") ==
            XiaoaiLogAction.None, "session stop does not interrupt an answer");
        Check(spokenSession.ProcessLine("PlaybackStateChanged, Playing ->Paused, BufferFinished: False") ==
            XiaoaiLogAction.None, "unfinished playback does not close XiaoAI");
        Check(spokenSession.ProcessLine("PlaybackStateChanged, Playing ->Paused, BufferFinished: True") ==
            XiaoaiLogAction.ScheduleClose, "finished answer schedules close");

        XiaoaiClosePolicy lateAnswer = new XiaoaiClosePolicy();
        lateAnswer.ProcessLine("Changing agent state: [Inactive] -> [Listening]");
        Check(lateAnswer.ProcessLine("Changing agent state: [Working] -> [Inactive]") ==
            XiaoaiLogAction.ScheduleClose, "inactive session schedules close");
        Check(lateAnswer.ProcessLine("MediaPlayerDialogAudioOutputAdapter-PlaybackStateChanged, Buffering ->Playing") ==
            XiaoaiLogAction.CancelClose, "late playback cancels pending close");
        Check(lateAnswer.ProcessLine("MediaPlayerDialogAudioOutputAdapter-PlaybackStateChanged, Playing ->Paused, BufferFinished: True") ==
            XiaoaiLogAction.ScheduleClose, "late answer closes only after playback finishes");

        XiaoaiClosePolicy powerTask = new XiaoaiClosePolicy();
        powerTask.ProcessLine("DialogManager: Session start: power-test");
        Check(powerTask.ProcessLine("DirectLineSpeechDialogBackend-OnNlpInstructionEvent, type:Power, text:") ==
            XiaoaiLogAction.CancelClose, "power task cancels pending window close");
        powerTask.ProcessLine("PlaybackStateChanged, Buffering ->Playing");
        Check(powerTask.ProcessLine("PlaybackStateChanged, Playing ->Paused, BufferFinished: True") ==
            XiaoaiLogAction.None, "power task remains open after answer playback finishes");

        int currentGeneration;
        IntPtr currentWindow;
        uint currentThread;
        uint currentProcess;
        Check(coordinatedState.TryGetCurrentTarget(out currentGeneration, out currentWindow,
            out currentThread, out currentProcess) && currentGeneration == newerGeneration &&
            currentWindow == new IntPtr(40) && currentThread == 440 && currentProcess == 4040,
            "current attached window can be closed with its validated identity");
        Check(!coordinatedState.TryGetCurrentTarget(oldGeneration, out currentWindow,
            out currentThread, out currentProcess) &&
            coordinatedState.TryGetCurrentTarget(newerGeneration, out currentWindow,
                out currentThread, out currentProcess),
            "old interaction cannot close a newer XiaoAI window");
        return 0;
    }
}
