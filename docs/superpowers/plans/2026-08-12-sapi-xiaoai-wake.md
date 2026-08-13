# SAPI 小爱离线唤醒器 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个无第三方运行时、无 API Key 的 Windows 后台程序，在本机识别“你好小爱”后启动现有 `xiaoai.exe`，使小爱同学进入聆听状态。

**Architecture:** 单个 C# 源文件包含现有配置读取、触发冷却、SAPI 监听和事件驱动窗口锚定，并使用 Windows 自带的 .NET Framework 编译器生成隐藏窗口程序。一个无测试框架的 C# 测试入口验证纯逻辑；最终只新增并交付一个 EXE。

**Tech Stack:** C#、.NET Framework 4.x、`System.Speech`、`System.Windows.Forms`、Windows SAPI 8.0 `zh-CN`

> **最终实现说明（2026-08-13）：** 本文保留下方最初的 TDD 步骤作为实施记录；其中嵌入的早期代码片段不是最终实现参考。经过完整评审和修复的权威实现是 [`source/Program.cs`](../../../source/Program.cs)，对应回归测试是 [`source/ProgramTests.cs`](../../../source/ProgramTests.cs)。最终版本固定选择 `MS-2052-80-DESK`，使用 `Stopwatch` 单调时间执行默认一秒、可配置的冷却，通过隐藏 WinForms dispatcher 异步报告启动/识别错误，并使用 DPI 感知、异步发现、generation + HWND/thread/process/title/class 身份校验和异步原生移动来锚定窗口。

## Global Constraints

- 唤醒词固定为“你好小爱”。
- 使用本机已安装的 `Microsoft Speech Recognizer 8.0 (zh-CN)`。
- 运行期间不得调用云端 API，不得要求账户或 AccessKey。
- 复用 `E:\各种素材\小米\VoiceXiaoai\xiaoai.exe`，不覆盖原项目文件。
- 触发必须调用原项目 `xiaoai.exe`；禁止改用包内 `Xiaoai.exe` 或 `shell:appsFolder` URI，因为普通启动只打开窗口而不保证进入聆听。
- 复用现有 `set.ini` 的 `sensitivities`，默认值 `0.75`；`cooldown_seconds` 控制冷却时间，默认值 `1` 秒。
- 不引入 NuGet 包、Python、托盘界面、设置窗口或自动更新。
- 最终只新增 `SapiXiaoai.exe` 一个文件；不得新增运行时配置、日志或模型文件。
- 小爱窗口必须使用 WinEvent 事件驱动锚定到主显示器工作区右下角，边距 `12` 像素；不得永久定时轮询。
- 当前工作区不是 Git 仓库；不要擅自初始化 Git，每个任务以测试输出作为检查点。

## File Map

- `source/Program.cs`：配置、冷却门、单实例、SAPI 监听、小爱启动和窗口锚定逻辑。
- `source/ProgramTests.cs`：零依赖测试入口。
- `outputs/SapiXiaoai.exe`：最终隐藏窗口可执行文件。
- `E:\各种素材\小米\VoiceXiaoai\SapiXiaoai.exe`：部署副本。
- `E:\各种素材\小米\VoiceXiaoai\set.ini`：复用既有配置，只把 `sensitivities` 调整为 `0.75`，保留其他键。

---

### Task 1: 可测试的配置和触发门

**Files:**
- Create: `source/Program.cs`
- Create: `source/ProgramTests.cs`

**Interfaces:**
- Produces: `AppSettings.Load(string path) -> AppSettings`
- Produces: `TriggerGate.TryEnter(float confidence, long timestamp, long frequency, AppSettings settings) -> bool`
- Produces: `RecognizerSupport.GetRequiredRecognizer() -> RecognizerInfo`

- [ ] **Step 1: 写失败测试**

创建 `ProgramTests.cs`，测试默认值、有效/无效配置、置信度门槛、单调时钟冷却边界和本机固定离线识别器：

```csharp
using System;
using System.IO;
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
        const long frequency = 1000;
        const long start = 123456;
        Check(!gate.TryEnter(0.74f, start, frequency, defaults), "below confidence rejected");
        Check(gate.TryEnter(0.75f, start, frequency, defaults), "threshold accepted");
        Check(!gate.TryEnter(0.99f, start + 4999, frequency, defaults), "cooldown rejected");
        Check(gate.TryEnter(0.99f, start + 5000, frequency, defaults), "cooldown elapsed");
        return 0;
    }
}
```

- [ ] **Step 2: 编译测试并确认失败**

运行：

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /out:build\SapiXiaoai.Tests.exe /reference:'C:\Windows\assembly\GAC_MSIL\System.Speech\3.0.0.0__31bf3856ad364e35\System.Speech.dll' source\Program.cs source\ProgramTests.cs
```

预期：编译失败，提示 `AppSettings`、`TriggerGate` 和 `RecognizerSupport` 尚未定义。

- [ ] **Step 3: 实现最小核心逻辑**

在 `Program.cs` 中实现：

```csharp
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
        private bool hasTriggered;
        private long lastTriggerTimestamp;

        public bool TryEnter(float confidence, long timestamp, long frequency, AppSettings settings)
        {
            if (confidence < settings.Confidence) return false;
            if (hasTriggered && timestamp - lastTriggerTimestamp < 5L * frequency) return false;
            hasTriggered = true;
            lastTriggerTimestamp = timestamp;
            return true;
        }
    }

    internal static class RecognizerSupport
    {
        public static RecognizerInfo GetRequiredRecognizer()
        {
            return SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(x => x.Id == "MS-2052-80-DESK" &&
                    x.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) &&
                    x.Description.IndexOf("Microsoft Speech Recognizer 8.0",
                        StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
```

- [ ] **Step 4: 编译并运行测试**

运行上一步编译命令，然后运行：

```powershell
& '.\build\SapiXiaoai.Tests.exe'
```

预期：八条 `PASS`，退出码为 `0`。

### Task 2: 后台 SAPI 监听程序

**Files:**
- Modify: `source/Program.cs`

**Interfaces:**
- Consumes: `AppSettings.Load`、`TriggerGate.TryEnter`、`RecognizerSupport.GetRequiredRecognizer`
- Produces: `Program.Main()` 隐藏窗口入口
- Produces: `Program.LaunchXiaoai()` 启动同目录 `xiaoai.exe`

- [ ] **Step 1: 扩充测试以验证固定唤醒语法**

在 `ProgramTests.cs` 顶部加入 `using System.Speech.Recognition;`，并在测试入口末尾创建 `zh-CN` `GrammarBuilder`，加载“你好小爱”后释放识别器；成功加载即通过：

```csharp
RecognizerInfo info = RecognizerSupport.GetRequiredRecognizer();
Check(info != null && info.Id == "MS-2052-80-DESK" &&
    info.Culture.Name == "zh-CN" &&
    info.Description.Contains("Microsoft Speech Recognizer 8.0"),
    "exact offline recognizer selected");
using (SpeechRecognitionEngine engine = new SpeechRecognitionEngine(info))
{
    GrammarBuilder builder = new GrammarBuilder("你好小爱");
    builder.Culture = info.Culture;
    engine.LoadGrammar(new Grammar(builder));
}
Check(true, "wake grammar loaded");
```

并在 `RecognizerSupport` 接口中增加：

```csharp
public static RecognizerInfo GetRequiredRecognizer()
{
    return SpeechRecognitionEngine.InstalledRecognizers()
        .FirstOrDefault(x => x.Id == "MS-2052-80-DESK" &&
            x.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) &&
            x.Description.Contains("Microsoft Speech Recognizer 8.0"));
}
```

- [ ] **Step 2: 编译并确认新测试失败**

运行 Task 1 的测试编译命令。

预期：编译失败，提示 `GetRequiredRecognizer` 不存在。

- [ ] **Step 3: 实现 SAPI 主循环**

在 `Program.cs` 中：

- 用 `InstalledRecognizers().FirstOrDefault(...)` 同时匹配固定 ID、`zh-CN` culture 和 8.0 description。
- 在 `#if !TEST` 中加入 `Program.Main`，避免测试入口冲突。
- 使用命名互斥量 `Local\SapiXiaoai.SingleInstance`。
- 配置路径固定为 EXE 同目录已有的 `set.ini`；不存在时使用默认值，不创建文件。
- helper 路径固定为 EXE 同目录的 `xiaoai.exe`。
- 使用 `GrammarBuilder("你好小爱")`、默认麦克风和 `RecognizeMode.Multiple`。
- `SpeechRecognized` 回调仅在使用 `Stopwatch.GetTimestamp/Frequency` 的 `TriggerGate.TryEnter` 返回 `true` 时调用 `Process.Start`，并立即释放返回的 `Process` 组件。
- 初始化失败用中文 `MessageBox.Show` 后退出；helper 启动失败通过隐藏 dispatcher 投递单个错误对话框，回调立即返回并继续监听。
- `RecognizeCompleted` 的意外 `Error` 或 `InputStreamEnded` 通过同一个 dispatcher 显示一次错误并退出消息泵；正常关停取消不重复报告。
- 用隐藏的 `Application.Run()` 消息循环保持后台进程存活，为后续 WinEvent 回调提供消息泵。

最终主循环不再内嵌复制在计划中；请直接使用 [`source/Program.cs`](../../../source/Program.cs) 的评审版本。它让隐藏 `Control` 与 `SpeechRecognitionEngine` 一起包围 `Application.Run()` 的完整生命周期，先创建 dispatcher handle，再订阅 `SpeechRecognized`/`RecognizeCompleted`。所有 SAPI 回调只做门控、进程启动或 `BeginInvoke` 投递；对话框和 `Application.ExitThread` 只在主 STA 消息泵执行。

- [ ] **Step 4: 运行全部核心测试**

使用以下命令重新编译测试入口，并运行测试 EXE：

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /define:TEST /out:build\SapiXiaoai.Tests.exe /reference:'C:\Windows\assembly\GAC_MSIL\System.Speech\3.0.0.0__31bf3856ad364e35\System.Speech.dll' /reference:System.Windows.Forms.dll /reference:System.Drawing.dll source\Program.cs source\ProgramTests.cs
& '.\build\SapiXiaoai.Tests.exe'
```

预期：十条 `PASS`，退出码为 `0`。

- [ ] **Step 5: 编译隐藏窗口生产程序**

运行：

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /optimize+ /target:winexe /out:build\SapiXiaoai.exe /reference:'C:\Windows\assembly\GAC_MSIL\System.Speech\3.0.0.0__31bf3856ad364e35\System.Speech.dll' /reference:System.Windows.Forms.dll /reference:System.Drawing.dll source\Program.cs
```

预期：生成 `build\SapiXiaoai.exe`，无编译错误。

### Task 3: 低资源窗口锚定

**Files:**
- Modify: `source/Program.cs`
- Modify: `source/ProgramTests.cs`

**Interfaces:**
- Produces: `WindowAnchor.CalculatePosition(Rectangle workArea, Size windowSize) -> Point`
- Produces: `WindowAnchor.AttachWhenAvailable() -> void`
- Consumes: 主显示器 `Screen.PrimaryScreen.WorkingArea`

- [ ] **Step 1: 写窗口坐标失败测试**

在测试入口加入：

```csharp
Point position = WindowAnchor.CalculatePosition(
    new Rectangle(0, 0, 1920, 1040), new Size(400, 300));
Check(position == new Point(1508, 728), "bottom-right with 12px margin");

Point clamped = WindowAnchor.CalculatePosition(
    new Rectangle(100, 50, 800, 600), new Size(1000, 700));
Check(clamped == new Point(100, 50), "oversized window clamped");
```

- [ ] **Step 2: 编译并确认失败**

在测试编译命令中增加 `/reference:System.Drawing.dll`，运行编译。

预期：编译失败，提示 `WindowAnchor` 尚未定义。

- [ ] **Step 3: 实现坐标计算和原生事件锚定**

最初的同步/raw-HWND 示例已删除，因为它在 `SpeechRecognized` 回调中执行 50 × 100 ms 等待，并可能把过期或复用的 HWND 当作当前目标。最终实现直接见 [`source/Program.cs`](../../../source/Program.cs)，其锚定路径为：

- 在创建任何屏幕/UI 状态之前启用进程 DPI 感知，所有 `12` 像素计算均使用物理坐标。
- 每次真实唤醒只创建一个 generation，并把最长五秒的发现工作提交给 ThreadPool；回调立即返回，空闲时没有轮询线程或 `Timer`。
- 附加状态同时保存 generation、HWND、线程 ID 和进程 ID；移动前在同一个状态锁内重新验证 thread/process、窗口类名和标题，失败只清除仍匹配的目标。
- `SetWindowPos` 使用 `SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS`；跨进程窗口无响应时只投递移动，不阻塞 WinForms/SAPI 事件路径。
- `EVENT_OBJECT_LOCATIONCHANGE` 钩子仍只在主 STA 消息泵上存活，并在应用退出时解除。

生产编译继续引用 `System.Drawing.dll`，不增加运行文件或依赖。

- [ ] **Step 4: 接入聆听触发**

在主线程进入 `Application.Run()` 前调用一次事件初始化，并在 `Process.Start(...)` 成功后查找目标窗口：

```csharp
WindowAnchor.Start();
// engine.RecognizeAsync(...) 之后由主线程调用 Application.Run()

// SpeechRecognized 回调成功启动 helper 后：
WindowAnchor.AttachWhenAvailable();
```

`WindowAnchor.Start()` 必须由运行 WinForms 消息循环的主线程调用，保证 `WINEVENT_OUTOFCONTEXT` 回调能够送达。全局钩子只订阅位置变化并立即按目标 HWND 过滤。最多五秒的短时重试只在真实唤醒后执行；空闲期不得存在 `Timer`、`while` 循环或后台轮询线程。

- [ ] **Step 5: 运行全部测试**

重新编译并运行测试。

预期：新增两条坐标测试通过；全部测试退出码为 `0`。

### Task 4: 隔离验证、交付与部署

**Files:**
- Create: `outputs/SapiXiaoai.exe`
- Create: `E:\各种素材\小米\VoiceXiaoai\SapiXiaoai.exe`
- Modify: `E:\各种素材\小米\VoiceXiaoai\set.ini`

**Interfaces:**
- Consumes: Task 3 的生产 EXE 和原项目既有 `set.ini`
- Consumes: 原项目现有 `xiaoai.exe`
- Produces: 只新增一个文件的部署版本

- [ ] **Step 1: 在临时隔离目录验证缺少 helper 的错误路径**

创建 `work\isolated`，只把生产 EXE 复制进去，不复制 `xiaoai.exe`，再启动程序：

```powershell
$isolated = 'work\isolated'
New-Item -ItemType Directory -Path $isolated -Force | Out-Null
Copy-Item 'build\SapiXiaoai.exe' -Destination $isolated -Force
Start-Process (Join-Path $isolated 'SapiXiaoai.exe')
```

预期：显示“找不到 xiaoai.exe”的中文提示并退出；不产生未处理异常窗口。

- [ ] **Step 2: 在原目录旁路启动并验证单实例**

先确认目标目录精确为 `E:\各种素材\小米\VoiceXiaoai`，把现有 `set.ini` 的 `sensitivities` 改为 `0.75` 并保留 `key`，再只复制新 EXE，连续启动两次：

```powershell
$target = (Resolve-Path -LiteralPath 'E:\各种素材\小米\VoiceXiaoai').Path
if ($target -ne 'E:\各种素材\小米\VoiceXiaoai') { throw "Unexpected target: $target" }
Copy-Item 'build\SapiXiaoai.exe' -Destination $target -Force
Start-Process (Join-Path $target 'SapiXiaoai.exe')
Start-Process (Join-Path $target 'SapiXiaoai.exe')
Start-Sleep -Seconds 2
@(Get-Process -Name SapiXiaoai -ErrorAction SilentlyContinue).Count
```

预期：输出 `1`。

- [ ] **Step 3: 验证运行时无 Picovoice 依赖**

检查新进程已加载模块和活动连接：

```powershell
$p = Get-Process -Name SapiXiaoai -ErrorAction Stop
$p.Modules | Where-Object { $_.ModuleName -match 'porcupine|python' }
Get-NetTCPConnection -OwningProcess $p.Id -ErrorAction SilentlyContinue
```

预期：两条命令均无输出。

- [ ] **Step 4: 做真实唤醒验收**

先让小爱完全退出，保持新唤醒器运行，对默认麦克风清楚说“你好小爱”。确认小爱界面出现语音波形或聆听倒计时，再立即口述一个可核对的查询。然后保持小爱窗口打开，再说一次“你好小爱”，重复同一检查。

预期：两种起始状态下，小爱都进入聆听并接收紧随其后的查询。仅打开或前置小爱窗口不算通过；配置的冷却时间内再次说唤醒词不会重复触发。

- [ ] **Step 5: 验证窗口锚定与空闲资源**

让小爱显示短窗口和包含回答的高窗口，分别确认右边缘和底边始终距离主显示器工作区 `12` 像素，且未覆盖任务栏。使用 Process Explorer 或连续 60 秒的进程采样确认空闲时无周期性 CPU 尖峰；检查源代码中不存在永久 `Timer` 或监听循环。

- [ ] **Step 6: 必要时只调灵敏度**

若漏唤醒，将 `set.ini` 的 `sensitivities` 从 `0.75` 依次降至 `0.70`、`0.65`；若误唤醒，将其依次升至 `0.80`、`0.85`。每次只改一个值并重启程序复测。

- [ ] **Step 7: 复制最终交付物**

只把验证通过的 EXE 复制到 `outputs`：

```powershell
Copy-Item 'build\SapiXiaoai.exe' -Destination 'outputs' -Force
```

确认原目录只新增 `SapiXiaoai.exe`，既有 `set.ini` 只修改 `sensitivities`，不删除或覆盖其他旧文件。

- [ ] **Step 8: 最终清洁检查**

重新运行核心测试、确认生产 EXE 存在并记录 SHA-256：

```powershell
& '.\build\SapiXiaoai.Tests.exe'
Get-FileHash -Algorithm SHA256 'outputs\SapiXiaoai.exe'
```

关闭本轮测试启动的 `SapiXiaoai` 后台进程。不要自动创建开机启动项；等用户确认真实唤醒效果后再添加快捷方式。


