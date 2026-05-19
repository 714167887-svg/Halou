using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using HalouSuite.Payload;
using DrawingColor = System.Drawing.Color;
using Clipboard = System.Windows.Forms.Clipboard;

// AutoCAD types only used inside JtPlotPng — wrapped via aliases to avoid
// polluting the rest of the file with using directives.
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;

namespace HalouSuite.Payload.Jt
{
    /// <summary>
    /// 从 1.1.74 版 JtLispBridge 移植过来的 jt-* 业务实现。
    /// Host 已经把 ResultBuffer 解析成基础类型再调用过来，这里只关心业务。
    /// 失败统一返回 false（Host 转成 nil），成功返回 true（Host 转成 T）。
    /// </summary>
    internal static class JtCommands
    {
        // -------- jt-embed-dwg / jt-extract-dwg --------
        public static bool EmbedDwg(string pngPath, string dwgPath)
        {
            try
            {
                if (string.IsNullOrEmpty(pngPath) || string.IsNullOrEmpty(dwgPath)) return false;
                if (!File.Exists(pngPath) || !File.Exists(dwgPath)) return false;
                byte[] dwg = File.ReadAllBytes(dwgPath);
                string b64 = Convert.ToBase64String(dwg);
                string payload = "1.0|" + b64;
                JtPngEmbed.WriteTextChunk(pngPath, JtPngEmbed.DefaultKeyword, payload);
                return true;
            }
            catch { return false; }
        }

        public static bool ExtractDwg(string pngPath, string outDwgPath)
        {
            try
            {
                if (string.IsNullOrEmpty(pngPath) || string.IsNullOrEmpty(outDwgPath)) return false;
                if (!File.Exists(pngPath)) return false;
                string text = JtPngEmbed.ReadTextChunk(pngPath, JtPngEmbed.DefaultKeyword);
                if (string.IsNullOrEmpty(text)) return false;
                int sep = text.IndexOf('|');
                if (sep < 0) return false;
                string b64 = text.Substring(sep + 1);
                byte[] dwg = Convert.FromBase64String(b64);
                File.WriteAllBytes(outDwgPath, dwg);
                return true;
            }
            catch { return false; }
        }

        // -------- jt-crop-white --------
        public static bool CropWhite(string pngPath, int tol)
        {
            try
            {
                if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath)) return false;
                using (var src = new Bitmap(pngPath))
                {
                    int w = src.Width, h = src.Height;
                    int top = 0, bottom = h - 1, left = 0, right = w - 1;
                    var bmpData = src.LockBits(new Rectangle(0, 0, w, h),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        int stride = bmpData.Stride;
                        byte[] buf = new byte[stride * h];
                        Marshal.Copy(bmpData.Scan0, buf, 0, buf.Length);
                        Func<int, bool> rowHasContent = delegate(int y)
                        {
                            int row = y * stride;
                            for (int x = 0; x < w; x++)
                            {
                                int p = row + x * 4;
                                byte b = buf[p], g = buf[p + 1], r = buf[p + 2];
                                if (r < tol || g < tol || b < tol) return true;
                            }
                            return false;
                        };
                        Func<int, bool> colHasContent = delegate(int x)
                        {
                            for (int y = 0; y < h; y++)
                            {
                                int p = y * stride + x * 4;
                                byte b = buf[p], g = buf[p + 1], r = buf[p + 2];
                                if (r < tol || g < tol || b < tol) return true;
                            }
                            return false;
                        };
                        while (top < h && !rowHasContent(top)) top++;
                        while (bottom > top && !rowHasContent(bottom)) bottom--;
                        while (left < w && !colHasContent(left)) left++;
                        while (right > left && !colHasContent(right)) right--;
                    }
                    finally { src.UnlockBits(bmpData); }

                    int cw = right - left + 1, ch = bottom - top + 1;
                    if (cw <= 1 || ch <= 1) return false;
                    if (cw == w && ch == h) return true; // 整张都是内容，无需裁剪

                    using (var dst = new Bitmap(cw, ch, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    using (var gfx = Graphics.FromImage(dst))
                    {
                        gfx.DrawImage(src, new Rectangle(0, 0, cw, ch),
                            new Rectangle(left, top, cw, ch), GraphicsUnit.Pixel);
                        string tmp = pngPath + ".crop.tmp.png";
                        dst.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                        gfx.Dispose();
                        dst.Dispose();
                        src.Dispose();
                        File.Delete(pngPath);
                        File.Move(tmp, pngPath);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        // -------- jt-upscale-png --------
        public static bool UpscalePng(string pngPath, int target)
        {
            try
            {
                if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath)) return false;
                if (target <= 0) target = 2400;
                int newW, newH;
                using (var src = new Bitmap(pngPath))
                {
                    int oldW = src.Width, oldH = src.Height;
                    int longEdge = Math.Max(oldW, oldH);
                    if (longEdge >= target) return true; // 已经够大
                    double scale = (double)target / longEdge;
                    newW = (int)Math.Round(oldW * scale);
                    newH = (int)Math.Round(oldH * scale);
                    string tmp = pngPath + ".up.tmp.png";
                    using (var dst = new Bitmap(newW, newH, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (var gfx = Graphics.FromImage(dst))
                        {
                            gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            gfx.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            gfx.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            gfx.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                            gfx.DrawImage(src, new Rectangle(0, 0, newW, newH));
                        }
                        dst.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    src.Dispose();
                    File.Delete(pngPath);
                    File.Move(tmp, pngPath);
                }
                return true;
            }
            catch { return false; }
        }

        // -------- jt-png-to-clipboard --------
        public static bool PngToClipboard(string pngPath)
        {
            try
            {
                if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath)) return false;
                var data = new System.Windows.Forms.DataObject();
                using (var img = System.Drawing.Image.FromFile(pngPath))
                {
                    var bmp = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var gfx = Graphics.FromImage(bmp))
                    {
                        gfx.Clear(DrawingColor.White);
                        gfx.DrawImage(img, 0, 0, img.Width, img.Height);
                    }
                    data.SetData(System.Windows.Forms.DataFormats.Bitmap, true, bmp);
                }
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch { return false; }
        }

        // -------- jt-merge-png-h --------
        public static bool MergePngHorizontal(string outPath, string[] inputPngs, int gap)
        {
            try
            {
                if (string.IsNullOrEmpty(outPath) || inputPngs == null) return false;
                var paths = new System.Collections.Generic.List<string>();
                foreach (var p in inputPngs)
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) paths.Add(p);
                }
                if (paths.Count == 0) return false;
                if (paths.Count == 1)
                {
                    File.Copy(paths[0], outPath, true);
                    return true;
                }

                if (gap < 0) gap = 0;
                var imgs = new System.Collections.Generic.List<Bitmap>();
                int maxH = 0;
                foreach (var p in paths)
                {
                    var bmp = new Bitmap(p);
                    imgs.Add(bmp);
                    if (bmp.Height > maxH) maxH = bmp.Height;
                }
                var resized = new System.Collections.Generic.List<Bitmap>();
                int totalW = 0;
                foreach (var src in imgs)
                {
                    int newW = (int)Math.Round((double)src.Width * maxH / src.Height);
                    var dst = new Bitmap(newW, maxH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.Clear(DrawingColor.White);
                        g.DrawImage(src, 0, 0, newW, maxH);
                    }
                    src.Dispose();
                    resized.Add(dst);
                    totalW += newW;
                }
                totalW += gap * (resized.Count - 1);

                using (var canvas = new Bitmap(totalW, maxH, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(DrawingColor.White);
                    int x = 0;
                    foreach (var b in resized)
                    {
                        g.DrawImage(b, x, 0);
                        x += b.Width + gap;
                    }
                    string tmp = outPath + ".merge.tmp.png";
                    canvas.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                    if (File.Exists(outPath)) File.Delete(outPath);
                    File.Move(tmp, outPath);
                }
                foreach (var b in resized) b.Dispose();
                return true;
            }
            catch { return false; }
        }

        // -------- jt-pngs-to-clipboard --------
        public static bool PngsToClipboard(string[] pngPaths)
        {
            try
            {
                if (pngPaths == null || pngPaths.Length == 0) return false;
                var files = new System.Collections.Specialized.StringCollection();
                var validPaths = new System.Collections.Generic.List<string>();
                foreach (var p in pngPaths)
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        files.Add(p);
                        validPaths.Add(p);
                    }
                }
                if (files.Count == 0) return false;

                var data = new System.Windows.Forms.DataObject();
                data.SetFileDropList(files);
                using (var img = System.Drawing.Image.FromFile(validPaths[0]))
                {
                    var bmp = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var gfx = Graphics.FromImage(bmp))
                    {
                        gfx.Clear(DrawingColor.White);
                        gfx.DrawImage(img, 0, 0, img.Width, img.Height);
                    }
                    data.SetData(System.Windows.Forms.DataFormats.Bitmap, true, bmp);
                }
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch { return false; }
        }

        // -------- jt-plot-png --------
        // v2.0.52: media 末尾若带 "|invert" sentinel，则出图成功后整张反色（白底黑线→黑底白线），
        //          用于"原底/黑底"模式得到"无留黑+视觉接近 CAD 黑底"的效果。
        //          这是为了不改 Contract/Host（常驻不可热更新）而走 in-band 通道。
        // v2.0.54: 异常曝光到 CAD 命令行 + 设备/介质 fallback 模糊匹配（兼容中文版 AutoCAD
        //          / 精简安装可能缺 "PublishToWeb PNG.pc3" 的情况）。
        public static bool PlotPng(string outPath, double x1, double y1, double x2, double y2, string media)
        {
            string diagPrefix = "[jt-plot-png] ";
            Action<string> log = (msg) =>
            {
                try
                {
                    var d = AcadApp.DocumentManager.MdiActiveDocument;
                    if (d != null) d.Editor.WriteMessage("\n" + diagPrefix + msg);
                }
                catch { }
            };
            try
            {
                if (string.IsNullOrEmpty(outPath)) { log("outPath 为空"); return false; }
                bool wantInvert = false;
                if (!string.IsNullOrEmpty(media))
                {
                    const string tag = "|invert";
                    if (media.EndsWith(tag, StringComparison.OrdinalIgnoreCase))
                    {
                        wantInvert = true;
                        media = media.Substring(0, media.Length - tag.Length).Trim();
                    }
                }
                if (string.IsNullOrEmpty(media)) media = "Sun Hi-Res (1600.00 x 1280.00 Pixels)";

                double xmin = Math.Min(x1, x2), xmax = Math.Max(x1, x2);
                double ymin = Math.Min(y1, y2), ymax = Math.Max(y1, y2);

                if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                { log("ProcessPlotState != NotPlotting，跳过 PLOT"); return false; }

                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null) { log("MdiActiveDocument == null"); return false; }
                var db = doc.Database;
                short bgOld = (short)AcadApp.GetSystemVariable("BACKGROUNDPLOT");
                AcadApp.SetSystemVariable("BACKGROUNDPLOT", (short)0);
                bool plotOk = false;
                try
                {
                    using (doc.LockDocument())
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        var layout = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);

                        var ps = new PlotSettings(layout.ModelType);
                        ps.CopyFrom(layout);
                        var psv = PlotSettingsValidator.Current;

                        // ---- 设备/介质 fallback ----
                        string chosenDevice = ResolvePngDevice(psv, ps, log);
                        if (string.IsNullOrEmpty(chosenDevice))
                        { log("找不到任何 PNG 输出设备（PublishToWeb PNG.pc3 / *PNG*.pc3）"); return false; }
                        string chosenMedia = ResolvePngMedia(psv, ps, chosenDevice, media, log);
                        if (string.IsNullOrEmpty(chosenMedia))
                        { log("找不到任何可用介质，原请求=" + media); return false; }
                        try { psv.SetPlotConfigurationName(ps, chosenDevice, chosenMedia); }
                        catch (Exception ex)
                        { log("SetPlotConfigurationName(" + chosenDevice + "," + chosenMedia + ") 失败: " + ex.Message); return false; }
                        psv.RefreshLists(ps);
                        psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                        psv.SetPlotWindowArea(ps,
                            new Extents2d(new Point2d(xmin, ymin), new Point2d(xmax, ymax)));
                        psv.SetUseStandardScale(ps, true);
                        psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                        psv.SetPlotCentered(ps, true);
                        psv.SetPlotRotation(ps, PlotRotation.Degrees000);

                        var pi = new PlotInfo();
                        pi.Layout = layout.ObjectId;
                        pi.OverrideSettings = ps;
                        var piv = new PlotInfoValidator();
                        piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                        piv.Validate(pi);

                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

                        using (var pe = PlotFactory.CreatePublishEngine())
                        {
                            pe.BeginPlot(null, null);
                            pe.BeginDocument(pi, doc.Name, null, 1, true, outPath);
                            var pageInfo = new PlotPageInfo();
                            pe.BeginPage(pageInfo, pi, true, null);
                            pe.BeginGenerateGraphics(null);
                            pe.EndGenerateGraphics(null);
                            pe.EndPage(null);
                            pe.EndDocument(null);
                            pe.EndPlot(null);
                        }
                        tr.Commit();
                        plotOk = true;
                        log("PLOT 成功 dev=" + chosenDevice + " media=" + chosenMedia + " invert=" + wantInvert);
                    }
                }
                catch (Exception exInner)
                {
                    log("内部异常: " + exInner.GetType().Name + ": " + exInner.Message);
                }
                finally
                {
                    AcadApp.SetSystemVariable("BACKGROUNDPLOT", bgOld);
                    if (!plotOk)
                    {
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    }
                }
                if (!plotOk) return false;
                if (!File.Exists(outPath)) { log("PLOT 报告成功但文件不存在: " + outPath); return false; }
                if (wantInvert && !InvertPng(outPath)) { log("反色失败"); return false; }
                return true;
            }
            catch (Exception exOuter)
            {
                log("外部异常: " + exOuter.GetType().Name + ": " + exOuter.Message);
                return false;
            }
        }

        // 选 PNG 输出设备：优先 PublishToWeb PNG.pc3；否则任何含 "PNG" 的 .pc3
        private static string ResolvePngDevice(PlotSettingsValidator psv, PlotSettings ps, Action<string> log)
        {
            const string preferred = "PublishToWeb PNG.pc3";
            try
            {
                var devices = psv.GetPlotDeviceList();
                foreach (var d in devices)
                {
                    if (string.Equals(d, preferred, StringComparison.OrdinalIgnoreCase)) return d;
                }
                foreach (var d in devices)
                {
                    if (d != null && d.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0
                        && d.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase))
                    { log("使用 fallback 设备: " + d); return d; }
                }
            }
            catch (Exception ex) { log("GetPlotDeviceList 失败: " + ex.Message); }
            return null;
        }

        // 选介质：优先 requested；否则任何含 "1600" 的；否则任何含 "Sun Hi-Res" 的；否则第一个
        private static string ResolvePngMedia(PlotSettingsValidator psv, PlotSettings ps, string device, string requested, Action<string> log)
        {
            try
            {
                // 先把 device 设上才能 GetCanonicalMediaNameList
                try { psv.SetPlotConfigurationName(ps, device, null); } catch { }
                psv.RefreshLists(ps);
                var medias = psv.GetCanonicalMediaNameList(ps);
                if (medias == null || medias.Count == 0) { log("device " + device + " 无可用介质"); return null; }
                foreach (var m in medias)
                {
                    if (string.Equals(m, requested, StringComparison.OrdinalIgnoreCase)) return m;
                }
                foreach (var m in medias)
                {
                    if (m != null && m.IndexOf("1600", StringComparison.Ordinal) >= 0)
                    { log("使用 fallback 介质(含1600): " + m); return m; }
                }
                foreach (var m in medias)
                {
                    if (m != null && m.IndexOf("Sun Hi-Res", StringComparison.OrdinalIgnoreCase) >= 0)
                    { log("使用 fallback 介质(Sun Hi-Res): " + m); return m; }
                }
                log("使用第一个可用介质: " + medias[0]);
                return medias[0];
            }
            catch (Exception ex) { log("ResolvePngMedia 失败: " + ex.Message); return null; }
        }

        // v2.0.52: 整张 PNG 像素反色（白↔黑、浅↔深），保留 alpha。
        private static bool InvertPng(string pngPath)
        {
            try
            {
                if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath)) return false;
                using (var src = new Bitmap(pngPath))
                {
                    int w = src.Width, h = src.Height;
                    var rect = new Rectangle(0, 0, w, h);
                    var data = src.LockBits(rect,
                        System.Drawing.Imaging.ImageLockMode.ReadWrite,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        int stride = data.Stride;
                        byte[] buf = new byte[stride * h];
                        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                        for (int y = 0; y < h; y++)
                        {
                            int row = y * stride;
                            for (int x = 0; x < w; x++)
                            {
                                int p = row + x * 4;
                                buf[p]     = (byte)(255 - buf[p]);     // B
                                buf[p + 1] = (byte)(255 - buf[p + 1]); // G
                                buf[p + 2] = (byte)(255 - buf[p + 2]); // R
                                // alpha 保持
                            }
                        }
                        Marshal.Copy(buf, 0, data.Scan0, buf.Length);
                    }
                    finally { src.UnlockBits(data); }
                    string tmp = pngPath + ".inv.tmp.png";
                    src.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                    src.Dispose();
                    File.Delete(pngPath);
                    File.Move(tmp, pngPath);
                }
                return true;
            }
            catch { return false; }
        }
    }
}
