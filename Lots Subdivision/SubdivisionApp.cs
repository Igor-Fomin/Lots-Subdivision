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
            ed.WriteMessage("\nSmart Subdivision Pro v5.0 Loaded. Type 'SubdivideUI' to start.");
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

                    if (parentRegion.Area < data.TargetArea)
                    {
                        ed.WriteMessage("\nError: Target area exceeds parent parcel area.");
                        parentRegion.Dispose();
                        return;
                    }

                    // Robust Direction Sensing: Compare +Depth and -Depth areas
                    Vector3d frontageDir = (state.FrontageLine.EndPoint - state.FrontageLine.StartPoint).GetNormal();
                    Vector3d sideVec = frontageDir.RotateBy(data.Angle, Vector3d.ZAxis);
                    
                    using (var regPos = CreateClippedRegion(parentRegion, state.FrontageLine, state.FrontageLine.Length, data.Depth, sideVec))
                    using (var regNeg = CreateClippedRegion(parentRegion, state.FrontageLine, state.FrontageLine.Length, data.Depth, -sideVec))
                    {
                        if (regNeg.Area > regPos.Area) sideVec = -sideVec;
                    }

                    Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
                    
                    if (data.LockWidth) SolveForDepth(parentRegion, state.FrontageLine, data, sideVec, out resultRegion);
                    else SolveForWidth(parentRegion, state.FrontageLine, data, sideVec, out resultRegion);

                    if (resultRegion != null && resultRegion.Area > 0)
                    {
                        if (Math.Abs(resultRegion.Area - data.TargetArea) > 1.0)
                        {
                            ed.WriteMessage($"\nWarning: Could not achieve exact target area. Achieved: {resultRegion.Area:F2}m2");
                        }

                        using (var lotClone = (Autodesk.AutoCAD.DatabaseServices.Region)resultRegion.Clone())
                            parentRegion.BooleanOperation(BooleanOperationType.BoolSubtract, lotClone);

                        UpdateParentParcel(parentPline, parentRegion, tr, db, state);
                        FinalizeLot(db, tr, resultRegion, ed);
                        ed.WriteMessage("\nSubdivision complete.");
                    }
                    else ed.WriteMessage("\nError: Could not solve within parcel boundaries.");
                    
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
            Autodesk.AutoCAD.DatabaseServices.Polyline lotPline = RegionToPolyline(region);
            if (lotPline == null) return;

            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            lotPline.SetDatabaseDefaults();
            lotPline.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS", Color.FromColorIndex(ColorMethod.ByAci, 3), LineWeight.LineWeight030); // Green
            btr.AppendEntity(lotPline);
            tr.AddNewlyCreatedDBObject(lotPline, true);
            
            AddSurveyLabels(db, tr, btr, lotPline);
        }

        private Autodesk.AutoCAD.DatabaseServices.Polyline RegionToPolyline(Autodesk.AutoCAD.DatabaseServices.Region region)
        {
            using (DBObjectCollection curves = new DBObjectCollection())
            {
                region.Explode(curves);
                if (curves.Count == 0) return null!;
                
                Autodesk.AutoCAD.DatabaseServices.Polyline pline = new Autodesk.AutoCAD.DatabaseServices.Polyline();
                int v = 0;
                // Simple vertex collection - assumes ordered curves from Explode
                foreach (DBObject obj in curves)
                {
                    if (obj is Curve curve)
                    {
                        pline.AddVertexAt(v++, new Point2d(curve.StartPoint.X, curve.StartPoint.Y), 0, 0, 0);
                    }
                    obj.Dispose();
                }
                pline.Closed = true;
                return pline;
            }
        }

        private void AddSurveyLabels(Database db, Transaction tr, BlockTableRecord btr, Autodesk.AutoCAD.DatabaseServices.Polyline pline)
        {
            string textLayer = GetOrCreateLayer(db, tr, "PROPOSED_TEXT", Color.FromColorIndex(ColorMethod.ByAci, 2));
            
            // Area Label
            Extents3d ext = pline.GeometricExtents;
            Point3d centroid = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
            using (MText areaText = new MText())
            {
                areaText.Contents = $"Area: {pline.Area:F2} m2";
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
