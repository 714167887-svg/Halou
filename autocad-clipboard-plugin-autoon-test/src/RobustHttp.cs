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
    /// <summary>
    /// 鲁棒下载：依次尝试 .NET WebClient、PowerShell Invoke-WebRequest、curl.exe，
    /// 每种方式再尝试 jsDelivr 镜像。任意一条通道成功即返回。
    /// 目的：当 GitHub raw 因 TLS / CDN / 路由被任一单点封死时，dll 仍能拉到 license.json 与新 dll，
    /// 不会再陷入 1.1.37 那种"内置代码连不上 GitHub → 自更新失效"的死锁。
    /// </summary>
    internal static class RobustHttp
    {
        public static string DownloadString(string url, IDictionary<string, string> headers)
        {
            return DownloadString(url, headers, null);
        }

        /// <summary>
        /// 带内容验证的下载：validator 返回 false 视为本通道+本源失败（多发生在中间链路把 HTTPS 劫持成 HTML 错误页），
        /// 自动 fallback 到下一通道/下一源（jsDelivr 镜像）。
        /// </summary>
        public static string DownloadString(string url, IDictionary<string, string> headers, Func<string, bool> validator)
        {
            EnsureTls();
            System.Exception last = null;
            foreach (string u in EnumerateUrls(url))
            {
                string s;
                try { s = WebClientGetString(u, headers); if (Validate(s, validator)) return s; last = new InvalidOperationException("内容校验失败 (WebClient)：" + Preview(s)); } catch (System.Exception e1) { last = e1; }
                try { s = PsGetString(u, headers); if (Validate(s, validator)) return s; last = new InvalidOperationException("内容校验失败 (PowerShell)：" + Preview(s)); } catch (System.Exception e2) { last = e2; }
                try { s = CurlGetString(u, headers); if (Validate(s, validator)) return s; last = new InvalidOperationException("内容校验失败 (curl)：" + Preview(s)); } catch (System.Exception e3) { last = e3; }
            }
            throw last != null ? last : new InvalidOperationException("下载失败：" + url);
        }

        public static void DownloadFile(string url, string destPath, IDictionary<string, string> headers)
        {
            DownloadFile(url, destPath, headers, 0);
        }

        /// <summary>
        /// 带最小尺寸校验的下载：低于 minBytes 视为失败（多为劫持页/限流页），自动 fallback。
        /// </summary>
        public static void DownloadFile(string url, string destPath, IDictionary<string, string> headers, long minBytes)
        {
            EnsureTls();
            System.Exception last = null;
            foreach (string u in EnumerateUrls(url))
            {
                try { WebClientGetFile(u, destPath, headers); if (FileOk(destPath, minBytes)) return; last = new InvalidOperationException("文件尺寸校验失败 (WebClient)"); } catch (System.Exception e1) { last = e1; } finally { if (!FileOk(destPath, minBytes)) SafeDel(destPath); }
                try { PsGetFile(u, destPath, headers); if (FileOk(destPath, minBytes)) return; last = new InvalidOperationException("文件尺寸校验失败 (PowerShell)"); } catch (System.Exception e2) { last = e2; } finally { if (!FileOk(destPath, minBytes)) SafeDel(destPath); }
                try { CurlGetFile(u, destPath, headers); if (FileOk(destPath, minBytes)) return; last = new InvalidOperationException("文件尺寸校验失败 (curl)"); } catch (System.Exception e3) { last = e3; } finally { if (!FileOk(destPath, minBytes)) SafeDel(destPath); }
            }
            throw last != null ? last : new InvalidOperationException("下载失败：" + url);
        }

        private static bool Validate(string s, Func<string, bool> validator)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (validator == null) return true;
            try { return validator(s); } catch { return false; }
        }

        private static string Preview(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            string t = s.Length > 80 ? s.Substring(0, 80) + "…" : s;
            return t.Replace("\r", "").Replace("\n", " ");
        }

        private static void EnsureTls()
        {
            try
            {
                const SecurityProtocolType tls12 = (SecurityProtocolType)3072;
                const SecurityProtocolType tls11 = (SecurityProtocolType)768;
                const SecurityProtocolType tls13 = (SecurityProtocolType)12288;
                ServicePointManager.SecurityProtocol |= tls12 | tls11;
                try { ServicePointManager.SecurityProtocol |= tls13; } catch { /* 旧 .NET 不支持 TLS 1.3，忽略 */ }
            }
            catch { }
        }

        private static IEnumerable<string> EnumerateUrls(string url)
        {
            yield return url;
            string mirror = ToJsDelivr(url);
            if (!string.IsNullOrEmpty(mirror)) yield return mirror;
        }

        // raw.githubusercontent.com/<owner>/<repo>/<branch>/<path>
        // → cdn.jsdelivr.net/gh/<owner>/<repo>@<branch>/<path>
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

        private static bool FileOk(string p, long minBytes = 0) { try { long len = new FileInfo(p).Length; return len > 0 && len >= minBytes; } catch { return false; } }
        private static void SafeDel(string p) { try { File.Delete(p); } catch { } }
        private static string StripBom(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s[0] == '\uFEFF') return s.Substring(1);
            return s;
        }

        // ---- 通道 1：.NET WebClient ----
        private static string WebClientGetString(string url, IDictionary<string, string> headers)
        {
            using (WebClient c = new WebClient())
            {
                c.Encoding = Encoding.UTF8;
                ApplyHeaders(c, headers);
                return StripBom(c.DownloadString(url));
            }
        }
        private static void WebClientGetFile(string url, string dest, IDictionary<string, string> headers)
        {
            using (WebClient c = new WebClient())
            {
                ApplyHeaders(c, headers);
                c.DownloadFile(url, dest);
            }
        }
        private static void ApplyHeaders(WebClient c, IDictionary<string, string> headers)
        {
            if (headers == null) return;
            foreach (KeyValuePair<string, string> kv in headers)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                c.Headers[kv.Key] = kv.Value ?? "";
            }
        }

        // ---- 通道 2：PowerShell Invoke-WebRequest ----
        private static string PsGetString(string url, IDictionary<string, string> headers)
        {
            string cmd = "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]'Tls12,Tls11';" +
                         "$r=Invoke-WebRequest -UseBasicParsing -TimeoutSec 30 -Uri '" + EscapePs(url) + "'" +
                         BuildPsHeaderArg(headers) + ";[Console]::OutputEncoding=[Text.Encoding]::UTF8;Write-Output $r.Content";
            string output;
            int code;
            string err;
            RunProcess("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd.Replace("\"", "\\\"") + "\"",
                40000, true, out output, out code, out err);
            if (code != 0) throw new InvalidOperationException("powershell 退出码 " + code + "：" + err);
            return StripBom(output);
        }
        private static void PsGetFile(string url, string dest, IDictionary<string, string> headers)
        {
            string cmd = "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]'Tls12,Tls11';" +
                         "Invoke-WebRequest -UseBasicParsing -TimeoutSec 60 -Uri '" + EscapePs(url) + "'" +
                         BuildPsHeaderArg(headers) + " -OutFile '" + EscapePs(dest) + "'";
            string output;
            int code;
            string err;
            RunProcess("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd.Replace("\"", "\\\"") + "\"",
                90000, false, out output, out code, out err);
            if (code != 0) throw new InvalidOperationException("powershell 退出码 " + code + "：" + err);
        }
        private static string BuildPsHeaderArg(IDictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0) return "";
            StringBuilder sb = new StringBuilder(" -Headers @{");
            bool first = true;
            foreach (KeyValuePair<string, string> kv in headers)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (!first) sb.Append(';');
                sb.Append("'").Append(EscapePs(kv.Key)).Append("'='").Append(EscapePs(kv.Value ?? "")).Append("'");
                first = false;
            }
            sb.Append('}');
            return sb.ToString();
        }
        private static string EscapePs(string s) { return s == null ? "" : s.Replace("'", "''"); }

        // ---- 通道 3：curl.exe (Win10 1803+ 自带，连接栈独立于 .NET) ----
        private static string CurlGetString(string url, IDictionary<string, string> headers)
        {
            string args = "-fsSL --max-time 30 " + BuildCurlHeaderArgs(headers) + " \"" + url.Replace("\"", "\\\"") + "\"";
            string output;
            int code;
            string err;
            RunProcess("curl.exe", args, 40000, true, out output, out code, out err);
            if (code != 0) throw new InvalidOperationException("curl 退出码 " + code + "：" + err);
            return StripBom(output);
        }
        private static void CurlGetFile(string url, string dest, IDictionary<string, string> headers)
        {
            string args = "-fsSL --max-time 60 " + BuildCurlHeaderArgs(headers) +
                          " -o \"" + dest.Replace("\"", "\\\"") + "\" \"" + url.Replace("\"", "\\\"") + "\"";
            string output;
            int code;
            string err;
            RunProcess("curl.exe", args, 90000, false, out output, out code, out err);
            if (code != 0) throw new InvalidOperationException("curl 退出码 " + code + "：" + err);
        }
        private static string BuildCurlHeaderArgs(IDictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in headers)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                sb.Append("-H \"").Append(kv.Key.Replace("\"", "\\\""))
                  .Append(": ").Append((kv.Value ?? "").Replace("\"", "\\\""))
                  .Append("\" ");
            }
            return sb.ToString();
        }

        private static void RunProcess(string fileName, string args, int timeoutMs,
            bool captureStdout, out string stdout, out int exitCode, out string stderr)
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
                    throw new TimeoutException(fileName + " 超时 " + timeoutMs + "ms");
                }
                stdout = outBuf;
                stderr = errBuf;
                exitCode = p.ExitCode;
            }
        }
    }
}
