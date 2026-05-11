using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using HalouSuite.Contract;

namespace HalouSuite.Host
{
    /// <summary>
    /// 所有 [CommandMethod] 的唯一注册位置。
    /// 命令体只做：1) 取当前 Payload 2) 转发调用。
    /// 这样 Payload 热替换后 acad 命令表里的入口仍然指向 Host 这份固定方法，但行为已切到新版业务逻辑。
    /// </summary>
    public sealed class HostCommands
    {
        private static IPayload P()
        {
            return HostExtension.CurrentPayload;
        }

        [CommandMethod("HALOU")]
        public void ShowHalouSuite()
        {
            DiagLog.Write("Cmd", "HALOU invoked, payload=" + (P() == null ? "(null)" : P().Version));
            IPayload p = P(); if (p != null) p.ShowPalette();
        }

        [CommandMethod("HALOUTOGGLE")]
        public void ToggleHalouSuite()
        {
            IPayload p = P(); if (p != null) p.TogglePalette();
        }

        [CommandMethod("HALOUREFRESH")]
        public void RefreshHalouSuite()
        {
            IPayload p = P(); if (p != null) p.RefreshManifest(true);
        }

        [CommandMethod("HALOUZK")]
        public void RunZkFeature()
        {
            IPayload p = P(); if (p != null) p.RunFeatureById("zk");
        }

        [CommandMethod("HALOUKB")]
        public void RunKbFeature()
        {
            IPayload p = P(); if (p != null) p.RunFeatureById("kb");
        }

        [CommandMethod("JSQHOOKON")]
        public void HookPasteClip()
        {
            IPayload p = P(); if (p != null) p.HookPasteClip(false);
        }

        [CommandMethod("JSQHOOKOFF")]
        public void UnhookPasteClip()
        {
            IPayload p = P(); if (p != null) p.UnhookPasteClip();
        }

        [CommandMethod("JSQPASTE")]
        public void PasteFromClipboard()
        {
            IPayload p = P(); if (p != null) p.PasteFromClipboard();
        }

        [CommandMethod("PASTECLIP")]
        public void PasteClipOverride()
        {
            IPayload p = P();
            if (p == null || !p.PasteClipOverrideHandled())
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    // 注意：`.` 前缀让 ._PASTECLIP 跳过 .NET [CommandMethod] 重写，调原生 CAD 命令。
                    // 不要 REDEFINE，否则会破坏 JSQHOOKON 之前 UNDEFINE 的状态。
                    doc.SendStringToExecute("._PASTECLIP ", true, false, false);
                }
            }
        }

        [CommandMethod("JSQPASTEFILE")]
        public void PasteFromFile()
        {
            IPayload p = P(); if (p != null) p.PasteFromFile();
        }

        [CommandMethod("HALOURELOAD")]
        public void ManualReload()
        {
            DiagLog.Write("Cmd", "HALOURELOAD invoked");
            PayloadLoader loader = HostExtension.Loader;
            if (loader == null) { DiagLog.Write("Cmd", "HALOURELOAD: loader is null"); return; }

            string[] files = System.IO.Directory.GetFiles(loader.PayloadDirectory, "HalouPayload.*.dll");
            if (files.Length == 0)
            {
                DiagLog.Write("Cmd", "HALOURELOAD: no payload files");
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null) doc.Editor.WriteMessage("\n[HalouHost] payloads 目录下没有 dll");
                return;
            }
            // 按版本号选最新（不能用字典序：HalouPayload.2.0.9.dll > HalouPayload.2.0.12.dll 是错的）
            string bestPath = null;
            System.Version bestVer = null;
            for (int i = 0; i < files.Length; i++)
            {
                string name = System.IO.Path.GetFileName(files[i]);
                const string prefix = "HalouPayload.";
                const string suffix = ".dll";
                if (name.Length <= prefix.Length + suffix.Length) continue;
                string mid = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
                System.Version v;
                if (!System.Version.TryParse(mid, out v)) continue;
                if (bestVer == null || v > bestVer) { bestVer = v; bestPath = files[i]; }
            }
            if (bestPath == null)
            {
                DiagLog.Write("Cmd", "HALOURELOAD: no parseable payload version");
                Document doc2 = Application.DocumentManager.MdiActiveDocument;
                if (doc2 != null) doc2.Editor.WriteMessage("\n[HalouHost] payloads 目录里没有可识别版本的 dll");
                return;
            }
            DiagLog.Write("Cmd", "HALOURELOAD: queuing " + bestPath);
            loader.RequestReload(bestPath, "manual reload");
        }

        [CommandMethod("HALOULKG")]
        public void RollbackToLkg()
        {
            DiagLog.Write("Cmd", "HALOULKG invoked");
            PayloadLoader loader = HostExtension.Loader;
            if (loader == null) return;
            string lkg = loader.ReadLkgPath();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (string.IsNullOrEmpty(lkg) || !System.IO.File.Exists(lkg))
            {
                if (doc != null) doc.Editor.WriteMessage("\n[HalouHost] 没有可用的 LKG（last-known-good）记录。");
                DiagLog.Write("Cmd", "HALOULKG: no LKG");
                return;
            }
            if (doc != null) doc.Editor.WriteMessage("\n[HalouHost] 排队回退到 LKG: " + System.IO.Path.GetFileName(lkg));
            loader.RequestReload(lkg, "manual rollback to LKG");
        }

        [CommandMethod("HALOUDISABLE")]
        public void DisablePayload()
        {
            DiagLog.Write("Cmd", "HALOUDISABLE invoked");
            PayloadLoader loader = HostExtension.Loader;
            if (loader == null) return;
            loader.SetDisabled(true);
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
                doc.Editor.WriteMessage("\n[HalouHost] 已置 disabled 标记。下次启动 CAD 将不加载 Payload，本次会话当前 Payload 仍在运行。HALOUENABLE 解除。");
        }

        [CommandMethod("HALOUENABLE")]
        public void EnablePayload()
        {
            DiagLog.Write("Cmd", "HALOUENABLE invoked");
            PayloadLoader loader = HostExtension.Loader;
            if (loader == null) return;
            loader.SetDisabled(false);
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
                doc.Editor.WriteMessage("\n[HalouHost] disabled 标记已清除。下次启动 CAD 将正常加载 Payload。");
        }

        [CommandMethod("HALOUSTATUS")]
        public void ShowStatus()
        {
            DiagLog.Write("Cmd", "HALOUSTATUS invoked");
            PayloadLoader loader = HostExtension.Loader;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (loader == null) { doc.Editor.WriteMessage("\n[HalouHost] loader 未初始化"); return; }
            IPayload p = loader.Current;
            string lkg = loader.ReadLkgPath();
            doc.Editor.WriteMessage(
                "\n[HalouHost] Host v" + HostExtension.HostVersion +
                " (API=" + PayloadLoader.CurrentHostApiLevel + ")" +
                "\n  Payload: " + (p == null ? "(未加载)" : ("v" + p.Version + " 需要 API>=" + p.RequiredHostApiLevel)) +
                "\n  当前文件: " + (string.IsNullOrEmpty(loader.CurrentPath) ? "(无)" : loader.CurrentPath) +
                "\n  LKG: " + (string.IsNullOrEmpty(lkg) ? "(无)" : lkg) +
                "\n  Disabled: " + (loader.IsDisabled ? "是" : "否"));
        }
    }
}
