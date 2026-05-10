using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;

namespace HalouSuite.Payload
{
    // 拖拽接管（COM IDropTarget 替换） + 文档级 LSP AutoLoad。
    // 字段 _installedDropTargets / 常量 AutoLoadDocFlag 见主文件。
    internal sealed partial class HalouSuiteManager
    {
        // ========== 图片拖拽接管（COM IDropTarget）==========
        private void InstallImageDropTarget()
        {
            try
            {
                var mw = Application.MainWindow;
                if (mw == null) return;
                IntPtr root = mw.Handle;
                if (root == IntPtr.Zero) return;

                // AutoCAD 的 drop target 通常在绘图区子窗口上，主窗口不一定注册。
                // 枚举所有子孙窗口，找到所有已注册 OleDropTargetInterface 的窗口，逐一替换。
                List<IntPtr> targets = new List<IntPtr>();
                // 根窗口本身也可能注册
                if (HalouDragDropInterop.GetProp(root, "OleDropTargetInterface") != IntPtr.Zero)
                    targets.Add(root);
                EnumChildrenRecursive(root, targets);

                if (targets.Count == 0)
                {
                    // 没有任何子窗口预先注册 → 直接挂主窗口（至少能响应 DragAcceptFiles/外部拖拽）
                    targets.Add(root);
                }

                foreach (IntPtr hwnd in targets)
                {
                    if (_installedDropTargets.ContainsKey(hwnd)) continue; // 已挂过不重复
                    try
                    {
                        HalouDragDropInterop.IDropTarget orig = null;
                        IntPtr origPtr = HalouDragDropInterop.GetProp(hwnd, "OleDropTargetInterface");
                        if (origPtr != IntPtr.Zero)
                        {
                            try
                            {
                                object o = Marshal.GetObjectForIUnknown(origPtr);
                                orig = o as HalouDragDropInterop.IDropTarget;
                            }
                            catch { orig = null; }
                        }

                        HalouDragDropInterop.RevokeDragDrop(hwnd);
                        var t = new HalouImageDropTarget(HandleDroppedImages);
                        t.SetFallback(orig);
                        int hr = HalouDragDropInterop.RegisterDragDrop(hwnd, t);
                        if (hr == 0)
                        {
                            _installedDropTargets[hwnd] = t;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void EnumChildrenRecursive(IntPtr parent, List<IntPtr> acc)
        {
            HalouDragDropInterop.EnumChildWindows(parent, delegate(IntPtr hwnd, IntPtr lParam)
            {
                try
                {
                    if (HalouDragDropInterop.GetProp(hwnd, "OleDropTargetInterface") != IntPtr.Zero)
                    {
                        acc.Add(hwnd);
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }

        private void OnDocumentActivatedForDrop(object sender, DocumentCollectionEventArgs e)
        {
            // 重新扫描子窗口；InstallImageDropTarget 内部会跳过已挂载的 hwnd
            try { InstallImageDropTarget(); } catch { }
        }

        private void UninstallImageDropTarget()
        {
            try
            {
                foreach (var kv in _installedDropTargets)
                {
                    try { HalouDragDropInterop.RevokeDragDrop(kv.Key); } catch { }
                }
                _installedDropTargets.Clear();
            }
            catch { }
        }

        private void HandleDroppedImages(List<string> files)
        {
            try
            {
                if (files == null || files.Count == 0) return;

                string temp = Environment.GetEnvironmentVariable("TEMP");
                if (string.IsNullOrWhiteSpace(temp)) temp = Path.GetTempPath();
                string listPath = Path.Combine(temp, "halou-oledrop.txt");

                // AutoCAD LSP open/read-line 默认按系统 ANSI(CP_936) 读，写 Default 编码
                File.WriteAllLines(listPath, files, System.Text.Encoding.Default);

                // 同步把辅助 PS1 复制到 TEMP，LSP 能稳定通过 TEMP 分支找到
                try
                {
                    string ps1Src = Path.Combine(_assemblyDirectory, "OLE", "oleimgdir-clipboard.ps1");
                    string ps1Dst = Path.Combine(temp, "oleimgdir-clipboard.ps1");
                    if (File.Exists(ps1Src) && (!File.Exists(ps1Dst) || File.GetLastWriteTimeUtc(ps1Src) > File.GetLastWriteTimeUtc(ps1Dst)))
                    {
                        File.Copy(ps1Src, ps1Dst, true);
                    }
                }
                catch { }

                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    WriteMessage(StatusPrefix + " 拖入图片但没有活动图纸，已忽略。");
                    return;
                }

                string olePath = Path.Combine(_assemblyDirectory, "OLE", "oleimgdir.lsp");
                string loadPrefix = string.Empty;
                if (File.Exists(olePath))
                {
                    string escaped = olePath.Replace('\\', '/').Replace("\"", "\\\"");
                    loadPrefix = string.Format("(if (not c:OLEDROP) (load \"{0}\")) ", escaped);
                }

                doc.SendStringToExecute(loadPrefix + "OLEDROP ", true, false, false);
            }
            catch (System.Exception ex)
            {
                WriteMessage("[OLEDROP] 处理拖拽失败：" + ex.Message);
            }
        }

        // ==================== AutoLoad ====================
        private void OnDocumentCreatedForAutoLoad(object sender, DocumentCollectionEventArgs e)
        {
            try { if (e != null && e.Document != null) AutoLoadFeaturesForDocument(e.Document); } catch { }
        }

        private void OnDocumentActivatedForAutoLoad(object sender, DocumentCollectionEventArgs e)
        {
            try { if (e != null && e.Document != null) AutoLoadFeaturesForDocument(e.Document); } catch { }
        }

        private void AutoLoadFeaturesForDocument(Document doc)
        {
            if (doc == null || _configuration == null || _manifest == null || _manifest.Features == null) return;
            if (_configuration.AutoLoadFeatures == null || _configuration.AutoLoadFeatures.Count == 0) return;

            try
            {
                if (doc.UserData != null && doc.UserData.Contains(AutoLoadDocFlag)) return;
                if (doc.UserData != null) doc.UserData[AutoLoadDocFlag] = true;
            }
            catch { }

            int loaded = 0;
            foreach (CadPluginFeature f in _manifest.Features)
            {
                if (f == null || !f.Enabled || string.IsNullOrWhiteSpace(f.Id)) continue;
                if (!"lisp".Equals((f.Kind ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) continue;

                bool on;
                if (!_configuration.AutoLoadFeatures.TryGetValue(f.Id, out on) || !on) continue;

                string loadPath = ResolveManifestPath(f.LoadPath);
                if (string.IsNullOrWhiteSpace(loadPath) || !File.Exists(loadPath)) continue;

                string normalized = loadPath.Replace('\\', '/').Replace("\"", "\\\"");
                try
                {
                    doc.SendStringToExecute(
                        string.Format("(progn (load \"{0}\") (princ)) ", normalized),
                        false, false, false);
                    loaded++;
                }
                catch { }
            }

            if (loaded > 0)
            {
                WriteMessage(string.Format("{0} 自动加载了 {1} 个 LSP 到当前文档。", StatusPrefix, loaded));
            }
        }
    }
}
