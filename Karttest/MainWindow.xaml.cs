using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Nts;
using Mapsui.Nts.Editing;
using Mapsui.Styles;
using NetTopologySuite.Geometries;

namespace Karttest;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private Mapsui.Nts.Editing.EditManager? editManager;
    private Mapsui.Nts.Widgets.EditingWidget? editingWidget;
    private Mapsui.Layers.WritableLayer? editLayer;
    // private Mapsui.UI.Wpf.MapControl? mapControl;

    private VectorStyle errorVectorStyle = new VectorStyle
    {
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.Red),
        Outline = new Pen(Color.Yellow, 5),
    };

    private VectorStyle drawingVectorStyle = new VectorStyle
    {
        Line = new Pen(Color.AliceBlue, 5),
        Fill = new Brush(Color.Purple),
        Outline = new Pen(Color.HotPink, 5),
    };

    private SymbolStyle vertexStyle = new SymbolStyle
    {
        SymbolType = SymbolType.Rectangle,
        Fill = new Brush(Color.Black),
        Outline = new Pen(Color.DarkBlue, 5),
        SymbolScale = 1.0
    };

    public MainWindow()
    {
        InitializeComponent();

        double latitude = 57.68991;
        double longitude = 11.95801;

        MapControl.Map ??= new Mapsui.Map();
        MapControl.Map?.Layers.Add(Mapsui.Tiling.OpenStreetMap.CreateTileLayer());

        // Create edit layer for drawing
        editLayer = new Mapsui.Layers.WritableLayer
        {
            Name = "EditLayer",
            Style = new Mapsui.Styles.VectorStyle
            {
                Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromString("rgba(255, 0, 0, 0.3)")),
                Line = new Mapsui.Styles.Pen(Mapsui.Styles.Color.FromString("Red"), 3)
            }
        };
        MapControl.Map?.Layers.Add(editLayer);

        // Convert lat/lon to spherical mercator coordinates
        var (x, y) = Mapsui.Projections.SphericalMercator.FromLonLat(longitude, latitude);
        var sphericalMercatorCoordinate = new Mapsui.MPoint(x, y);

        // Set initial map position and zoom level
        MapControl.Map?.Navigator.CenterOn(sphericalMercatorCoordinate);
        MapControl.Map?.Navigator.ZoomTo(5); // Resolution in map units (lower = more zoomed in)

        // Add map to the grid (insert at index 0 so button stays on top)
        // MainGrid.Children.Insert(0, MapControl);

        // Disable stop button initially since edit mode is not active
        StopEditButton.IsEnabled = false;

        MapControl.PreviewMouseRightButtonUp += MapControl_PreviewMouseRightUp;
        MapControl.PreviewMouseLeftButtonDown += MapControl_PreviewMouseLeftDown;
        MapControl.PreviewMouseMove += MapControl_PreviewMouseMove;
    }

    private void MapControl_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (editManager?.EditMode != EditMode.DrawingPolygon)
        {
            return;
        }

        Polygon? polygon = GetCurrentSketchPolygon();
        if (polygon == null)
        {
            return;
        }

        if (HasPolygonCrossingLines(polygon))
        {
            if (!HasError)
            {
                HasError = true;
            }
        }
        else
        {
            if (HasError)
            {
                HasError = false;
            }
        }
    }

    private void MapControl_PreviewMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        Debug.WriteLine("Left mouse button down");

        if (editManager?.EditMode != EditMode.AddPolygon && editManager?.EditMode != EditMode.DrawingPolygon)
        {
            return;
        }

        if (HasError)
        {
            e.Handled = true;
        }
    }

    private void MapControl_PreviewMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        Debug.WriteLine("Right mouse button down");
        EndEdit();
    }

    private bool HasPolygonCrossingLines(Polygon polygon)
    {
        var isCrossing = false;

        if (polygon != null)
        {
            List<Coordinate> vertices = polygon.ExteriorRing.Coordinates.ToList();
            var segments = new List<LineSegment>(polygon.ExteriorRing.NumPoints - 1);
            for (int i = 1; i < polygon.ExteriorRing.NumPoints; i++)
            {
                var a = polygon.ExteriorRing.GetCoordinateN(i - 1);
                var b = polygon.ExteriorRing.GetCoordinateN(i);
                segments.Add(new LineSegment(a, b));
            }

            if (segments.Count > 2 && vertices.Count > 1)
            {
                Coordinate firstCoordinate = vertices[0];
                Coordinate lastCoordinate = vertices[vertices.Count - 1];

                if (!lastCoordinate.Equals2D(firstCoordinate))
                {
                    vertices.Add(new Coordinate(firstCoordinate.X, firstCoordinate.Y));
                    segments.Add(new LineSegment(lastCoordinate, firstCoordinate));
                }

                isCrossing = vertices.Any(p => IsCrossing(segments, new MPoint(p.X, p.Y)));
            }
        }

        return isCrossing;
    }

    private static bool IsCrossing(List<LineSegment> segments, MPoint pointMoved)
    {
        // eps is a tolerance in map units(EPSG:3857 = meters). 0.01 ≈ 1 cm; adjust if needed.
        // TODO: Verify this with a test, this is AI doing

        double eps = 0.01;

        if (segments == null || segments.Count <= 2)
            return false;

        var touching = segments.Where(s => IsEndpointOf(s, pointMoved, eps)).ToList();
        if (touching.Count == 0) return false;

        var others = segments.Except(touching).ToList();

        foreach (var segA in touching)
        {
            foreach (var segB in others)
            {
                if (ShareEndpoint(segA, segB, eps))
                    continue;

                var crosses = CrossingLines(
                    segA.P1.X, segA.P1.Y,
                    segA.P0.X, segA.P0.Y,
                    segB.P1.X, segB.P1.Y,
                    segB.P0.X, segB.P0.Y);

                if (crosses) return true;
            }
        }

        return false;
    }

    private static bool IsEndpointOf(LineSegment s, MPoint p, double eps)
        => AlmostEqual(s.P0, p, eps) || AlmostEqual(s.P1, p, eps);

    private static bool ShareEndpoint(LineSegment a, LineSegment b, double eps)
        => AlmostEqual(a.P0, b.P0, eps) || AlmostEqual(a.P0, b.P1, eps)
                                        || AlmostEqual(a.P1, b.P0, eps) || AlmostEqual(a.P1, b.P1, eps);

    private static bool AlmostEqual(Coordinate c, MPoint p, double eps)
        => Math.Abs(c.X - p.X) <= eps && Math.Abs(c.Y - p.Y) <= eps;

    private static bool AlmostEqual(Coordinate a, Coordinate b, double eps)
        => Math.Abs(a.X - b.X) <= eps && Math.Abs(a.Y - b.Y) <= eps;

    private Polygon? GetCurrentSketchPolygon()
    {
        var features = editingWidget?.Layer?.GetFeatures();
        var gf = features?.OfType<GeometryFeature>().FirstOrDefault();
        Polygon? polygon = gf?.Geometry as Polygon;

        return polygon;
    }

    private void EndEdit()
    {
        // Set edit mode to None
        if (editManager != null)
        {
            editManager.EditMode = Mapsui.Nts.Editing.EditMode.None;
        }

        // Remove editing widget
        if (editingWidget != null && MapControl?.Map != null)
        {
            var widgets = MapControl.Map.Widgets.ToList();
            widgets.Remove(editingWidget);
            MapControl.Map.Widgets.Clear();
            foreach (var widget in widgets)
            {
                MapControl.Map.Widgets.Add(widget);
            }

            // editingWidget?.Layer?.Clear();
            MapControl.Map.Refresh();
            editingWidget = null;
        }

        // Disable stop button since edit mode is now inactive
        StopEditButton.IsEnabled = false;
        StartEditButton.IsEnabled = true;
    }

    private void StartEdit_Click(object sender, RoutedEventArgs e)
    {
        // Enter edit mode
        if (editingWidget == null)
        {
            this.editManager = new EditManager
            {
                Layer = editLayer,
                EditMode = EditMode.AddPolygon
            };

            editingWidget = new Mapsui.Nts.Widgets.EditingWidget(editManager);
            MapControl?.Map?.Widgets.Add(editingWidget);
            SetEditStyles();

            // Enable stop button since edit mode is now active
            StopEditButton.IsEnabled = true;
            StartEditButton.IsEnabled = false;
        }
    }

    private bool hasError = false;

    public bool HasError
    {
        get => hasError;
        private set
        {
            if (hasError == value) return;
            hasError = value;
            SetEditStyles();
        }
    }

    private void SetEditStyles()
    {
        if (editingWidget?.Layer == null || 
            editLayer==null)
        {
            return;
        }

        StyleCollection styleCollection = new StyleCollection { };
        if (!hasError)
        {
            styleCollection.Styles.Add(this.drawingVectorStyle);
            styleCollection.Styles.Add(this.vertexStyle);
        }
        else
        {
            styleCollection.Styles.Add(this.errorVectorStyle);
            styleCollection.Styles.Add(this.vertexStyle);
        }
        
        this.editLayer.Style = styleCollection;
        MapControl?.Map.Refresh();
    }

    private void StopEditButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void StopEdit_Click(object sender, RoutedEventArgs e)
    {
        // Mark event as handled to prevent it from reaching the map
        e.Handled = true;

        EndEdit();
    }

    private static bool CrossingLines(double l1Lat1, double l1Long1, double l1Lat2, double l1Long2, double l2Lat1,
        double l2Long1, double l2Lat2, double l2Long2)
    {
        //Lars H:
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
}