using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using HalouSuite.Contract;

namespace HalouSuite.Host
{
    /// <summary>
    /// 所有 [LispFunction] 的唯一注册位置。
    /// 入参 ResultBuffer 在 Host 层解析为基础类型，再交给 Payload。
    /// </summary>
    public sealed class HostLispBridge
    {
        private static ResultBuffer T()
        {
            return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
        }

        private static ResultBuffer Bool(bool ok)
        {
            return ok ? T() : null;
        }

        [LispFunction("halou-auth")]
        public ResultBuffer CheckFeatureAuth(ResultBuffer args)
        {
            try
            {
                string id = ReadString(args, 0);
                if (string.IsNullOrWhiteSpace(id)) return null;
                IPayload p = HostExtension.CurrentPayload;
                return Bool(p != null && p.IsFeatureAuthorized(id.Trim()));
            }
            catch { return null; }
        }

        [LispFunction("jt-embed-dwg")]
        public ResultBuffer JtEmbedDwg(ResultBuffer args)
        {
            try { IPayload p = HostExtension.CurrentPayload; return Bool(p != null && p.JtEmbedDwg(ReadString(args, 0), ReadString(args, 1))); } catch { return null; }
        }

        [LispFunction("jt-extract-dwg")]
        public ResultBuffer JtExtractDwg(ResultBuffer args)
        {
            try { IPayload p = HostExtension.CurrentPayload; return Bool(p != null && p.JtExtractDwg(ReadString(args, 0), ReadString(args, 1))); } catch { return null; }
        }

        [LispFunction("jt-crop-white")]
        public ResultBuffer JtCropWhite(ResultBuffer args)
        {
            try { IPayload p = HostExtension.CurrentPayload; return Bool(p != null && p.JtCropWhite(ReadString(args, 0), ReadInt(args, 1, 252))); } catch { return null; }
        }

        [LispFunction("jt-upscale-png")]
        public ResultBuffer JtUpscalePng(ResultBuffer args)
        {
            try { IPayload p = HostExtension.CurrentPayload; return Bool(p != null && p.JtUpscalePng(ReadString(args, 0), ReadInt(args, 1, 2400))); } catch { return null; }
        }

        [LispFunction("jt-png-to-clipboard")]
        public ResultBuffer JtPngToClipboard(ResultBuffer args)
        {
            try { IPayload p = HostExtension.CurrentPayload; return Bool(p != null && p.JtPngToClipboard(ReadString(args, 0))); } catch { return null; }
        }

        [LispFunction("jt-merge-png-h")]
        public ResultBuffer JtMergePngHorizontal(ResultBuffer args)
        {
            try
            {
                IPayload p = HostExtension.CurrentPayload;
                if (p == null) return null;
                TypedValue[] arr = Arr(args);
                if (arr == null || arr.Length < 2) return null;
                string outPath = arr[0].Value as string;
                if (string.IsNullOrEmpty(outPath)) return null;

                // 末尾若是数字则当 gap，其余视为 png 路径
                int gap = 16;
                int last = arr.Length - 1;
                short tcLast = arr[last].TypeCode;
                if (tcLast == (short)LispDataType.Int16 || tcLast == (short)LispDataType.Int32 || tcLast == (short)LispDataType.Double)
                {
                    try { gap = Convert.ToInt32(arr[last].Value); last--; } catch { }
                }
                System.Collections.Generic.List<string> pngs = new System.Collections.Generic.List<string>();
                for (int i = 1; i <= last; i++)
                {
                    string s = arr[i].Value as string;
                    if (!string.IsNullOrEmpty(s)) pngs.Add(s);
                }
                return Bool(p.JtMergePngHorizontal(outPath, pngs.ToArray(), gap));
            }
            catch { return null; }
        }

        [LispFunction("jt-pngs-to-clipboard")]
        public ResultBuffer JtPngsToClipboard(ResultBuffer args)
        {
            try { IPayload p = HostExtension.CurrentPayload; return Bool(p != null && p.JtPngsToClipboard(ReadStringList(args, 0))); } catch { return null; }
        }

        [LispFunction("jt-plot-png")]
        public ResultBuffer JtPlotPng(ResultBuffer args)
        {
            try
            {
                IPayload p = HostExtension.CurrentPayload;
                if (p == null) return null;
                TypedValue[] arr = Arr(args);
                if (arr == null || arr.Length < 5) return null;
                string outPath = arr[0].Value as string;
                if (string.IsNullOrEmpty(outPath)) return null;
                double x1 = Convert.ToDouble(arr[1].Value);
                double y1 = Convert.ToDouble(arr[2].Value);
                double x2 = Convert.ToDouble(arr[3].Value);
                double y2 = Convert.ToDouble(arr[4].Value);
                string media = (arr.Length >= 6 && arr[5].Value is string)
                    ? (string)arr[5].Value
                    : "Sun Hi-Res (1600.00 x 1280.00 Pixels)";
                return Bool(p.JtPlotPng(outPath, x1, y1, x2, y2, media));
            }
            catch { return null; }
        }

        // ---------- helpers ----------
        private static TypedValue[] Arr(ResultBuffer args)
        {
            return args == null ? null : args.AsArray();
        }

        private static string ReadString(ResultBuffer args, int idx)
        {
            TypedValue[] arr = Arr(args);
            if (arr == null || idx >= arr.Length) return null;
            return arr[idx].Value as string;
        }

        private static int ReadInt(ResultBuffer args, int idx, int defaultValue)
        {
            TypedValue[] arr = Arr(args);
            if (arr == null || idx >= arr.Length) return defaultValue;
            object v = arr[idx].Value;
            try { return Convert.ToInt32(v); } catch { return defaultValue; }
        }

        private static double ReadDouble(ResultBuffer args, int idx, double defaultValue)
        {
            TypedValue[] arr = Arr(args);
            if (arr == null || idx >= arr.Length) return defaultValue;
            object v = arr[idx].Value;
            try { return Convert.ToDouble(v); } catch { return defaultValue; }
        }

        private static string[] ReadStringList(ResultBuffer args, int startIdx)
        {
            TypedValue[] arr = Arr(args);
            if (arr == null || startIdx >= arr.Length) return new string[0];

            System.Collections.Generic.List<string> result = new System.Collections.Generic.List<string>();
            for (int i = startIdx; i < arr.Length; i++)
            {
                short code = arr[i].TypeCode;
                if (code == (short)LispDataType.ListBegin || code == (short)LispDataType.ListEnd) continue;
                string s = arr[i].Value as string;
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
            return result.ToArray();
        }
    }
}
