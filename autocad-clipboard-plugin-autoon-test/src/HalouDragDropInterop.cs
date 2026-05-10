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
    internal static class HalouDragDropInterop
    {
        public const int S_OK = 0;
        public const int DROPEFFECT_NONE = 0;
        public const int DROPEFFECT_COPY = 1;
        public const short CF_HDROP = 15;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTL { public int x; public int y; }

        [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDropTarget
        {
            [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
            [PreserveSig] int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);
            [PreserveSig] int DragLeave();
            [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
        }

        [DllImport("ole32.dll")]
        public static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget pDropTarget);

        [DllImport("ole32.dll")]
        public static extern int RevokeDragDrop(IntPtr hwnd);

        [DllImport("ole32.dll")]
        public static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM pmedium);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetProp(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint DragQueryFile(IntPtr hDrop, uint iFile, System.Text.StringBuilder lpszFile, int cch);

        public static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif", ".webp"
        };

        public static bool TryExtractFiles(System.Runtime.InteropServices.ComTypes.IDataObject data, out List<string> files)
        {
            files = new List<string>();
            if (data == null) return false;
            var fmt = new System.Runtime.InteropServices.ComTypes.FORMATETC
            {
                cfFormat = CF_HDROP,
                dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                ptd = IntPtr.Zero,
                tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
            };
            System.Runtime.InteropServices.ComTypes.STGMEDIUM medium;
            try { data.GetData(ref fmt, out medium); }
            catch { return false; }
            try
            {
                IntPtr hDrop = medium.unionmember;
                if (hDrop == IntPtr.Zero) return false;
                uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    uint len = DragQueryFile(hDrop, i, null, 0);
                    var sb = new System.Text.StringBuilder((int)len + 1);
                    DragQueryFile(hDrop, i, sb, sb.Capacity);
                    files.Add(sb.ToString());
                }
                return count > 0;
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }

        public static bool AllImages(List<string> files)
        {
            if (files == null || files.Count == 0) return false;
            foreach (var p in files)
            {
                try
                {
                    string ext = Path.GetExtension(p);
                    if (string.IsNullOrEmpty(ext) || !ImageExts.Contains(ext)) return false;
                }
                catch { return false; }
            }
            return true;
        }
    }

    /// <summary>
    /// 挂在 AutoCAD 主窗口上的 OLE IDropTarget。
    /// 当拖入**全部**为图片文件时接管（触发 OLEDROP 流程），其他情况转发给原 target。
}
