using System;
using System.Net;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using HalouSuite.Contract;

namespace HalouSuite.Host
{
    /// <summary>
    /// Host 在 acad.exe 启动时被加载一次，永驻进程。
    /// 职责：启动 PayloadLoader、销毁旧 Payload 加载新 Payload。
    /// </summary>
    public sealed class HostExtension : IExtensionApplication
    {
        public const string HostVersion = "2.0.0";

        private static PayloadLoader s_loader;
        internal static PayloadLoader Loader { get { return s_loader; } }
        internal static IPayload CurrentPayload { get { return s_loader == null ? null : s_loader.Current; } }

        public void Initialize()
        {
            DiagLog.Reset();
            DiagLog.Write("Host", "Initialize() begin, HostVersion=" + HostVersion);

            try
            {
                const SecurityProtocolType tls12 = (SecurityProtocolType)3072;
                const SecurityProtocolType tls11 = (SecurityProtocolType)768;
                ServicePointManager.SecurityProtocol |= tls12 | tls11;
            }
            catch { }

            try
            {
                s_loader = new PayloadLoader(HostVersion);
                s_loader.LoadInitial();
                DiagLog.Write("Host", "Initialize() done, payload=" + (CurrentPayload == null ? "(none)" : CurrentPayload.Version));

                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    string payloadVer = CurrentPayload == null ? "(none)" : CurrentPayload.Version;
                    doc.Editor.WriteMessage(
                        "\nHalou Host v" + HostVersion + " (API=" + PayloadLoader.CurrentHostApiLevel + ")" +
                        " 已加载，Payload v" + payloadVer +
                        "。HALOU 打开面板，HALOURELOAD 热重载，HALOUSTATUS 查看状态，HALOULKG 回退，HALOUDISABLE/ENABLE 开关。");
                }
            }
            catch (System.Exception ex)
            {
                DiagLog.Write("Host", "Initialize() FAIL: " + ex);
                System.Diagnostics.Trace.WriteLine("[HalouHost.Initialize] " + ex);
                try
                {
                    Document d = Application.DocumentManager.MdiActiveDocument;
                    if (d != null) d.Editor.WriteMessage("\nHalou Host 启动失败: " + ex.Message);
                }
                catch { }
            }
        }

        public void Terminate()
        {
            try { if (s_loader != null) s_loader.Dispose(); } catch { }
            s_loader = null;
        }
    }
}
