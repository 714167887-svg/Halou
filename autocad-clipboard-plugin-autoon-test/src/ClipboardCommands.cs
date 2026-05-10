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
    public class ClipboardCommands : IExtensionApplication
    {
        private const string FormatPrefix = "JSQCAD:";
        private static readonly HalouSuiteManager SuiteManager = new HalouSuiteManager();

        internal static bool IsFeatureAuthorizedForLisp(string featureId)
        {
            try
            {
                SuiteManager.Initialize();
                return SuiteManager.LicenseStatus != LicenseStatus.Denied
                    && SuiteManager.IsFeatureAllowed(featureId);
            }
            catch
            {
                return false;
            }
        }

        public void Initialize()
        {
            // v1.1.39: 强制启用 TLS 1.2 / 1.1，避免 .NET Framework 4.x 默认 TLS 1.0 被 GitHub 拒绝
            // （表现：授权校验报"无效的 JSON 基元: ."、面板显示无法联网/版本不更新）。
            try
            {
                const SecurityProtocolType tls12 = (SecurityProtocolType)3072;
                const SecurityProtocolType tls11 = (SecurityProtocolType)768;
                ServicePointManager.SecurityProtocol |= tls12 | tls11;
            }
            catch { }

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                EnablePasteClipHook(doc, silent: true);
                doc.Editor.WriteMessage(
                    "\nJSQ clipboard plugin loaded. Ctrl+V is now routed through this plugin for the current AutoCAD session.");
            }

            SuiteManager.Initialize();
        }

        public void Terminate()
        {
            SuiteManager.Dispose();
        }

        [CommandMethod("HALOU")]
        public void ShowHalouSuite()
        {
            SuiteManager.ShowPalette();
        }

        [CommandMethod("HALOUTOGGLE")]
        public void ToggleHalouSuite()
        {
            SuiteManager.TogglePalette();
        }

        [CommandMethod("HALOUREFRESH")]
        public void RefreshHalouSuite()
        {
            SuiteManager.RefreshManifest(manual: true);
        }

        [CommandMethod("HALOUZK")]
        public void RunZkFeature()
        {
            SuiteManager.RunFeatureById("zk");
        }

        [CommandMethod("HALOUKB")]
        public void RunKbFeature()
        {
            SuiteManager.RunFeatureById("kb");
        }

        [CommandMethod("JSQHOOKON")]
        public void HookPasteClip()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            EnablePasteClipHook(doc, silent: false);
        }

        [CommandMethod("JSQHOOKOFF")]
        public void UnhookPasteClip()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            doc.Editor.WriteMessage("\nRestoring native PASTECLIP for this AutoCAD session.");
            doc.SendStringToExecute("._REDEFINE PASTECLIP ", true, false, false);
        }

        [CommandMethod("JSQPASTE")]
        public void PasteFromClipboard()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            Editor ed = doc.Editor;
            string raw = ReadClipboardText();
            if (string.IsNullOrWhiteSpace(raw))
            {
                ed.WriteMessage("\nClipboard is empty or does not contain text.");
                return;
            }

            try
            {
                int count = InsertPayload(raw, doc, promptForInsertionPoint: true);
                ed.WriteMessage("\nJSQPASTE created {0} entit{1}.", count, count == 1 ? "y" : "ies");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nJSQPASTE failed: {0}", ex.Message);
            }
        }

        [CommandMethod("PASTECLIP")]
        public void PasteClipOverride()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            Editor ed = doc.Editor;
            string raw = ReadClipboardText();
            if (!LooksLikeJsqPayload(raw))
            {
                InvokeNativePasteClip(doc);
                return;
            }

            try
            {
                int count = InsertPayload(raw, doc, promptForInsertionPoint: true);
                ed.WriteMessage("\nPASTECLIP routed to JSQCAD and created {0} entit{1}.", count, count == 1 ? "y" : "ies");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nJSQCAD clipboard payload is invalid: {0}", ex.Message);
            }
        }

        [CommandMethod("JSQPASTEFILE")]
        public void PasteFromFile()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            Editor ed = doc.Editor;
            PromptOpenFileOptions options = new PromptOpenFileOptions("\nSelect a JSQCAD JSON file");
            options.Filter = "JSON Files (*.json)|*.json|Text Files (*.txt)|*.txt|All Files (*.*)|*.*";
            PromptFileNameResult result = ed.GetFileNameForOpen(options);
            if (result.Status != PromptStatus.OK)
            {
                return;
            }

            try
            {
                string raw = File.ReadAllText(result.StringResult);
                int count = InsertPayload(raw, doc, promptForInsertionPoint: true);
                ed.WriteMessage("\nJSQPASTEFILE created {0} entit{1}.", count, count == 1 ? "y" : "ies");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nJSQPASTEFILE failed: {0}", ex.Message);
            }
        }

        private static int InsertPayload(string raw, Document doc, bool promptForInsertionPoint)
        {
            CadClipboardPayload payload = ParsePayload(raw);
            if (payload == null)
            {
                throw new InvalidOperationException("Could not parse payload.");
            }

            if (payload.entities == null || payload.entities.Count == 0)
            {
                throw new InvalidOperationException("Payload does not contain any entities.");
            }

            Editor ed = doc.Editor;
            Database db = doc.Database;
            double unitScale = ResolveUnitScale(payload.units);
            Point2d basePoint = ToPoint2d(payload.basePoint, unitScale, 0.0, 0.0);
            Point3d insertionPoint = Point3d.Origin;

            if (promptForInsertionPoint)
            {
                PromptPointOptions pointOptions = new PromptPointOptions("\nSpecify insertion point <0,0>: ");
                pointOptions.AllowNone = true;
                PromptPointResult pointResult = ed.GetPoint(pointOptions);
                if (pointResult.Status == PromptStatus.Cancel)
                {
                    throw new InvalidOperationException("Command canceled.");
                }

                if (pointResult.Status == PromptStatus.OK)
                {
                    insertionPoint = pointResult.Value;
                }
            }

            double offsetX = insertionPoint.X - basePoint.X;
            double offsetY = insertionPoint.Y - basePoint.Y;

            int created = 0;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                EnsureLayers(payload, db, tr);

                foreach (CadEntityDefinition entityDef in payload.entities)
                {
                    Entity entity = BuildEntity(entityDef, unitScale, offsetX, offsetY);
                    if (entity == null)
                    {
                        continue;
                    }

                    ApplyEntityProperties(entity, entityDef, db, tr);
                    currentSpace.AppendEntity(entity);
                    tr.AddNewlyCreatedDBObject(entity, true);
                    created++;
                }

                tr.Commit();
            }

            return created;
        }

        private static CadClipboardPayload ParsePayload(string raw)
        {
            string normalized = NormalizePayload(raw);
            JavaScriptSerializer serializer = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };

            CadClipboardPayload payload = serializer.Deserialize<CadClipboardPayload>(normalized);
            if (payload == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(payload.format) &&
                !payload.format.Equals("JSQCAD/1.0", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Unsupported format: " + payload.format);
            }

            return payload;
        }

        private static string NormalizePayload(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            string normalized = raw.Trim();
            if (normalized.StartsWith(FormatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(FormatPrefix.Length).Trim();
            }

            return normalized;
        }

        private static bool LooksLikeJsqPayload(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            string normalized = raw.TrimStart();
            if (normalized.StartsWith(FormatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalized.StartsWith("{", StringComparison.Ordinal) &&
                   normalized.IndexOf("\"format\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   normalized.IndexOf("JSQCAD/1.0", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadClipboardText()
        {
            if (Clipboard.ContainsText(System.Windows.Forms.TextDataFormat.UnicodeText))
            {
                return Clipboard.GetText(System.Windows.Forms.TextDataFormat.UnicodeText);
            }

            if (Clipboard.ContainsText())
            {
                return Clipboard.GetText();
            }

            return string.Empty;
        }

        private static void InvokeNativePasteClip(Document doc)
        {
            doc.SendStringToExecute("._PASTECLIP ", true, false, false);
        }

        private static void EnablePasteClipHook(Document doc, bool silent)
        {
            if (!silent)
            {
                doc.Editor.WriteMessage(
                    "\nEnabling Ctrl+V hook for this AutoCAD session. Clipboard text with JSQCAD: will be drawn as CAD geometry.");
            }

            doc.SendStringToExecute("._UNDEFINE PASTECLIP ", true, false, false);
        }

        private static void EnsureLayers(CadClipboardPayload payload, Database db, Transaction tr)
        {
            if (payload.layers == null || payload.layers.Count == 0)
            {
                return;
            }

            foreach (CadLayerDefinition layer in payload.layers)
            {
                if (layer == null || string.IsNullOrWhiteSpace(layer.name))
                {
                    continue;
                }

                EnsureLayer(layer.name, layer.colorIndex, db, tr);
            }
        }

        private static ObjectId EnsureLayer(string layerName, short? colorIndex, Database db, Transaction tr)
        {
            LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(layerName))
            {
                return layerTable[layerName];
            }

            layerTable.UpgradeOpen();
            LayerTableRecord record = new LayerTableRecord
            {
                Name = layerName
            };
            if (colorIndex.HasValue)
            {
                record.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, colorIndex.Value);
            }

            ObjectId layerId = layerTable.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return layerId;
        }

        private static void ApplyEntityProperties(Entity entity, CadEntityDefinition entityDef, Database db, Transaction tr)
        {
            if (!string.IsNullOrWhiteSpace(entityDef.layer))
            {
                EnsureLayer(entityDef.layer, entityDef.colorIndex, db, tr);
                entity.Layer = entityDef.layer;
            }

            if (entityDef.colorIndex.HasValue)
            {
                entity.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, entityDef.colorIndex.Value);
            }
        }

        private static Entity BuildEntity(CadEntityDefinition entityDef, double unitScale, double offsetX, double offsetY)
        {
            if (entityDef == null || string.IsNullOrWhiteSpace(entityDef.type))
            {
                return null;
            }

            string type = entityDef.type.Trim().ToLowerInvariant();
            switch (type)
            {
                case "polyline":
                    return BuildPolyline(entityDef, unitScale, offsetX, offsetY);
                case "line":
                    return BuildLine(entityDef, unitScale, offsetX, offsetY);
                case "circle":
                    return BuildCircle(entityDef, unitScale, offsetX, offsetY);
                case "arc":
                    return BuildArc(entityDef, unitScale, offsetX, offsetY);
                case "text":
                    return BuildText(entityDef, unitScale, offsetX, offsetY);
                default:
                    throw new InvalidOperationException("Unsupported entity type: " + entityDef.type);
            }
        }

        private static Entity BuildPolyline(CadEntityDefinition entityDef, double unitScale, double offsetX, double offsetY)
        {
            if (entityDef.vertices == null || entityDef.vertices.Length < 2)
            {
                throw new InvalidOperationException("Polyline must contain at least two vertices.");
            }

            Polyline polyline = new Polyline();
            for (int i = 0; i < entityDef.vertices.Length; i++)
            {
                double[] vertex = entityDef.vertices[i];
                Point2d point = ToPoint2d(vertex, unitScale, offsetX, offsetY);
                double bulge = 0.0;
                if (entityDef.bulges != null && i < entityDef.bulges.Length)
                {
                    bulge = entityDef.bulges[i];
                }

                polyline.AddVertexAt(i, point, bulge, 0.0, 0.0);
            }

            polyline.Closed = entityDef.closed;
            return polyline;
        }

        private static Entity BuildLine(CadEntityDefinition entityDef, double unitScale, double offsetX, double offsetY)
        {
            Point3d start = ToPoint3d(entityDef.start, unitScale, offsetX, offsetY);
            Point3d end = ToPoint3d(entityDef.end, unitScale, offsetX, offsetY);
            return new Line(start, end);
        }

        private static Entity BuildCircle(CadEntityDefinition entityDef, double unitScale, double offsetX, double offsetY)
        {
            Point3d center = ToPoint3d(entityDef.center, unitScale, offsetX, offsetY);
            if (!entityDef.radius.HasValue)
            {
                throw new InvalidOperationException("Circle requires radius.");
            }

            return new Circle(center, Vector3d.ZAxis, entityDef.radius.Value * unitScale);
        }

        private static Entity BuildArc(CadEntityDefinition entityDef, double unitScale, double offsetX, double offsetY)
        {
            Point3d center = ToPoint3d(entityDef.center, unitScale, offsetX, offsetY);
            if (!entityDef.radius.HasValue || !entityDef.startAngle.HasValue || !entityDef.endAngle.HasValue)
            {
                throw new InvalidOperationException("Arc requires center, radius, startAngle and endAngle.");
            }

            return new Arc(
                center,
                entityDef.radius.Value * unitScale,
                DegreesToRadians(entityDef.startAngle.Value),
                DegreesToRadians(entityDef.endAngle.Value));
        }

        private static Entity BuildText(CadEntityDefinition entityDef, double unitScale, double offsetX, double offsetY)
        {
            if (string.IsNullOrWhiteSpace(entityDef.value))
            {
                throw new InvalidOperationException("Text requires value.");
            }

            DBText text = new DBText
            {
                Position = ToPoint3d(entityDef.position, unitScale, offsetX, offsetY),
                Height = (entityDef.height ?? 5.0) * unitScale,
                TextString = entityDef.value,
                Rotation = DegreesToRadians(entityDef.rotation ?? 0.0)
            };
            return text;
        }

        private static Point2d ToPoint2d(double[] coordinates, double unitScale, double offsetX, double offsetY)
        {
            if (coordinates == null || coordinates.Length < 2)
            {
                return new Point2d(offsetX, offsetY);
            }

            return new Point2d(
                coordinates[0] * unitScale + offsetX,
                coordinates[1] * unitScale + offsetY);
        }

        private static Point3d ToPoint3d(double[] coordinates, double unitScale, double offsetX, double offsetY)
        {
            Point2d point = ToPoint2d(coordinates, unitScale, offsetX, offsetY);
            double z = 0.0;
            if (coordinates != null && coordinates.Length > 2)
            {
                z = coordinates[2] * unitScale;
            }

            return new Point3d(point.X, point.Y, z);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static double ResolveUnitScale(string units)
        {
            string normalized = string.IsNullOrWhiteSpace(units)
                ? "mm"
                : units.Trim().ToLowerInvariant();

            switch (normalized)
            {
                case "mm":
                case "millimeter":
                case "millimeters":
                    return 1.0;
                case "cm":
                case "centimeter":
                case "centimeters":
                    return 10.0;
                case "m":
                case "meter":
                case "meters":
                    return 1000.0;
                case "in":
                case "inch":
                case "inches":
                    return 25.4;
                default:
                    throw new InvalidOperationException("Unsupported units: " + units);
            }
        }
    }
}
