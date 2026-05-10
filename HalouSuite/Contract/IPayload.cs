using System;

namespace HalouSuite.Contract
{
    /// <summary>
    /// 可热替换业务包必须实现的统一入口。
    /// Host 通过反射 (Activator.CreateInstance) 创建，名字硬编码为 "HalouSuite.Payload.PayloadEntry"。
    /// 该接口不能引用 AutoCAD 类型，所有 LISP 参数在 Host 层解析后以基础类型传入。
    /// </summary>
    public interface IPayload : IDisposable
    {
        /// <summary>语义版本号，由 Payload 自报，托盘和气泡通知会显示。</summary>
        string Version { get; }

        /// <summary>
        /// Payload 要求的最低 Host API Level。Host.HostApiLevel &lt; 此值时拒绝加载并回退到 LKG。
        /// 这条属性由 Host 在 Activate 之前读取，因此实现里禁止访问 Activate 后才有的字段。
        /// </summary>
        int RequiredHostApiLevel { get; }

        /// <summary>由 Host 在加载/重载完成后调用一次。负责创建 PaletteSet / TrayItem / HotKey 等所有 UI 资源。</summary>
        void Activate(IHostServices host);

        // ===== Command 转发面（对应原 [CommandMethod]） =====
        void ShowPalette();
        void TogglePalette();
        void RefreshManifest(bool manual);
        void RunFeatureById(string featureId);
        void HookPasteClip(bool silent);
        void UnhookPasteClip();
        void PasteFromClipboard();
        /// <summary>PASTECLIP 重定向：返回 false 表示不是 JSQ 格式，Host 应回落原生 PASTECLIP。</summary>
        bool PasteClipOverrideHandled();
        void PasteFromFile();

        /// <summary>halou-auth LISP：检查某 feature 当前是否被授权。</summary>
        bool IsFeatureAuthorized(string featureId);

        // ===== JT LISP 转发面（对应原 [LispFunction]） =====
        // 这些签名与 1.1.74 的 LISP API 语义对齐：
        //   (jt-crop-white path [tol])      → 就地裁切，不写新文件
        //   (jt-upscale-png path [target])  → 就地放大，不写新文件
        //   (jt-merge-png-h out p1 p2 ... [gap])
        //   (jt-plot-png out x1 y1 x2 y2 [media])
        bool JtEmbedDwg(string pngPath, string dwgPath);
        bool JtExtractDwg(string pngPath, string outDwgPath);
        bool JtCropWhite(string pngPath, int tolerance);
        bool JtUpscalePng(string pngPath, int targetLongEdge);
        bool JtPngToClipboard(string pngPath);
        bool JtMergePngHorizontal(string outPath, string[] inputPngs, int gap);
        bool JtPngsToClipboard(string[] pngPaths);
        bool JtPlotPng(string outPath, double x1, double y1, double x2, double y2, string media);
    }
}
