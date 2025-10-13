using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Manipulations;
using Mapsui.Nts;
using Mapsui.Nts.Editing;
using Mapsui.Nts.Extensions;
using Mapsui.Styles;
using NetTopologySuite.Geometries;
using Point = System.Windows.Point;

namespace Karttest;

public enum EditStatus
{
    None,
    Error,
    Accepted,
    Finished,
    Editing
}

public partial class MainWindow : Window
{
    private EditManager? editManager;
    private Mapsui.Nts.Widgets.EditingWidget? editingWidget;
    private readonly Mapsui.Layers.WritableLayer? editLayer;
    private readonly Mapsui.Layers.WritableLayer? polygonLayer;

    private readonly VectorStyle errorVectorStyle = new VectorStyle
    {
        Fill = new Brush(Color.FromRgba(236, 29, 28, 200)),
        Outline = new Pen(Color.FromRgba(236, 29, 28, 255), 5),
    };

    private readonly VectorStyle acceptedVectorStyle = new VectorStyle
    {
        Fill = new Brush(Color.FromRgba(16, 230, 66, 200)),
        Outline = new Pen(Color.FromRgba(16, 230, 66, 255), 5),
    };
    
    private readonly VectorStyle finishedVectorStyle = new VectorStyle
    {
        Fill = new Brush(Color.FromRgba(149, 166, 203, 200)),
        Outline = new Pen(Color.FromRgba(149, 166, 203, 255), 5),
    };

    private readonly VectorStyle editingVectorStyle = new VectorStyle
    {
        Line = new Pen(Color.FromRgba(61, 61, 60, 255), 2),
        Fill = new Brush(Color.FromRgba(158, 158, 129, 200)),
        Outline = new Pen(Color.FromRgba(158, 158, 129, 255), 5),
    };

    private readonly SymbolStyle vertexStyle = new SymbolStyle
    {
        SymbolType = SymbolType.Ellipse,
        Fill = new Brush(Color.FromRgba(124, 149, 156, 200)),
        Outline = new Pen(Color.FromRgba(124, 149, 156, 255), 2),
        SymbolScale = 0.5
    };

    public MainWindow()
    {
        InitializeComponent();

        double latitude = 57.68991;
        double longitude = 11.95801;

        MapControl.Map ??= new Mapsui.Map();
        MapControl.Map?.Layers.Add(Mapsui.Tiling.OpenStreetMap.CreateTileLayer());
        
        editLayer = new Mapsui.Layers.WritableLayer
        {
            Name = "EditLayer"
        };
        MapControl.Map?.Layers.Add(editLayer);

        polygonLayer = new Mapsui.Layers.WritableLayer
        {
            Name = "PolygonLayer"
        };
        MapControl.Map?.Layers.Add(polygonLayer);
        
        (double x, double y) = Mapsui.Projections.SphericalMercator.FromLonLat(longitude, latitude);
        var sphericalMercatorCoordinate = new Mapsui.MPoint(x, y);
        
        MapControl.Map?.Navigator.CenterOn(sphericalMercatorCoordinate);
        MapControl.Map?.Navigator.ZoomTo(5);
        
        CancelEditButton.IsEnabled = false;

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

        if (editStatus == EditStatus.Finished)
        {
            e.Handled = true;
            return;
        }

        Polygon? polygon = GetCurrentSketchPolygon();
        if (polygon == null)
        {
            return;
        }

        if (polygon.Coordinates.Length > 2)
        {
            Point worldPoint = e.GetPosition(MapControl);
            MapInfo? mapInfo = MapControl.GetMapInfo(new ScreenPosition(worldPoint.X, worldPoint.Y), [editLayer]);
            MPoint? world = mapInfo?.WorldPosition;
            double pixelTolerance = 20.0 * MapControl.Map.Navigator.Viewport.Resolution;
            bool isClosingClick = world != null && GeometryHelpers.IsNear(polygon.Coordinates[0], world, pixelTolerance);
            if (isClosingClick)
            {
                EditStatus = EditStatus.Accepted;    
            }
            else
            {
                EditStatus = EditStatus.Editing;
            }
        }
        
        if (HasPolygonCrossingLines(polygon))
        {
            if (!EditStatus.Equals(EditStatus.Error))
            {
                EditStatus = EditStatus.Error;
            }
        }
        else
        {
            if (EditStatus.Equals(EditStatus.Error))
            {
                EditStatus = EditStatus.Editing;
            }
        }
    }

    private static Polygon? BuildCandidatePolygon(List<Coordinate> openRing, bool forceClose)
    {
        if (openRing.Count < 3) return null;
        var ringCoords = new List<Coordinate>(openRing);

        if (forceClose && !ringCoords[0].Equals2D(ringCoords[^1]))
            ringCoords.Add(ringCoords[0]);

        if (!ringCoords[0].Equals2D(ringCoords[^1]))
            return null;

        var ring = new LinearRing(ringCoords.ToArray());
        if (!ring.IsValid) return null; // invalid ring shape
        return new Polygon(ring);
    }

    private void MapControl_PreviewMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        Debug.WriteLine("Left mouse button down");

        if (editStatus == EditStatus.Accepted)
        {
            e.Handled = true;
            
            Point worldPoint = e.GetPosition(MapControl);
            MapInfo? mapInfo = MapControl.GetMapInfo(new ScreenPosition(worldPoint.X, worldPoint.Y), [editLayer]);
            MPoint? coordinatePoint = mapInfo?.WorldPosition;

            this.editManager?.AddVertex(coordinatePoint?.ToCoordinate());
            this.editManager.EditMode = EditMode.None;
            EditStatus = EditStatus.Finished;
            return;
        }

        if (editManager?.EditMode != EditMode.AddPolygon && 
            editManager?.EditMode != EditMode.DrawingPolygon)
        {
            return;
        }

        if (EditStatus == EditStatus.Error)
        {
            e.Handled = true;
        }
    }

    private void MapControl_PreviewMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        Debug.WriteLine("Right mouse button down");
        CancelEdit();
    }

    private bool HasPolygonCrossingLines(Polygon? polygon)
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

                isCrossing = vertices.Any(p => GeometryHelpers.IsCrossing(segments, new MPoint(p.X, p.Y)));
            }
        }

        return isCrossing;
    }

    private Polygon? GetCurrentSketchPolygon()
    {
        var features = editingWidget?.Layer?.GetFeatures();
        var gf = features?.OfType<GeometryFeature>().FirstOrDefault();
        Polygon? polygon = gf?.Geometry as Polygon;

        return polygon;
    }

    private void CancelEdit()
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

            editingWidget?.Layer?.Clear();
            MapControl.Map.Refresh();
            editingWidget = null;
        }

        // Disable stop button since edit mode is now inactive
        CancelEditButton.IsEnabled = false;
        StartEditButton.IsEnabled = true;
        EditStatus = EditStatus.None;
    }

    private void StartEdit_Click(object sender, RoutedEventArgs e)
    {
        EditStatus = EditStatus.Editing;
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
            CancelEditButton.IsEnabled = true;
            StartEditButton.IsEnabled = false;
        }
    }

    private EditStatus editStatus = EditStatus.None;

    private EditStatus EditStatus
    {
        get => editStatus;
        set
        {
            editStatus = value;
            if (editStatus == EditStatus.Finished)
            {
                SaveEditButton.IsEnabled = true;
            }
            else
            {
                SaveEditButton.IsEnabled = false;
            }
            
            SetEditStyles();
        }
    }

    private void SetEditStyles()
    {
        if (editingWidget?.Layer == null ||
            editLayer == null)
        {
            return;
        }

        StyleCollection styleCollection = new StyleCollection();
        styleCollection.Styles.Add(this.vertexStyle);

        switch (EditStatus)
        {
            case EditStatus.Error:
                styleCollection.Styles.Add(this.errorVectorStyle);
                break;
            case EditStatus.Accepted:
                styleCollection.Styles.Add(this.acceptedVectorStyle);
                break;
            case EditStatus.Finished:
                styleCollection.Styles.Add(this.finishedVectorStyle);
                break;
            case EditStatus.Editing:
            case EditStatus.None:
            default:
                styleCollection.Styles.Add(this.editingVectorStyle);
                break;
        }

        this.editLayer.Style = styleCollection;
        MapControl?.Map.Refresh();
    }

    private void StopEdit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        CancelEdit();
    }

    private void SaveEditButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (editManager != null)
        {
            editManager.EditMode = Mapsui.Nts.Editing.EditMode.None;
        }
        
        // Copy active polygon to polygon layer
        // Maybe check if it is valid?
        
        var features = editingWidget?.Layer?.GetFeatures();
        var geometryFeature = features?.OfType<GeometryFeature>().FirstOrDefault(); // Should be my poly, needs some error handling I guess

        if (geometryFeature != null)
        {
            polygonLayer?.Add(geometryFeature);
            editLayer?.Clear();
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
            
            MapControl.Map.Refresh();
            editingWidget = null;
        }

        // Disable stop button since edit mode is now inactive
        CancelEditButton.IsEnabled = false;
        StartEditButton.IsEnabled = true;
        EditStatus = EditStatus.None;
    }
}