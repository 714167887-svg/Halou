using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace JsqClipboardCadPlugin
{
    // 自更新（守护脚本 + DLL/manifest swap）+ AutoCAD 自启动（注册表 Applications 子键）。
    // 常量 AutoStartAppName 见主文件。
    internal sealed partial class HalouSuiteManager
    {
        public string CurrentAssemblyPath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        public bool IsUpdateAvailable()
        {
            return CompareVersion(CurrentVersion, _latestVersion) < 0 && !string.IsNullOrWhiteSpace(_latestDownloadUrl);
        }

        public bool DownloadUpdate(out string installedPath, out string errorMessage)
        {
            installedPath = null;
            errorMessage = null;
            if (!IsUpdateAvailable())
            {
                errorMessage = "没有可用更新";
                return false;
            }

            string dllPath = Assembly.GetExecutingAssembly().Location;
            string targetDir = Path.GetDirectoryName(dllPath);
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                errorMessage = "无法确定当前 DLL 目录";
                return false;
            }

            string pendingPath = Path.Combine(targetDir, "JsqClipboardCadPlugin.dll.new");
            string batPath = Path.Combine(targetDir, "apply-halou-update.bat");
            string manifestPendingPath = Path.Combine(targetDir, "halou-plugin-manifest.json.new");
            string manifestTargetPath = Path.Combine(targetDir, "halou-plugin-manifest.json");
            bool manifestDownloaded = false;

            try
            {
                Dictionary<string, string> dh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                dh["User-Agent"] = "HalouSuite/" + CurrentVersion;
                // dll 至少 100 KB；劫持页通常只有几 KB
                RobustHttp.DownloadFile(_latestDownloadUrl, pendingPath, dh, 102400);
            }
            catch (System.Exception ex)
            {
                errorMessage = "下载失败：" + ex.Message;
                return false;
            }

            // 同步下载 manifest（与 DLL 同目录）；下载失败不阻断主流程，DLL 升级仍继续
            try
            {
                string manifestUrl = DeriveManifestUrl(_latestDownloadUrl);
                if (!string.IsNullOrWhiteSpace(manifestUrl))
                {
                    Dictionary<string, string> mh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    mh["User-Agent"] = "HalouSuite/" + CurrentVersion;
                    RobustHttp.DownloadFile(manifestUrl, manifestPendingPath, mh);
                    manifestDownloaded = File.Exists(manifestPendingPath) && new FileInfo(manifestPendingPath).Length > 0;
                }
            }
            catch
            {
                // manifest 是辅助文件，下载失败时不阻塞 DLL 升级
                manifestDownloaded = false;
                try { if (File.Exists(manifestPendingPath)) File.Delete(manifestPendingPath); } catch { }
            }

            // 守护脚本：后台轮询 acad.exe，退出后自动替换，无需用户手动操作。
            // 用户照常用完 CAD，正常关闭时新版本自动就位，下次启动即 v{latest}。
            // move 失败时回到循环（用户可能关了又开），重新等待。
            string manifestMoveCmd = manifestDownloaded
                ? "move /Y \"" + manifestPendingPath + "\" \"" + manifestTargetPath + "\" >nul 2>&1\r\n"
                : "";
            string bat =
                "@echo off\r\n" +
                "chcp 65001 >nul\r\n" +
                ":wait\r\n" +
                "tasklist /FI \"IMAGENAME eq acad.exe\" 2>nul | find /i \"acad.exe\" >nul\r\n" +
                "if %ERRORLEVEL%==0 ( timeout /T 10 /NOBREAK >nul & goto wait )\r\n" +
                "timeout /T 3 /NOBREAK >nul\r\n" +
                "tasklist /FI \"IMAGENAME eq acad.exe\" 2>nul | find /i \"acad.exe\" >nul\r\n" +
                "if %ERRORLEVEL%==0 ( goto wait )\r\n" +
                "move /Y \"" + pendingPath + "\" \"" + dllPath + "\" >nul 2>&1\r\n" +
                "if %ERRORLEVEL% NEQ 0 ( timeout /T 5 /NOBREAK >nul & goto wait )\r\n" +
                manifestMoveCmd +
                "del \"%~f0\" >nul 2>&1\r\n" +
                "exit /b 0\r\n";
            File.WriteAllText(batPath, bat, System.Text.Encoding.UTF8);

            // 后台启动守护进程（完全脱离 CAD，隐藏窗口）
            // 用 start /b 二次 detach，即使 AutoCAD 退出也不会连坐守护进程
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c start \"HalouUpdate\" /b cmd /c \"" + batPath + "\"");
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (System.Exception ex)
            {
                errorMessage = "守护进程启动失败：" + ex.Message + "（DLL 已下载到 " + pendingPath + "，可手动替换）";
                installedPath = pendingPath;
                return false;
            }

            installedPath = batPath;
            return true;
        }

        /// <summary>
        /// 根据 DLL 的下载 URL 推导同目录下的 manifest URL。
        /// 例如 https://.../release/JsqClipboardCadPlugin.dll →
        ///      https://.../release/halou-plugin-manifest.json
        /// </summary>
        private static string DeriveManifestUrl(string dllUrl)
        {
            if (string.IsNullOrWhiteSpace(dllUrl)) return null;
            try
            {
                int q = dllUrl.IndexOf('?');
                string baseUrl = q >= 0 ? dllUrl.Substring(0, q) : dllUrl;
                int slash = baseUrl.LastIndexOf('/');
                if (slash <= 0) return null;
                return baseUrl.Substring(0, slash + 1) + "halou-plugin-manifest.json"
                       + (q >= 0 ? dllUrl.Substring(q) : string.Empty);
            }
            catch { return null; }
        }

        private static int CompareVersion(string a, string b)
        {
            Version va, vb;
            if (!Version.TryParse((a ?? "0").Trim(), out va)) va = new Version(0, 0);
            if (!Version.TryParse((b ?? "0").Trim(), out vb)) vb = new Version(0, 0);
            return va.CompareTo(vb);
        }

        // ===== 自启动注册表写入 =====

        public bool IsAutoStartInstalled()
        {
            int hits = 0;
            ForEachAutoCadApplicationsKey(true, delegate(Microsoft.Win32.RegistryKey appsKey)
            {
                using (Microsoft.Win32.RegistryKey entry = appsKey.OpenSubKey(AutoStartAppName, false))
                {
                    if (entry != null)
                    {
                        object loader = entry.GetValue("LOADER");
                        if (loader != null && !string.IsNullOrWhiteSpace(loader.ToString()))
                        {
                            hits++;
                        }
                    }
                }
            });
            return hits > 0;
        }

        public int InstallAutoStart(out string detail)
        {
            string dllPath = CurrentAssemblyPath;
            int installed = 0;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            ForEachAutoCadApplicationsKey(false, delegate(Microsoft.Win32.RegistryKey appsKey)
            {
                using (Microsoft.Win32.RegistryKey entry = appsKey.CreateSubKey(AutoStartAppName))
                {
                    if (entry == null)
                    {
                        return;
                    }

                    entry.SetValue("DESCRIPTION", "Halou 插件集合（统一壳）", Microsoft.Win32.RegistryValueKind.String);
                    entry.SetValue("LOADCTRLS", 2, Microsoft.Win32.RegistryValueKind.DWord); // 2 = 启动时加载
                    entry.SetValue("LOADER", dllPath, Microsoft.Win32.RegistryValueKind.String);
                    entry.SetValue("MANAGED", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    installed++;
                    sb.AppendLine(appsKey.Name);
                }
            });

            detail = sb.ToString().TrimEnd();
            return installed;
        }

        public int UninstallAutoStart(out string detail)
        {
            int removed = 0;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            ForEachAutoCadApplicationsKey(false, delegate(Microsoft.Win32.RegistryKey appsKey)
            {
                try
                {
                    appsKey.DeleteSubKeyTree(AutoStartAppName, false);
                    removed++;
                    sb.AppendLine(appsKey.Name);
                }
                catch
                {
                }
            });

            detail = sb.ToString().TrimEnd();
            return removed;
        }

        private static void ForEachAutoCadApplicationsKey(bool readOnly, Action<Microsoft.Win32.RegistryKey> callback)
        {
            const string rootPath = @"Software\Autodesk\AutoCAD";
            using (Microsoft.Win32.RegistryKey root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(rootPath, !readOnly))
            {
                if (root == null)
                {
                    return;
                }

                foreach (string versionName in root.GetSubKeyNames())
                {
                    using (Microsoft.Win32.RegistryKey versionKey = root.OpenSubKey(versionName, !readOnly))
                    {
                        if (versionKey == null)
                        {
                            continue;
                        }

                        foreach (string productName in versionKey.GetSubKeyNames())
                        {
                            if (!productName.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            using (Microsoft.Win32.RegistryKey productKey = versionKey.OpenSubKey(productName, !readOnly))
                            {
                                if (productKey == null)
                                {
                                    continue;
                                }

                                Microsoft.Win32.RegistryKey appsKey = readOnly
                                    ? productKey.OpenSubKey("Applications", false)
                                    : productKey.CreateSubKey("Applications");
                                if (appsKey == null)
                                {
                                    continue;
                                }

                                try
                                {
                                    callback(appsKey);
                                }
                                finally
                                {
                                    appsKey.Close();
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
