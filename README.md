# Halou 项目根（2026-05 重组）

本目录从 `W/` 中独立出来，集中所有 Halou CAD 插件相关源码、发版脚本、自动化工具，与 `W/`（jsq 网页相关）/`HUAHUI/`（沐新卫浴计算器）平级。

## 目录速查

| 路径 | 用途 | 是否活跃 |
|---|---|---|
| [HalouSuite/](HalouSuite/) | **Phase 2 新架构**：Contract/Host/Payload 三件套源码（`build-all.ps1`、`PHASE2-MIGRATION-PLAN.md`） | 在建主线 |
| [HalouSuiteUpdater/](HalouSuiteUpdater/) | 旧版 1.x 自更新守护进程（`HalouSuiteUpdater.exe v3`） | 仅服务 1.x |
| [CAD Halou插件/](CAD%20Halou插件/) | 发版脚本中枢：`publish-halou-suite.ps1`（新链路）、`publish-release.ps1`（旧链路）、`license.json` 工作副本、`build-autocad-plugin.ps1`、`install-autocad-autostart.ps1` | 活跃 |
| [autocad-clipboard-plugin-autoon-test/](autocad-clipboard-plugin-autoon-test/) | 1.x 旧版完整源码（28 个 .cs，~5000 行），Phase 2 迁移源 | 维护模式 |
| [ZK/](ZK/) | ZK 钣金/规格相关 LISP 与文档（`ZKK_Unfold_V13.lsp` 等） | 活跃 |
| [OLE/](OLE/) | OLE 批量插图功能（`oleimgdir.lsp` + clipboard 桥） | 活跃 |
| [KB/](KB/) | KB 加工产出表 LISP（`KB_Yield.lsp`） | 活跃 |
| [JT/](JT/) | JT 截图相关 LISP（`JT_Snapshot.lsp`） | 活跃 |
| [PB/](PB/) | PB CAD 提取/排版 Python 脚本（`pb_extract.py`、`kb_layout.py`） | 活跃 |

## 顶层脚本入口

| 脚本 | 用途 |
|---|---|
| [发布Halou新版本.bat](%E5%8F%91%E5%B8%83Halou%E6%96%B0%E7%89%88%E6%9C%AC.bat) | **新链路发版**：`发布Halou新版本.bat 2.0.X "说明"` → 走 Phase 2 publish-halou-suite.ps1 |
| [一键发布Halou新版本.bat](%E4%B8%80%E9%94%AE%E5%8F%91%E5%B8%83Halou%E6%96%B0%E7%89%88%E6%9C%AC.bat) | 旧链路 1.x 发版（CAD Halou插件\release.ps1） |
| [发布CAD Halou新版本.bat](%E5%8F%91%E5%B8%83CAD%20Halou%E6%96%B0%E7%89%88%E6%9C%AC.bat) | 旧链路 1.x 发版（publish-release.ps1，可输入 commit message） |
| [构建CAD Halou插件.bat](%E6%9E%84%E5%BB%BACAD%20Halou%E6%8F%92%E4%BB%B6.bat) / .ps1 | 仅构建 1.x DLL，不发版 |
| [启用CAD Halou自启动.bat](%E5%90%AF%E7%94%A8CAD%20Halou%E8%87%AA%E5%90%AF%E5%8A%A8.bat) | 注册 LOADER 让 acad 启动时自动加载 |
| [停用CAD Halou自启动.bat](%E5%81%9C%E7%94%A8CAD%20Halou%E8%87%AA%E5%90%AF%E5%8A%A8.bat) | 取消 LOADER 注册 |

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

## 关键事实/坑

- **License 端点**：`PayloadConstants.DefaultLicenseEndpoint` 用 `https://cdn.jsdelivr.net/gh/714167887-svg/halou-release@main/license.json`（v2.0.2 起）。**raw.githubusercontent.com 有 ~5min CDN 缓存且忽略 query string，必须用 jsDelivr**
- **git stderr 坑**：发版脚本调 git 时必须用 `cmd /c "git ... 2>&1"`，否则 `$ErrorActionPreference=Stop` 会把 LF/CRLF warning 当 NativeCommandError 抛出
- **中文 .ps1 必须 UTF-8 BOM**，否则 PS5.1 按 GBK 解析报"字符串缺少终止符"
- **license.json 同步坑**：halou-release 仓被 1.x 守护进程频繁推送，发 Phase 2 版前必须把远端 license.json 同步回 [CAD Halou插件/license.json](CAD%20Halou插件/license.json) 作为基线
- **`.Location == ""` 坑**：`Assembly.Load(byte[])` 加载的程序集 `.Location` 为空，所有读 Location 的代码必须 try/catch 降级到 `AppDomain.CurrentDomain.BaseDirectory`

## 与外部仓的关系

- **本目录无独立 .git**，物理位于顶层 `halou wode/` 仓内，但顶层仓 `.gitignore` 是 `*` 白名单（仅跟踪 .github + 同步脚本），所以 Halou/ 默认不被任何 git 跟踪
- **halou-release**（独立 git 仓 `github.com/714167887-svg/halou-release`）：发布产物 + license.json 真源，本地通常克隆在 `C:\Users\Administrator\Desktop\halou-release\`
- **W**（独立 git 仓 `github.com/714167887-svg/W`）：剥离 Halou 相关后只保留 jsq 等
- **HUAHUI**（独立 git 仓 `github.com/714167887-svg/huahui-jsq`）：沐新卫浴计算器网页

## 重组遗留（手动收尾）

- 本次 W 仓重组已 commit + push：`634bd63..68f014a`（删除 1037 个 Halou 相关条目，README 同步改写）
- Halou/ 下的 1.x 源码已**基于 v1.1.74**（之前曾是 1.1.68），Phase 2 改动（HalouSuite 2.0.2 + jsDelivr 端点）全部保留
- 备份分支：`W` 仓有 `backup-pre-relocate-2026-05-11`，需要时可还原

## 另一台机器远端同步影响

| 场景 | 影响 | 处理 |
|---|---|---|
| 另一台 `W` 仓 `git pull` | 会**收到 1037 个删除**，本地 ZK / autocad-clipboard / CAD Halou插件 / HalouSuiteUpdater / KB / OLE / JT / PB / 拆分钣金激光图 + 6 个 Halou .bat 全部消失 | 这台机器上的 [Halou/](.) 不在 git 跟踪中，**另一台机器拉完 W 后这些代码会真的没了**；需要另想办法搬过去 |
| 另一台有未推送的 ZK / autocad-clipboard 改动 | `git pull` 会因 modify/delete 冲突 | 先在另一台 `git stash` 或 push 上去（注意会和这次的 push 又一次冲突）→ 推荐：在搬迁前先全部推送 |
| jsq 改动 | 不受影响，仍在 `W/jsq/` | 正常 |
| HUAHUI 仓 | **完全不受影响**（独立 git 仓 `huahui-jsq`） | 正常同步 |
| halou-release 仓 | **完全不受影响**（独立 git 仓 `halou-release`） | 发版脚本路径用 `$PSScriptRoot`，搬迁后仍然正确 |

### 另一台机器同步推荐步骤（按顺序执行）

1. **关 acad**（避免锁文件）。
2. 另一台先把 W 仓本地未提交改动处理掉：`cd W; git stash` 或 commit/push（如果想保留，stash 更安全）。
3. `git pull` —— 这台会变得跟刚 push 的状态一致（jsq + 同步脚本，没有 Halou 相关）。
4. 把这台**Halou/ 整个目录**复制过去（推荐方式：U 盘 / SMB 共享 / robocopy 网络路径 / 或给 Halou 单独建 git 仓后 clone）。
5. 若之前 stash 过：去 [Halou/](.) 下对应路径恢复（`git stash pop` 在 W 里会失败，因为文件不存在了；需要手动从 stash 里 `git show stash@{0}:相对路径` 取出 patch，再应用到 Halou/）。

### 强烈推荐：给 Halou/ 单独建 git 仓

否则两台机器之间 Halou/ 没有版本控制，靠手工同步极易出错。建议：

```powershell
cd Halou
git init
# 在 GitHub 建一个空仓（如 https://github.com/714167887-svg/Halou.git）
git remote add origin https://github.com/714167887-svg/Halou.git
# 简单 .gitignore（排除 acad 临时文件、py cache、二进制产物）
@"
__pycache__/
*.pyc
*.dwl
*.dwl2
*.bak
*.old
*.tmp
*.exe
ZK/_frames/
ZK/*.mp4
JT/*.png
PB/_tmp/
PB/11/
HalouSuite/**/dist/
autocad-clipboard-plugin-autoon-test/dist/
CAD Halou插件/release/
"@ | Set-Content .gitignore -Encoding UTF8
git add -A
git commit -m "init Halou repo (relocated from W on 2026-05-11, based on v1.1.74 + Phase 2 v2.0.2)"
git push -u origin main
```

之后另一台机器直接 `git clone https://github.com/714167887-svg/Halou.git` 即可。

