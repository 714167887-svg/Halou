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
    internal sealed class HalouImageDropTarget : HalouDragDropInterop.IDropTarget
    {
        private readonly Action<List<string>> _onImageDrop;
        private HalouDragDropInterop.IDropTarget _fallback;
        private bool _hijack;

        public HalouImageDropTarget(Action<List<string>> onImageDrop) { _onImageDrop = onImageDrop; }
        public void SetFallback(HalouDragDropInterop.IDropTarget fallback) { _fallback = fallback; }

        public int DragEnter(System.Runtime.InteropServices.ComTypes.IDataObject pDataObj, int grfKeyState, HalouDragDropInterop.POINTL pt, ref int pdwEffect)
        {
            List<string> files;
            if (HalouDragDropInterop.TryExtractFiles(pDataObj, out files) && HalouDragDropInterop.AllImages(files))
            {
                _hijack = true;
                pdwEffect = HalouDragDropInterop.DROPEFFECT_COPY;
                return HalouDragDropInterop.S_OK;
            }
            _hijack = false;
            if (_fallback != null) return _fallback.DragEnter(pDataObj, grfKeyState, pt, ref pdwEffect);
            pdwEffect = HalouDragDropInterop.DROPEFFECT_NONE;
            return HalouDragDropInterop.S_OK;
        }

        public int DragOver(int grfKeyState, HalouDragDropInterop.POINTL pt, ref int pdwEffect)
        {
            if (_hijack) { pdwEffect = HalouDragDropInterop.DROPEFFECT_COPY; return HalouDragDropInterop.S_OK; }
            if (_fallback != null) return _fallback.DragOver(grfKeyState, pt, ref pdwEffect);
            pdwEffect = HalouDragDropInterop.DROPEFFECT_NONE;
            return HalouDragDropInterop.S_OK;
        }

        public int DragLeave()
        {
            _hijack = false;
            if (_fallback != null) return _fallback.DragLeave();
            return HalouDragDropInterop.S_OK;
        }

        public int Drop(System.Runtime.InteropServices.ComTypes.IDataObject pDataObj, int grfKeyState, HalouDragDropInterop.POINTL pt, ref int pdwEffect)
        {
            try
            {
                List<string> files;
                if (HalouDragDropInterop.TryExtractFiles(pDataObj, out files) && HalouDragDropInterop.AllImages(files))
                {
                    _hijack = false;
                    pdwEffect = HalouDragDropInterop.DROPEFFECT_COPY;
                    try { if (_onImageDrop != null) _onImageDrop(files); } catch { }
                    return HalouDragDropInterop.S_OK;
                }
            }
            catch { }
            _hijack = false;
            if (_fallback != null) return _fallback.Drop(pDataObj, grfKeyState, pt, ref pdwEffect);
            pdwEffect = HalouDragDropInterop.DROPEFFECT_NONE;
            return HalouDragDropInterop.S_OK;
        }
    }



    /// <summary>
}
