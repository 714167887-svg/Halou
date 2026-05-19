using System;
using System.IO;
using HalouSuite.Contract;
using HalouSuite.Payload.Commands;
using HalouSuite.Payload.Jt;

namespace HalouSuite.Payload
{
    /// <summary>
    /// Phase 2 阶段入口。
    /// Batch A：POCO/Util 已迁移。
    /// Batch B：jt-* LISP 9 个已接通真实业务。
    /// Batch C：剪贴板 5 个命令已接通。
    /// Batch C-2：HalouSuiteManager 业务核心（manifest / license / 功能调度）已接通；
    ///            HALOU/HALOUTOGGLE 暂用 MessageBox 兜底，HALOUREFRESH/HALOUZK/HALOUKB 全功能可用。
    /// 待办：Batch D（Palette/Tray/HotKey）+ CommandAliases 动态别名 + DragDrop。
    /// </summary>
    public sealed class PayloadEntry : IPayload
    {
        public const string PayloadVersion = "2.0.56";
        private IHostServices _host;
        // 让 HalouSuiteManager 能在构造期间拿到 host（用于定位资源目录 PayloadDirectory），
        // 因为 host 通过 Assembly.Load(byte[]) 加载本 dll，导致 Assembly.Location 为空，
        // 不能再依赖 GetExecutingAssembly().Location 推断资源解压位置。
        internal static IHostServices CurrentHost { get; private set; }
        private HalouSuiteManager _suite;
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "HalouPayload.log");

        public string Version { get { return PayloadVersion; } }
        public int RequiredHostApiLevel { get { return 2; } }

        private void DiagWrite(string msg)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " [Payload " + PayloadVersion + "] " + msg + "\r\n");
            }
            catch { }
        }

        public void Activate(IHostServices host)
        {
            _host = host;
            CurrentHost = host;
            DiagWrite("Activate called. host=" + (host == null ? "(null)" : ("v" + host.HostVersion)));
            try
            {
                _suite = new HalouSuiteManager();
                _suite.Initialize();
                DiagWrite("HalouSuiteManager initialized, license=" + _suite.LicenseStatus);
            }
            catch (Exception ex)
            {
                DiagWrite("HalouSuiteManager Initialize FAIL: " + ex.Message);
            }
            if (_host != null)
            {
                _host.WriteLine("\n[HalouPayload " + PayloadVersion + "] Activated. Host v" +
                                host.HostVersion + ", license=" +
                                (_suite != null ? _suite.LicenseStatus.ToString() : "(init failed)"));
            }
        }

        public void Dispose()
        {
            DiagWrite("Dispose called.");
            try { if (_suite != null) { _suite.Dispose(); _suite = null; } } catch { }
            try { if (_host != null) _host.WriteLine("\n[HalouPayload " + PayloadVersion + "] Disposed."); } catch { }
            _host = null;
            CurrentHost = null;
        }

        public void ShowPalette() { if (_suite != null) _suite.ShowPalette(); else Stub("HALOU"); }
        public void TogglePalette() { if (_suite != null) _suite.TogglePalette(); else Stub("HALOUTOGGLE"); }
        public void RefreshManifest(bool manual) { if (_suite != null) _suite.RefreshManifest(manual); else Stub("HALOUREFRESH"); }
        public void RunFeatureById(string featureId) { if (_suite != null) _suite.RunFeatureById(featureId); else Stub("RunFeature:" + featureId); }
        public void HookPasteClip(bool silent) { DiagWrite("HookPasteClip silent=" + silent); PasteImpl.HookPasteClip(silent); }
        public void UnhookPasteClip() { DiagWrite("UnhookPasteClip"); PasteImpl.UnhookPasteClip(); }
        public void PasteFromClipboard() { DiagWrite("PasteFromClipboard"); PasteImpl.PasteFromClipboard(); }
        public bool PasteClipOverrideHandled() { DiagWrite("PasteClipOverride"); return PasteImpl.PasteClipOverrideHandled(); }
        public void PasteFromFile() { DiagWrite("PasteFromFile"); PasteImpl.PasteFromFile(); }
        public bool IsFeatureAuthorized(string featureId)
        {
            if (_suite == null) return true; // fail-open before init
            return _suite.LicenseStatus != LicenseStatus.Denied && _suite.IsFeatureAllowed(featureId);
        }

        public bool JtEmbedDwg(string pngPath, string dwgPath) { return JtCommands.EmbedDwg(pngPath, dwgPath); }
        public bool JtExtractDwg(string pngPath, string outDwgPath) { return JtCommands.ExtractDwg(pngPath, outDwgPath); }
        public bool JtCropWhite(string pngPath, int tolerance) { return JtCommands.CropWhite(pngPath, tolerance); }
        public bool JtUpscalePng(string pngPath, int targetLongEdge) { return JtCommands.UpscalePng(pngPath, targetLongEdge); }
        public bool JtPngToClipboard(string pngPath) { return JtCommands.PngToClipboard(pngPath); }
        public bool JtMergePngHorizontal(string outPath, string[] inputPngs, int gap) { return JtCommands.MergePngHorizontal(outPath, inputPngs, gap); }
        public bool JtPngsToClipboard(string[] pngPaths) { return JtCommands.PngsToClipboard(pngPaths); }
        public bool JtPlotPng(string outPath, double x1, double y1, double x2, double y2, string media) { return JtCommands.PlotPng(outPath, x1, y1, x2, y2, media); }

        private void Stub(string what)
        {
            DiagWrite("Stub: " + what);
            try { if (_host != null) _host.WriteLine("\n[HalouPayload stub] 收到 " + what + " —— 阶段 2 才会接上真实逻辑"); } catch { }
        }
    }
}
