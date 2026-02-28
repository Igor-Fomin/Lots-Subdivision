using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Colors;
using System.Collections.Generic;
using System;

[assembly: ExtensionApplication(typeof(Lots_Subdivision.SubdivisionApp))]

namespace Lots_Subdivision
{
    public class SubdivisionState
    {
        public ObjectId ParentId { get; set; } = ObjectId.Null;
        public Autodesk.AutoCAD.DatabaseServices.Polyline? FrontagePath { get; set; }
        public Point3d StartPt { get; set; }
        public Point3d EndPt { get; set; }
    }

    public class SubdivisionApp : IExtensionApplication
    {
        private SubdivisionWindow? _win;
        private static Dictionary<Document, SubdivisionState> _docStates = new Dictionary<Document, SubdivisionState>();

        public void Initialize()
        {
        }

        public void Terminate() { }

        private SubdivisionState GetCurrentState()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (!_docStates.ContainsKey(doc)) _docStates[doc] = new SubdivisionState();
            return _docStates[doc];
        }

        [CommandMethod("SubdivideUI")]
        public void SubdivideUI()
        {
            if (_win == null || !_win.IsVisible)
            {
                _win = new SubdivisionWindow();
                _win.OnPickParent += (s, e) => PickParentAndFrontage();
                _win.OnPickFrontage += (s, e) => PickFrontageOnly();
                _win.OnPickAngle += (s, e) => PickAngle();
                _win.OnExecute += (data) => ExecuteSubdivision(data);
                Application.ShowModelessWindow(_win);
            }
        }

        private void PickParentAndFrontage()
        {
            if (PickParent()) PickFrontageOnly();
        }

        private bool PickParent()
        {
            var state = GetCurrentState();
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect Parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            
            var per = ed.GetEntity(peo);
            if (per.Status == PromptStatus.OK)
            {
                state.ParentId = per.ObjectId;
                _win?.UpdateStatus("Parent Selected");
                return true;
            }
            return false;
        }

        private void PickFrontageOnly()
        {
            var state = GetCurrentState();
            if (state.ParentId.IsNull) { PickParent(); return; }
            
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            
            PromptPointOptions ppo1 = new PromptPointOptions("\nPick Frontage START Point on Polyline:");
            var ppr1 = ed.GetPoint(ppo1);
            if (ppr1.Status != PromptStatus.OK) return;

            PromptPointOptions ppo2 = new PromptPointOptions("\nPick Frontage END Point on Polyline:");
            var ppr2 = ed.GetPoint(ppo2);
            if (ppr2.Status != PromptStatus.OK) return;

            state.StartPt = ppr1.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            state.EndPt = ppr2.Value.TransformBy(ed.CurrentUserCoordinateSystem);

            using (var tr = state.ParentId.Database.TransactionManager.StartTransaction())
            {
                var pline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(state.ParentId, OpenMode.ForRead);
                
                double startParam = pline.GetParameterAtPoint(pline.GetClosestPointTo(state.StartPt, false));
                double endParam = pline.GetParameterAtPoint(pline.GetClosestPointTo(state.EndPt, false));

                if (startParam > endParam)
                {
                    double temp = startParam;
                    startParam = endParam;
                    endParam = temp;
                }

                state.FrontagePath = ExtractSubPolyline(pline, startParam, endParam);
                _win?.UpdateStatus("Frontage Ready");
                tr.Commit();
            }
        }

        private Autodesk.AutoCAD.DatabaseServices.Polyline ExtractSubPolyline(Autodesk.AutoCAD.DatabaseServices.Polyline source, double startParam, double endParam)
        {
            Autodesk.AutoCAD.DatabaseServices.Polyline sub = new Autodesk.AutoCAD.DatabaseServices.Polyline();
            int startIndex = (int)Math.Floor(startParam);
            int endIndex = (int)Math.Floor(endParam);

            int vCount = 0;

            Func<double, Point2d> getPt = (p) => {
                Point3d p3 = source.GetPointAtParameter(p);
                return new Point2d(p3.X, p3.Y);
            };

            if (startIndex == endIndex)
            {
                // Both points on the same segment
                double b = source.GetBulgeAt(startIndex);
                double newBulge = 0;
                if (b != 0)
                {
                    double sweep = 4.0 * Math.Atan(b);
                    newBulge = Math.Tan(sweep * (endParam - startParam) / 4.0);
                }
                sub.AddVertexAt(vCount++, getPt(startParam), newBulge, 0, 0);
                sub.AddVertexAt(vCount++, getPt(endParam), 0, 0, 0);
            }
            else
            {
                // Start segment
                double bStart = source.GetBulgeAt(startIndex);
                double startBulge = 0;
                if (bStart != 0)
                {
                    double sweep = 4.0 * Math.Atan(bStart);
                    startBulge = Math.Tan(sweep * (1.0 - (startParam - startIndex)) / 4.0);
                }
                sub.AddVertexAt(vCount++, getPt(startParam), startBulge, 0, 0);

                // Full segments in between
                for (int i = startIndex + 1; i < endIndex; i++)
                {
                    sub.AddVertexAt(vCount++, source.GetPoint2dAt(i), source.GetBulgeAt(i), 0, 0);
                }

                // End segment
                double bEnd = source.GetBulgeAt(endIndex);
                double endBulge = 0;
                if (bEnd != 0 && endParam > endIndex)
                {
                    double sweep = 4.0 * Math.Atan(bEnd);
                    endBulge = Math.Tan(sweep * (endParam - endIndex) / 4.0);
                }
                sub.AddVertexAt(vCount++, source.GetPoint2dAt(endIndex), endBulge, 0, 0);
                sub.AddVertexAt(vCount++, getPt(endParam), 0, 0, 0);
            }
            
            return sub;
        }

        private void PickAngle()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            PromptPointOptions ppo1 = new PromptPointOptions("\nPick first point for side angle:");
            var ppr1 = ed.GetPoint(ppo1);
            if (ppr1.Status != PromptStatus.OK) return;

            PromptPointOptions ppo2 = new PromptPointOptions("\nPick second point for side angle:");
            ppo2.BasePoint = ppr1.Value; ppo2.UseBasePoint = true;
            var ppr2 = ed.GetPoint(ppo2);
            if (ppr2.Status != PromptStatus.OK) return;

            Vector3d v = ppr2.Value - ppr1.Value;
            double angDeg = Math.Atan2(v.Y, v.X) * (180.0 / Math.PI);
            
            SubdivisionSettings.Angle = angDeg;
            _win?.UpdateAngleDisplay(angDeg);
            _win?.UpdateStatus("Angle Updated");
        }

        private void ExecuteSubdivision(SubdivisionWindow.SubdivisionData data)
        {
            var state = GetCurrentState();
            if (state.ParentId.IsNull || state.ParentId.IsErased || state.FrontagePath == null) {
                Application.ShowAlertDialog("The parent parcel or frontage path is missing. Please select them again.");
                return;
            }

            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            using (DocumentLock loc = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try {
                    var parentPline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(state.ParentId, OpenMode.ForWrite);
                    if (parentPline.IsErased) 
                    {
                        ed.WriteMessage("\nError: Parent polyline was erased.");
                        return;
                    }
                    var parentRegion = CreateRegionFromPolyline(parentPline);
                    if (parentRegion == null) return;

                    if (parentRegion.Area < data.TargetArea)
                    {
                        ed.WriteMessage("\nError: Target area exceeds parent parcel area.");
                        parentRegion.Dispose();
                        return;
                    }

                    // Robust Direction Sensing
                    Vector3d frontageDir = (state.FrontagePath.EndPoint - state.FrontagePath.StartPoint).GetNormal();
                    Vector3d sideVec = frontageDir.RotateBy(data.Angle, Vector3d.ZAxis);
                    
                    using (var regPos = CreateClippedRegion(parentRegion, state.FrontagePath, state.FrontagePath.Length, data.Depth, sideVec))
                    using (var regNeg = CreateClippedRegion(parentRegion, state.FrontagePath, state.FrontagePath.Length, data.Depth, -sideVec))
                    {
                        double posArea = regPos?.Area ?? 0;
                        double negArea = regNeg?.Area ?? 0;
                        if (negArea > posArea) sideVec = -sideVec;
                    }

                    Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
                    
                    if (data.LockWidth) SolveForDepth(parentRegion, state.FrontagePath, data, sideVec, ed, out resultRegion);
                    else SolveForWidth(parentRegion, state.FrontagePath, data, sideVec, ed, out resultRegion);

                    if (resultRegion != null && resultRegion.Area > 0.001)
                    {
                        if (Math.Abs(resultRegion.Area - data.TargetArea) > 0.1)
                        {
                            ed.WriteMessage($"\nWarning: Could not achieve exact target area. Achieved: {resultRegion.Area:F2}m2 (Diff: {resultRegion.Area - data.TargetArea:F2})");
                        }

                        using (var lotClone = (Autodesk.AutoCAD.DatabaseServices.Region)resultRegion.Clone())
                            parentRegion.BooleanOperation(BooleanOperationType.BoolSubtract, lotClone);

                        UpdateParentParcel(parentPline, parentRegion, tr, db, state);
                        FinalizeLot(db, tr, resultRegion, ed);
                        ed.WriteMessage("\nSubdivision complete.");
                        resultRegion.Dispose();
                    }
                    else
                    {
                        ed.WriteMessage("\nError: Could not solve within parcel boundaries. The target area may be unreachable.");
                        resultRegion?.Dispose();
                    }
                    
                    parentRegion.Dispose();
                    tr.Commit();
                } catch (System.Exception ex) {
                    ed.WriteMessage("\nError in ExecuteSubdivision: " + ex.Message);
                    tr.Abort();
                }
            }
        }

        private void SolveForWidth(Autodesk.AutoCAD.DatabaseServices.Region parent, Autodesk.AutoCAD.DatabaseServices.Polyline frontage, SubdivisionWindow.SubdivisionData data, Vector3d sideVec, Editor ed, out Autodesk.AutoCAD.DatabaseServices.Region? res)
        {
            double min = 0; double max = frontage.Length;
            double cur = (min + max) / 2;
            res = null;
            double tolerance = 0.0001;
            
            ed.WriteMessage("\n--- Starting Width Solver ---");
            for (int i = 0; i < 50; i++)
            {
                var testRes = CreateClippedRegion(parent, frontage, cur, data.Depth, sideVec);
                double currentArea = testRes?.Area ?? 0;
                
                ed.WriteMessage($"\nIter {i}: Min={min:F2}, Max={max:F2}, Cur={cur:F2}, Area={currentArea:F2}");

                if (testRes == null || currentArea < 0.001)
                {
                    testRes?.Dispose();
                    min = cur;
                }
                else
                {
                    if (res != null) res.Dispose();
                    res = testRes;
                    if (Math.Abs(currentArea - data.TargetArea) < tolerance) break;
                    if (currentArea < data.TargetArea) min = cur; else max = cur;
                }
                cur = (min + max) / 2;
                if (Math.Abs(max - min) < 1e-8) break;
            }
        }

        private void SolveForDepth(Autodesk.AutoCAD.DatabaseServices.Region parent, Autodesk.AutoCAD.DatabaseServices.Polyline frontage, SubdivisionWindow.SubdivisionData data, Vector3d sideVec, Editor ed, out Autodesk.AutoCAD.DatabaseServices.Region? res)
        {
            Extents3d ext = parent.GeometricExtents;
            double maxDepth = ext.MinPoint.DistanceTo(ext.MaxPoint) * 5.0;
            double min = 0; double max = maxDepth;
            double cur = (min + max) / 2;
            res = null;
            double tolerance = 0.0001;
            
            ed.WriteMessage("\n--- Starting Depth Solver ---");
            for (int i = 0; i < 50; i++)
            {
                var testRes = CreateClippedRegion(parent, frontage, frontage.Length, cur, sideVec);
                double currentArea = testRes?.Area ?? 0;

                ed.WriteMessage($"\nIter {i}: Min={min:F2}, Max={max:F2}, Cur={cur:F2}, Area={currentArea:F2}");

                if (testRes == null || currentArea < 0.001)
                {
                    testRes?.Dispose();
                    min = cur;
                }
                else
                {
                    if (res != null) res.Dispose();
                    res = testRes;
                    if (Math.Abs(currentArea - data.TargetArea) < tolerance) break;
                    if (currentArea < data.TargetArea) min = cur; else max = cur;
                }
                cur = (min + max) / 2;
                if (Math.Abs(max - min) < 1e-8) break;
            }
        }

        private Autodesk.AutoCAD.DatabaseServices.Region? CreateClippedRegion(Autodesk.AutoCAD.DatabaseServices.Region parent, Autodesk.AutoCAD.DatabaseServices.Polyline frontage, double w, double d, Vector3d side)
        {
            using (var partialFrontage = ExtractFrontageOfWidth(frontage, w))
            {
                if (partialFrontage == null) return null;

                using (var box = new Autodesk.AutoCAD.DatabaseServices.Polyline())
                {
                    int n = partialFrontage.NumberOfVertices;
                    int v = 0;

                    // Side 1: Forward along the frontage
                    for (int i = 0; i < n; i++)
                    {
                        box.AddVertexAt(v++, partialFrontage.GetPoint2dAt(i), partialFrontage.GetBulgeAt(i), 0, 0);
                    }

                    // Side 2 & 3: Backward along the offset frontage
                    Vector2d offset = new Vector2d(side.X * d, side.Y * d);
                    for (int i = n - 1; i >= 0; i--)
                    {
                        // Invert bulges for the backward path
                        double b = (i == 0) ? 0 : -partialFrontage.GetBulgeAt(i - 1);
                        box.AddVertexAt(v++, partialFrontage.GetPoint2dAt(i) + offset, b, 0, 0);
                    }

                    box.Closed = true;

                    using (var br = CreateRegionFromPolyline(box))
                    {
                        if (br == null) return null;
                        
                        Autodesk.AutoCAD.DatabaseServices.Region res = (Autodesk.AutoCAD.DatabaseServices.Region)parent.Clone();
                        res.BooleanOperation(BooleanOperationType.BoolIntersect, br);
                        br.Dispose();
                        return res;
                    }
                }
            }
        }

        private Autodesk.AutoCAD.DatabaseServices.Polyline ExtractFrontageOfWidth(Autodesk.AutoCAD.DatabaseServices.Polyline frontage, double w)
        {
            if (w >= frontage.Length - 0.001) return (Autodesk.AutoCAD.DatabaseServices.Polyline)frontage.Clone();
            
            double endParam = frontage.GetParameterAtDistance(w);
            return ExtractSubPolyline(frontage, 0, endParam);
        }

        private void UpdateParentParcel(Autodesk.AutoCAD.DatabaseServices.Polyline oldPline, Autodesk.AutoCAD.DatabaseServices.Region remaining, Transaction tr, Database db, SubdivisionState state)
        {
            Autodesk.AutoCAD.DatabaseServices.Polyline newPline = RegionToPolyline(remaining);
            if (newPline == null) return;

            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            newPline.SetDatabaseDefaults();
            newPline.Layer = GetOrCreateLayer(db, tr, "EXISTING_PARCEL", Color.FromColorIndex(ColorMethod.ByAci, 1)); // Red
            btr.AppendEntity(newPline);
            tr.AddNewlyCreatedDBObject(newPline, true);
            
            state.ParentId = newPline.ObjectId;
            oldPline.UpgradeOpen(); oldPline.Erase();
        }

        private void FinalizeLot(Database db, Transaction tr, Autodesk.AutoCAD.DatabaseServices.Region region, Editor ed)
        {
            double preciseArea = region.Area;
            Autodesk.AutoCAD.DatabaseServices.Polyline lotPline = RegionToPolyline(region);
            if (lotPline == null) return;

            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            lotPline.SetDatabaseDefaults();
            lotPline.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS", Color.FromColorIndex(ColorMethod.ByAci, 3), LineWeight.LineWeight030); // Green
            btr.AppendEntity(lotPline);
            tr.AddNewlyCreatedDBObject(lotPline, true);
            
            AddSurveyLabels(db, tr, btr, lotPline, preciseArea);
        }

        private Autodesk.AutoCAD.DatabaseServices.Polyline RegionToPolyline(Autodesk.AutoCAD.DatabaseServices.Region region)
        {
            using (DBObjectCollection exploded = new DBObjectCollection())
            {
                region.Explode(exploded);
                if (exploded.Count == 0) return null!;

                List<Curve> curves = new List<Curve>();
                foreach (DBObject obj in exploded)
                {
                    if (obj is Curve curve) curves.Add(curve);
                    else obj.Dispose();
                }

                if (curves.Count == 0) return null!;

                Autodesk.AutoCAD.DatabaseServices.Polyline pline = new Autodesk.AutoCAD.DatabaseServices.Polyline();
                double tolerance = 0.001;

                // Simple daisy-chain logic for a single loop
                Curve first = curves[0];
                Point3d currentPt = first.StartPoint;
                Point3d nextPt = first.EndPoint;
                
                pline.AddVertexAt(0, new Point2d(currentPt.X, currentPt.Y), GetBulge(first, false), 0, 0);
                curves.RemoveAt(0);

                int v = 1;
                while (curves.Count > 0)
                {
                    bool found = false;
                    for (int i = 0; i < curves.Count; i++)
                    {
                        Curve c = curves[i];
                        if (c.StartPoint.DistanceTo(nextPt) < tolerance)
                        {
                            pline.AddVertexAt(v++, new Point2d(c.StartPoint.X, c.StartPoint.Y), GetBulge(c, false), 0, 0);
                            nextPt = c.EndPoint;
                            curves.RemoveAt(i);
                            found = true;
                            break;
                        }
                        else if (c.EndPoint.DistanceTo(nextPt) < tolerance)
                        {
                            pline.AddVertexAt(v++, new Point2d(c.EndPoint.X, c.EndPoint.Y), GetBulge(c, true), 0, 0);
                            nextPt = c.StartPoint;
                            curves.RemoveAt(i);
                            found = true;
                            break;
                        }
                    }
                    if (!found) break; // Should probably handle multiple loops, but for a simple lot this is usually enough
                }

                foreach (Curve c in curves) c.Dispose();
                foreach (DBObject obj in exploded) if (!obj.IsDisposed) obj.Dispose();

                if (pline.NumberOfVertices > 0) pline.Closed = true;
                return pline;
            }
        }

        private double GetBulge(Curve curve, bool reverse)
        {
            if (curve is Arc arc)
            {
                double angle = arc.EndAngle - arc.StartAngle;
                if (angle < 0) angle += 2 * Math.PI;
                double bulge = Math.Tan(angle / 4.0);
                return reverse ? -bulge : bulge;
            }
            return 0;
        }

        private void AddSurveyLabels(Database db, Transaction tr, BlockTableRecord btr, Autodesk.AutoCAD.DatabaseServices.Polyline pline, double preciseArea)
        {
            string textLayer = GetOrCreateLayer(db, tr, "PROPOSED_TEXT", Color.FromColorIndex(ColorMethod.ByAci, 2));

            // Area Label
            Extents3d ext = pline.GeometricExtents;
            Point3d centroid = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
            using (MText areaText = new MText())
            {
                areaText.Contents = $"Area: {preciseArea:F2} m2";
                areaText.Location = centroid;
                areaText.Height = 1.0; areaText.Layer = textLayer;
                btr.AppendEntity(areaText);
                tr.AddNewlyCreatedDBObject(areaText, true);
            }

            // Segment Labels
            for (int i = 0; i < pline.NumberOfVertices; i++)
            {
                Point3d start = pline.GetPoint3dAt(i);
                Point3d end = pline.GetPoint3dAt((i + 1) % pline.NumberOfVertices);
                double len = start.DistanceTo(end);
                Point3d mid = start + (end - start) * 0.5;
                double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);

                using (DBText txt = new DBText())
                {
                    txt.TextString = len.ToString("F3") + "m";
                    txt.Position = mid;
                    txt.Height = 0.5;
                    txt.Rotation = angle;
                    txt.Layer = textLayer;
                    txt.HorizontalMode = TextHorizontalMode.TextCenter;
                    txt.AlignmentPoint = mid;
                    btr.AppendEntity(txt);
                    tr.AddNewlyCreatedDBObject(txt, true);
                }
            }
        }

        private string GetOrCreateLayer(Database db, Transaction tr, string name, Color? color = null, LineWeight lw = LineWeight.ByLayer)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(name))
            {
                lt.UpgradeOpen();
                LayerTableRecord ltr = new LayerTableRecord();
                ltr.Name = name;
                if (color != null) ltr.Color = color;
                ltr.LineWeight = lw;
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
            return name;
        }

        private Autodesk.AutoCAD.DatabaseServices.Region? CreateRegionFromPolyline(Autodesk.AutoCAD.DatabaseServices.Polyline pline)
        {
            using (var flat = (Autodesk.AutoCAD.DatabaseServices.Polyline)pline.Clone())
            {
                flat.Elevation = 0;
                using (var curves = new DBObjectCollection { flat })
                using (var regions = Autodesk.AutoCAD.DatabaseServices.Region.CreateFromCurves(curves))
                {
                    if (regions.Count > 0)
                    {
                        var r = (Autodesk.AutoCAD.DatabaseServices.Region)regions[0];
                        for (int i = 1; i < regions.Count; i++) regions[i].Dispose();
                        return r;
                    }
                    return null;
                }
            }
        }
    }
}
