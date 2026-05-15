using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using Clipboard = System.Windows.Forms.Clipboard;
using DrawingColor = System.Drawing.Color;
using DrawingFont = System.Drawing.Font;
using Keys = System.Windows.Forms.Keys;

namespace HalouSuite.Payload
{
    // partial class 拆分（v1.1.43 之后的结构整理，仅拆分文件，不改运行逻辑）：
    //   HalouSuiteManager.cs                       —— 字段/常量、构造、Initialize/Dispose、共享工具
    //   HalouSuiteManager.License.cs               —— 授权检查、功能白名单
    //   HalouSuiteManager.SelfUpdate.cs            —— DLL 自更新、AutoCAD 自启动注册
    //   HalouSuiteManager.Configuration.cs         —— 配置/清单刷新、功能调度执行
    //   HalouSuiteManager.Ui.cs                    —— Palette/Tray/HotKey 交互
    //   HalouSuiteManager.CommandAliases.cs        —— 动态命令别名（反射 + LISP 兜底）
    //   HalouSuiteManager.DocumentIntegration.cs   —— 拖拽接管 + 文档级 AutoLoad
    internal sealed partial class HalouSuiteManager : IDisposable
    {
        // ===== 通用常量 =====
        private const string PaletteTitle = "Halou 插件集合";
        private const string StatusPrefix = "Halou Suite";
        private const string ProtectedPayloadResourcePrefix = "ProtectedPayload.";
        private const string LegacyPayloadResourcePrefix = "Payload.";
        private const string ProtectedPayloadResourceKey = "HalouSuite.Payload.Resources.2026-05";
        // v2.0.17：刷新最小间隔从 60s 提到 300s（5 分钟），避免每分钟一次同步网络阻塞 UI
        private const int MinimumRefreshSeconds = 300;
        // Phase 2 起，CurrentVersion 与 PayloadEntry.PayloadVersion 同步
        // （1.1.68 是 Phase 2 拆分前 OLD host 末版本号，保留为历史参考）
        public const string CurrentVersion = PayloadEntry.PayloadVersion;
        public const string DefaultLicenseEndpoint =
            "https://raw.githubusercontent.com/714167887-svg/halou-release/main/license.json";

        // ===== 路径 / 资源 =====
        private readonly string _assemblyDirectory;
        private readonly string _configDirectory;
        private readonly string _configPath;
        private readonly string _manifestCachePath;
        private readonly string _localManifestPath;
        private readonly Icon _suiteIcon;
        private readonly Guid _paletteId = new Guid("E2F6F5D0-4B0B-4C36-89C7-0DF25839E3F1");

        // ===== 运行时状态 =====
        private bool _initialized;
        private PaletteSet _paletteSet;
        private SuitePaletteControl _paletteControl;
        private TrayItem _trayItem;
        private HotKeyWindow _hotKeyWindow;
        private Dictionary<int, string> _hotKeyFeatureMap;
        private System.Windows.Forms.Timer _refreshTimer;
        private SuiteConfiguration _configuration;
        private PluginManifest _manifest;
        private string _statusMessage;
        private LicenseStatus _licenseStatus = LicenseStatus.Unknown;
        private string _licenseMessage = "尚未检查";
        // 当前账号允许使用的功能 Id 集合；null 或包含 "*" 表示不限制
        private HashSet<string> _allowedFeatures;
        private string _latestVersion = CurrentVersion;
        private string _latestDownloadUrl;
        private string _releaseNotes;

        // ===== 各领域专属常量 / 字段（保持集中以便阅读全貌） =====
        // 自启动注册（SelfUpdate 文件使用）
        private const string AutoStartAppName = "HalouSuite";

        // 命令别名（CommandAliases 文件使用）
        private const string DynamicCommandGroup = "HALOU_DYNAMIC";
        private readonly List<string> _registeredAliases = new List<string>();

        // 拖拽接管（DocumentIntegration 文件使用）
        private readonly Dictionary<IntPtr, HalouImageDropTarget> _installedDropTargets
            = new Dictionary<IntPtr, HalouImageDropTarget>();
        // v2.0.18：DocumentActivated 高频触发，用 Timer 防抖（合并 500ms 内多次调用）
        private System.Windows.Forms.Timer _dropInstallTimer;

        // AutoLoad（DocumentIntegration 文件使用）
        private const string AutoLoadDocFlag = "HalouSuite.AutoLoaded";

        public HalouSuiteManager()
        {
            // 资源目录选择优先级：
            //   1) host.PayloadDirectory（Host 通过 Assembly.Load(byte[]) 加载本 dll，所以本 dll Location 为空，
            //      必须问 Host 才能拿到真实 dll 所在目录 %LOCALAPPDATA%\HalouSuite\payloads\）
            //   2) Assembly.GetExecutingAssembly().Location（兼容旧路径：直接 LoadFrom 文件）
            //   3) AppDomain.BaseDirectory（兜底；在 acad 里就是 acad.exe 所在目录，写权限受限，仅作为最后回退）
            string asmDir = null;
            try
            {
                var host = PayloadEntry.CurrentHost;
                if (host != null && !string.IsNullOrWhiteSpace(host.PayloadDirectory))
                {
                    asmDir = host.PayloadDirectory;
                }
            }
            catch { }
            if (string.IsNullOrEmpty(asmDir))
            {
                try
                {
                    var loc = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(loc))
                    {
                        asmDir = Path.GetDirectoryName(loc);
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(asmDir))
            {
                asmDir = AppDomain.CurrentDomain.BaseDirectory;
            }
            _assemblyDirectory = asmDir;
            _configDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HalouCadSuite");
            _configPath = Path.Combine(_configDirectory, "config.json");
            _manifestCachePath = Path.Combine(_configDirectory, "manifest-cache.json");
            _localManifestPath = Path.Combine(_assemblyDirectory ?? "", "halou-plugin-manifest.json");
            _suiteIcon = BuildSuiteIcon();
        }

        public void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_configDirectory);
            ExtractEmbeddedPayloads();
            CleanupPlaintextPayloadFiles();
            EnsureOleHelperInTemp();
            _configuration = SuiteConfiguration.Load(_configPath, _localManifestPath);
            _manifest = PluginManifest.Load(_configuration, _localManifestPath, _manifestCachePath, out _statusMessage);

            EnsurePalette();
            EnsureTrayItem();
            EnsureHotKeyWindow();
            ApplyHotKeyRegistration();
            ApplyCommandAliases();
            EnsureRefreshTimer();
            UpdatePaletteView();
            InstallImageDropTarget();

            // 绘图区 IDropTarget 在首个文档激活时才注册；首挂可能扫不到子窗口。
            // 订阅 DocumentActivated，每次切换文档都重挂一次，确保接管生效。
            try
            {
                Application.DocumentManager.DocumentActivated += OnDocumentActivatedForDrop;
            }
            catch { }

            // 订阅 AutoLoad 事件：新建/打开/激活文档时自动加载开了开关的 LSP。
            try
            {
                Application.DocumentManager.DocumentCreated += OnDocumentCreatedForAutoLoad;
                Application.DocumentManager.DocumentActivated += OnDocumentActivatedForAutoLoad;
            }
            catch { }

            // 对启动时已存在的当前文档立即跑一次 AutoLoad。
            try
            {
                Document curDoc = Application.DocumentManager.MdiActiveDocument;
                if (curDoc != null) AutoLoadFeaturesForDocument(curDoc);
            }
            catch { }

            _initialized = true;
            WriteMessage(string.Format(
                "{0} v{1} 已加载。热键 {2}，命令 HALOU 打开插件集合。",
                StatusPrefix,
                CurrentVersion,
                _configuration.Hotkey));

            ResumePendingUpdateWatcher();
            // v2.0.17：授权校验是同步网络（最坏 30s+），扔到后台线程，避免阻塞 acad 启动
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { TryCheckLicense(silent: true); } catch { }
                MarshalToUi(() =>
                {
                    try { UpdatePaletteView(); } catch { }
                });
            });
        }

        // 把回调 marshal 回 palette 所在的主线程（acad UI 线程）。
        // 如果 palette 还没建好，就丢弃这次刷新（下次 timer 会再来）。
        private void MarshalToUi(Action action)
        {
            if (action == null) return;
            try
            {
                var ctrl = _paletteControl;
                if (ctrl != null && ctrl.IsHandleCreated && !ctrl.IsDisposed)
                {
                    ctrl.BeginInvoke((System.Windows.Forms.MethodInvoker)delegate
                    {
                        try { action(); } catch { }
                    });
                }
            }
            catch { }
        }

        // 上次守护进程若被中断（电脑重启/任务被杀），重新启动守护等待本次 CAD 会话结束后 swap
        private void ResumePendingUpdateWatcher()
        {
            try
            {
                string dir = _assemblyDirectory;
                if (string.IsNullOrWhiteSpace(dir)) return;
                string pending = Path.Combine(dir, "JsqClipboardCadPlugin.dll.new");
                string bat = Path.Combine(dir, "apply-halou-update.bat");
                if (!File.Exists(pending) || !File.Exists(bat)) return;

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c start \"HalouUpdate\" /b cmd /c \"" + bat + "\"");
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
        }

        private void ExtractEmbeddedPayloads()
        {
            // 只解压非敏感资源。LSP/PS1 自 v2.0.34 起以 ProtectedPayload.* 加密嵌入，
            // 不再落到 payloads\OLE/ZK/KB/JT 等可直读目录。
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] resNames = asm.GetManifestResourceNames();
                string dllPath = asm.Location;
                string dllHash = null;
                try
                {
                    dllHash = File.GetLastWriteTimeUtc(dllPath).Ticks.ToString();
                }
                catch { }

                foreach (string res in resNames)
                {
                    string targetDir;
                    string fileName;

                    // EmbeddedManifest.<filename>：解到 DLL 根目录（用于 manifest 自愈）
                    if (res.StartsWith("EmbeddedManifest.", StringComparison.Ordinal))
                    {
                        fileName = res.Substring("EmbeddedManifest.".Length);
                        targetDir = _assemblyDirectory;
                    }
                    // 旧包兼容：若仍存在明文 Payload.* 资源，只解压非 LSP/PS1；敏感脚本改为运行时临时解密加载。
                    else if (res.StartsWith(LegacyPayloadResourcePrefix, StringComparison.Ordinal))
                    {
                        string rel = res.Substring(LegacyPayloadResourcePrefix.Length);
                        int firstDot = rel.IndexOf('.');
                        if (firstDot <= 0) continue;
                        string subdir = rel.Substring(0, firstDot);
                        fileName = rel.Substring(firstDot + 1);
                        if (IsProtectedScriptFileName(fileName)) continue;
                        targetDir = Path.Combine(_assemblyDirectory, subdir);
                    }
                    else if (res.StartsWith(ProtectedPayloadResourcePrefix, StringComparison.Ordinal)) continue;
                    else continue;

                    string targetPath = Path.Combine(targetDir, fileName);
                    string stampPath = targetPath + ".stamp";

                    // 已有同版本则跳过
                    if (File.Exists(targetPath) && File.Exists(stampPath))
                    {
                        string existing = null;
                        try { existing = File.ReadAllText(stampPath); } catch { }
                        if (existing != null && existing == dllHash) continue;
                    }

                    Directory.CreateDirectory(targetDir);
                    using (Stream s = asm.GetManifestResourceStream(res))
                    using (FileStream fs = File.Create(targetPath))
                    {
                        if (s != null) s.CopyTo(fs);
                    }
                    try { File.WriteAllText(stampPath, dllHash ?? string.Empty); } catch { }
                }
            }
            catch (System.Exception ex)
            {
                WriteMessage(string.Format("{0} 嵌入资源解压失败：{1}", StatusPrefix, ex.Message));
            }
        }

        private static bool IsProtectedScriptFileName(string fileName)
        {
            string ext = Path.GetExtension(fileName ?? string.Empty);
            return string.Equals(ext, ".lsp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".ps1", StringComparison.OrdinalIgnoreCase);
        }

        private void CleanupPlaintextPayloadFiles()
        {
            // 新版不再需要 payloads\OLE/ZK/KB/JT 下的明文源码。清掉旧版本遗留，避免客户直接浏览到 LISP。
            try
            {
                foreach (string sub in new[] { "OLE", "ZK", "KB", "JT" })
                {
                    string dir = Path.Combine(_assemblyDirectory, sub);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string pattern in new[] { "*.lsp", "*.ps1", "*.stamp" })
                    {
                        foreach (string file in Directory.GetFiles(dir, pattern))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }

                    try
                    {
                        if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                            Directory.Delete(dir, false);
                    }
                    catch { }
                }
            }
            catch { }

            CleanupRuntimePayloadFiles();
        }

        private void CleanupRuntimePayloadFiles()
        {
            try
            {
                string dir = GetProtectedRuntimeDirectory();
                if (!Directory.Exists(dir)) return;
                DateTime cutoff = DateTime.UtcNow.AddHours(-6);
                foreach (string file in Directory.GetFiles(dir, "halou-*.lsp"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private string GetProtectedRuntimeDirectory()
        {
            string temp = Environment.GetEnvironmentVariable("TEMP");
            if (string.IsNullOrWhiteSpace(temp)) temp = Path.GetTempPath();
            return Path.Combine(temp, "HalouSuite", "runtime");
        }

        private string BuildProtectedResourceName(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;
            string normalized = relativePath.Replace('\\', '/').Trim('/');
            string[] parts = normalized.Split('/');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) return null;
            return ProtectedPayloadResourcePrefix + parts[0] + "." + parts[1];
        }

        private Stream OpenProtectedResourceStream(string relativePath)
        {
            string resName = BuildProtectedResourceName(relativePath);
            if (string.IsNullOrWhiteSpace(resName)) return null;
            try { return Assembly.GetExecutingAssembly().GetManifestResourceStream(resName); }
            catch { return null; }
        }

        private bool HasProtectedPayloadResource(string relativePath)
        {
            using (Stream s = OpenProtectedResourceStream(relativePath))
            {
                return s != null;
            }
        }

        private byte[] ReadAllBytes(Stream stream)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private byte[] DecryptProtectedResource(Stream stream)
        {
            byte[] blob = ReadAllBytes(stream);
            if (blob.Length < 36) throw new InvalidDataException("受保护资源格式不完整。 ");
            string magic = Encoding.ASCII.GetString(blob, 0, 4);
            if (!string.Equals(magic, "HLR1", StringComparison.Ordinal) && !string.Equals(magic, "HLR2", StringComparison.Ordinal))
                throw new InvalidDataException("受保护资源标识不正确。 ");

            byte[] salt = new byte[16];
            byte[] iv = new byte[16];
            Buffer.BlockCopy(blob, 4, salt, 0, salt.Length);
            Buffer.BlockCopy(blob, 20, iv, 0, iv.Length);

            using (Rfc2898DeriveBytes derive = new Rfc2898DeriveBytes(ProtectedPayloadResourceKey, salt, 10000))
            using (AesManaged aes = new AesManaged())
            {
                byte[] aesKey = derive.GetBytes(32);
                byte[] hmacKey = derive.GetBytes(32);
                int cipherOffset = 36;
                int cipherLength = blob.Length - cipherOffset;
                if (string.Equals(magic, "HLR2", StringComparison.Ordinal))
                {
                    if (blob.Length < 68) throw new InvalidDataException("受保护资源校验信息不完整。 ");
                    cipherLength -= 32;
                    byte[] expected = new byte[32];
                    Buffer.BlockCopy(blob, blob.Length - 32, expected, 0, expected.Length);
                    byte[] body = new byte[blob.Length - 32];
                    Buffer.BlockCopy(blob, 0, body, 0, body.Length);
                    using (HMACSHA256 hmac = new HMACSHA256(hmacKey))
                    {
                        byte[] actual = hmac.ComputeHash(body);
                        if (!FixedTimeEquals(expected, actual)) throw new InvalidDataException("受保护资源校验失败。 ");
                    }
                }

                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.IV = iv;
                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                using (MemoryStream input = new MemoryStream(blob, cipherOffset, cipherLength))
                using (CryptoStream crypto = new CryptoStream(input, decryptor, CryptoStreamMode.Read))
                using (MemoryStream plain = new MemoryStream())
                {
                    crypto.CopyTo(plain);
                    return plain.ToArray();
                }
            }
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private string WriteProtectedResourceToRuntime(string relativePath, string extension)
        {
            using (Stream s = OpenProtectedResourceStream(relativePath))
            {
                if (s == null) return null;
                byte[] plain = DecryptProtectedResource(s);
                string dir = GetProtectedRuntimeDirectory();
                Directory.CreateDirectory(dir);
                string ext = string.IsNullOrWhiteSpace(extension) ? Path.GetExtension(relativePath) : extension;
                if (string.IsNullOrWhiteSpace(ext)) ext = ".tmp";
                string path = Path.Combine(dir, "halou-" + Guid.NewGuid().ToString("N") + ext);
                File.WriteAllBytes(path, plain);
                return path;
            }
        }

        private bool TryBuildLispLoadExpression(string relativePath, bool deleteAfterLoad, out string expression)
        {
            expression = null;
            string runtimePath = null;
            if (HasProtectedPayloadResource(relativePath))
            {
                runtimePath = WriteProtectedResourceToRuntime(relativePath, ".lsp");
            }
            else
            {
                string loadPath = ResolveManifestPath(relativePath);
                if (!string.IsNullOrWhiteSpace(loadPath) && File.Exists(loadPath)) runtimePath = loadPath;
            }

            if (string.IsNullOrWhiteSpace(runtimePath) || !File.Exists(runtimePath)) return false;

            string escaped = runtimePath.Replace('\\', '/').Replace("\"", "\\\"");
            if (deleteAfterLoad && HasProtectedPayloadResource(relativePath))
            {
                expression = string.Format("(progn (load \"{0}\") (vl-catch-all-apply 'vl-file-delete (list \"{0}\")) (princ))", escaped);
            }
            else
            {
                expression = string.Format("(progn (load \"{0}\") (princ))", escaped);
            }
            return true;
        }

        // 把 OLE 辅助 PS1 复制到 %TEMP%，保证 LSP 里 oleimg:helper-path 的 TEMP 分支 100% 命中，
        // 避免 *load-truename* 在某些加载场景下未设置导致的"未找到辅助脚本"。
        private void EnsureOleHelperInTemp()
        {
            try
            {
                string temp = Environment.GetEnvironmentVariable("TEMP");
                if (string.IsNullOrWhiteSpace(temp)) temp = Path.GetTempPath();
                string dst = Path.Combine(temp, "oleimgdir-clipboard.ps1");
                if (HasProtectedPayloadResource("OLE/oleimgdir-clipboard.ps1"))
                {
                    using (Stream s = OpenProtectedResourceStream("OLE/oleimgdir-clipboard.ps1"))
                    {
                        if (s != null) File.WriteAllBytes(dst, DecryptProtectedResource(s));
                    }
                    return;
                }

                string src = Path.Combine(_assemblyDirectory, "OLE", "oleimgdir-clipboard.ps1");
                if (!File.Exists(src)) return;
                if (!File.Exists(dst) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dst))
                {
                    File.Copy(src, dst, true);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            // 注销动态命令别名
            foreach (string alias in _registeredAliases)
            {
                try { TryRemoveAcadCommand(DynamicCommandGroup, alias); } catch { }
            }
            _registeredAliases.Clear();

            UninstallImageDropTarget();
            try { Application.DocumentManager.DocumentActivated -= OnDocumentActivatedForDrop; } catch { }
            try { Application.DocumentManager.DocumentCreated -= OnDocumentCreatedForAutoLoad; } catch { }
            try { Application.DocumentManager.DocumentActivated -= OnDocumentActivatedForAutoLoad; } catch { }

            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
                _refreshTimer = null;
            }

            if (_dropInstallTimer != null)
            {
                try { _dropInstallTimer.Stop(); } catch { }
                try { _dropInstallTimer.Dispose(); } catch { }
                _dropInstallTimer = null;
            }

            if (_hotKeyWindow != null)
            {
                _hotKeyWindow.HotKeyPressed -= OnHotKeyPressed;
                _hotKeyWindow.Dispose();
                _hotKeyWindow = null;
            }

            if (_trayItem != null)
            {
                _trayItem.MouseDown -= OnTrayItemMouseDown;
                _trayItem.Deleted -= OnTrayItemDeleted;
                StatusBar statusBar = Application.StatusBar;
                if (statusBar != null && statusBar.TrayItems.Contains(_trayItem))
                {
                    statusBar.TrayItems.Remove(_trayItem);
                    statusBar.Update();
                }

                _trayItem.Dispose();
                _trayItem = null;
            }

            if (_paletteControl != null)
            {
                _paletteControl.Dispose();
                _paletteControl = null;
            }

            if (_paletteSet != null)
            {
                _paletteSet.Visible = false;
                _paletteSet.Dispose();
                _paletteSet = null;
            }
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                Initialize();
            }
        }

        // ===== 共享工具：路径解析、日志、对话框、图标 =====

        private string ResolveManifestPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            if (Path.IsPathRooted(path))
            {
                return path;
            }

            // 相对路径总是相对 DLL 目录解析：嵌入资源固定解压到 DLL 旁，
            // 不受 manifest.BaseDirectory（可能来自远程/开发机绝对路径）影响。
            // 如果 DLL 旁找不到，再回退到 manifest.BaseDirectory 兼容历史布局。
            string primary = Path.GetFullPath(Path.Combine(_assemblyDirectory, path));
            if (File.Exists(primary))
            {
                return primary;
            }

            string baseDirectory = _manifest != null && !string.IsNullOrWhiteSpace(_manifest.BaseDirectory)
                ? _manifest.BaseDirectory
                : _assemblyDirectory;
            string fallback = Path.GetFullPath(Path.Combine(baseDirectory, path));
            return File.Exists(fallback) ? fallback : primary;
        }

        private void ShowInfo(string title, string message, bool isError)
        {
            WriteMessage(string.Format("{0}: {1}", title, message));
            System.Windows.Forms.MessageBox.Show(
                message,
                title,
                System.Windows.Forms.MessageBoxButtons.OK,
                isError ? System.Windows.Forms.MessageBoxIcon.Error : System.Windows.Forms.MessageBoxIcon.Information);
        }

        private void WriteMessage(string message)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                doc.Editor.WriteMessage("\n" + message);
            }
        }

        private static Icon BuildSuiteIcon()
        {
            // v2.0.25：状态栏 TrayItem 标准尺寸 16x16；32px 显得过大违和。回到 16px。
            // v2.0.24：彻底回退到最简单可靠形式——单尺寸 Bitmap.GetHicon() + Icon.FromHandle。
            // multi-size ICO 流方式（v2.0.19~v2.0.23）反复出现 "请求的范围扩展超过了数组的结尾" 异常，
            // 在 acad 进程中复现率高（可能因 GDI+ 在 acad 的 STA 线程上对 PNG-ICO 解析路径有 bug）。
            using (Bitmap bmp = RenderSuiteCatBitmap(16))
            {
                IntPtr hIcon = bmp.GetHicon();
                // FromHandle 不拥有 handle；Clone 出独立 Icon 后 DestroyIcon 释放原 handle，
                // 避免 GDI handle 泄漏。
                using (Icon temp = Icon.FromHandle(hIcon))
                {
                    Icon clone = (Icon)temp.Clone();
                    DestroyIcon(hIcon);
                    return clone;
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        internal static Bitmap RenderSuiteCatBitmap(int size)
        {
            // v2.0.22：像素风格 —— 16x16 像素艺术模板 + NearestNeighbor 放大保留颗粒感
            using (Bitmap source = BuildPixelCatSource16())
            {
                Bitmap dest = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(dest))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    g.Clear(DrawingColor.Transparent);
                    g.DrawImage(source, new System.Drawing.Rectangle(0, 0, size, size),
                                0, 0, 16, 16, GraphicsUnit.Pixel);
                }
                return dest;
            }
        }

        private static Bitmap BuildPixelCatSource16()
        {
            // 调色板（0=透明, 1=outline, 2=fur, 3=innerEar, 4=eye, 5=nose, 6=cheek, 7=highlight）
            DrawingColor[] palette = new DrawingColor[]
            {
                DrawingColor.Transparent,
                DrawingColor.FromArgb(62, 45, 39),
                DrawingColor.FromArgb(248, 215, 165),
                DrawingColor.FromArgb(250, 170, 188),
                DrawingColor.FromArgb(40, 30, 30),
                DrawingColor.FromArgb(235, 130, 150),
                DrawingColor.FromArgb(252, 175, 180),
                DrawingColor.White,
            };

            // 16x16 像素阵 [y, x]
            byte[,] pix = new byte[16, 16]
            {
                { 0,0,1,1,0,0,0,0,0,0,0,0,1,1,0,0 }, // y=0  耳尖
                { 0,1,3,3,1,0,0,0,0,0,0,1,3,3,1,0 }, // y=1  内耳
                { 0,1,3,3,1,1,0,0,0,0,1,1,3,3,1,0 }, // y=2  内耳+耳根
                { 0,1,2,2,2,2,1,1,1,1,2,2,2,2,1,0 }, // y=3  耳根融入头顶
                { 0,1,2,2,2,2,2,2,2,2,2,2,2,2,1,0 }, // y=4  额头
                { 0,1,2,2,2,2,2,2,2,2,2,2,2,2,1,0 }, // y=5  额头
                { 0,1,2,2,7,4,2,2,2,2,4,7,2,2,1,0 }, // y=6  眼睛上半 + 高光
                { 0,1,2,2,4,4,2,2,2,2,4,4,2,2,1,0 }, // y=7  眼睛下半
                { 0,1,2,2,6,2,2,5,5,2,2,6,2,2,1,0 }, // y=8  腮红 + 鼻子
                { 0,1,2,2,6,2,2,1,1,2,2,6,2,2,1,0 }, // y=9  嘴中间
                { 0,1,2,2,2,2,1,2,2,1,2,2,2,2,1,0 }, // y=10 嘴两侧 ω
                { 0,0,1,2,2,2,2,2,2,2,2,2,2,1,0,0 }, // y=11 下颊变窄
                { 0,0,0,1,1,1,1,1,1,1,1,1,1,0,0,0 }, // y=12 下巴
                { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0 }, // y=13
                { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0 }, // y=14
                { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0 }, // y=15
            };

            Bitmap src = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    src.SetPixel(x, y, palette[pix[y, x]]);
                }
            }
            return src;
        }
    }
}
