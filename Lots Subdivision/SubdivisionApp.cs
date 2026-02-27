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
    public class SubdivisionApp : IExtensionApplication
    {
        private SubdivisionWindow? _win;
        private ObjectId _parentId = ObjectId.Null;
        private Autodesk.AutoCAD.DatabaseServices.Line? _frontageLine;
        private Point3d _pickPt;

        public void Initialize()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage("\nSubdivision Dashboard Loaded. Type 'SubdivideUI' to start.");
        }

        public void Terminate() { }

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
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect Parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed."); // Fix crash: SetRejectMessage first
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            
            var per = ed.GetEntity(peo);
            if (per.Status == PromptStatus.OK)
            {
                _parentId = per.ObjectId;
                _win?.UpdateStatus("Parent Selected");
                return true;
            }
            return false;
        }

        private void PickFrontageOnly()
        {
            if (_parentId.IsNull) { PickParent(); return; }
            
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            PromptPointOptions ppo = new PromptPointOptions("\nPick Frontage Segment on Polyline:");
            var ppr = ed.GetPoint(ppo);
            
            if (ppr.Status == PromptStatus.OK)
            {
                _pickPt = ppr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                using (var tr = _parentId.Database.TransactionManager.StartTransaction())
                {
                    var pline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(_parentId, OpenMode.ForRead);
                    Point3d closest = pline.GetClosestPointTo(_pickPt, false);
                    double param = pline.GetParameterAtPoint(closest);
                    int idx = (int)Math.Floor(param);
                    
                    _frontageLine = new Autodesk.AutoCAD.DatabaseServices.Line(pline.GetPoint3dAt(idx), pline.GetPoint3dAt((idx + 1) % pline.NumberOfVertices));
                    _win?.UpdateStatus("Frontage Ready");
                    tr.Commit();
                }
            }
        }

        private void PickAngle()
        {
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
            if (_frontageLine != null)
            {
                Vector3d fv = _frontageLine.EndPoint - _frontageLine.StartPoint;
                double ang = fv.GetAngleTo(v, Vector3d.ZAxis);
                angDeg = ang * (180.0 / Math.PI);
            }
            else
            {
                angDeg = Math.Atan2(v.Y, v.X) * (180.0 / Math.PI);
            }
            
            SubdivisionSettings.Angle = angDeg;
            _win?.UpdateAngleDisplay(angDeg); // Sync to UI
            _win?.UpdateStatus("Angle Updated");
        }

        private void ExecuteSubdivision(SubdivisionWindow.SubdivisionData data)
        {
            if (_parentId.IsNull || _frontageLine == null) {
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
                    var parentPline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(_parentId, OpenMode.ForWrite);
                    var parentRegion = CreateRegionFromPolyline(parentPline);
                    if (parentRegion == null) return;

                    // Initial side vector
                    Vector3d frontageDir = (_frontageLine.EndPoint - _frontageLine.StartPoint).GetNormal();
                    Vector3d sideVec = frontageDir.RotateBy(data.Angle, Vector3d.ZAxis);
                    
                    // Direction Sensing Logic: Flip if pick point is on opposite side
                    Point3d midFrontage = _frontageLine.StartPoint + (frontageDir * (_frontageLine.Length / 2.0));
                    Vector3d toPick = (_pickPt - midFrontage).GetNormal();
                    if (toPick.DotProduct(sideVec) < 0) sideVec = -sideVec;

                    Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
                    
                    // Run Solver
                    if (data.LockWidth) SolveForDepth(parentRegion, _frontageLine, data, sideVec, out resultRegion);
                    else SolveForWidth(parentRegion, _frontageLine, data, sideVec, out resultRegion);

                    // Debug/Fallback: If no area (outside), flip and retry once
                    if (resultRegion == null || resultRegion.Area < 0.001)
                    {
                        sideVec = -sideVec;
                        if (data.LockWidth) SolveForDepth(parentRegion, _frontageLine, data, sideVec, out resultRegion);
                        else SolveForWidth(parentRegion, _frontageLine, data, sideVec, out resultRegion);
                    }

                    if (resultRegion != null && resultRegion.Area > 0)
                    {
                        // Parcel Reduction: Subtract lot from parent
                        using (var lotClone = (Autodesk.AutoCAD.DatabaseServices.Region)resultRegion.Clone())
                            parentRegion.BooleanOperation(BooleanOperationType.BoolSubtract, lotClone);

                        // Visual Update: Replace old boundary with new remaining boundary
                        UpdateParentPolyline(parentPline, parentRegion, tr, db);
                        
                        // Finalize the new lot
                        FinalizeLot(db, tr, resultRegion, ed);
                        ed.WriteMessage("\nLot created and parent parcel updated.");
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

        private void UpdateParentPolyline(Autodesk.AutoCAD.DatabaseServices.Polyline oldPline, Autodesk.AutoCAD.DatabaseServices.Region remainingRegion, Transaction tr, Database db)
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
                        _parentId = newPline.ObjectId; // Automatically update for next cut
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
            Extents3d ext = region.GeometricExtents;
            Point3d centroid = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
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
                using (var curves = new DBObjectCollection())
                {
                    curves.Add(flat);
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
}
