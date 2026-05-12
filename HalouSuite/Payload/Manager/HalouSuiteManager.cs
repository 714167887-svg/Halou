using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
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
            // 将嵌入到 DLL 里的 LSP/辅助脚本解压到 DLL 旁的子目录，
            // 让买家更新 DLL 后不用单独推送资源文件。
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
                    // Payload.<subdir>.<filename>：解到 DLL 旁同名子目录
                    else if (res.StartsWith("Payload.", StringComparison.Ordinal))
                    {
                        string rel = res.Substring("Payload.".Length);
                        int firstDot = rel.IndexOf('.');
                        if (firstDot <= 0) continue;
                        string subdir = rel.Substring(0, firstDot);
                        fileName = rel.Substring(firstDot + 1);
                        targetDir = Path.Combine(_assemblyDirectory, subdir);
                    }
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

        // 把 OLE 辅助 PS1 复制到 %TEMP%，保证 LSP 里 oleimg:helper-path 的 TEMP 分支 100% 命中，
        // 避免 *load-truename* 在某些加载场景下未设置导致的"未找到辅助脚本"。
        private void EnsureOleHelperInTemp()
        {
            try
            {
                string src = Path.Combine(_assemblyDirectory, "OLE", "oleimgdir-clipboard.ps1");
                if (!File.Exists(src)) return;
                string temp = Environment.GetEnvironmentVariable("TEMP");
                if (string.IsNullOrWhiteSpace(temp)) temp = Path.GetTempPath();
                string dst = Path.Combine(temp, "oleimgdir-clipboard.ps1");
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
            // v2.0.18+：用 GDI+ 矢量风格绘制 + multi-size ICO（16/24/32/48/64），高 DPI 下也清晰。
            int[] sizes = new[] { 16, 24, 32, 48, 64 };
            List<byte[]> pngs = new List<byte[]>(sizes.Length);
            foreach (int s in sizes)
            {
                using (Bitmap bmp = RenderSuiteCatBitmap(s))
                using (MemoryStream pngMs = new MemoryStream())
                {
                    bmp.Save(pngMs, System.Drawing.Imaging.ImageFormat.Png);
                    pngs.Add(pngMs.ToArray());
                }
            }

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                // ICONDIR
                bw.Write((short)0);              // Reserved
                bw.Write((short)1);              // Type = icon
                bw.Write((short)sizes.Length);   // Count

                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int s = sizes[i];
                    bw.Write((byte)(s >= 256 ? 0 : s)); // width
                    bw.Write((byte)(s >= 256 ? 0 : s)); // height
                    bw.Write((byte)0);                  // colorCount
                    bw.Write((byte)0);                  // reserved
                    bw.Write((short)1);                 // planes
                    bw.Write((short)32);                // bitCount
                    bw.Write((int)pngs[i].Length);      // size
                    bw.Write((int)offset);              // offset
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) bw.Write(p);
                bw.Flush();
                ms.Position = 0;
                // 必须 new MemoryStream(copy) 否则 Icon ctor 持有引用 + 我们 dispose 会失效；
                // 但 new Icon(Stream) 文档说会读完即释放依赖，安全；如出问题改为 byte[] -> 新 stream。
                return new Icon(ms);
            }
        }

        internal static Bitmap RenderSuiteCatBitmap(int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.Clear(DrawingColor.Transparent);

                float s = size / 32f;
                DrawingColor outline = DrawingColor.FromArgb(62, 45, 39);
                DrawingColor fur = DrawingColor.FromArgb(238, 197, 133);
                DrawingColor innerEar = DrawingColor.FromArgb(244, 160, 176);
                DrawingColor eye = DrawingColor.FromArgb(30, 30, 30);
                DrawingColor nose = DrawingColor.FromArgb(230, 120, 140);
                DrawingColor cheek = DrawingColor.FromArgb(255, 220, 190);

                // 左右耳（先画耳朵，让头部覆盖耳根，更自然）
                PointF[] leftEar = { new PointF(4 * s, 13 * s), new PointF(8 * s, 2 * s), new PointF(13 * s, 11 * s) };
                PointF[] rightEar = { new PointF(28 * s, 13 * s), new PointF(24 * s, 2 * s), new PointF(19 * s, 11 * s) };
                using (SolidBrush b = new SolidBrush(outline))
                {
                    g.FillPolygon(b, leftEar);
                    g.FillPolygon(b, rightEar);
                }
                PointF[] leftEarInner = { new PointF(6.6f * s, 11.5f * s), new PointF(8.6f * s, 5.5f * s), new PointF(11.2f * s, 10.5f * s) };
                PointF[] rightEarInner = { new PointF(25.4f * s, 11.5f * s), new PointF(23.4f * s, 5.5f * s), new PointF(20.8f * s, 10.5f * s) };
                using (SolidBrush b = new SolidBrush(innerEar))
                {
                    g.FillPolygon(b, leftEarInner);
                    g.FillPolygon(b, rightEarInner);
                }

                // 头部椭圆
                using (SolidBrush b = new SolidBrush(fur))
                {
                    g.FillEllipse(b, 3 * s, 8 * s, 26 * s, 22 * s);
                }
                using (Pen pen = new Pen(outline, Math.Max(1f, 1.3f * s)))
                {
                    g.DrawEllipse(pen, 3 * s, 8 * s, 26 * s, 22 * s);
                }

                // 脸颊
                using (SolidBrush b = new SolidBrush(cheek))
                {
                    g.FillEllipse(b, 5.5f * s, 21.5f * s, 5.5f * s, 3.6f * s);
                    g.FillEllipse(b, 21 * s, 21.5f * s, 5.5f * s, 3.6f * s);
                }

                // 眼睛
                using (SolidBrush b = new SolidBrush(eye))
                {
                    g.FillEllipse(b, 9 * s, 16 * s, 3.6f * s, 4.6f * s);
                    g.FillEllipse(b, 19.4f * s, 16 * s, 3.6f * s, 4.6f * s);
                }
                using (SolidBrush b = new SolidBrush(DrawingColor.White))
                {
                    g.FillEllipse(b, 9.7f * s, 16.5f * s, 1.3f * s, 1.3f * s);
                    g.FillEllipse(b, 20.1f * s, 16.5f * s, 1.3f * s, 1.3f * s);
                }

                // 鼻子（倒三角，圆角效果靠抗锯齿 + 小尺寸即可）
                PointF[] noseTri = { new PointF(14.4f * s, 21 * s), new PointF(17.6f * s, 21 * s), new PointF(16 * s, 23 * s) };
                using (SolidBrush b = new SolidBrush(nose))
                {
                    g.FillPolygon(b, noseTri);
                }

                // 嘴：两段小弧
                using (Pen pen = new Pen(outline, Math.Max(1f, 1.1f * s)))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawArc(pen, 13 * s, 22.5f * s, 3 * s, 3 * s, 20, 140);
                    g.DrawArc(pen, 16 * s, 22.5f * s, 3 * s, 3 * s, 20, 140);
                }

                // 胡须（仅在 size >= 24 时画，避免 16px 太挤）
                if (size >= 24)
                {
                    using (Pen pen = new Pen(outline, Math.Max(0.8f, 0.7f * s)))
                    {
                        pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        g.DrawLine(pen, 2 * s, 22 * s, 7 * s, 22.6f * s);
                        g.DrawLine(pen, 2 * s, 24 * s, 7 * s, 24 * s);
                        g.DrawLine(pen, 25 * s, 22.6f * s, 30 * s, 22 * s);
                        g.DrawLine(pen, 25 * s, 24 * s, 30 * s, 24 * s);
                    }
                }
            }
            return bmp;
        }
    }
}
