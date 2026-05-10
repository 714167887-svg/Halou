# Halou Suite CAD 插件 — 升级记录

> 2026-05 起的版本日志。每个条目记录关键改动；详细 commit 见 [W](https://github.com/714167887-svg/W) 与 [halou-release](https://github.com/714167887-svg/halou-release) 仓。

## 1.1.43 — 2026-05-07
- 修复 BOM 兼容：`LicenseInfo.Parse` 解析前主动剥 `\uFEFF`；`RobustHttp` 的 PowerShell / curl 通道返回值统一剥 BOM
- license.json 重写为无 BOM（解决"无效的 JSON 基元：﻿"死锁）

## 1.1.42 — 2026-05-07
- `RobustHttp.DownloadString` 增加内容校验回调；license / manifest 调用都要求"以 `{` 开头"
- 中间链路把 raw.githubusercontent 劫持成 HTML 时，验证失败 → 自动 fallback 到下一通道/jsDelivr 镜像
- `DownloadFile` 增加最小尺寸校验（dll < 100 KB 视为劫持）

## 1.1.41 — 2026-05-07
- 新增 `RobustHttp` 类：三通道（WebClient → PowerShell `Invoke-WebRequest` → `curl.exe`）× 双源（GitHub raw → jsDelivr 镜像）= 6 路兜底
- 替换全部 GitHub WebClient 直连调用，消除自更新单点故障

## 1.1.40 — 2026-05-07
- JT 出图减闪屏：保存并设置 `VTENABLE = 0` 关闭 ZOOM/VIEW 过渡动画，命令结束恢复
- 继承 v1.1.39 的白底输出 + TEMP 缓存 + TLS 1.2 修复

## 1.1.39 — 2026-05-07
- JT 输出改白底：出图前临时切白模型空间背景，PNGOUT 截图，命令结束自动还原
- `Initialize()` 显式启用 TLS 1.2/1.1，修复 .NET 默认 TLS 1.0 被 GitHub 拒绝导致的"无法联网校验"

## 1.1.38 — 2026-05-07
- JT 不再在工作目录留 PNG 缓存，改用 `%TEMP%\halou-jt\`
- JT 命令开始时清掉上一轮残留（CF_HDROP 是路径引用，无法在命令结束后立即删除，故采用 next-run 清理策略）

## 1.1.37 — 2026-05-06（稳定基线）
- 跳过 PlotEngine（中文 CAD 2021 调不通且会干扰后续 PNGOUT）
- JT 直接走 PNGOUT，画质与 v1.1.33 一致

## 1.1.32–1.1.36 — JT 出图能力迭代
- `1.1.32` JT 多 PNG via CF_HDROP（多帧时支持多图剪贴板）
- `1.1.33` 关键修复：JT WBLOCK + OOPS 流程纠正
- `1.1.34` JT PlotEngine 高分辨率（后于 1.1.37 回退）
- `1.1.35` JT 选中帧高亮显示
- `1.1.36` PlotEngine eInvalidInput 修复

## 1.1.27–1.1.31 — JT 画质优化
- `1.1.27` JT 像素提示 + 定位
- `1.1.28` 剪贴板高分辨率 PNG，保留 CAD 实体
- `1.1.29` 取消上采样避免模糊
- `1.1.30` 修复 `jt-png-to-clipboard` AccessViolation
- `1.1.31` JT 多帧合并

## 1.1.23–1.1.26 — JT 早期版本
- `1.1.23` JT zoom_W + PNGOUT direct ss
- `1.1.24` 自动白边裁剪
- `1.1.25` JT 裁剪容差 + 对角线
- `1.1.26` 上采样到 2400px

## 配套工具

- **HalouSuiteUpdater.exe**（v3）：桌面常驻应急工具。当客户 dll 自更新失效（1.1.40 之前的版本）时，双击 exe 一键修复 TLS 并把 dll 替换到 `%LOCALAPPDATA%\HalouSuite\bin\`。v2 起内置 jsDelivr 镜像兜底；v3 起内置自更新（启动时拉 `release/HalouSuiteUpdater.version`，远端版本号大则下载新 exe 并 PowerShell 替换自身后重启）。

## 发版流程

从 1.1.44 起，发版用一键脚本：

```powershell
e:\halou wode\W\一键发布Halou新版本.bat 1.1.44 "1.1.44 修复 XXX"
```

详见 [release.ps1](release.ps1)。
