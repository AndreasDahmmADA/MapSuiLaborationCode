using Mapsui;
using NetTopologySuite.Geometries;

namespace Karttest;

internal static class GeometryHelpers
{
    public static bool CrossingLines(
        double l1Lat1, 
        double l1Long1, 
        double l1Lat2, 
        double l1Long2, 
        double l2Lat1,
        double l2Long1, 
        double l2Lat2, 
        double l2Long2)
    {
        double latInt = 0.0;
        double longInt = 0.0;
        double A1 = l1Long2 - l1Long1; //y2 - y1;
        double B1 = l1Lat1 - l1Lat2; //x1 - x2;
        double C1 = A1 * l1Lat1 + B1 * l1Long1; //A * x1 + B * y1;

        double A2 = l2Long2 - l2Long1; //y2 - y1;
        double B2 = l2Lat1 - l2Lat2; //x1 - x2;
        double C2 = A2 * l2Lat1 + B2 * l2Long1; //A * x1 + B * y1;
        double det = A1 * B2 - A2 * B1;
        if (det == 0)
        {
            return false;
        }

        latInt = (B2 * C1 - B1 * C2) / det;
        longInt = (A1 * C2 - A2 * C1) / det;
        double lengthLine1 = (l1Lat1 - l1Lat2) * (l1Lat1 - l1Lat2) + (l1Long1 - l1Long2) * (l1Long1 - l1Long2);
        double distTo11 = (l1Lat1 - latInt) * (l1Lat1 - latInt) + (l1Long1 - longInt) * (l1Long1 - longInt);
        double distTo12 = (latInt - l1Lat2) * (latInt - l1Lat2) + (longInt - l1Long2) * (longInt - l1Long2);
        if (!(distTo11 < lengthLine1 && distTo12 < lengthLine1))
        {
            return false;
        }

        double lengthLine2 = (l2Lat1 - l2Lat2) * (l2Lat1 - l2Lat2) + (l2Long1 - l2Long2) * (l2Long1 - l2Long2);
        double distTo21 = (l2Lat1 - latInt) * (l2Lat1 - latInt) + (l2Long1 - longInt) * (l2Long1 - longInt);
        double distTo22 = (latInt - l2Lat2) * (latInt - l2Lat2) + (longInt - l2Long2) * (longInt - l2Long2);
        if (!(distTo21 < lengthLine2 && distTo22 < lengthLine2))
        {
            return false;
        }

        return true;
    }
    
    public static bool IsEndpointOf(LineSegment s, MPoint p, double eps)
        => AlmostEqual(s.P0, p, eps) || AlmostEqual(s.P1, p, eps);

    public static bool ShareEndpoint(LineSegment a, LineSegment b, double eps)
        => AlmostEqual(a.P0, b.P0, eps) || AlmostEqual(a.P0, b.P1, eps)
                                        || AlmostEqual(a.P1, b.P0, eps) || AlmostEqual(a.P1, b.P1, eps);

    public static bool AlmostEqual(Coordinate c, MPoint p, double eps)
        => Math.Abs(c.X - p.X) <= eps && Math.Abs(c.Y - p.Y) <= eps;

    public static bool AlmostEqual(Coordinate a, Coordinate b, double eps)
        => Math.Abs(a.X - b.X) <= eps && Math.Abs(a.Y - b.Y) <= eps;
    
    public static bool IsNear(Coordinate a, MPoint b, double tol)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y;
        return (dx * dx + dy * dy) <= tol * tol;
    }
    
    public static bool IsCrossing(List<LineSegment> segments, MPoint pointMoved)
    {
        // eps is a tolerance in map units(EPSG:3857 = meters). 0.01 ≈ 1 cm; adjust if needed.
        // TODO: Verify this with a test, this is AI doing

        double eps = 0.01;

        if (segments == null || segments.Count <= 2)
            return false;

        var touching = segments.Where(s => GeometryHelpers.IsEndpointOf(s, pointMoved, eps)).ToList();
        if (touching.Count == 0) return false;

        var others = segments.Except(touching).ToList();

        foreach (var segA in touching)
        {
            foreach (var segB in others)
            {
                if (GeometryHelpers.ShareEndpoint(segA, segB, eps))
                    continue;

                var crosses = GeometryHelpers.CrossingLines(
                    segA.P1.X, segA.P1.Y,
                    segA.P0.X, segA.P0.Y,
                    segB.P1.X, segB.P1.Y,
                    segB.P0.X, segB.P0.Y);

                if (crosses) return true;
            }
        }

        return false;
    }
}