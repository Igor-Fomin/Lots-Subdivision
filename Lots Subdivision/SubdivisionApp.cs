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
        public Autodesk.AutoCAD.DatabaseServices.Line? FrontageLine { get; set; }
        public Point3d PickPt { get; set; }
    }

    public class SubdivisionApp : IExtensionApplication
    {
        private SubdivisionWindow? _win;
        private static Dictionary<Document, SubdivisionState> _docStates = new Dictionary<Document, SubdivisionState>();

        public void Initialize()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage("\nSubdivision Dashboard v2.0 Loaded. Type 'SubdivideUI' to start.");
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
                // Ensure URI matches project assembly and file structure
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
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed."); // SetRejectMessage first
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
            PromptPointOptions ppo = new PromptPointOptions("\nPick Frontage Segment on Polyline:");
            var ppr = ed.GetPoint(ppo);
            
            if (ppr.Status == PromptStatus.OK)
            {
                state.PickPt = ppr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                using (var tr = state.ParentId.Database.TransactionManager.StartTransaction())
                {
                    var pline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(state.ParentId, OpenMode.ForRead);
                    Point3d closest = pline.GetClosestPointTo(state.PickPt, false);
                    double param = pline.GetParameterAtPoint(closest);
                    int idx = (int)Math.Floor(param);
                    
                    state.FrontageLine = new Autodesk.AutoCAD.DatabaseServices.Line(pline.GetPoint3dAt(idx), pline.GetPoint3dAt((idx + 1) % pline.NumberOfVertices));
                    _win?.UpdateStatus("Frontage Ready");
                    tr.Commit();
                }
            }
        }

        private void PickAngle()
        {
            var state = GetCurrentState();
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            PromptPointOptions ppo1 = new PromptPointOptions("\nPick first point for side angle:");
            var ppr1 = ed.GetPoint(ppo1);
            if (ppr1.Status != PromptStatus.OK) return;

            PromptPointOptions ppo2 = new PromptPointOptions("\nPick second point for side angle:");
            ppo2.BasePoint = ppr1.Value;
            ppo2.UseBasePoint = true;
            var ppr2 = ed.GetPoint(ppo2);
            if (ppr2.Status != PromptStatus.OK) return;

            Vector3d v = ppr2.Value - ppr1.Value;
            double angDeg = 0;
            if (state.FrontageLine != null)
            {
                Vector3d fv = state.FrontageLine.EndPoint - state.FrontageLine.StartPoint;
                double ang = fv.GetAngleTo(v, Vector3d.ZAxis);
                angDeg = ang * (180.0 / Math.PI);
            }
            else
            {
                angDeg = Math.Atan2(v.Y, v.X) * (180.0 / Math.PI);
            }
            
            SubdivisionSettings.Angle = angDeg;
            _win?.UpdateAngleDisplay(angDeg); // Sync to UI thread
            _win?.UpdateStatus("Angle Updated");
        }

        private void ExecuteSubdivision(SubdivisionWindow.SubdivisionData data)
        {
            var state = GetCurrentState();
            if (state.ParentId.IsNull || state.FrontageLine == null) {
                Application.ShowAlertDialog("Select Parent and Frontage first.");
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
                    var parentRegion = CreateRegionFromPolyline(parentPline);
                    if (parentRegion == null) return;

                    // Improved Direction Sensing: Nudge test
                    Vector3d frontageDir = (state.FrontageLine.EndPoint - state.FrontageLine.StartPoint).GetNormal();
                    Vector3d sideVec = frontageDir.RotateBy(data.Angle, Vector3d.ZAxis);
                    
                    Point3d midFrontage = state.FrontageLine.StartPoint + (frontageDir * (state.FrontageLine.Length / 2.0));
                    Point3d testPt = midFrontage + (sideVec * 0.01);
                    if (!IsPointInRegion(parentRegion, testPt)) sideVec = -sideVec;

                    Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
                    
                    if (data.LockWidth) SolveForDepth(parentRegion, state.FrontageLine, data, sideVec, out resultRegion);
                    else SolveForWidth(parentRegion, state.FrontageLine, data, sideVec, out resultRegion);

                    // Solver Fallback with proper disposal
                    if (resultRegion == null || resultRegion.Area < 0.001)
                    {
                        resultRegion?.Dispose();
                        sideVec = -sideVec;
                        if (data.LockWidth) SolveForDepth(parentRegion, state.FrontageLine, data, sideVec, out resultRegion);
                        else SolveForWidth(parentRegion, state.FrontageLine, data, sideVec, out resultRegion);
                    }

                    if (resultRegion != null && resultRegion.Area > 0)
                    {
                        // Parcel Reduction
                        using (var lotClone = (Autodesk.AutoCAD.DatabaseServices.Region)resultRegion.Clone())
                            parentRegion.BooleanOperation(BooleanOperationType.BoolSubtract, lotClone);

                        // Redraw Parent Parcel and Update State
                        UpdateParentParcel(parentPline, parentRegion, tr, db, state);
                        
                        // Finalize the new lot
                        FinalizeLot(db, tr, resultRegion, ed);
                        ed.WriteMessage("\nSubdivision complete.");
                    }
                    else
                    {
                        ed.WriteMessage("\nError: Could not calculate area inside the parcel.");
                    }
                    
                    parentRegion.Dispose();
                    tr.Commit();
                } catch (System.Exception ex) {
                    ed.WriteMessage("\nError: " + ex.Message);
                    tr.Abort();
                }
            }
        }

        private void SolveForWidth(Autodesk.AutoCAD.DatabaseServices.Region parent, Autodesk.AutoCAD.DatabaseServices.Line frontage, SubdivisionWindow.SubdivisionData data, Vector3d sideVec, out Autodesk.AutoCAD.DatabaseServices.Region? res)
        {
            double min = 0; double max = frontage.Length;
            double cur = (min + max) / 2;
            res = null;
            for (int i = 0; i < 100; i++)
            {
                if (res != null) res.Dispose();
                res = CreateClippedRegion(parent, frontage, cur, data.Depth, sideVec);
                if (Math.Abs(res.Area - data.TargetArea) < 0.001) break;
                if (res.Area < data.TargetArea) min = cur; else max = cur;
                cur = (min + max) / 2;
            }
        }

        private void SolveForDepth(Autodesk.AutoCAD.DatabaseServices.Region parent, Autodesk.AutoCAD.DatabaseServices.Line frontage, SubdivisionWindow.SubdivisionData data, Vector3d sideVec, out Autodesk.AutoCAD.DatabaseServices.Region? res)
        {
            double min = 0; double max = parent.GeometricExtents.MaxPoint.DistanceTo(parent.GeometricExtents.MinPoint) * 5;
            double cur = (min + max) / 2;
            res = null;
            for (int i = 0; i < 100; i++)
            {
                if (res != null) res.Dispose();
                res = CreateClippedRegion(parent, frontage, data.Width, cur, sideVec);
                if (Math.Abs(res.Area - data.TargetArea) < 0.001) break;
                if (res.Area < data.TargetArea) min = cur; else max = cur;
                cur = (min + max) / 2;
            }
        }

        private Autodesk.AutoCAD.DatabaseServices.Region CreateClippedRegion(Autodesk.AutoCAD.DatabaseServices.Region parent, Autodesk.AutoCAD.DatabaseServices.Line frontage, double w, double d, Vector3d side)
        {
            Point3d p1 = frontage.StartPoint;
            Point3d p2 = p1 + (frontage.EndPoint - p1).GetNormal() * w;
            Point3d p3 = p2 + (side * d);
            Point3d p4 = p1 + (side * d);

            using (var box = new Autodesk.AutoCAD.DatabaseServices.Polyline(4))
            {
                box.AddVertexAt(0, new Point2d(p1.X, p1.Y), 0, 0, 0);
                box.AddVertexAt(1, new Point2d(p2.X, p2.Y), 0, 0, 0);
                box.AddVertexAt(2, new Point2d(p3.X, p3.Y), 0, 0, 0);
                box.AddVertexAt(3, new Point2d(p4.X, p4.Y), 0, 0, 0);
                box.Closed = true;
                using (var br = CreateRegionFromPolyline(box))
                {
                    Autodesk.AutoCAD.DatabaseServices.Region res = (Autodesk.AutoCAD.DatabaseServices.Region)parent.Clone();
                    if (br != null) { res.BooleanOperation(BooleanOperationType.BoolIntersect, br); br.Dispose(); }
                    return res;
                }
            }
        }

        private void UpdateParentParcel(Autodesk.AutoCAD.DatabaseServices.Polyline oldPline, Autodesk.AutoCAD.DatabaseServices.Region remainingRegion, Transaction tr, Database db, SubdivisionState state)
        {
            using (DBObjectCollection exploded = new DBObjectCollection())
            {
                remainingRegion.Explode(exploded);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                foreach (DBObject obj in exploded)
                {
                    if (obj is Autodesk.AutoCAD.DatabaseServices.Polyline newPline)
                    {
                        newPline.SetDatabaseDefaults();
                        newPline.Layer = oldPline.Layer;
                        btr.AppendEntity(newPline);
                        tr.AddNewlyCreatedDBObject(newPline, true);
                        state.ParentId = newPline.ObjectId; // Update state for continuous cuts
                        
                        // Re-project frontage onto new boundary to keep it synchronized
                        if (state.FrontageLine != null)
                        {
                            Point3d mid = state.FrontageLine.StartPoint + (state.FrontageLine.EndPoint - state.FrontageLine.StartPoint) * 0.5;
                            Point3d closest = newPline.GetClosestPointTo(mid, false);
                            double param = newPline.GetParameterAtPoint(closest);
                            int idx = (int)Math.Floor(param);
                            state.FrontageLine = new Autodesk.AutoCAD.DatabaseServices.Line(newPline.GetPoint3dAt(idx), newPline.GetPoint3dAt((idx + 1) % newPline.NumberOfVertices));
                        }
                    }
                    else obj.Dispose();
                }
                oldPline.UpgradeOpen(); oldPline.Erase();
            }
        }

        private void FinalizeLot(Database db, Transaction tr, Autodesk.AutoCAD.DatabaseServices.Region region, Editor ed)
        {
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            region.SetDatabaseDefaults();
            region.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS", Color.FromColorIndex(ColorMethod.ByAci, 4), LineWeight.LineWeight030);
            btr.AppendEntity(region);
            tr.AddNewlyCreatedDBObject(region, true);
            AddSurveyLabels(db, tr, btr, region);
        }

        private void AddSurveyLabels(Database db, Transaction tr, BlockTableRecord btr, Autodesk.AutoCAD.DatabaseServices.Region region)
        {
            string textLayer = GetOrCreateLayer(db, tr, "PROPOSED_TEXT", Color.FromColorIndex(ColorMethod.ByAci, 2));
            
            // Smart Interior Centroid
            Extents3d ext = region.GeometricExtents;
            Point3d centroid = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
            if (!IsPointInRegion(region, centroid)) centroid = GetNearestPointInside(region, centroid);

            using (MText areaText = new MText())
            {
                areaText.Contents = $"Area: {region.Area:F3} m2";
                areaText.Location = centroid;
                areaText.Height = 1.0; areaText.Layer = textLayer;
                btr.AppendEntity(areaText);
                tr.AddNewlyCreatedDBObject(areaText, true);
            }
            using (DBObjectCollection exploded = new DBObjectCollection())
            {
                region.Explode(exploded);
                foreach (DBObject obj in exploded)
                {
                    if (obj is Curve curve)
                    {
                        double len = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
                        using (AlignedDimension dim = new AlignedDimension(curve.StartPoint, curve.EndPoint, curve.GetPointAtDist(len/2), "", db.Dimstyle))
                        {
                            dim.Layer = textLayer; btr.AppendEntity(dim); tr.AddNewlyCreatedDBObject(dim, true);
                        }
                    }
                    obj.Dispose();
                }
            }
        }

        private Point3d GetNearestPointInside(Autodesk.AutoCAD.DatabaseServices.Region region, Point3d pt)
        {
            using (DBObjectCollection curves = new DBObjectCollection())
            {
                region.Explode(curves);
                Point3d nearestPt = pt;
                double minDist = double.MaxValue;
                foreach (DBObject obj in curves)
                {
                    if (obj is Curve curve)
                    {
                        Point3d closest = curve.GetClosestPointTo(pt, false);
                        double dist = pt.DistanceTo(closest);
                        if (dist < minDist) { minDist = dist; nearestPt = closest; }
                    }
                    obj.Dispose();
                }
                Extents3d ext = region.GeometricExtents;
                Point3d center = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
                Vector3d nudgeDir = (center - nearestPt).GetNormal();
                Point3d testPt = nearestPt + nudgeDir * 0.1;
                for (int i = 0; i < 10; i++)
                {
                    if (IsPointInRegion(region, testPt)) return testPt;
                    testPt += nudgeDir * 0.1;
                }
                return nearestPt;
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

        private bool IsPointInRegion(Autodesk.AutoCAD.DatabaseServices.Region region, Point3d pt)
        {
            using (Circle tinyCircle = new Circle(pt, Vector3d.ZAxis, 0.001))
            using (DBObjectCollection curves = new DBObjectCollection { tinyCircle })
            using (DBObjectCollection regions = Autodesk.AutoCAD.DatabaseServices.Region.CreateFromCurves(curves))
            {
                if (regions.Count == 0) return false;
                using (var circleRegion = (Autodesk.AutoCAD.DatabaseServices.Region)regions[0])
                {
                    for (int i = 1; i < regions.Count; i++) regions[i].Dispose();
                    using (var intersect = (Autodesk.AutoCAD.DatabaseServices.Region)region.Clone())
                    {
                        intersect.BooleanOperation(BooleanOperationType.BoolIntersect, circleRegion);
                        return intersect.Area > 0;
                    }
                }
            }
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
