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

namespace JsqClipboardCadPlugin
{
    public sealed class JtLispBridge
    {
        [LispFunction("jt-embed-dwg")]
        public ResultBuffer EmbedDwg(ResultBuffer args)
        {
            try
            {
                if (args == null) return null;
                var arr = args.AsArray();
                if (arr.Length < 2) return null;
                string pngPath = arr[0].Value as string;
                string dwgPath = arr[1].Value as string;
                if (string.IsNullOrEmpty(pngPath) || string.IsNullOrEmpty(dwgPath)) return null;
                if (!File.Exists(pngPath) || !File.Exists(dwgPath)) return null;

                byte[] dwg = File.ReadAllBytes(dwgPath);
                string b64 = Convert.ToBase64String(dwg);
                string payload = "1.0|" + b64;
                JtPngEmbed.WriteTextChunk(pngPath, JtPngEmbed.DefaultKeyword, payload);
                return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
            }
            catch (System.Exception ex)
            {
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n[jt-embed-dwg] " + ex.Message); } catch { }
                return null;
            }
        }

        [LispFunction("jt-extract-dwg")]
        public ResultBuffer ExtractDwg(ResultBuffer args)
        {
            try
            {
                if (args == null) return null;
                var arr = args.AsArray();
                if (arr.Length < 2) return null;
                string pngPath = arr[0].Value as string;
                string outDwgPath = arr[1].Value as string;
                if (string.IsNullOrEmpty(pngPath) || string.IsNullOrEmpty(outDwgPath)) return null;
                if (!File.Exists(pngPath)) return null;

                string text = JtPngEmbed.ReadTextChunk(pngPath, JtPngEmbed.DefaultKeyword);
                if (string.IsNullOrEmpty(text)) return null;
                int sep = text.IndexOf('|');
                if (sep < 0) return null;
                string b64 = text.Substring(sep + 1);
                byte[] dwg = Convert.FromBase64String(b64);
                File.WriteAllBytes(outDwgPath, dwg);
                return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
            }
            catch (System.Exception ex)
            {
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n[jt-extract-dwg] " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>
        /// (jt-crop-white "png-path" [tolerance]) → T/Nil
        [LispFunction("jt-crop-white")]
        public ResultBuffer CropWhite(ResultBuffer args)
        {
            try
            {
                if (args == null) return null;
                var arr = args.AsArray();
                if (arr.Length < 1) return null;
                string pngPath = arr[0].Value as string;
                if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath)) return null;
                int tol = 252;
                if (arr.Length >= 2 && arr[1].Value is short) tol = (short)arr[1].Value;
                else if (arr.Length >= 2 && arr[1].Value is int) tol = (int)arr[1].Value;
                else if (arr.Length >= 2 && arr[1].Value is double) tol = (int)(double)arr[1].Value;

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
                        // 行扫描函数
                        Func<int, bool> rowHasContent = (y) =>
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
                        Func<int, bool> colHasContent = (x) =>
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
                    if (cw <= 1 || ch <= 1) return null;
                    try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                        string.Format("\n[jt-crop-white] tol={0} 裁前 {1}x{2} → 裁后 {3}x{4} (left={5} top={6} right={7} bottom={8})",
                            tol, w, h, cw, ch, left, top, right, bottom)); } catch { }
                    if (cw == w && ch == h)
                        return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));

                    using (var dst = new Bitmap(cw, ch, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    using (var gfx = Graphics.FromImage(dst))
                    {
                        gfx.DrawImage(src, new Rectangle(0, 0, cw, ch),
                            new Rectangle(left, top, cw, ch), GraphicsUnit.Pixel);
                        // 暂存到临时再替换，避免 LockBits/Save 同源冲突
                        string tmp = pngPath + ".crop.tmp.png";
                        dst.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                        // src 已 Dispose? 还没，需要先释放
                        gfx.Dispose();
                        dst.Dispose();
                        src.Dispose();
                        File.Delete(pngPath);
                        File.Move(tmp, pngPath);
                    }
                }
                return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
            }
            catch (System.Exception ex)
            {
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n[jt-crop-white] " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>
        /// (jt-upscale-png "png-path" [maxLongEdge=2400]) → T/Nil
        [LispFunction("jt-upscale-png")]
        public ResultBuffer UpscalePng(ResultBuffer args)
        {
            try
            {
                if (args == null) return null;
                var arr = args.AsArray();
                if (arr.Length < 1) return null;
                string pngPath = arr[0].Value as string;
                if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath)) return null;
                int target = 2400;
                if (arr.Length >= 2 && arr[1].Value is short) target = (short)arr[1].Value;
                else if (arr.Length >= 2 && arr[1].Value is int) target = (int)arr[1].Value;
                else if (arr.Length >= 2 && arr[1].Value is double) target = (int)(double)arr[1].Value;

                int newW, newH, oldW, oldH;
                using (var src = new Bitmap(pngPath))
                {
                    oldW = src.Width; oldH = src.Height;
                    int longEdge = Math.Max(oldW, oldH);
                    if (longEdge >= target)
                    {
                        try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                            string.Format("\n[jt-upscale-png] 已 {0}x{1} ≥ {2}, 跳过", oldW, oldH, target)); } catch { }
                        return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
                    }
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
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                    string.Format("\n[jt-upscale-png] {0}x{1} → {2}x{3}", oldW, oldH, newW, newH)); } catch { }
                return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
            }
            catch (System.Exception ex)
            {
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n[jt-upscale-png] " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>
        /// (jt-png-to-clipboard "png-path") → T/Nil
        /// 用一张干净的 DataObject 把 PNG 写进剪贴板（仅 Bitmap+DIB），
        /// 完全替换之前 COPYCLIP 放进去的内容。
        /// v1.1.30: 不再尝试保留 CAD 私有 COM 格式 —— 那些格式（"Embed Source"、
        /// "Object Descriptor"、"AcadEntity" 等）是 CAD 进程内的 IStream/IStorage
        /// COM 指针，跨进程读取会触发 Access Violation，且 .NET catch 抓不住 AV。
        /// 同机 CAD→CAD 粘贴实体请用 JTPASTE（PNG 里已嵌入 DWG）。
        /// </summary>
        [LispFunction("jt-png-to-clipboard")]
        public ResultBuffer PngToClipboard(ResultBuffer args)
        {
            try
            {
                if (args == null) return null;
                var arr = args.AsArray();
                if (arr.Length < 1) return null;
                string pngPath = arr[0].Value as string;
                if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath)) return null;

                // 直接用一张全新 DataObject，不读取任何现有剪贴板内容（避免 AV）
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
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                    "\n[jt-png-to-clipboard] PNG 已写入剪贴板位图"); } catch { }
                return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
            }
            catch (System.Exception ex)
            {
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n[jt-png-to-clipboard] " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>
        /// (jt-merge-png-h "out-path" "png1" "png2" ... [gap]) → T/Nil
        /// 横向拼接多张 PNG 到 out-path。所有图先统一缩到最大高度（按比例），
        /// 之间留 gap(默认 16) 像素白色间隔。最后一个数值参数若为整数则当 gap。
        /// 用于 JT 命令一次出多个方框、合成一张大图进剪贴板。
        /// </summary>
        [LispFunction("jt-merge-png-h")]
        public ResultBuffer MergePngHorizontal(ResultBuffer args)
        {
            try
            {
                if (args == null) return null;
                var arr = args.AsArray();
                if (arr.Length < 2) return null;
                string outPath = arr[0].Value as string;
                if (string.IsNullOrEmpty(outPath)) return null;

                // 收集 png 路径；末尾若是整数则作为 gap
                int gap = 16;
                int lastIdx = arr.Length - 1;
                if (arr[lastIdx].TypeCode == (int)LispDataType.Int16 ||
                    arr[lastIdx].TypeCode == (int)LispDataType.Int32 ||
                    arr[lastIdx].TypeCode == (int)LispDataType.Double)
                {
                    try { gap = System.Convert.ToInt32(arr[lastIdx].Value); lastIdx--; }
                    catch { }
                }
                var paths = new System.Collections.Generic.List<string>();
                for (int i = 1; i <= lastIdx; i++)
                {
                    string p = arr[i].Value as string;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) paths.Add(p);
                }
                if (paths.Count == 0) return null;
                if (paths.Count == 1)
                {
                    File.Copy(paths[0], outPath, true);
                    return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
                }

                // 1) 加载、计算最大高度
                var imgs = new System.Collections.Generic.List<Bitmap>();
                int maxH = 0;
                foreach (var p in paths)
                {
                    var bmp = new Bitmap(p);
                    imgs.Add(bmp);
                    if (bmp.Height > maxH) maxH = bmp.Height;
                }
                // 2) 按比例缩到统一高度，累加宽度
                var resized = new System.Collections.Generic.List<Bitmap>();
                int totalW = 0;
                foreach (var src in imgs)
                {
                    int newW = (int)System.Math.Round((double)src.Width * maxH / src.Height);
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

                // 3) 拼接
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

                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                    string.Format("\n[jt-merge-png-h] 已合成 {0} 张 → {1}", paths.Count, outPath)); } catch { }
                return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
            }
            catch (System.Exception ex)
            {
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n[jt-merge-png-h] " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>
        /// (jt-pngs-to-clipboard "png1" "png2" ...) → T/Nil
        /// 把多张 PNG 文件用 CF_HDROP（文件拖放）格式写入剪贴板。
        /// 微信、QQ 等聊天软件粘贴时会**分别**发送每张图（不会合成一张大图）。
        /// 同时把第一张作为 Bitmap 兜底，方便不支持 CF_HDROP 的应用。
        /// </summary>
        [LispFunction("jt-pngs-to-clipboard")]
        public ResultBuffer PngsToClipboard(ResultBuffer args)
        {
            try
            {
                if (args == null) return null;
                var arr = args.AsArray();
                var files = new System.Collections.Specialized.StringCollection();
                var validPaths = new System.Collections.Generic.List<string>();
                for (int i = 0; i < arr.Length; i++)
                {
                    string p = arr[i].Value as string;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        files.Add(p);
                        validPaths.Add(p);
                    }
                }
                if (files.Count == 0) return null;

                var data = new System.Windows.Forms.DataObject();
                // 1) CF_HDROP 文件列表（微信粘贴 = 多张图分别发）
                data.SetFileDropList(files);
                // 2) 第一张作为 Bitmap 兜底
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
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                    string.Format("\n[jt-pngs-to-clipboard] {0} 张 PNG 已写入剪贴板（CF_HDROP）", files.Count)); } catch { }
                return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
            }
            catch (System.Exception ex)
            {
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n[jt-pngs-to-clipboard] " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>
        /// (jt-plot-png "out-path" x1 y1 x2 y2 [media]) → T/Nil
        /// 用 PlotEngine 把模型空间 (x1,y1)-(x2,y2) 矩形区域出图为 PNG。
        /// 像素由 media 决定（PublishToWeb PNG.pc3 内置纸张规格）：
        ///   "VGA (640.00 x 480.00 Pixels)"
        ///   "SVGA (800.00 x 600.00 Pixels)"
        ///   "XGA (1024.00 x 768.00 Pixels)"
        ///   "SXGA (1280.00 x 1024.00 Pixels)"
        ///   "Sun Hi-Res (1600.00 x 1280.00 Pixels)"  ← 默认
        /// PlotEngine 是矢量直接渲染到画布，不是位图拉伸 → 又大又锐。
        /// 必须确保 BACKGROUNDPLOT=0。
        /// </summary>
        [LispFunction("jt-plot-png")]
        public ResultBuffer PlotPng(ResultBuffer args)
        {
            try
            {
                if (args == null) return null;
                var arr = args.AsArray();
                if (arr.Length < 5) return null;
                string outPath = arr[0].Value as string;
                if (string.IsNullOrEmpty(outPath)) return null;
                double x1 = System.Convert.ToDouble(arr[1].Value);
                double y1 = System.Convert.ToDouble(arr[2].Value);
                double x2 = System.Convert.ToDouble(arr[3].Value);
                double y2 = System.Convert.ToDouble(arr[4].Value);
                string media = (arr.Length >= 6 && arr[5].Value is string)
                    ? (string)arr[5].Value
                    : "Sun Hi-Res (1600.00 x 1280.00 Pixels)";

                // 归一化矩形（确保 minPt < maxPt）
                double xmin = System.Math.Min(x1, x2), xmax = System.Math.Max(x1, x2);
                double ymin = System.Math.Min(y1, y2), ymax = System.Math.Max(y1, y2);

                if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                {
                    try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                        "\n[jt-plot-png] 已有打印进行中，跳过"); } catch { }
                    return null;
                }

                var doc = Application.DocumentManager.MdiActiveDocument;
                var db = doc.Database;
                short bgOld = (short)Application.GetSystemVariable("BACKGROUNDPLOT");
                Application.SetSystemVariable("BACKGROUNDPLOT", (short)0);
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
                        // ★ 关键顺序：device→media→PlotType(Window)→PlotWindowArea→Scale→Centered
                        psv.SetPlotConfigurationName(ps, "PublishToWeb PNG.pc3", media);
                        psv.RefreshLists(ps);
                        psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                        psv.SetPlotWindowArea(ps,
                            new Extents2d(new Point2d(xmin, ymin), new Point2d(xmax, ymax)));
                        psv.SetUseStandardScale(ps, true);
                        psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                        psv.SetPlotCentered(ps, true);
                        psv.SetPlotRotation(ps, PlotRotation.Degrees000);
                        // 不设 ctb（避免 monochrome.ctb 不存在导致 eInvalidInput）

                        var pi = new PlotInfo();
                        pi.Layout = layout.ObjectId;
                        pi.OverrideSettings = ps;
                        var piv = new PlotInfoValidator();
                        piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                        piv.Validate(pi);

                        // 先把目标文件删掉，避免上次残留导致 PlotEngine 写入失败
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
                    }
                }
                finally
                {
                    Application.SetSystemVariable("BACKGROUNDPLOT", bgOld);
                    // 失败时清理残留文件，避免后续回退 PNGOUT 写不进去
                    if (!plotOk)
                    {
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    }
                }

                if (!File.Exists(outPath)) return null;
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                    string.Format("\n[jt-plot-png] media=\"{0}\" → {1}", media, outPath)); } catch { }
                return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
            }
            catch (System.Exception ex)
            {
                try { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n[jt-plot-png] " + ex.Message); } catch { }
                return null;
            }
        }
    }

}
