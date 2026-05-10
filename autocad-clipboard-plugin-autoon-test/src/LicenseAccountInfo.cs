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
    internal sealed class LicenseAccountInfo
    {
        public bool Allowed { get; set; }
        public string Reason { get; set; }
        public string Note { get; set; }
        public string ExpiresAt { get; set; }
        // 功能白名单：null 或包含 "*" 表示不限制；否则只允许列出的功能 Id（大小写不敏感）
        public List<string> Features { get; set; }
    }

}
