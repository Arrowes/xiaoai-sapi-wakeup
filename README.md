# SAPI 小爱离线唤醒器

使用 Windows 中文 SAPI 离线识别“你好小爱”，自动让 PC 版小爱同学进入聆听状态。

## 使用

打开 `dist/SapiXiaoai/SapiXiaoai.exe`。运行文件夹必须同时保留 `xiaoai.exe` 和 `set.ini`。

```ini
[settings]
sensitivities = 0.75
cooldown_seconds = 1
```

源码和测试位于 `source/`，可运行文件位于 `dist/SapiXiaoai/`。

## 来源

`xiaoai.exe` 参考 [chnzzy/OneClickXiaoai](https://github.com/chnzzy/OneClickXiaoai)，上游采用 GPL-3.0 许可证，副本见 `LICENSE-OneClickXiaoai.txt`。
