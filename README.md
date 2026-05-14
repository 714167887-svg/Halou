# Halou — CAD Halou 主仓（2026-05 重组）

## 用途

本目录从 `W/` 中独立出来，集中所有 Halou CAD 插件相关源码、发版脚本、自动化工具，与 `W/`（jsq 网页相关）平级，当前作为独立 Git 仓维护。

## 目录速查

| 路径 | 用途 | 是否活跃 |
|---|---|---|
| [HalouSuite/](HalouSuite/) | **Phase 2 新架构**：Contract/Host/Payload 三件套源码（`build-all.ps1`、`PHASE2-MIGRATION-PLAN.md`） | 在建主线 |
| [HalouSuiteUpdater/](HalouSuiteUpdater/) | 旧版 1.x 自更新守护进程（`HalouSuiteUpdater.exe v3`） | 仅服务 1.x |
| `CAD Halou插件/` | 发版脚本中枢：`publish-halou-suite.ps1`（新链路）、`publish-release.ps1`（旧链路）、`license.json` 工作副本、`build-autocad-plugin.ps1`、`install-autocad-autostart.ps1` | 活跃 |
| [autocad-clipboard-plugin-autoon-test/](autocad-clipboard-plugin-autoon-test/) | 1.x 旧版完整源码（28 个 .cs，~5000 行），Phase 2 迁移源 | 维护模式 |
| [ZK/](ZK/) | ZK 钣金/规格相关 LISP 与文档（`ZKK_Unfold_V13.lsp` 等） | 活跃 |
| [OLE/](OLE/) | OLE 批量插图功能（`oleimgdir.lsp` + clipboard 桥） | 活跃 |
| [KB/](KB/) | KB 加工产出表 LISP（`KB_Yield.lsp`） | 活跃 |
| [JT/](JT/) | JT 截图相关 LISP（`JT_Snapshot.lsp`） | 活跃 |
| [PB/](PB/) | PB CAD 提取/排版 Python 脚本（`pb_extract.py`、`kb_layout.py`） | 活跃 |

## 常用操作

| 脚本 | 用途 |
|---|---|
| `发布Halou新版本.bat` | **新链路发版**：`发布Halou新版本.bat 2.0.X "说明"` → 走 Phase 2 `publish-halou-suite.ps1` |
| `一键发布Halou新版本.bat` | 旧链路 1.x 发版（`CAD Halou插件\release.ps1`） |
| `发布CAD Halou新版本.bat` | 旧链路 1.x 发版（`publish-release.ps1`，可输入 commit message） |
| `构建CAD Halou插件.bat` / `.ps1` | 仅构建 1.x DLL，不发版 |
| `启用CAD Halou自启动.bat` | 注册 LOADER 让 acad 启动时自动加载 |
| `停用CAD Halou自启动.bat` | 取消 LOADER 注册 |

## 维护路由

| 要改什么 | 先看哪里 |
|---|---|
| Phase 2 Host/Contract/Payload | `HalouSuite/` |
| Phase 2 发版、license、release notes | `CAD Halou插件/` |
| 旧版 1.x 插件行为 | `autocad-clipboard-plugin-autoon-test/src/` |
| ZK 展开/开缺 | `ZK/ZKK_Unfold_V13.lsp`，必要时同步 V13.1 |
| KB 产出表 | `KB/KB_Yield.lsp` |
| JT 截图导出 | `JT/JT_Snapshot.lsp` |
| OLE 批量导图 | `OLE/oleimgdir.lsp`、`OLE/oleimgdir-clipboard.ps1` |
| PB 提取/排版 | `PB/pb_extract.py`、`PB/kb_layout.py` |

## 不应直接修改

- `HalouSuite/**/dist/`
- `HalouSuite/**/bin/`
- `HalouSuite/**/obj/`
- `autocad-clipboard-plugin-autoon-test/dist/`
- `CAD Halou插件/release/`
- `PB/_tmp/`、`PB/11/`、`PB/output/`
- `ZK/_frames/`、`JT/*_out/`
- `*.dwl`、`*.dwl2`、`*.bak`、`*.old`、`*.tmp`

## 三个版本/架构并存

| 架构 | 版本 | 源码 | 产物 | 客户端更新 |
|---|---|---|---|---|
| Phase 1（旧版） | 1.1.74 | [autocad-clipboard-plugin-autoon-test/src/](autocad-clipboard-plugin-autoon-test/src/) | `JsqClipboardCadPlugin.dll` | HalouSuiteUpdater.exe 守护进程关 acad 换 dll |
| **Phase 2（新版）** | **2.0.2** | [HalouSuite/](HalouSuite/) | `HalouContract.dll` + `HalouHost.dll` + `HalouPayload.<Ver>.dll` | acad 内热重载（HALOURELOAD），无需关 acad |
| Updater | v3 | [HalouSuiteUpdater/Program.cs](HalouSuiteUpdater/Program.cs) | `HalouSuiteUpdater.exe` | 自更新（halou-release/release/HalouSuiteUpdater.version） |

## 发版（Phase 2，推荐）

```bat
cd Halou
发布Halou新版本.bat 2.0.3 "本次修复 ZK 开缺长度"
```

脚本流程：改 `PayloadEntry.cs` 版本号 → `build-all.ps1` 编译三件套 → 复制三个 DLL + manifest 到 `halou-release/release/` → 改 `license.json` 的 `latest_version` / `download_url` / `release_notes` → git push 到 halou-release 仓 → 客户端 acad 启动或点"下载新版本"自动热重载。

## 已知坑/注意事项

- **License 端点**：`PayloadConstants.DefaultLicenseEndpoint` 用 `https://cdn.jsdelivr.net/gh/714167887-svg/halou-release@main/license.json`（v2.0.2 起）。**raw.githubusercontent.com 有 ~5min CDN 缓存且忽略 query string，必须用 jsDelivr**
- **git stderr 坑**：发版脚本调 git 时必须用 `cmd /c "git ... 2>&1"`，否则 `$ErrorActionPreference=Stop` 会把 LF/CRLF warning 当 NativeCommandError 抛出
- **中文 .ps1 必须 UTF-8 BOM**，否则 PS5.1 按 GBK 解析报"字符串缺少终止符"
- **license.json 同步坑**：halou-release 仓被 1.x 守护进程频繁推送，发 Phase 2 版前必须把远端 `license.json` 同步回 `CAD Halou插件/license.json` 作为基线
- **`.Location == ""` 坑**：`Assembly.Load(byte[])` 加载的程序集 `.Location` 为空，所有读 Location 的代码必须 try/catch 降级到 `AppDomain.CurrentDomain.BaseDirectory`

## 与外部仓的关系

- **本目录当前是独立 Git 仓**，与根配置仓、`W/`、`1530/` 分开提交和推送
- **halou-release**（独立 git 仓 `github.com/714167887-svg/halou-release`）：发布产物 + license.json 真源，本地通常克隆在 `C:\Users\Administrator\Desktop\halou-release\`
- **W**（独立 git 仓 `github.com/714167887-svg/W`）：剥离 Halou 相关后只保留 jsq

## 重组遗留（手动收尾）

- 本次 W 仓重组已 commit + push：`634bd63..68f014a`（删除 1037 个 Halou 相关条目，README 同步改写）
- Halou/ 下的 1.x 源码已**基于 v1.1.74**（之前曾是 1.1.68），Phase 2 改动（HalouSuite 2.0.2 + jsDelivr 端点）全部保留
- 备份分支：`W` 仓有 `backup-pre-relocate-2026-05-11`，需要时可还原

## 多机同步

| 场景 | 处理 |
|---|---|
| 开始改 Halou 前 | 在 `Halou/` 仓执行拉取，确认 `git status` 干净 |
| 改完 Halou 后 | 只在 `Halou/` 仓提交和推送，不要提交到根配置仓或 `W/` 仓 |
| 同步另一台机器 | 另一台直接拉取 Halou 仓；不要再依赖 W 仓携带 Halou 代码 |
| halou-release 发布仓 | 仍是独立发布产物仓；发版脚本会同步 release 文件和 `license.json` |

执行发版或同步前建议先关闭 AutoCAD，避免 DLL/LSP/临时文件被锁。

