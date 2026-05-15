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
            // v2.0.18：DocumentActivated 高频触发（每次切换文档、打开图纸都走），
            // 原实现每次都同步 EnumChildWindows + Register/Revoke DragDrop（系统调用），频繁切文档会加重 UI 线程负担。
            // 改为：如果已经成功挂上过（6 以上任意窗口）则跳过；否则用 Timer 防抖 500ms 后才调用 InstallImageDropTarget。
            try
            {
                if (_installedDropTargets.Count > 0) return;
                if (_dropInstallTimer == null)
                {
                    _dropInstallTimer = new System.Windows.Forms.Timer();
                    _dropInstallTimer.Interval = 500;
                    _dropInstallTimer.Tick += OnDropInstallTimerTick;
                }
                _dropInstallTimer.Stop();
                _dropInstallTimer.Start();
            }
            catch { }
        }

        private void OnDropInstallTimerTick(object sender, EventArgs e)
        {
            try { _dropInstallTimer.Stop(); } catch { }
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

                // 同步把辅助 PS1 解密/复制到 TEMP，LSP 能稳定通过 TEMP 分支找到。
                EnsureOleHelperInTemp();

                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    WriteMessage(StatusPrefix + " 拖入图片但没有活动图纸，已忽略。");
                    return;
                }

                string loadPrefix = string.Empty;
                string loadExpression;
                if (TryBuildLispLoadExpression("OLE/oleimgdir.lsp", true, out loadExpression))
                {
                    loadPrefix = loadExpression + " ";
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

                string loadExpression;
                if (!TryBuildLispLoadExpression(f.LoadPath, true, out loadExpression)) continue;
                try
                {
                    doc.SendStringToExecute(loadExpression + " ", false, false, false);
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
