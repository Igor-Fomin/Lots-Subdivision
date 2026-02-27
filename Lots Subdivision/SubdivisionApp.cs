using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.GraphicsInterface;
using System.Collections.Generic;
using System;

[assembly: ExtensionApplication(typeof(Lots_Subdivision.SubdivisionApp))]

namespace Lots_Subdivision
{
    public class SubdivisionApp : IExtensionApplication
    {
        public void Initialize()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage("\nLots Subdivision Plugin Loaded. Type 'CreateLot' to start.");
        }

        public void Terminate() { }

        [CommandMethod("SubdivideUI")]
        public void SubdivideUI()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            
            SubdivisionWindow win = new SubdivisionWindow();
            bool? result = Application.ShowModalWindow(win);
            
            if (result == true && win.SelectionData != null)
            {
                var data = win.SelectionData;
                switch (data.ModeIndex)
                {
                    case 0: // Single Lot (Area Engine)
                        ExecuteSingleLot(data);
                        break;
                    case 1: // Fixed Depth
                        ExecuteFixedDepth(data);
                        break;
                    case 2: // Fixed Width
                        ExecuteFixedWidth(data);
                        break;
                    case 3: // Batch
                        ExecuteBatch(data);
                        break;
                    case 4: // Interactive
                        ExecuteInteractive(data);
                        break;
                    case 5: // Proportional
                        ExecuteProportional(data);
                        break;
                    case 6: // Priority
                        ExecutePriority(data);
                        break;
                }
            }
        }

        private void ExecuteSingleLot(SubdivisionWindow.SubdivisionData data)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var parentPline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                var frontageLine = (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);
                var parentRegion = CreateRegionFromPolyline(parentPline);
                if (parentRegion == null) return;

                double min = 0; double max = frontageLine.Length;
                double currentDist = (min + max) / 2;
                Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
                IntegerCollection ic = new IntegerCollection();

                for (int i = 0; i < 100; i++)
                {
                    if (resultRegion != null) resultRegion.Dispose();
                    resultRegion = CreateAngledSplitRegion(parentRegion, frontageLine, currentDist, data.Angle);
                    if (Math.Abs(resultRegion.Area - data.TargetArea) < 0.001) break;
                    if (resultRegion.Area < data.TargetArea) min = currentDist; else max = currentDist;
                    currentDist = (min + max) / 2;
                }

                if (resultRegion != null && resultRegion.Area > 0)
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    resultRegion.SetDatabaseDefaults();
                    resultRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                    btr.AppendEntity(resultRegion);
                    tr.AddNewlyCreatedDBObject(resultRegion, true);
                    AddSurveyLabels(db, tr, btr, resultRegion);
                }
                tr.Commit();
            }
        }

        // Additional helper runners for other modes
        private void ExecuteFixedDepth(SubdivisionWindow.SubdivisionData data) { /* ... Similar logic to CreateLotFixedDepth but uses 'data' ... */ }
        private void ExecuteFixedWidth(SubdivisionWindow.SubdivisionData data) { /* ... Similar logic to CreateLotFixedWidth ... */ }
        private void ExecuteBatch(SubdivisionWindow.SubdivisionData data) { /* ... Similar logic to BatchSubdivide ... */ }
        private void ExecuteInteractive(SubdivisionWindow.SubdivisionData data) { /* ... Similar logic to CreateLotInteractive ... */ }
        private void ExecuteProportional(SubdivisionWindow.SubdivisionData data) { /* ... Similar logic to CreateLotProportional ... */ }
        private void ExecutePriority(SubdivisionWindow.SubdivisionData data) { /* ... Similar logic to CreateLotPriority ... */ }

        private double PromptAngle(Editor ed)
        {
            PromptAngleOptions pao = new PromptAngleOptions("\nEnter Side Boundary Angle relative to frontage (default 90):");
            pao.DefaultValue = Math.PI / 2.0;
            pao.UseDefaultValue = true;
            PromptDoubleResult par = ed.GetAngle(pao);
            if (par.Status == PromptStatus.OK) return par.Value;
            return Math.PI / 2.0;
        }

        private Vector3d GetSideVector(Autodesk.AutoCAD.DatabaseServices.Line frontage, double angle)
        {
            Vector3d dir = (frontage.EndPoint - frontage.StartPoint).GetNormal();
            return dir.RotateBy(angle, Vector3d.ZAxis);
        }

        [CommandMethod("CreateLot")]
        public void CreateLot()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.SetRejectMessage("\nOnly a Line is allowed.");
            leo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;

            PromptDoubleOptions pdo = new PromptDoubleOptions("\nEnter Target Area (m2):");
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double targetArea = pdr.Value;

            double angle = PromptAngle(ed);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Autodesk.AutoCAD.DatabaseServices.Polyline parentPline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Line frontageLine = (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);

                if (!parentPline.Closed)
                {
                    ed.WriteMessage("\nError: Parent Polyline must be closed.");
                    return;
                }

                Autodesk.AutoCAD.DatabaseServices.Region? parentRegion = CreateRegionFromPolyline(parentPline);
                if (parentRegion == null)
                {
                    ed.WriteMessage("\nError: Could not create region from polyline.");
                    return;
                }

                if (targetArea >= parentRegion.Area)
                {
                    ed.WriteMessage("\nError: Target area is greater than or equal to total area.");
                    return;
                }

                double min = 0;
                double max = frontageLine.Length;
                double currentDist = (min + max) / 2;
                double tolerance = 0.001;
                int maxIterations = 100;

                Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
                IntegerCollection ic = new IntegerCollection();

                for (int i = 0; i < maxIterations; i++)
                {
                    if (resultRegion != null) resultRegion.Dispose();

                    Point3d startPt = frontageLine.StartPoint;
                    Vector3d dir = (frontageLine.EndPoint - startPt).GetNormal();
                    Point3d splitPt = startPt + (dir * currentDist);
                    Vector3d sideVec = GetSideVector(frontageLine, angle);
                    
                    Extents3d extents = parentRegion.GeometricExtents;
                    double size = (extents.MaxPoint - extents.MinPoint).Length;
                    Point3d p1 = splitPt + (sideVec * size);
                    Point3d p2 = splitPt - (sideVec * size);

                    using (Autodesk.AutoCAD.DatabaseServices.Line blade = new Autodesk.AutoCAD.DatabaseServices.Line(p1, p2))
                    {
                        blade.ColorIndex = 1; 
                        TransientManager.CurrentTransientManager.AddTransient(blade, TransientDrawingMode.Main, 128, ic);
                        ed.UpdateScreen();
                        
                        resultRegion = CreateAngledSplitRegion(parentRegion, frontageLine, currentDist, angle);
                        double currentArea = resultRegion.Area;

                        TransientManager.CurrentTransientManager.EraseTransient(blade, ic);

                        if (Math.Abs(currentArea - targetArea) < tolerance) break;
                        if (currentArea < targetArea) min = currentDist; else max = currentDist;
                        currentDist = (min + max) / 2;
                    }
                }

                if (resultRegion != null)
                {
                    if (IsMultiplePieces(resultRegion)) ed.WriteMessage("\nWarning: Resulting lot consists of multiple separate pieces.");

                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    resultRegion.SetDatabaseDefaults();
                    resultRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                    btr.AppendEntity(resultRegion);
                    tr.AddNewlyCreatedDBObject(resultRegion, true);
                    AddSurveyLabels(db, tr, btr, resultRegion);
                    ed.WriteMessage($"\nLot created with area: {resultRegion.Area:F3} and frontage width: {currentDist:F3}");
                }
                tr.Commit();
            }
        }

        private void AddSurveyLabels(Database db, Transaction tr, BlockTableRecord btr, Autodesk.AutoCAD.DatabaseServices.Region region)
        {
            string textLayer = GetOrCreateLayer(db, tr, "PROPOSED_TEXT");

            Extents3d ext3d = region.GeometricExtents;
            Point3d centroid = new Point3d((ext3d.MinPoint.X + ext3d.MaxPoint.X) / 2, (ext3d.MinPoint.Y + ext3d.MaxPoint.Y) / 2, 0);

            if (!IsPointInRegion(region, centroid))
            {
                centroid = GetNearestPointInside(region, centroid);
            }

            MText areaText = new MText();
            areaText.Contents = $"Area: {region.Area:F3} m2";
            areaText.Location = centroid;
            areaText.Height = 1.0; 
            areaText.Layer = textLayer;
            btr.AppendEntity(areaText);
            tr.AddNewlyCreatedDBObject(areaText, true);

            DBObjectCollection exploded = new DBObjectCollection();
            region.Explode(exploded);
            foreach (DBObject obj in exploded)
            {
                if (obj is Curve curve)
                {
                    double length = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
                    Point3d midPt = curve.GetPointAtDist(length / 2);
                    Vector3d dir = curve.GetFirstDerivative(curve.StartParam);
                    double angle = Math.Atan2(dir.Y, dir.X);
                    DBText distText = new DBText();
                    distText.TextString = $"{length:F3}";
                    distText.Position = midPt;
                    distText.Height = 0.5;
                    distText.Rotation = angle;
                    distText.Layer = textLayer;
                    btr.AppendEntity(distText);
                    tr.AddNewlyCreatedDBObject(distText, true);
                }
                obj.Dispose();
            }
        }

        private bool IsPointInRegion(Autodesk.AutoCAD.DatabaseServices.Region region, Point3d pt)
        {
            using (Circle tinyCircle = new Circle(pt, Vector3d.ZAxis, 0.001))
            {
                DBObjectCollection curves = new DBObjectCollection();
                curves.Add(tinyCircle);
                using (DBObjectCollection regions = Autodesk.AutoCAD.DatabaseServices.Region.CreateFromCurves(curves))
                {
                    if (regions.Count == 0) return false;
                    using (Autodesk.AutoCAD.DatabaseServices.Region circleRegion = (Autodesk.AutoCAD.DatabaseServices.Region)regions[0])
                    {
                        using (Autodesk.AutoCAD.DatabaseServices.Region intersect = (Autodesk.AutoCAD.DatabaseServices.Region)region.Clone())
                        {
                            intersect.BooleanOperation(BooleanOperationType.BoolIntersect, circleRegion);
                            return intersect.Area > 0;
                        }
                    }
                }
            }
        }

        private Point3d GetNearestPointInside(Autodesk.AutoCAD.DatabaseServices.Region region, Point3d pt)
        {
            DBObjectCollection curves = new DBObjectCollection();
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

        private string GetOrCreateLayer(Database db, Transaction tr, string layerName)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                LayerTableRecord ltr = new LayerTableRecord();
                ltr.Name = layerName;
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
            return layerName;
        }

        [CommandMethod("CreateLotFixedWidth")]
        public void CreateLotFixedWidth()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;
            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.SetRejectMessage("\nOnly a Line is allowed.");
            leo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;
            PromptDoubleOptions pdo = new PromptDoubleOptions("\nEnter Target Area (m2):");
            pdo.AllowNegative = false; pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double targetArea = pdr.Value;
            PromptDoubleOptions wdo = new PromptDoubleOptions("\nEnter Fixed Frontage Width (m):");
            wdo.AllowNegative = false; wdo.AllowZero = false;
            PromptDoubleResult wdr = ed.GetDouble(wdo);
            if (wdr.Status != PromptStatus.OK) return;
            double width = wdr.Value;
            double angle = PromptAngle(ed);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Autodesk.AutoCAD.DatabaseServices.Polyline parentPline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Line frontageLine = (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);
                if (width > frontageLine.Length) { ed.WriteMessage("\nError: Fixed width is greater than frontage length."); return; }
                Autodesk.AutoCAD.DatabaseServices.Region? parentRegion = CreateRegionFromPolyline(parentPline);
                if (parentRegion == null) return;
                double min = 0; double max = parentRegion.GeometricExtents.MaxPoint.DistanceTo(parentRegion.GeometricExtents.MinPoint);
                double currentDepth = (min + max) / 2;
                double tolerance = 0.001;
                Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
                for (int i = 0; i < 100; i++)
                {
                    if (resultRegion != null) resultRegion.Dispose();
                    resultRegion = CreateAngledSplitRegion(parentRegion, frontageLine, width, angle);
                    Autodesk.AutoCAD.DatabaseServices.Line backBoundary = OffsetLineAtAngle(frontageLine, currentDepth, angle);
                    resultRegion = IntersectWithAngledDepth(resultRegion, frontageLine, backBoundary, angle);
                    if (Math.Abs(resultRegion.Area - targetArea) < tolerance) break;
                    if (resultRegion.Area < targetArea) min = currentDepth; else max = currentDepth;
                    currentDepth = (min + max) / 2;
                }
                if (resultRegion != null && Math.Abs(resultRegion.Area - targetArea) < tolerance * 10)
                {
                    if (IsMultiplePieces(resultRegion)) ed.WriteMessage("\nWarning: Resulting lot consists of multiple separate pieces.");
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    resultRegion.SetDatabaseDefaults();
                    resultRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                    btr.AppendEntity(resultRegion);
                    tr.AddNewlyCreatedDBObject(resultRegion, true);
                    AddSurveyLabels(db, tr, btr, resultRegion);
                }
                tr.Commit();
            }
        }

        [CommandMethod("CreateLotInteractive")]
        public void CreateLotInteractive()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;
            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.SetRejectMessage("\nOnly a Line is allowed.");
            leo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;
            PromptDoubleOptions pdo = new PromptDoubleOptions("\nEnter Target Area (m2):");
            pdo.AllowNegative = false; pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double targetArea = pdr.Value;
            PromptDoubleOptions ddo = new PromptDoubleOptions("\nEnter Desired Depth (m):");
            ddo.AllowNegative = false; ddo.AllowZero = false;
            PromptDoubleResult ddr = ed.GetDouble(ddo);
            if (ddr.Status != PromptStatus.OK) return;
            double prefDepth = ddr.Value;
            PromptDoubleOptions wdo = new PromptDoubleOptions("\nEnter Desired Frontage Width (m):");
            wdo.AllowNegative = false; wdo.AllowZero = false;
            PromptDoubleResult wdr = ed.GetDouble(wdo);
            if (wdr.Status != PromptStatus.OK) return;
            double prefWidth = wdr.Value;
            double angle = PromptAngle(ed);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Autodesk.AutoCAD.DatabaseServices.Polyline parentPline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Line frontageLine = (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Region? parentRegion = CreateRegionFromPolyline(parentPline);
                if (parentRegion == null) return;
                Autodesk.AutoCAD.DatabaseServices.Region? initialReg = CreateAngledSplitRegion(parentRegion, frontageLine, prefWidth, angle);
                Autodesk.AutoCAD.DatabaseServices.Line backLine = OffsetLineAtAngle(frontageLine, prefDepth, angle);
                initialReg = IntersectWithAngledDepth(initialReg, frontageLine, backLine, angle);
                double initialArea = initialReg.Area; initialReg.Dispose();
                double tolerance = 0.001;
                if (Math.Abs(initialArea - targetArea) > tolerance)
                {
                    PromptKeywordOptions pko = new PromptKeywordOptions($"\nDimensions give {initialArea:F2}m2, not target {targetArea:F2}m2. Adjust [Width/Depth]?");
                    pko.Keywords.Add("Width"); pko.Keywords.Add("Depth"); pko.Keywords.Default = "Width";
                    PromptResult pkr = ed.GetKeywords(pko);
                    if (pkr.Status != PromptStatus.OK) return;
                    Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
                    if (pkr.StringResult == "Width")
                    {
                        double min = 0; double max = frontageLine.Length;
                        double currentWidth = (min + max) / 2;
                        for (int i = 0; i < 100; i++)
                        {
                            if (resultRegion != null) resultRegion.Dispose();
                            resultRegion = CreateAngledSplitRegion(parentRegion, frontageLine, currentWidth, angle);
                            resultRegion = IntersectWithAngledDepth(resultRegion, frontageLine, backLine, angle);
                            if (Math.Abs(resultRegion.Area - targetArea) < tolerance) break;
                            if (resultRegion.Area < targetArea) min = currentWidth; else max = currentWidth;
                            currentWidth = (min + max) / 2;
                        }
                    }
                    else
                    {
                        double min = 0; double max = parentRegion.GeometricExtents.MaxPoint.DistanceTo(parentRegion.GeometricExtents.MinPoint);
                        double currentDepth = (min + max) / 2;
                        for (int i = 0; i < 100; i++)
                        {
                            if (resultRegion != null) resultRegion.Dispose();
                            resultRegion = CreateAngledSplitRegion(parentRegion, frontageLine, prefWidth, angle);
                            Autodesk.AutoCAD.DatabaseServices.Line testBack = OffsetLineAtAngle(frontageLine, currentDepth, angle);
                            resultRegion = IntersectWithAngledDepth(resultRegion, frontageLine, testBack, angle);
                            if (Math.Abs(resultRegion.Area - targetArea) < tolerance) break;
                            if (resultRegion.Area < targetArea) min = currentDepth; else max = currentDepth;
                            currentDepth = (min + max) / 2;
                        }
                    }
                    if (resultRegion != null && resultRegion.Area > 0)
                    {
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                        resultRegion.SetDatabaseDefaults();
                        resultRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                        btr.AppendEntity(resultRegion);
                        tr.AddNewlyCreatedDBObject(resultRegion, true);
                        AddSurveyLabels(db, tr, btr, resultRegion);
                    }
                }
                tr.Commit();
            }
        }

        [CommandMethod("CreateLotProportional")]
        public void CreateLotProportional()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;
            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.SetRejectMessage("\nOnly a Line is allowed.");
            leo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;
            PromptDoubleOptions pdo = new PromptDoubleOptions("\nEnter Target Area (m2):");
            pdo.AllowNegative = false; pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double targetArea = pdr.Value;
            PromptDoubleOptions wdo = new PromptDoubleOptions("\nEnter Desired Frontage Width (m):");
            wdo.AllowNegative = false; wdo.AllowZero = false;
            PromptDoubleResult wdr = ed.GetDouble(wdo);
            if (wdr.Status != PromptStatus.OK) return;
            double prefWidth = wdr.Value;
            PromptDoubleOptions ddo = new PromptDoubleOptions("\nEnter Desired Depth (m):");
            ddo.AllowNegative = false; ddo.AllowZero = false;
            PromptDoubleResult ddr = ed.GetDouble(ddo);
            if (ddr.Status != PromptStatus.OK) return;
            double prefDepth = ddr.Value;
            double angle = PromptAngle(ed);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Autodesk.AutoCAD.DatabaseServices.Polyline parentPline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Line frontageLine = (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Region? parentRegion = CreateRegionFromPolyline(parentPline);
                if (parentRegion == null) return;
                double minScale = 0; double maxScale = 10.0;
                double currentScale = Math.Sqrt(targetArea / (prefWidth * prefDepth)); 
                double tolerance = 0.001;
                Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
                for (int i = 0; i < 100; i++)
                {
                    if (resultRegion != null) resultRegion.Dispose();
                    double testWidth = prefWidth * currentScale;
                    double testDepth = prefDepth * currentScale;
                    resultRegion = CreateAngledSplitRegion(parentRegion, frontageLine, testWidth, angle);
                    Autodesk.AutoCAD.DatabaseServices.Line testBack = OffsetLineAtAngle(frontageLine, testDepth, angle);
                    resultRegion = IntersectWithAngledDepth(resultRegion, frontageLine, testBack, angle);
                    if (Math.Abs(resultRegion.Area - targetArea) < tolerance) break;
                    if (resultRegion.Area < targetArea) minScale = currentScale; else maxScale = currentScale;
                    currentScale = (minScale + maxScale) / 2;
                }
                if (resultRegion != null && resultRegion.Area > 0)
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    resultRegion.SetDatabaseDefaults();
                    resultRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                    btr.AppendEntity(resultRegion);
                    tr.AddNewlyCreatedDBObject(resultRegion, true);
                    AddSurveyLabels(db, tr, btr, resultRegion);
                }
                tr.Commit();
            }
        }

        [CommandMethod("CreateLotPriority")]
        public void CreateLotPriority()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;
            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.SetRejectMessage("\nOnly a Line is allowed.");
            leo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;
            PromptDoubleOptions pdo = new PromptDoubleOptions("\nEnter Target Area (m2):");
            pdo.AllowNegative = false; pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double targetArea = pdr.Value;
            PromptDoubleOptions wdo = new PromptDoubleOptions("\nEnter Preferred Frontage Width (m):");
            wdo.AllowNegative = false; wdo.AllowZero = false;
            PromptDoubleResult wdr = ed.GetDouble(wdo);
            if (wdr.Status != PromptStatus.OK) return;
            double prefWidth = wdr.Value;
            PromptDoubleOptions ddo = new PromptDoubleOptions("\nEnter Preferred Depth (m):");
            ddo.AllowNegative = false; ddo.AllowZero = false;
            PromptDoubleResult ddr = ed.GetDouble(ddo);
            if (ddr.Status != PromptStatus.OK) return;
            double prefDepth = ddr.Value;
            PromptDoubleOptions mdo = new PromptDoubleOptions("\nEnter Minimum Allowed Width (m):");
            mdo.AllowNegative = false; mdo.AllowZero = false; mdo.DefaultValue = 10.0; mdo.UseDefaultValue = true;
            PromptDoubleResult mdr = ed.GetDouble(mdo);
            if (mdr.Status != PromptStatus.OK) return;
            double minWidth = mdr.Value;
            double angle = PromptAngle(ed);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Autodesk.AutoCAD.DatabaseServices.Polyline parentPline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Line frontageLine = (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Region? parentRegion = CreateRegionFromPolyline(parentPline);
                if (parentRegion == null) return;
                SolveLotWithPreference(parentRegion, frontageLine, targetArea, prefWidth, prefDepth, minWidth, angle, db, tr, ed);
                tr.Commit();
            }
        }

        private void SolveLotWithPreference(Autodesk.AutoCAD.DatabaseServices.Region parentRegion, Autodesk.AutoCAD.DatabaseServices.Line frontageLine, double targetArea, double prefWidth, double prefDepth, double minWidth, double angle, Database db, Transaction tr, Editor ed)
        {
            double min = 0; double max = frontageLine.Length;
            double currentWidth = (min + max) / 2;
            double tolerance = 0.001;
            Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
            Autodesk.AutoCAD.DatabaseServices.Line backLine = OffsetLineAtAngle(frontageLine, prefDepth, angle);
            for (int i = 0; i < 100; i++)
            {
                if (resultRegion != null) resultRegion.Dispose();
                resultRegion = CreateAngledSplitRegion(parentRegion, frontageLine, currentWidth, angle);
                resultRegion = IntersectWithAngledDepth(resultRegion, frontageLine, backLine, angle);
                if (Math.Abs(resultRegion.Area - targetArea) < tolerance) break;
                if (resultRegion.Area < targetArea) min = currentWidth; else max = currentWidth;
                currentWidth = (min + max) / 2;
            }
            if (currentWidth < minWidth)
            {
                if (resultRegion != null) resultRegion.Dispose();
                min = 0; max = parentRegion.GeometricExtents.MaxPoint.DistanceTo(parentRegion.GeometricExtents.MinPoint);
                double currentDepth = (min + max) / 2;
                for (int i = 0; i < 100; i++)
                {
                    if (resultRegion != null) resultRegion.Dispose();
                    resultRegion = CreateAngledSplitRegion(parentRegion, frontageLine, prefWidth, angle);
                    Autodesk.AutoCAD.DatabaseServices.Line testBack = OffsetLineAtAngle(frontageLine, currentDepth, angle);
                    resultRegion = IntersectWithAngledDepth(resultRegion, frontageLine, testBack, angle);
                    if (Math.Abs(resultRegion.Area - targetArea) < tolerance) break;
                    if (resultRegion.Area < targetArea) min = currentDepth; else max = currentDepth;
                    currentDepth = (min + max) / 2;
                }
            }
            if (resultRegion != null && resultRegion.Area > 0)
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                resultRegion.SetDatabaseDefaults();
                resultRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                btr.AppendEntity(resultRegion);
                tr.AddNewlyCreatedDBObject(resultRegion, true);
                AddSurveyLabels(db, tr, btr, resultRegion);
            }
        }

        private Autodesk.AutoCAD.DatabaseServices.Line OffsetLineAtAngle(Autodesk.AutoCAD.DatabaseServices.Line original, double depth, double angle)
        {
            Vector3d sideVec = GetSideVector(original, angle);
            Point3d startPt = original.StartPoint + sideVec * depth;
            Point3d endPt = original.EndPoint + sideVec * depth;
            return new Autodesk.AutoCAD.DatabaseServices.Line(startPt, endPt);
        }

        private Autodesk.AutoCAD.DatabaseServices.Region IntersectWithAngledDepth(Autodesk.AutoCAD.DatabaseServices.Region currentResult, Autodesk.AutoCAD.DatabaseServices.Line frontage, Autodesk.AutoCAD.DatabaseServices.Line backBoundary, double angle)
        {
            Point3d p1 = frontage.StartPoint;
            Point3d p2 = frontage.EndPoint;
            Point3d p3 = backBoundary.EndPoint;
            Point3d p4 = backBoundary.StartPoint;
            Autodesk.AutoCAD.DatabaseServices.Polyline clipBox = new Autodesk.AutoCAD.DatabaseServices.Polyline(4);
            clipBox.AddVertexAt(0, new Point2d(p1.X, p1.Y), 0, 0, 0);
            clipBox.AddVertexAt(1, new Point2d(p2.X, p2.Y), 0, 0, 0);
            clipBox.AddVertexAt(2, new Point2d(p3.X, p3.Y), 0, 0, 0);
            clipBox.AddVertexAt(3, new Point2d(p4.X, p4.Y), 0, 0, 0);
            clipBox.Closed = true;
            Autodesk.AutoCAD.DatabaseServices.Region? clipRegion = CreateRegionFromPolyline(clipBox);
            clipBox.Dispose();
            if (clipRegion == null) return currentResult;
            currentResult.BooleanOperation(BooleanOperationType.BoolIntersect, clipRegion);
            clipRegion.Dispose();
            return currentResult;
        }

        private bool IsMultiplePieces(Autodesk.AutoCAD.DatabaseServices.Region region)
        {
            DBObjectCollection exploded = new DBObjectCollection();
            region.Explode(exploded);
            using (DBObjectCollection regions = Autodesk.AutoCAD.DatabaseServices.Region.CreateFromCurves(exploded))
            {
                foreach (DBObject obj in exploded) obj.Dispose();
                return regions.Count > 1;
            }
        }

        [CommandMethod("BatchSubdivide")]
        public void BatchSubdivide()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;
            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.SetRejectMessage("\nOnly a Line is allowed.");
            leo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;
            PromptIntegerOptions pio = new PromptIntegerOptions("\nEnter Number of Lots:");
            pio.LowerLimit = 1;
            PromptIntegerResult pir = ed.GetInteger(pio);
            if (pir.Status != PromptStatus.OK) return;
            int numLots = pir.Value;
            PromptDoubleOptions pdo = new PromptDoubleOptions("\nEnter Target Area per lot (m2):");
            pdo.AllowNegative = false; pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double targetArea = pdr.Value;
            double angle = PromptAngle(ed);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Autodesk.AutoCAD.DatabaseServices.Polyline parentPline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Line frontageLine = (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Region? remainingRegion = CreateRegionFromPolyline(parentPline);
                if (remainingRegion == null) return;
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                double lastDist = 0;
                double maxDist = frontageLine.Length;
                for (int i = 0; i < numLots; i++)
                {
                    double min = lastDist; double max = maxDist;
                    double currentDist = (min + max) / 2;
                    double tolerance = 0.001;
                    Autodesk.AutoCAD.DatabaseServices.Region? lotRegion = null;
                    for (int j = 0; j < 100; j++)
                    {
                        if (lotRegion != null) lotRegion.Dispose();
                        lotRegion = CreateAngledSplitRegion(remainingRegion, frontageLine, currentDist, angle);
                        if (Math.Abs(lotRegion.Area - targetArea) < tolerance) break;
                        if (lotRegion.Area < targetArea) min = currentDist; else max = currentDist;
                        currentDist = (min + max) / 2;
                    }
                    if (lotRegion != null && lotRegion.Area > 0)
                    {
                        lotRegion.SetDatabaseDefaults();
                        lotRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                        btr.AppendEntity(lotRegion);
                        tr.AddNewlyCreatedDBObject(lotRegion, true);
                        AddSurveyLabels(db, tr, btr, lotRegion);
                        remainingRegion.BooleanOperation(BooleanOperationType.BoolSubtract, (Autodesk.AutoCAD.DatabaseServices.Region)lotRegion.Clone());
                        lastDist = currentDist;
                    }
                }
                tr.Commit();
            }
        }

        [CommandMethod("CreateLotFixedDepth")]
        public void CreateLotFixedDepth()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;
            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.SetRejectMessage("\nOnly a Line is allowed.");
            leo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;
            PromptDoubleOptions pdo = new PromptDoubleOptions("\nEnter Target Area (m2):");
            pdo.AllowNegative = false; pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double targetArea = pdr.Value;
            PromptDoubleOptions ddo = new PromptDoubleOptions("\nEnter Fixed Depth (m):");
            ddo.AllowNegative = false; ddo.AllowZero = false;
            PromptDoubleResult ddr = ed.GetDouble(ddo);
            if (ddr.Status != PromptStatus.OK) return;
            double depth = ddr.Value;
            double angle = PromptAngle(ed);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Autodesk.AutoCAD.DatabaseServices.Polyline parentPline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Line frontageLine = (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);
                Autodesk.AutoCAD.DatabaseServices.Region? parentRegion = CreateRegionFromPolyline(parentPline);
                if (parentRegion == null) return;
                Vector3d sideVec = GetSideVector(frontageLine, angle);
                Point3d testStart = frontageLine.StartPoint + (sideVec * depth);
                Point3d testEnd = frontageLine.EndPoint + (sideVec * depth);
                if (!IsPointInRegion(parentRegion, testStart))
                {
                    testStart = frontageLine.StartPoint - (sideVec * depth);
                    testEnd = frontageLine.EndPoint - (sideVec * depth);
                }
                Autodesk.AutoCAD.DatabaseServices.Line backBoundaryLine = new Autodesk.AutoCAD.DatabaseServices.Line(testStart, testEnd);
                double min = 0; double max = frontageLine.Length;
                double currentDist = (min + max) / 2;
                double tolerance = 0.001;
                Autodesk.AutoCAD.DatabaseServices.Region? resultRegion = null;
                IntegerCollection ic = new IntegerCollection();
                for (int i = 0; i < 100; i++)
                {
                    if (resultRegion != null) resultRegion.Dispose();
                    Point3d startPt = frontageLine.StartPoint;
                    Vector3d dir = (frontageLine.EndPoint - startPt).GetNormal();
                    Point3d splitPt = startPt + (dir * currentDist);
                    using (Autodesk.AutoCAD.DatabaseServices.Line blade = new Autodesk.AutoCAD.DatabaseServices.Line(splitPt + sideVec * 5, splitPt - sideVec * 5))
                    {
                        blade.ColorIndex = 1;
                        TransientManager.CurrentTransientManager.AddTransient(blade, TransientDrawingMode.Main, 128, ic);
                        ed.UpdateScreen();
                        resultRegion = CreateAngledSplitRegion(parentRegion, frontageLine, currentDist, angle);
                        resultRegion = IntersectWithAngledDepth(resultRegion, frontageLine, backBoundaryLine, angle);
                        double currentArea = resultRegion.Area;
                        TransientManager.CurrentTransientManager.EraseTransient(blade, ic);
                        if (Math.Abs(currentArea - targetArea) < tolerance) break;
                        if (currentArea < targetArea) min = currentDist; else max = currentDist;
                        currentDist = (min + max) / 2;
                    }
                }
                if (resultRegion != null && Math.Abs(resultRegion.Area - targetArea) < tolerance * 10)
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    resultRegion.SetDatabaseDefaults();
                    resultRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                    btr.AppendEntity(resultRegion);
                    tr.AddNewlyCreatedDBObject(resultRegion, true);
                    AddSurveyLabels(db, tr, btr, resultRegion);
                }
                tr.Commit();
            }
        }

        private Autodesk.AutoCAD.DatabaseServices.Region CreateAngledSplitRegion(Autodesk.AutoCAD.DatabaseServices.Region parentRegion, Autodesk.AutoCAD.DatabaseServices.Line frontage, double distance, double angle)
        {
            Point3d startPt = frontage.StartPoint;
            Point3d endPt = frontage.EndPoint;
            Vector3d dir = (endPt - startPt).GetNormal();
            Point3d splitPt = startPt + (dir * distance);
            Vector3d sideVec = GetSideVector(frontage, angle);
            Extents3d extents = parentRegion.GeometricExtents;
            double size = (extents.MaxPoint - extents.MinPoint).Length * 2.0;
            Point3d p1 = splitPt + (sideVec * size);
            Point3d p2 = splitPt - (sideVec * size);
            Point3d p3 = startPt - (dir * size) - (sideVec * size);
            Point3d p4 = startPt - (dir * size) + (sideVec * size);
            Autodesk.AutoCAD.DatabaseServices.Polyline box = new Autodesk.AutoCAD.DatabaseServices.Polyline(4);
            box.AddVertexAt(0, new Point2d(p1.X, p1.Y), 0, 0, 0);
            box.AddVertexAt(1, new Point2d(p2.X, p2.Y), 0, 0, 0);
            box.AddVertexAt(2, new Point2d(p3.X, p3.Y), 0, 0, 0);
            box.AddVertexAt(3, new Point2d(p4.X, p4.Y), 0, 0, 0);
            box.Closed = true;
            Autodesk.AutoCAD.DatabaseServices.Region? boxRegion = CreateRegionFromPolyline(box);
            box.Dispose();
            if (boxRegion == null) return (Autodesk.AutoCAD.DatabaseServices.Region)parentRegion.Clone();
            Autodesk.AutoCAD.DatabaseServices.Region intersection = (Autodesk.AutoCAD.DatabaseServices.Region)parentRegion.Clone();
            intersection.BooleanOperation(BooleanOperationType.BoolIntersect, boxRegion);
            boxRegion.Dispose();
            return intersection;
        }

        private Autodesk.AutoCAD.DatabaseServices.Region? CreateRegionFromPolyline(Autodesk.AutoCAD.DatabaseServices.Polyline pline)
        {
            DBObjectCollection curves = new DBObjectCollection();
            curves.Add(pline);
            DBObjectCollection regions = Autodesk.AutoCAD.DatabaseServices.Region.CreateFromCurves(curves);
            if (regions.Count > 0)
                return (Autodesk.AutoCAD.DatabaseServices.Region)regions[0];
            return null;
        }
    }
}
