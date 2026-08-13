# SAPI 小爱离线唤醒器

一个精简的 Windows 常驻程序：使用系统自带的简体中文 SAPI 识别固定短语“你好小爱”，再调用原项目 `xiaoai.exe` 让微软商店版小爱同学进入聆听状态。

设计目标：无云端 API、无 AccessKey、无第三方运行时，空闲资源占用和文件数量尽可能小。

可直接运行的全部文件位于 `dist/SapiXiaoai/`。保持其中三个文件同目录，运行 `SapiXiaoai.exe` 即可；`set.ini` 只保存本地识别灵敏度，不含旧在线 API 密钥。

详细设计与实施步骤见 `docs/superpowers/`。
