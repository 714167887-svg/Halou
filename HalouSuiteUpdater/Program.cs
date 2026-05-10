using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HalouSuiteUpdater
{
    internal static class Program
    {
        // 工具自身版本：发新版时手动 +1
        private const int SelfVersion = 3;
        private const string SelfVersionUrl =
            "https://raw.githubusercontent.com/714167887-svg/halou-release/main/release/HalouSuiteUpdater.version";
        private const string SelfExeUrl =
            "https://raw.githubusercontent.com/714167887-svg/halou-release/main/release/HalouSuiteUpdater.exe";
        private const string DllUrl =
            "https://raw.githubusercontent.com/714167887-svg/halou-release/main/release/JsqClipboardCadPlugin.dll";
        private const string LicenseUrl =
            "https://raw.githubusercontent.com/714167887-svg/halou-release/main/license.json";
        private const string DllName = "JsqClipboardCadPlugin.dll";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                // 1) 强制启用 TLS 1.2 / 1.1（这台机器的 .NET Framework 默认是 TLS 1.0，无法访问 GitHub）
                try
                {
                    const SecurityProtocolType tls12 = (SecurityProtocolType)3072;
                    const SecurityProtocolType tls11 = (SecurityProtocolType)768;
                    ServicePointManager.SecurityProtocol |= tls12 | tls11;
                }
                catch { }

                // 1.5) 自更新：检查远端是否有新 exe；若有，下载、用 PowerShell 安排在本进程退出后替换并重启
                if (args == null || args.Length == 0 || args[0] != "--no-self-update")
                {
                    if (TrySelfUpdate())
                    {
                        return 0; // 已交给 PowerShell 后续替换+重启
                    }
                }

                // 2) 搜索本机所有 JsqClipboardCadPlugin.dll
                List<string> dllPaths = FindAllDllPaths();
                if (dllPaths.Count == 0)
                {
                    MessageBox.Show(
                        "未在本机找到 JsqClipboardCadPlugin.dll。\r\n\r\n" +
                        "请先按之前的安装步骤把 Halou Suite 装到 CAD 上，再运行本工具升级。",
                        "Halou Suite 升级器",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return 1;
                }

                // 3) 提示用户先关 CAD（dll 被占用就无法覆盖）
                if (IsCadRunning())
                {
                    DialogResult r = MessageBox.Show(
                        "检测到 AutoCAD 正在运行。继续会强制关闭 AutoCAD（请先保存图纸！）。\r\n\r\n继续吗？",
                        "Halou Suite 升级器",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return 2;

                    foreach (Process p in Process.GetProcessesByName("acad"))
                    {
                        try { p.Kill(); p.WaitForExit(5000); } catch { }
                    }
                    Thread.Sleep(800);
                }

                // 4) 下载新 dll 到临时目录（三重兜底 + jsDelivr 镜像）
                string tmpDll = Path.Combine(Path.GetTempPath(), "HalouSuite_" + Guid.NewGuid().ToString("N") + ".dll");
                string licenseText;
                try
                {
                    RobustHttp.DownloadFile(DllUrl + "?_t=" + DateTime.UtcNow.Ticks, tmpDll);
                    licenseText = RobustHttp.DownloadString(LicenseUrl + "?_t=" + DateTime.UtcNow.Ticks);
                }
                catch (Exception exDl)
                {
                    MessageBox.Show(
                        "下载新版 dll 失败：\r\n" + exDl.Message + "\r\n\r\n" +
                        "请用浏览器测试以下任一链接是否可访问：\r\n" +
                        DllUrl + "\r\n" +
                        "https://cdn.jsdelivr.net/gh/714167887-svg/halou-release@main/release/JsqClipboardCadPlugin.dll",
                        "Halou Suite 升级器",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return 3;
                }

                FileInfo fi = new FileInfo(tmpDll);
                if (!fi.Exists || fi.Length < 50000)
                {
                    MessageBox.Show(
                        "下载到的 dll 文件大小异常（" + (fi.Exists ? fi.Length.ToString() : "0") + " 字节）。\r\n\r\n" +
                        "请用浏览器手动下载：\r\n" + DllUrl,
                        "Halou Suite 升级器",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return 4;
                }

                // 5) 备份 + 覆盖每一处 dll
                StringBuilder log = new StringBuilder();
                int okCount = 0;
                foreach (string target in dllPaths)
                {
                    try
                    {
                        string bak = target + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                        if (File.Exists(target))
                        {
                            try { File.Copy(target, bak, true); } catch { /* 备份失败也继续 */ }
                        }
                        File.Copy(tmpDll, target, true);
                        log.AppendLine("✔ " + target);
                        okCount++;
                    }
                    catch (Exception exCp)
                    {
                        log.AppendLine("✗ " + target + "  ← " + exCp.Message);
                    }
                }

                try { File.Delete(tmpDll); } catch { }

                string newVer = ExtractValue(licenseText, "latest_version");
                MessageBox.Show(
                    "升级完成（" + okCount + "/" + dllPaths.Count + " 处替换成功）。\r\n\r\n" +
                    "新版本：" + (string.IsNullOrEmpty(newVer) ? "未知" : newVer) + "\r\n\r\n" +
                    log.ToString() + "\r\n" +
                    "请重新启动 AutoCAD。",
                    "Halou Suite 升级器",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("升级失败：\r\n" + ex.ToString(),
                    "Halou Suite 升级器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 99;
            }
        }

        private static List<string> FindAllDllPaths()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 常见安装根目录
            string[] roots = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HalouSuite"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HalouSuite"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Autodesk", "ApplicationPlugins"),
                @"C:\ProgramData\Autodesk\ApplicationPlugins",
                @"C:\ProgramData\HalouSuite",
                @"C:\Program Files\Autodesk\ApplicationPlugins",
                @"C:\Program Files (x86)\Autodesk\ApplicationPlugins",
                @"C:\Program Files\HalouSuite",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "HalouSuite"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"),
            };

            foreach (string root in roots)
            {
                SafeSearch(root, set);
            }

            // 兜底：如果上面都没命中，扫 C/D/E 盘根的 HalouSuite/CAD 相关目录（限制深度）
            if (set.Count == 0)
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed) continue;
                    try
                    {
                        foreach (string sub in Directory.GetDirectories(d.RootDirectory.FullName))
                        {
                            string name = Path.GetFileName(sub);
                            if (name.IndexOf("halou", StringComparison.OrdinalIgnoreCase) >= 0
                                || name.IndexOf("cad", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                SafeSearch(sub, set);
                            }
                        }
                    }
                    catch { }
                }
            }

            return set.ToList();
        }

        private static void SafeSearch(string root, HashSet<string> set)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            try
            {
                foreach (string f in Directory.GetFiles(root, DllName, SearchOption.AllDirectories))
                {
                    set.Add(f);
                }
            }
            catch { }
        }

        private static bool IsCadRunning()
        {
            try { return Process.GetProcessesByName("acad").Length > 0; }
            catch { return false; }
        }

        /// <summary>
        /// 检查远端是否发布了更新版的本工具；若有：下载到临时目录，
        /// 启动一个 PowerShell 进程在本进程退出 1 秒后用新文件覆盖自身并重启。
        /// 返回 true 表示已交给 PowerShell 接管，调用方应立即退出。
        /// </summary>
        private static bool TrySelfUpdate()
        {
            try
            {
                string verText = RobustHttp.DownloadString(SelfVersionUrl + "?_t=" + DateTime.UtcNow.Ticks);
                if (string.IsNullOrWhiteSpace(verText)) return false;
                int remote;
                if (!int.TryParse(verText.Trim(), out remote)) return false;
                if (remote <= SelfVersion) return false;

                string tmpExe = Path.Combine(Path.GetTempPath(),
                    "HalouSuiteUpdater_v" + remote + "_" + Guid.NewGuid().ToString("N") + ".exe");
                RobustHttp.DownloadFile(SelfExeUrl + "?_t=" + DateTime.UtcNow.Ticks, tmpExe);
                FileInfo fi = new FileInfo(tmpExe);
                if (!fi.Exists || fi.Length < 4096) return false;

                string selfPath = Application.ExecutablePath;
                // PowerShell 单行脚本：等本进程退出 → 覆盖自身 → 重启（带 --no-self-update 防递归）
                string ps =
                    "Start-Sleep -Milliseconds 1500;" +
                    "try { Copy-Item -LiteralPath '" + tmpExe.Replace("'", "''") + "' -Destination '" + selfPath.Replace("'", "''") + "' -Force } catch { Start-Sleep 1; Copy-Item -LiteralPath '" + tmpExe.Replace("'", "''") + "' -Destination '" + selfPath.Replace("'", "''") + "' -Force };" +
                    "Start-Process -FilePath '" + selfPath.Replace("'", "''") + "' -ArgumentList '--no-self-update';" +
                    "Remove-Item -LiteralPath '" + tmpExe.Replace("'", "''") + "' -Force -ErrorAction SilentlyContinue";

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" + ps.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                // 自更新失败不影响主流程
                return false;
            }
        }

        private static string ExtractValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string token = "\"" + key + "\"";
            int i = json.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i = json.IndexOf(':', i);
            if (i < 0) return null;
            int q1 = json.IndexOf('"', i);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }
    }

    /// <summary>
    /// 鲁棒下载：依次尝试 .NET WebClient → PowerShell Invoke-WebRequest → curl.exe，
    /// 每种再尝试 jsDelivr 镜像。任一通道+任一源成功即返回。
    /// </summary>
    internal static class RobustHttp
    {
        public static string DownloadString(string url)
        {
            EnsureTls();
            Exception last = null;
            foreach (string u in EnumerateUrls(url))
            {
                try { return WcGetString(u); } catch (Exception e1) { last = e1; }
                try { return PsGetString(u); } catch (Exception e2) { last = e2; }
                try { return CurlGetString(u); } catch (Exception e3) { last = e3; }
            }
            throw last != null ? last : new InvalidOperationException("下载失败：" + url);
        }

        public static void DownloadFile(string url, string destPath)
        {
            EnsureTls();
            Exception last = null;
            foreach (string u in EnumerateUrls(url))
            {
                try { WcGetFile(u, destPath); if (FileOk(destPath)) return; } catch (Exception e1) { last = e1; SafeDel(destPath); }
                try { PsGetFile(u, destPath); if (FileOk(destPath)) return; } catch (Exception e2) { last = e2; SafeDel(destPath); }
                try { CurlGetFile(u, destPath); if (FileOk(destPath)) return; } catch (Exception e3) { last = e3; SafeDel(destPath); }
            }
            throw last != null ? last : new InvalidOperationException("下载失败：" + url);
        }

        private static void EnsureTls()
        {
            try
            {
                const SecurityProtocolType tls12 = (SecurityProtocolType)3072;
                const SecurityProtocolType tls11 = (SecurityProtocolType)768;
                ServicePointManager.SecurityProtocol |= tls12 | tls11;
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
            }
            catch { }
        }

        private static IEnumerable<string> EnumerateUrls(string url)
        {
            yield return url;
            string mirror = ToJsDelivr(url);
            if (!string.IsNullOrEmpty(mirror)) yield return mirror;
        }

        private static string ToJsDelivr(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            const string p = "https://raw.githubusercontent.com/";
            if (!url.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return null;
            int q = url.IndexOf('?');
            string clean = q >= 0 ? url.Substring(0, q) : url;
            string query = q >= 0 ? url.Substring(q) : "";
            string rest = clean.Substring(p.Length);
            string[] parts = rest.Split(new[] { '/' }, 4);
            if (parts.Length < 4) return null;
            return string.Format("https://cdn.jsdelivr.net/gh/{0}/{1}@{2}/{3}{4}",
                parts[0], parts[1], parts[2], parts[3], query);
        }

        private static bool FileOk(string p) { try { return new FileInfo(p).Length > 0; } catch { return false; } }
        private static void SafeDel(string p) { try { File.Delete(p); } catch { } }

        private static string WcGetString(string url)
        {
            using (WebClient c = new WebClient())
            {
                c.Encoding = Encoding.UTF8;
                c.Headers["User-Agent"] = "HalouSuiteUpdater";
                return c.DownloadString(url);
            }
        }
        private static void WcGetFile(string url, string dest)
        {
            using (WebClient c = new WebClient())
            {
                c.Headers["User-Agent"] = "HalouSuiteUpdater";
                c.DownloadFile(url, dest);
            }
        }

        private static string PsGetString(string url)
        {
            string cmd = "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]'Tls12,Tls11';" +
                         "$r=Invoke-WebRequest -UseBasicParsing -TimeoutSec 30 -Uri '" + Esc(url) + "';" +
                         "[Console]::OutputEncoding=[Text.Encoding]::UTF8;Write-Output $r.Content";
            string o, err; int code;
            Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd.Replace("\"", "\\\"") + "\"",
                40000, true, out o, out code, out err);
            if (code != 0) throw new InvalidOperationException("powershell " + code + "：" + err);
            return o;
        }
        private static void PsGetFile(string url, string dest)
        {
            string cmd = "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]'Tls12,Tls11';" +
                         "Invoke-WebRequest -UseBasicParsing -TimeoutSec 60 -Uri '" + Esc(url) + "' -OutFile '" + Esc(dest) + "'";
            string o, err; int code;
            Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd.Replace("\"", "\\\"") + "\"",
                90000, false, out o, out code, out err);
            if (code != 0) throw new InvalidOperationException("powershell " + code + "：" + err);
        }
        private static string CurlGetString(string url)
        {
            string args = "-fsSL --max-time 30 -A HalouSuiteUpdater \"" + url.Replace("\"", "\\\"") + "\"";
            string o, err; int code;
            Run("curl.exe", args, 40000, true, out o, out code, out err);
            if (code != 0) throw new InvalidOperationException("curl " + code + "：" + err);
            return o;
        }
        private static void CurlGetFile(string url, string dest)
        {
            string args = "-fsSL --max-time 60 -A HalouSuiteUpdater -o \"" + dest.Replace("\"", "\\\"") + "\" \"" + url.Replace("\"", "\\\"") + "\"";
            string o, err; int code;
            Run("curl.exe", args, 90000, false, out o, out code, out err);
            if (code != 0) throw new InvalidOperationException("curl " + code + "：" + err);
        }

        private static string Esc(string s) { return s == null ? "" : s.Replace("'", "''"); }

        private static void Run(string fileName, string args, int timeoutMs, bool captureStdout,
            out string stdout, out int exitCode, out string stderr)
        {
            ProcessStartInfo psi = new ProcessStartInfo(fileName, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            if (captureStdout)
            {
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
            }
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                string outBuf = captureStdout ? p.StandardOutput.ReadToEnd() : "";
                string errBuf = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException(fileName + " 超时");
                }
                stdout = outBuf;
                stderr = errBuf;
                exitCode = p.ExitCode;
            }
        }
    }
}
