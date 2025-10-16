using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Manipulations;
using Mapsui.Nts;
using Mapsui.Nts.Editing;
using Mapsui.Styles;
using NetTopologySuite.Geometries;
using Point = System.Windows.Point;

namespace Karttest;

public enum EditState
{
    None,
    Error,
    Accepted,
    Finished,
    Editing,
    Deleting,
    Dragging,
}

public partial class MainWindow : INotifyPropertyChanged
{
    private EditManager? editManager;
    private Mapsui.Nts.Widgets.EditingWidget? editingWidget;
    private Mapsui.Layers.WritableLayer? editLayer;
    private Mapsui.Layers.WritableLayer? polygonLayer;

    private Coordinate? deleteCandidate;
    private EditMode? previousEditMode;
    private bool isDragMode;

    private DispatcherTimer? editModeTimer;
    private EditMode lastObservedEditMode = EditMode.None;

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

        MapControl.Map.Layers.Add(Mapsui.Tiling.OpenStreetMap.CreateTileLayer());

        InitializeEditLayer();
        InitializePolygonLayer();

        (double x, double y) = Mapsui.Projections.SphericalMercator.FromLonLat(longitude, latitude);
        var sphericalMercatorCoordinate = new MPoint(x, y);

        MapControl.Map.Navigator.CenterOn(sphericalMercatorCoordinate);
        MapControl.Map.Navigator.ZoomTo(5);

        CancelEditButton.IsEnabled = false;

        MapControl.PreviewMouseRightButtonUp += MapControl_PreviewMouseRightUp;
        MapControl.PreviewMouseLeftButtonDown += MapControl_PreviewMouseLeftDown;
        MapControl.PreviewMouseMove += MapControl_PreviewMouseMove;
        MapControl.PreviewMouseLeftButtonUp += MapControl_MouseLeftButtonUp;
        MapControl.PreviewKeyDown += MapControl_KeyDown;
        MapControl.PreviewKeyUp += MapControl_KeyUp;
        MapControl.Focus();

        InitializeEditModeTimer();

        DataContext = this;
    }

    private void MapControl_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopDragCoordinate();
    }

    public EditMode CurrentEditMode => editManager?.EditMode ?? EditMode.None;

    private void InitializeEditModeTimer()
    {
        editModeTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(150),
            DispatcherPriority.Background,
            (_, _) => PollEditMode(),
            Dispatcher);

        editModeTimer.Start();
    }

    private void PollEditMode()
    {
        var mode = editManager?.EditMode ?? EditMode.None;
        if (mode == lastObservedEditMode)
        {
            return;
        }

        lastObservedEditMode = mode;
        OnPropertyChanged(nameof(CurrentEditMode));
    }

    private bool hasDeleted;

    public EditMode EditMode => editManager?.EditMode ?? EditMode.None;

    private bool isShiftHeld;

    public bool IsShiftHeld
    {
        get => isShiftHeld;
        private set
        {
            if (isShiftHeld == value) return;
            isShiftHeld = value;

            if (isShiftHeld)
            {
                if (editManager?.EditMode == EditMode.AddPolygon ||
                    editManager?.EditMode == EditMode.DrawingPolygon ||
                    editManager?.EditMode == EditMode.Modify)
                {
                    previousEditMode = editManager.EditMode;
                    editManager.EditMode = EditMode.None;
                }
            }
            else
            {
                if (editManager != null && previousEditMode.HasValue)
                {
                    if (!hasDeleted)
                    {
                        editManager.EditMode = previousEditMode.Value;
                    }
                    else
                    {
                        previousEditMode = editManager.EditMode;
                        editManager.EditMode = EditMode.Modify;
                    }
                }
            }

            OnPropertyChanged(nameof(IsShiftHeld));
        }
    }

    private bool isCtrlHeld;

    public bool IsCtrlHeld
    {
        get => isCtrlHeld;
        private set
        {
            if (isCtrlHeld == value) return;
            isCtrlHeld = value;

            if (isCtrlHeld)
            {
                if (editManager?.EditMode == EditMode.AddPolygon ||
                    editManager?.EditMode == EditMode.DrawingPolygon || 
                    editManager?.EditMode == EditMode.Modify)
                {
                    previousEditMode = editManager.EditMode;
                    editManager.EditMode = EditMode.None;
                }
            }
            else
            {
                if (editManager != null && previousEditMode.HasValue)
                {
                    editManager.EditMode = previousEditMode.Value;
                    previousEditMode = null;
                }
            }

            OnPropertyChanged(nameof(isCtrlHeld));
        }
    }

    private string currentEditState;
    public string CurrentEditState
    {
        get => currentEditState;
        set
        {
            currentEditState = value;
            OnPropertyChanged(nameof(CurrentEditState));
        }
    }

    private void MapControl_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            IsCtrlHeld = false;
        }

        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            IsShiftHeld = false;
        }
    }

    private void MapControl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            if (!IsShiftHeld)
            {
                IsCtrlHeld = true;
            }
        }

        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            if (!IsCtrlHeld)
            {
                IsShiftHeld = true;
            }
        }
    }

    private void InitializeEditLayer()
    {
        editLayer = new Mapsui.Layers.WritableLayer
        {
            Name = "EditLayer"
        };
        MapControl.Map.Layers.Add(editLayer);
    }

    private void DoneEditButton_OnClick(object sender, RoutedEventArgs e)
    {
        throw new NotImplementedException();
    }

    private void InitializePolygonLayer()
    {
        StyleCollection polygonStyleCollection = new StyleCollection();
        polygonStyleCollection.Styles.Add(finishedVectorStyle);
        polygonLayer = new Mapsui.Layers.WritableLayer
        {
            Name = "PolygonLayer",
            Style = polygonStyleCollection,
        };
        MapControl.Map.Layers.Add(polygonLayer);
    }

    private void MapControl_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        DragCoordinate(e);
        
        // Maybe create this on click, so we keep in memory and do not need to recalculate every time
        Polygon? polygon = GetCurrentSketchPolygon();

        if (IsShiftHeld &&
            (editManager?.EditMode == EditMode.None || 
             editManager?.EditMode == EditMode.Modify) &&
            polygon != null &&
            polygon.Coordinates.Length > 5)
        {
            var world = GetWorldPoint(e);
            double pixelTolerance = 12.0 * MapControl.Map.Navigator.Viewport.Resolution;
            
            deleteCandidate = null;
            int coordinateIndex = 0;
            foreach (Coordinate coordinate in polygon.Coordinates)
            {
                bool isHoverVertex = coordinateIndex == polygon.Coordinates.Length - 2;
                bool isNearPolygon = !isHoverVertex &&
                                     world != null && 
                                     GeometryHelpers.IsNear(coordinate, world, pixelTolerance);
                if (isNearPolygon)
                {
                    MapControl.Cursor = Cursors.Cross;
                    EditState = EditState.Deleting;
                    deleteCandidate = coordinate;
                    break;
                }
                
                MapControl.Cursor = Cursors.Arrow;
                EditState = EditState.Editing;
                coordinateIndex++;
            }
        }

        if (IsCtrlHeld && 
            (editManager?.EditMode == EditMode.None || 
             editManager?.EditMode==EditMode.Modify) && 
            polygon != null)
        {
            MPoint world = GetWorldPoint(e);
            double pixelTolerance = 12.0 * MapControl.Map.Navigator.Viewport.Resolution;
            
            foreach (Coordinate coordinate in polygon.Coordinates)
            {
                bool isNearPolygon = world != null && GeometryHelpers.IsNear(coordinate, world, pixelTolerance);
                if (isNearPolygon)
                {
                    MapControl.Cursor = Cursors.Hand;
                    isDragMode = true;
                    break;
                }

                MapControl.Cursor = Cursors.Arrow;
                isDragMode = false;
            }
        }

        if (editManager?.EditMode == EditMode.None)
        {
            e.Handled = true;
            return;
        }

        if (editManager?.EditMode != EditMode.DrawingPolygon &&
            editManager?.EditMode != EditMode.Modify)
        {
            return;
        }

        if (editState == EditState.Finished)
        {
            e.Handled = true;
            return;
        }

        if (polygon == null)
        {
            return;
        }

        if (polygon.Coordinates.Length > 2)
        {
            var world = GetWorldPoint(e);
            double pixelTolerance = 20.0 * MapControl.Map.Navigator.Viewport.Resolution;
            bool isNearFirstPolygon =
                GeometryHelpers.IsNear(polygon.Coordinates[0], world, pixelTolerance);
            if (isNearFirstPolygon)
            {
                EditState = EditState.Accepted;
            }
            else
            {
                EditState = EditState.Editing;
            }
        }

        if (HasPolygonCrossingLines(polygon))
        {
            if (!EditState.Equals(EditState.Error))
            {
                EditState = EditState.Error;
            }
        }
        else
        {
            if (EditState.Equals(EditState.Error))
            {
                EditState = EditState.Editing;
            }
        }
    }

    private MPoint GetWorldPoint(MouseEventArgs e)
    {
        if (editLayer == null)
        {
            throw new ArgumentNullException(nameof(editLayer));
        }
        
        Point worldPoint = e.GetPosition(MapControl);
        MapInfo mapInfo = MapControl.GetMapInfo(new ScreenPosition(worldPoint.X, worldPoint.Y), [editLayer]);
        MPoint world = mapInfo.WorldPosition;
        return world;
    }

    private void MapControl_PreviewMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        Debug.WriteLine("Left mouse button down");

        if (IsShiftHeld &&
            (editManager?.EditMode == EditMode.None || 
             editManager?.EditMode == EditMode.Modify) &&
            deleteCandidate != null)
        {
            DeleteCoordinate(e);
        }
        
        if(IsCtrlHeld && 
           (editManager?.EditMode == EditMode.None || 
            editManager?.EditMode == EditMode.Modify) && 
           isDragMode)
        {
           StartDragCoordinate(e); 
        }

        if (editManager?.EditMode != EditMode.AddPolygon &&
            editManager?.EditMode != EditMode.DrawingPolygon &&
            EditMode != EditMode.Modify)
        {
            return;
        }

        if (editState == EditState.Finished)
        {
            return;
        }

        if (editState == EditState.Accepted)
        {
            ClosePolygon();
            e.Handled = true;
        }
    }

    private void ClosePolygon()
    {
        var geometryFeature = GetFeature();
        var polygon = geometryFeature?.Geometry as Polygon;
        if (polygon == null)
        {
            return;
        }
        
        editManager?.AddVertex(polygon.Coordinates[0]!); // Close by adding first coordinate again
        editManager!.EditMode = EditMode.None;
        EditState = EditState.Finished;
    }

    private bool CanDeleteCoordinate()
    {
        // Hover vertex cant be deleted
        // The polygon must have four or more vertices
        
        return false;
    }

    private bool isDragging;
    private void StartDragCoordinate(MouseButtonEventArgs e)
    {
        EditState = EditState.Dragging;
        if (editManager == null || 
            editLayer==null)
        {
            return;
        }
        
        editManager.EndEdit();
        editManager.EditMode = EditMode.Modify;
        var point = e.GetPosition(MapControl);
        var mapInfo = MapControl.GetMapInfo(new ScreenPosition(point.X, point.Y), new[] { editLayer });
        
        isDragging = editManager.StartDragging(mapInfo, editManager.VertexRadius);
        if (isDragging)
        {
            e.Handled = true;
        }
    }

    private void DragCoordinate(MouseEventArgs e)
    {
        if (!isDragging || e.LeftButton != MouseButtonState.Pressed) return;
        
        var point = e.GetPosition(MapControl);
        editManager?.Dragging(new NetTopologySuite.Geometries.Point(point.X, point.Y));
        editManager?.Layer?.DataHasChanged();
        MapControl.Refresh();
    }

    private void StopDragCoordinate()
    {
        if (!isDragging)
        {
            return;
        }

        editManager?.StopDragging();
        isDragging = false;
        isDragMode = false;

        editManager?.Layer?.DataHasChanged();
        MapControl.Refresh();
    }
    
    private void DeleteCoordinate(MouseButtonEventArgs e)
    {
        if (editManager == null || 
            editLayer == null)
        {
            return;
        }        
        
        Point worldPoint = e.GetPosition(MapControl);
        MapInfo mapInfo = MapControl.GetMapInfo(new ScreenPosition(worldPoint.X, worldPoint.Y), new[] { editLayer });

        editManager.EndEdit();
        editManager.EditMode = EditMode.Modify;

        editManager.TryDeleteCoordinate(mapInfo, editManager.VertexRadius);

        editManager.EndEdit();
        editManager.Layer?.DataHasChanged();
        MapControl.Map.Refresh();
        
        hasDeleted = true;
        MapControl.Cursor = Cursors.Arrow;
    }

    private GeometryFeature? GetFeature()
    {
        var features = editingWidget?.Layer?.GetFeatures();
        var geometryFeature =
            features?.OfType<GeometryFeature>()
                .FirstOrDefault(); // Should be my poly, needs some error handling I guess
        return geometryFeature;
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
        var geometryFeature = GetFeature();
        Polygon? polygon = geometryFeature?.Geometry as Polygon;

        return polygon;
    }

    private void CancelEdit()
    {
        RemoveEditingWidget();
        
        CancelEditButton.IsEnabled = false;
        StartEditButton.IsEnabled = true;
        EditState = EditState.None;
    }

    private void RemoveEditingWidget()
    {
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
            editingWidget = null;
            editManager = null;
            MapControl.Map.Refresh();
        }
    }

    private void StartEdit_Click(object sender, RoutedEventArgs e)
    {
        hasDeleted = false;
        EditState = EditState.Editing;

        if (editingWidget != null)
        {
            return;
        }
        
        this.editManager = new EditManager
        {
            Layer = editLayer,
            EditMode = EditMode.AddPolygon
        };

        editingWidget = new Mapsui.Nts.Widgets.EditingWidget(editManager);
        MapControl?.Map.Widgets.Add(editingWidget);
        SetEditStyles();
            
        CancelEditButton.IsEnabled = true;
        StartEditButton.IsEnabled = false;
    }

    private EditState editState = EditState.None;

    private EditState EditState
    {
        get => editState;
        set
        {
            editState = value;
            if (editState == EditState.Finished)
            {
                SaveEditButton.IsEnabled = true;
            }
            else
            {
                SaveEditButton.IsEnabled = false;
            }

            SetEditStyles();
            CurrentEditState = editState.ToString();
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

        switch (EditState)
        {
            case EditState.Error:
                styleCollection.Styles.Add(this.errorVectorStyle);
                break;
            case EditState.Accepted:
                styleCollection.Styles.Add(this.acceptedVectorStyle);
                break;
            case EditState.Finished:
                styleCollection.Styles.Add(this.finishedVectorStyle);
                break;
            case EditState.Editing:
            case EditState.None:
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
        SavePolygon();
        RemoveEditingWidget();

        CancelEditButton.IsEnabled = false;
        StartEditButton.IsEnabled = true;
        EditState = EditState.None;
    }

    private void SavePolygon()
    {
        var geometryFeature = GetFeature();

        if (geometryFeature != null)
        {
            polygonLayer?.Add(geometryFeature);
            editLayer?.Clear();
        }
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        if (propertyName != null)
        {
            OnPropertyChanged(propertyName);
        }
        
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected override void OnClosed(EventArgs e)
    {
        editModeTimer?.Stop();
        base.OnClosed(e);
    }
}