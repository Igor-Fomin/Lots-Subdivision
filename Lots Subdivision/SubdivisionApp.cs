using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
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

        [CommandMethod("CreateLot")]
        public void CreateLot()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            // 1. Select Parent Polyline
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            // 2. Select Frontage Line
            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.SetRejectMessage("\nOnly a Line is allowed.");
            leo.AddAllowedClass(typeof(Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;

            // 3. Enter Target Area
            PromptDoubleOptions pdo = new PromptDoubleOptions("\nEnter Target Area (m2):");
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double targetArea = pdr.Value;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Polyline parentPline = (Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                Line frontageLine = (Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);

                if (!parentPline.Closed)
                {
                    ed.WriteMessage("\nError: Parent Polyline must be closed.");
                    return;
                }

                // Convert Polyline to Region
                Region? parentRegion = CreateRegionFromPolyline(parentPline);
                if (parentRegion == null)
                {
                    ed.WriteMessage("\nError: Could not create region from polyline.");
                    return;
                }

                double totalArea = parentRegion.Area;
                if (targetArea >= totalArea)
                {
                    ed.WriteMessage("\nError: Target area is greater than or equal to total area.");
                    return;
                }

                // Binary Search Logic
                double min = 0;
                double max = frontageLine.Length;
                double currentDist = (min + max) / 2;
                double tolerance = 0.001;
                int maxIterations = 100;

                Region? resultRegion = null;

                for (int i = 0; i < maxIterations; i++)
                {
                    if (resultRegion != null) resultRegion.Dispose();

                    resultRegion = CreateSplitRegion(parentRegion, frontageLine, currentDist);
                    double currentArea = resultRegion.Area;

                    if (Math.Abs(currentArea - targetArea) < tolerance)
                        break;

                    if (currentArea < targetArea)
                        min = currentDist;
                    else
                        max = currentDist;

                    currentDist = (min + max) / 2;
                }

                // Generate new Polyline from resultRegion
                if (resultRegion != null)
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    
                    // Add the result region to the drawing
                    resultRegion.SetDatabaseDefaults();
                    resultRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                    btr.AppendEntity(resultRegion);
                    tr.AddNewlyCreatedDBObject(resultRegion, true);
                    
                    AddSurveyLabels(db, tr, btr, resultRegion);

                    ed.WriteMessage($"\nLot created with area: {resultRegion.Area:F3}");
                }

                tr.Commit();
            }
        }

        private void AddSurveyLabels(Database db, Transaction tr, BlockTableRecord btr, Region region)
        {
            string textLayer = GetOrCreateLayer(db, tr, "PROPOSED_TEXT");

            // 1. Area Label at Centroid
            // Note: Region.AreaProperties gives the centroid relative to origin.
            // But we need the point in WCS.
            // For 2D regions in XY plane, centroid is straightforward.
            // Wait, Region.AreaProperties is not directly available in .NET.
            // We can use the Region's centroid from its properties if available, or calculate from extents for simplicity if necessary.
            // Actually, let's use the bounding box center for now as a fallback if centroid is hard to get.
            Extents3d ext = region.GeometricExtents;
            Point3d centroid = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);

            MText areaText = new MText();
            areaText.Contents = $"Area: {region.Area:F3} m2";
            areaText.Location = centroid;
            areaText.Height = 1.0; // Default height
            areaText.Layer = textLayer;
            btr.AppendEntity(areaText);
            tr.AddNewlyCreatedDBObject(areaText, true);

            // 2. Segment labels
            // Explode region to get boundary curves
            DBObjectCollection exploded = new DBObjectCollection();
            region.Explode(exploded);

            foreach (DBObject obj in exploded)
            {
                if (obj is Curve curve)
                {
                    double length = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
                    Point3d midPt = curve.GetPointAtDist(length / 2);
                    
                    // Determine rotation for the text
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

        [CommandMethod("BatchSubdivide")]
        public void BatchSubdivide()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            // 1. Inputs
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.SetRejectMessage("\nOnly a Line is allowed.");
            leo.AddAllowedClass(typeof(Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;

            PromptIntegerOptions pio = new PromptIntegerOptions("\nEnter Number of Lots:");
            pio.LowerLimit = 1;
            PromptIntegerResult pir = ed.GetInteger(pio);
            if (pir.Status != PromptStatus.OK) return;
            int numLots = pir.Value;

            PromptDoubleOptions pdo = new PromptDoubleOptions("\nEnter Target Area per lot (m2):");
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double targetArea = pdr.Value;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Polyline parentPline = (Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                Line frontageLine = (Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);

                Region? remainingRegion = CreateRegionFromPolyline(parentPline);
                if (remainingRegion == null) return;

                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                double lastDist = 0;
                double maxDist = frontageLine.Length;

                for (int i = 0; i < numLots; i++)
                {
                    double min = lastDist;
                    double max = maxDist;
                    double currentDist = (min + max) / 2;
                    double tolerance = 0.001;
                    int maxIterations = 100;

                    Region? lotRegion = null;

                    for (int j = 0; j < maxIterations; j++)
                    {
                        if (lotRegion != null) lotRegion.Dispose();
                        lotRegion = CreateSplitRegion(remainingRegion, frontageLine, currentDist);
                        
                        double currentArea = lotRegion.Area;
                        if (Math.Abs(currentArea - targetArea) < tolerance) break;
                        if (currentArea < targetArea) min = currentDist;
                        else max = currentDist;
                        currentDist = (min + max) / 2;
                        if (max - min < 1e-8) break;
                    }

                    if (lotRegion != null && lotRegion.Area > 0)
                    {
                        // Finalize this lot
                        lotRegion.SetDatabaseDefaults();
                        lotRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                        btr.AppendEntity(lotRegion);
                        tr.AddNewlyCreatedDBObject(lotRegion, true);
                        
                        AddSurveyLabels(db, tr, btr, lotRegion);

                        // Subtract from remaining
                        remainingRegion.BooleanOperation(BooleanOperationType.BoolSubtract, (Region)lotRegion.Clone());
                        
                        ed.WriteMessage($"\nLot {i + 1} created. Remaining area: {remainingRegion.Area:F3}");
                        lastDist = currentDist;
                    }
                    else
                    {
                        ed.WriteMessage("\nError: Remaining area is insufficient for the next lot.");
                        break;
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

            // 1. Select Parent Polyline
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect the parent Polyline:");
            peo.SetRejectMessage("\nOnly a closed Polyline is allowed.");
            peo.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            // 2. Select Frontage Line
            PromptEntityOptions leo = new PromptEntityOptions("\nSelect the frontage Line:");
            leo.SetRejectMessage("\nOnly a Line is allowed.");
            leo.AddAllowedClass(typeof(Line), true);
            PromptEntityResult ler = ed.GetEntity(leo);
            if (ler.Status != PromptStatus.OK) return;

            // 3. Enter Target Area
            PromptDoubleOptions pdo = new PromptDoubleOptions("\nEnter Target Area (m2):");
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double targetArea = pdr.Value;

            // 4. Enter Fixed Depth
            PromptDoubleOptions ddo = new PromptDoubleOptions("\nEnter Fixed Depth (m):");
            ddo.AllowNegative = false;
            ddo.AllowZero = false;
            PromptDoubleResult ddr = ed.GetDouble(ddo);
            if (ddr.Status != PromptStatus.OK) return;
            double depth = ddr.Value;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Polyline parentPline = (Polyline)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                Line frontageLine = (Line)tr.GetObject(ler.ObjectId, OpenMode.ForRead);

                Region? parentRegion = CreateRegionFromPolyline(parentPline);
                if (parentRegion == null) return;

                // Binary Search for the side boundary position
                double min = 0;
                double max = frontageLine.Length;
                double currentDist = (min + max) / 2;
                double tolerance = 0.001;
                int maxIterations = 100;

                Region? resultRegion = null;

                for (int i = 0; i < maxIterations; i++)
                {
                    if (resultRegion != null) resultRegion.Dispose();

                    resultRegion = CreateFixedDepthRegion(parentRegion, frontageLine, currentDist, depth);
                    double currentArea = resultRegion.Area;

                    if (Math.Abs(currentArea - targetArea) < tolerance)
                        break;

                    if (currentArea < targetArea)
                        min = currentDist;
                    else
                        max = currentDist;

                    currentDist = (min + max) / 2;
                }

                if (resultRegion != null && Math.Abs(resultRegion.Area - targetArea) < tolerance * 10)
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    resultRegion.SetDatabaseDefaults();
                    resultRegion.Layer = GetOrCreateLayer(db, tr, "PROPOSED_LOTS");
                    btr.AppendEntity(resultRegion);
                    tr.AddNewlyCreatedDBObject(resultRegion, true);
                    
                    AddSurveyLabels(db, tr, btr, resultRegion);

                    ed.WriteMessage($"\nLot created with area: {resultRegion.Area:F3} at depth {depth}");
                }
                else
                {
                    ed.WriteMessage("\nError: Geometric constraint not possible.");
                    if (resultRegion != null) resultRegion.Dispose();
                }

                tr.Commit();
            }
        }

        private Region CreateFixedDepthRegion(Region parentRegion, Line frontage, double widthDist, double depth)
        {
            Point3d startPt = frontage.StartPoint;
            Point3d endPt = frontage.EndPoint;
            Vector3d dir = (endPt - startPt).GetNormal();
            Vector3d perp = dir.CrossProduct(Vector3d.ZAxis).GetNormal();

            // We want to create a rectangle starting from startPt, moving widthDist along dir, and depth along perp.
            Point3d p1 = startPt;
            Point3d p2 = startPt + (dir * widthDist);
            Point3d p3 = p2 + (perp * depth);
            Point3d p4 = p1 + (perp * depth);

            Polyline box = new Polyline(4);
            box.AddVertexAt(0, new Point2d(p1.X, p1.Y), 0, 0, 0);
            box.AddVertexAt(1, new Point2d(p2.X, p2.Y), 0, 0, 0);
            box.AddVertexAt(2, new Point2d(p3.X, p3.Y), 0, 0, 0);
            box.AddVertexAt(3, new Point2d(p4.X, p4.Y), 0, 0, 0);
            box.Closed = true;

            Region? boxRegion = CreateRegionFromPolyline(box);
            box.Dispose();

            if (boxRegion == null) return (Region)parentRegion.Clone();

            Region intersection = (Region)parentRegion.Clone();
            intersection.BooleanOperation(BooleanOperationType.BoolIntersect, boxRegion);
            boxRegion.Dispose();

            return intersection;
        }

        private Region? CreateRegionFromPolyline(Polyline pline)
        {
            DBObjectCollection curves = new DBObjectCollection();
            curves.Add(pline);
            DBObjectCollection regions = Region.CreateFromCurves(curves);
            if (regions.Count > 0)
                return (Region)regions[0];
            return null;
        }

        private Region CreateSplitRegion(Region parentRegion, Line frontage, double distance)
        {
            Point3d startPt = frontage.StartPoint;
            Point3d endPt = frontage.EndPoint;
            Vector3d dir = (endPt - startPt).GetNormal();
            Point3d splitPt = startPt + (dir * distance);

            // Perpendicular vector for the blade
            Vector3d perp = dir.CrossProduct(Vector3d.ZAxis).GetNormal();

            // Create a very large rectangle that represents the area "behind" the blade
            Extents3d extents = parentRegion.GeometricExtents;
            double size = (extents.MaxPoint - extents.MinPoint).Length * 2.0;

            Point3d p1 = splitPt + (perp * size);
            Point3d p2 = splitPt - (perp * size);
            Point3d p3 = startPt - (dir * size) - (perp * size);
            Point3d p4 = startPt - (dir * size) + (perp * size);

            Polyline box = new Polyline(4);
            box.AddVertexAt(0, new Point2d(p1.X, p1.Y), 0, 0, 0);
            box.AddVertexAt(1, new Point2d(p2.X, p2.Y), 0, 0, 0);
            box.AddVertexAt(2, new Point2d(p3.X, p3.Y), 0, 0, 0);
            box.AddVertexAt(3, new Point2d(p4.X, p4.Y), 0, 0, 0);
            box.Closed = true;

            Region? boxRegion = CreateRegionFromPolyline(box);
            box.Dispose();

            if (boxRegion == null) return (Region)parentRegion.Clone();

            Region intersection = (Region)parentRegion.Clone();
            intersection.BooleanOperation(BooleanOperationType.BoolIntersect, boxRegion);
            boxRegion.Dispose();

            return intersection;
        }
    }
}
