# Halou Suite — 常见问题排查

按"症状 → 根因 → 处理"的次序整理。最右栏给出**最快操作**，方便发给客户复制粘贴。

---

## 1. 面板显示「无法联网校验：无效的 JSON 基元」

**根因**：客户 dll ≤ 1.1.42，且 license.json 带 UTF-8 BOM（`EF BB BF`），.NET 的 `JavaScriptSerializer` 不剥 BOM 直接报"无效 JSON 基元"。

**处理**：升级到 1.1.43+。dll 解析前主动剥 BOM；license.json 已重写为无 BOM。

**最快操作**：
> 完全关闭 CAD → 替换 `%LOCALAPPDATA%\HalouSuite\bin\JsqClipboardCadPlugin.dll` 为 1.1.43 → 重启 CAD。

---

## 2. 面板显示「无法联网校验：The remote name could not be resolved」

**根因**：DNS 污染，客户机器解析不到 `raw.githubusercontent.com`。

**处理**：
- 1.1.41+ 已自动 fallback 到 `cdn.jsdelivr.net`（国内可达性更好）；先确认客户装的是 1.1.41+。
- 仍不行 → 让客户改 DNS 为 `223.5.5.5` / `1.1.1.1`，或用代理。

---

## 3. 面板显示「无法联网校验：Could not establish trust」/ TLS 错误

**根因**：客户机器装了某些 SSL 拦截工具（360/火绒/企业代理），或 .NET 走老 TLS 协议被 GitHub 拒绝。

**处理**：1.1.39+ 在 `Initialize()` 强制启用 TLS 1.2/1.1。1.1.41+ 还可 fallback 到 PowerShell / curl 子进程，绕过 .NET 的 TLS 栈。

---

## 4. 面板版本号停留在某个旧版本不更新

**根因**：dll 文件被某个进程占用 / 在多个路径都有 dll，更新只覆盖了一处。

**处理**：用 HalouSuiteUpdater.exe 一键扫描所有路径替换。或手动检查这些位置：
- `%LOCALAPPDATA%\HalouSuite\bin\` ← **客户主路径**
- `%APPDATA%\Autodesk\ApplicationPlugins\*\Contents\Windows\`
- `%PROGRAMDATA%\Autodesk\ApplicationPlugins\*\Contents\Windows\`
- `C:\Program Files\Autodesk\ApplicationPlugins\*\Contents\Windows\`

---

## 5. JT 出来的图是黑底

**根因**：客户用了 1.1.37 / 1.1.38（直接 PNGOUT 走当前模型背景）。

**处理**：升 1.1.39+。命令开始时切白底、结束自动还原。

---

## 6. JT 命令开始/结束时画面闪一下

**说明**：这是切换模型空间背景色的物理副作用，**无法消除**。1.1.40 起把 `VTENABLE` 临时设 0 抑制 ZOOM 过渡动画，已最大限度减闪。

---

## 7. JT 在工作目录留下 `JT*.png` 文件

**根因**：客户在 1.1.37 及之前。

**处理**：升 1.1.38+。改用 `%TEMP%\halou-jt\` 缓存目录；下一次 JT 自动清掉上一轮文件。

> 为何不在命令结束立刻删？因为 CF_HDROP 剪贴板是**路径引用**而非文件内容，微信/QQ 是异步读取的——立刻删会让粘贴失败。

---

## 8. 客户卡在 1.1.37 不更新（典型死锁）

**根因链**：1.1.37 dll 没有 TLS 1.2 fix → .NET 默认 TLS 1.0 被 GitHub 拒绝 → license.json 拉不到 → 自更新永远失效 → 面板永远显示 1.1.37。

**处理**（按可达性递进）：
1. 让客户跑 `HalouSuiteUpdater.exe`（v2+ 自带 jsDelivr 镜像兜底，几乎一定能拉到 1.1.43）。
2. 跑不动 → 微信发给他 1.1.43 dll，让他手动替换。

---

## 9. 客户报告「无法联网校验：内容校验失败 (curl)：<html>...」

**说明**：这是 1.1.42+ 的新错误形式。意思是：

> WebClient/PowerShell/curl 三条通道、raw/jsDelivr 两个源、共 6 路尝试，**所有路径返回的都是 HTML 而非 JSON**。

**根因**：客户网络对所有 GitHub 资源都做了内容劫持。

**处理**：让客户切手机热点验证；或微信直接发 dll。

---

## 10. 让我给客户应急包

**最快**：

```powershell
# 已有最新 dll，跳过编译直接打包
e:\halou wode\W\CAD Halou插件\release.ps1 -NewVersion 1.1.43 -Notes "..." -SkipPush
# 或者只复制 dll：
Copy-Item e:\halou-release\release\JsqClipboardCadPlugin.dll $env:USERPROFILE\Desktop\
```

附带[操作说明](#操作说明文案)。

---

## 操作说明文案（直接复制给客户）

```
Halou Suite 升级（手动替换 dll，1 分钟搞定）
=============================================
1. 完全关闭 AutoCAD（任务管理器无 acad.exe）
2. Win+R → 粘贴：%LOCALAPPDATA%\HalouSuite\bin → 回车
3. 把我发的 JsqClipboardCadPlugin.dll 拖进去 → 选"替换目标中的文件"
4. 重启 AutoCAD → HALOU → 检查更新，应显示新版本号、授权正常
```

---

## 诊断信息收集模板

让客户复制粘贴以下命令到 PowerShell，把输出发回来：

```powershell
# 1. 当前装的 dll 版本和大小
Get-ChildItem "$env:LOCALAPPDATA\HalouSuite\bin\JsqClipboardCadPlugin.dll" |
    Select-Object FullName, Length, @{N='Version';E={$_.VersionInfo.ProductVersion}}

# 2. 测主源 / 镜像源是否可达 + 内容前 100 字
$urls = @(
    'https://raw.githubusercontent.com/714167887-svg/halou-release/main/license.json',
    'https://cdn.jsdelivr.net/gh/714167887-svg/halou-release@main/license.json'
)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]'Tls12,Tls11'
foreach ($u in $urls) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 -Uri $u
        Write-Host "OK  $u  status=$($r.StatusCode)  len=$($r.Content.Length)"
        Write-Host "    head:" ($r.Content.Substring(0, [Math]::Min(80, $r.Content.Length)))
    } catch {
        Write-Host "ERR $u  $($_.Exception.Message)"
    }
}
```
