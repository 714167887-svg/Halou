# Phase 2 迁移清单 — 把 1.1.74 业务从 JsqClipboardCadPlugin 搬入 HalouPayload

> 文档自动生成于 2026-05-10。基于盘点 4 个维度（命令/服务/状态/初始化链）。
> 1.1.74 源码：`W/autocad-clipboard-plugin-autoon-test/src/`
> 目标：`W/HalouSuite/Payload/`

---

## 0. 总览

- **10 个命令** + **9 个 LISP 函数** = 19 个对外接口（已在 IPayload 占位）
- **14 个长生命周期对象**（PaletteSet、托盘、热键、拖拽、定时器、Manifest、License…）
- **13 个持久化资源**（config.json、manifest 缓存、注册表 AutoStart…）
- **28 个源文件**，共 ~5000+ 行 C#

## 1. 迁移分层（决定每段代码归属 Host 还是 Payload）

| 留在 Host（永驻、不可重启） | 进 Payload（可热替换） |
|------------------------------|-------------------------|
| `[CommandMethod]` 注册壳 ✅ 已做 | 命令实现体 |
| `[LispFunction]` 注册壳 ✅ 已做（HostLispBridge） | LISP 实现体 |
| Payload 加载/卸载/版本协商 ✅ 已做 | PaletteSet / 托盘 / 热键 / 拖拽 |
| `IHostServices` 提供能力面 ✅ 已做 | 业务状态、Manifest、License、定时器 |
| **AutoStart 注册表写入**（注：当前由 install 脚本管，不在 DLL 内）| 配置读写、缓存 |
| **更新守护进程触发**（需要 Host 知道关 acad 后 swap 哪个文件） | 远程下载逻辑（可在 Payload 完成下载、Host 决定是否 swap） |

## 2. 迁移批次（按依赖与风险递增）

### Batch A — 无 UI、无 acad API：纯函数（最安全，先做）
| 任务 | 源文件 | 目标 | 风险 |
|------|--------|------|------|
| A1 | `JtPngEmbed.cs` + `RobustHttp.cs` 中的纯函数 | Payload/Util/ | 无（纯 byte 操作） |
| A2 | `SuiteConfiguration.cs` `LicenseInfo.cs` `LicenseAccountInfo.cs` `LicenseStatus.cs` `PluginManifest.cs` `CadPluginFeature.cs` `CadEntityDefinition.cs` `CadLayerDefinition.cs` `CadClipboardPayload.cs` `HotkeyBinding.cs` `HotKeyModifiers.cs` `HotKeyPressedEventArgs.cs` | Payload/Models/ | 无（纯 POCO） |
| **验收** | 三个 DLL build 全绿；Payload 编译只引用 HalouContract.dll，未触碰 acmgd | — |

### Batch B — 9 个 jt-* LISP（PNG/DWG 图片处理，纯文件 I/O）
| 任务 | 源文件 | 目标 | 风险 |
|------|--------|------|------|
| B1 | `JtLispBridge.cs` 全部 9 个函数体 | Payload/PayloadEntry.Jt.cs（partial） | jt-plot-png 依赖 acdbmgd PlotEngine —— 需要 Payload 引用 acmgd |
| B2 | Host 已注册的 [LispFunction] 在 HostLispBridge 里转发 | 已就位 | 验证 LISP 调用链通 |
| **验收** | LSP 文件里 `(jt-crop-white "x.png" "y.png" 5 250)` 仍能工作 | — |

### Batch C — 5 个非 UI 的业务命令
| 任务 | 命令 | 源 | 目标 | 风险 |
|------|------|-----|------|------|
| C1 | JSQHOOKON / JSQHOOKOFF | ClipboardCommands | Payload/PasteHook.cs | 涉及 Internal.Utils 反射、PASTECLIP 重定义 |
| C2 | JSQPASTE / JSQPASTEFILE | ClipboardCommands | Payload/PasteCommands.cs | 实体注入（acdbmgd） |
| C3 | PASTECLIP override | Host 已经写好 fallback 链 | Payload 实现 PasteClipOverrideHandled() 真实逻辑 | Host 已 fallback ✓ |
| C4 | HALOUREFRESH | HalouSuiteManager | Payload/PayloadEntry.cs RefreshManifest | 触发远端下载 + UI 刷新 |
| **验收** | LISP `(jt-extract-dwg ...)` 仍能用；JSQPASTE 把剪贴板 JSON 还原成实体 | — |

### Batch D — UI 主体（PaletteSet + 托盘 + 控件）
| 任务 | 模块 | 源 | 目标 | 风险 |
|------|------|-----|------|------|
| D1 | SuitePaletteControl + 子控件 | SuitePaletteControl.cs HotkeyCaptureTextBox.cs | Payload/Ui/ | WinForms 组件 Dispose 链；多次 Activate 不能创建重复面板 |
| D2 | HalouSuiteManager.Ui.cs（EnsurePalette/EnsureTrayItem） | HalouSuiteManager.Ui.cs | Payload/PayloadServices.Ui.cs | acmgd PaletteSet/TrayItem |
| D3 | HotKeyWindow + ApplyHotKeyRegistration | HotKeyWindow.cs HalouSuiteManager.Ui.cs | Payload/Ui/ | Win32 RegisterHotKey；旧 instance Dispose 必须 UnregisterAll，否则下次注册冲突 |
| **验收** | HALOU 打开面板；面板上点击各功能按钮触发对应 RunFeatureById；Ctrl+Shift+~ 弹面板；HALOURELOAD 重载后旧面板消失新面板出现，无重复托盘 | — |

### Batch E — 文档级集成与拖拽（最易出问题）
| 任务 | 模块 | 源 | 目标 | 风险 |
|------|------|-----|------|------|
| E1 | DocumentActivated/DocumentCreated 订阅 | DocumentIntegration.cs | Payload/PayloadServices.Doc.cs | 反订阅必须严格对称；遗漏 → 旧 instance 还在收事件 → 双重处理或崩溃 |
| E2 | InstallImageDropTarget + RevokeDragDrop | HalouImageDropTarget.cs HalouDragDropInterop.cs | Payload/Drop/ | OLE COM 接管；Dispose 时必须 RevokeDragDrop 否则 CAD 子窗口拖拽全废 |
| E3 | OLE 辅助 ps1 + 嵌入资源解压 | EnsureOleHelperInTemp ExtractEmbeddedPayloads | Payload/Resources/ | 资源路径变了：以前在 DLL 同目录，现在 Payload dll 在 payloads/，应改用 Host.HostDirectory |
| **验收** | 文档切换时拖入图片仍能落到正确层；HALOURELOAD 后拖拽仍可用 | — |

### Batch F — License + AutoUpdate
| 任务 | 模块 | 源 | 目标 | 风险 |
|------|------|-----|------|------|
| F1 | License.cs（TryCheckLicense + IsFeatureAuthorized） | HalouSuiteManager.License.cs | Payload/License.cs | 只读外部 HTTP；可热替换；缓存放 ConfigDirectory |
| F2 | SelfUpdate（DownloadUpdate） | HalouSuiteManager.SelfUpdate.cs | **改成 Payload 下载、Host swap** | 守护进程批处理 swap 的目标改为 payload dll 文件名（语义版本路由） |
| F3 | HALOULKG/HALOUDISABLE/HALOUENABLE 已就位 ✅ | — | — | — |
| **验收** | 非授权功能在面板上灰显；版本检测能下载新 payload 到 payloads/ 并触发 HALOURELOAD | — |

### Batch G — CommandAliases（最棘手，留最后）
| 任务 | 模块 | 源 | 目标 | 风险 |
|------|------|-----|------|------|
| G1 | ApplyCommandAliases / TryRemoveAcadCommand | HalouSuiteManager.CommandAliases.cs | **保留在 Host**（因为 acad 命令表是进程级的，注销后再注册有时序问题）| 反射 Internal.Utils.AddCommand；Payload 通过 IHostServices 新增 RegisterAlias/UnregisterAlias 调用 |
| G2 | AutoLoad 文档标记 | DocumentIntegration.cs | Payload | 文档 UserData 标记的 key 不可冲突 |

### Batch H — 收尾
- H1：删除 Host 端遗留的 stub 提示文案
- H2：build-payload-installer.ps1：把 Host+Payload+Contract 三件套打包成新发行版
- H3：替代 install.ps1 的安装脚本（同时迁老用户 1.1.74 的 config.json）
- H4：把记忆库 `halou-suite-host-payload-2026-05.md` 升级到 v3 (Phase 2 完成)

## 3. 必须扩 Contract 的接口位（现在缺，Phase 2 期间补）

| 缺口 | 需要新加的接口 | 原因 |
|------|----------------|------|
| 命令别名注册 | `IHostServices.RegisterAlias(string alias, string featureId)` / `UnregisterAlias` | 别名不能放 Payload，否则 reload 时短暂没命令 |
| Document 事件 | `IHostServices.DocumentActivated/DocumentCreated`（包装 acad 事件，确保 Payload Dispose 时自动解绑）| 防订阅泄漏 |
| 状态栏写消息 | `IHostServices.WriteLine` ✅ 已有 | — |
| 嵌入资源根 | `IHostServices.HostDirectory` ✅ 已有 | OLE/ps1 同目录解压 |
| 版本提示气泡 | `IHostServices.ShowBalloonTip(string title, string body)` | 自动更新通知 |
| 配置变更广播 | Payload 内部事件，不进 Contract | — |

每加一个接口，`HostApiLevel` +1，旧 Payload 自动被拒绝。

## 4. 迁移期"双轨并存"策略

不准备一次性切换。每个 Batch 完成后立即烟测：
1. 部署新 Payload → HALOURELOAD → 验证该 Batch 的命令工作
2. 失败立即 HALOULKG 回滚到上个 Batch 的 LKG
3. 通过则 commit，记忆库追加 "Batch X 完成"

## 5. 风险红线

| 风险 | 触发 | 缓解 |
|------|------|------|
| Payload 卸载时漏 RevokeDragDrop | 拖拽接管未清理 | E2 Dispose 单元测试：模拟 reload 100 次后 RegisterDragDrop 仍成功 |
| HotKey 注册冲突 | 旧 Payload Dispose 没 Unregister | D3 必须先 UnregisterAll 再 Register |
| 别名注销失败 → 命令卡死 | Internal.Utils.RemoveCommand 反射失败 | G1 放 Host 而非 Payload，由 Host 在 reload 时统一管 |
| 守护进程 swap 错文件 | 1.1.74 改的是 JsqClipboardCadPlugin.dll，2.x 改的是 HalouPayload.<ver>.dll | F2 改写 apply-halou-update.bat，文件名作为参数传入 |
| 老用户 config.json 路径不变 | 1.1.74 写到 %AppData%\HalouCadSuite，Phase 2 也用同路径 | 直接复用，无迁移 |

## 6. Phase 2 验收标准（全部 Batch 完成后）

- [ ] 用户从 1.1.74 升级到 Host+Payload v2.0.0：注册表 LOADER 改一次，业务零行为差异
- [ ] HALOURELOAD 在 GUI 中无感切换 Payload，面板/托盘/拖拽全部不出错
- [ ] HALOULKG 一键回退到上个版本
- [ ] 投放不兼容 Payload，acad 启动后自动回退 LKG
- [ ] 9 个 jt-* LSP 在外部 acad 脚本中行为不变
- [ ] CAD 关闭时无残留进程、无残留 COM 注册
