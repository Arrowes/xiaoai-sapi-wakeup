# SAPI 小爱离线唤醒器

使用 Windows 中文 SAPI 离线识别“你好小爱”，自动播放提示音并让 PC 版小爱同学进入聆听状态。麦克风休眠或重连后会自动恢复监听。无指令时，或小爱回答播放完毕后，窗口会在 1 秒后自动关闭，后台服务仍保持运行。关机、重启等电源任务不会自动关闭窗口，以免中断任务。

## 使用

打开 `dist/SapiXiaoai/SapiXiaoai.exe`。运行文件夹必须同时保留 `xiaoai.exe` 和 `set.ini`。

提示音会在唤醒后延迟 1.3 秒播放，使用 Windows Media 中显示为“语音打开.wav”的系统音效（实际文件名 `Speech On.wav`）；文件缺失或无法播放时自动使用 Windows 默认提示音。

```ini
[settings]
sensitivities = 0.75
cooldown_seconds = 1
```

- `sensitivities`：语音识别置信度门槛，范围为 `0–1`。数值越高越严格，可减少误唤醒，但也可能降低识别率。
- `cooldown_seconds`：两次有效唤醒之间的最短间隔，单位为秒，支持小数。

源码和测试位于 `source/`，可运行文件位于 `dist/SapiXiaoai/`。

## 来源

`xiaoai.exe` 参考 [chnzzy/OneClickXiaoai](https://github.com/chnzzy/OneClickXiaoai)，上游采用 GPL-3.0 许可证，副本见 `LICENSE-OneClickXiaoai.txt`。
