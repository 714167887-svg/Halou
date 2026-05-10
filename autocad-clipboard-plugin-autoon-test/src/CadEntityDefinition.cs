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
    public class CadEntityDefinition
    {
        public string type { get; set; }
        public string layer { get; set; }
        public short? colorIndex { get; set; }
        public bool closed { get; set; }
        public double[][] vertices { get; set; }
        public double[] bulges { get; set; }
        public double[] start { get; set; }
        public double[] end { get; set; }
        public double[] center { get; set; }
        public double? radius { get; set; }
        public double? startAngle { get; set; }
        public double? endAngle { get; set; }
        public string value { get; set; }
        public double[] position { get; set; }
        public double? height { get; set; }
        public double? rotation { get; set; }
    }

}
