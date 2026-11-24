using System.Numerics;
using Jarvis.Core.Vault;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Jarvis.App.Settings;

/// <summary>
/// "About me" as a living graph, like Obsidian's: you in the middle, folders and topics around you,
/// each fact around its topic, related facts joined across topics. What's drawn glides toward the
/// simulation (never jumps), an edit keeps everything that was already there in place and grows new
/// nodes out of their parent, and a click (or a search hit) glides the camera in to centre that node.
/// It stops animating once everything is still, so a settled graph costs nothing.
/// Drag a node and the rest follow; drag the background to pan; the wheel zooms around the pointer.
/// </summary>
internal sealed partial class KnowledgeGraphView : Grid
{
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 0x5B, 0x9C, 0xFF), Color.FromArgb(255, 0xB3, 0x88, 0xFF), Color.FromArgb(255, 0x3F, 0xD0, 0xB5),
        Color.FromArgb(255, 0xFF, 0xB8, 0x4D), Color.FromArgb(255, 0xFF, 0x7A, 0xB6), Color.FromArgb(255, 0x8B, 0xD8, 0x5F),
        Color.FromArgb(255, 0xFF, 0x8F, 0x5E), Color.FromArgb(255, 0x5E, 0xD6, 0xF2),
    ];

    private static readonly Color YouColor = Color.FromArgb(255, 0xF4, 0xF7, 0xFF);
    private static readonly Color FolderColor = Color.FromArgb(255, 0xC9, 0xD2, 0xE3);
    private static readonly Color SearchColor = Color.FromArgb(255, 0xFF, 0xE0, 0x66);

    private const double MinZoom = 0.35;
    private const double MaxZoom = 3.0;
    private const double FocusZoom = 1.9;
    private const float Settled = 0.04f;
    private const int SettledFrames = 30;

    /// <summary>Each frame what's drawn covers this share of the way to the simulation: smooth, never a jump.</summary>
    private const float Glide = 0.2f;

    /// <summary>The camera's share of the way to where it's going, per frame (an ease-out over about half a second).</summary>
    private const double CameraGlide = 0.14;

    private readonly Canvas _world = new();
    private readonly CompositeTransform _camera = new();
    private readonly DispatcherQueueTimer _frame;
    private readonly List<(Ellipse Dot, TextBlock Label)> _shapes = [];
    private readonly List<(Line Line, int A, int B, bool Related)> _lines = [];
    private List<HashSet<int>> _neighbours = [];
    private HashSet<int> _matches = [];
    private ForceLayout? _layout;
    private Vector2[] _display = [];
    private Vector2 _pan;
    private double _zoom = 1;
    private (Vector2 Pan, double Zoom)? _cameraTarget;
    private int _quiet;
    private int _hover = -1;
    private int _selected = -1;
    private int _dragging = -1;
    private Point? _panFrom;
    private bool _moved;

    public KnowledgeGraphView()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)); // takes the pointer everywhere
        CornerRadius = new CornerRadius(12);
        _world.RenderTransform = _camera;
        Children.Add(_world);
        _frame = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _frame.Interval = TimeSpan.FromMilliseconds(16);
        _frame.Tick += (_, _) => Frame();
        SizeChanged += (_, e) =>
        {
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            Aim();
        };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCanceled += (_, _) => EndDrag();
        PointerWheelChanged += OnWheel;
        Unloaded += (_, _) => _frame.Stop();
    }

    /// <summary>The node clicked (null: the background).</summary>
    public event Action<GraphNode?>? Picked;

    /// <summary>
    /// Shows a (new) graph. Nodes already on screen stay where they are; new ones grow out of their
    /// parent; the camera and the picked node carry over when that node still exists.
    /// </summary>
    public void Show(KnowledgeMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var previous = _layout?.Snapshot();
        var drawn = _layout is null ? null : _layout.Nodes.Select((node, i) => (node.Id, i)).ToDictionary(pair => pair.Id, pair => _display[pair.i]);
        var selectedId = _selected >= 0 && _layout is not null ? _layout.Nodes[_selected].Id : null;

        _world.Children.Clear();
        _shapes.Clear();
        _lines.Clear();
        _hover = _dragging = -1;
        _matches = [];
        _layout = new ForceLayout(map, previous);
        _display = [.. _layout.Positions.Select((position, i) => drawn is not null && drawn.TryGetValue(map.Nodes[i].Id, out var was) ? was : position)];
        var index = map.Nodes.Select((node, i) => (node.Id, i)).ToDictionary(pair => pair.Id, pair => pair.i);
        _selected = selectedId is not null && index.TryGetValue(selectedId, out var again) ? again : -1;
        _neighbours = [.. map.Nodes.Select(_ => new HashSet<int>())];
        foreach (var edge in map.Edges)
        {
            if (!index.TryGetValue(edge.From, out var a) || !index.TryGetValue(edge.To, out var b))
            {
                continue;
            }

            _neighbours[a].Add(b);
            _neighbours[b].Add(a);
            var line = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(edge.Related ? (byte)0x50 : (byte)0x70, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = edge.Related ? 0.8 : 1.1,
                StrokeDashArray = edge.Related ? [3, 3] : null,
                IsHitTestVisible = false,
            };
            _lines.Add((line, a, b, edge.Related));
            _world.Children.Add(line);
        }

        for (var i = 0; i < map.Nodes.Count; i++)
        {
            var node = map.Nodes[i];
            var radius = Radius(node.Kind);
            var dot = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = new SolidColorBrush(ColorOf(node)),
                Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0x0B, 0x10, 0x1C)),
                StrokeThickness = 1.5,
            };
            ToolTipService.SetToolTip(dot, node.Kind == GraphNodeKind.Fact ? node.Text : node.Label);
            var at = i;
            dot.PointerEntered += (_, _) =>
            {
                _hover = at;
                Emphasise();
            };
            dot.PointerExited += (_, _) =>
            {
                if (_hover == at)
                {
                    _hover = -1;
                    Emphasise();
                }
            };
            dot.PointerPressed += (_, e) =>
            {
                _dragging = at;
                _moved = false;
                CapturePointer(e.Pointer);
                e.Handled = true;
            };
            var label = new TextBlock
            {
                Text = node.Label,
                FontSize = node.Kind == GraphNodeKind.Fact ? 10 : 12,
                FontWeight = node.Kind == GraphNodeKind.Fact ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                IsHitTestVisible = false,
            };
            _shapes.Add((dot, label));
            _world.Children.Add(dot);
            _world.Children.Add(label);
        }

        Aim();
        Render();
        Emphasise();
        Wake();
    }

    /// <summary>Lights up every node matching the search (and zooms to the first); empty clears it. Returns how many matched.</summary>
    public int Search(string query)
    {
        if (_layout is null)
        {
            return 0;
        }

        _matches = [.. Enumerable.Range(0, _layout.Nodes.Count).Where(i => KnowledgeGraph.Matches(_layout.Nodes[i], query))];
        Emphasise();
        if (_matches.Count > 0)
        {
            FocusOn(_matches.Min(), 1.4);
        }

        return _matches.Count;
    }

    /// <summary>Back to the whole map: zoomed out, you in the middle.</summary>
    public void Recenter()
    {
        _selected = -1;
        Emphasise();
        _cameraTarget = (Vector2.Zero, 1);
        Wake();
    }

    public void ClearSelection()
    {
        _selected = -1;
        Emphasise();
    }

    private static double Radius(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.You => 13,
        GraphNodeKind.Folder => 10,
        GraphNodeKind.Topic => 8,
        _ => 4.5,
    };

    private static Color ColorOf(GraphNode node) => node.Kind switch
    {
        GraphNodeKind.You => YouColor,
        GraphNodeKind.Folder => FolderColor,
        _ => Palette[Math.Max(0, node.Group) % Palette.Length],
    };

    private void Wake()
    {
        _quiet = 0;
        if (!_frame.IsRunning)
        {
            _frame.Start();
        }
    }

    private void Frame()
    {
        if (_layout is null)
        {
            _frame.Stop();
            return;
        }

        var moving = _layout.Step();
        float gap = 0;
        for (var i = 0; i < _display.Length; i++)
        {
            var step = (_layout.Positions[i] - _display[i]) * Glide;
            _display[i] += step;
            gap = Math.Max(gap, step.Length());
        }

        var camera = StepCamera();
        Render();
        // Everything still: stop, so a settled graph costs nothing (a drag, a search or a new graph wakes it).
        _quiet = moving < Settled && gap < 0.05f && !camera && _dragging < 0 ? _quiet + 1 : 0;
        if (_quiet > SettledFrames)
        {
            _frame.Stop();
        }
    }

    /// <summary>Eases the camera toward its target; false once it's there (or has none).</summary>
    private bool StepCamera()
    {
        if (_cameraTarget is not var (pan, zoom))
        {
            return false;
        }

        _pan += (pan - _pan) * (float)CameraGlide;
        _zoom += (zoom - _zoom) * CameraGlide;
        var close = Vector2.Distance(_pan, pan) < 0.5f && Math.Abs(_zoom - zoom) < 0.005;
        if (close)
        {
            (_pan, _zoom, _cameraTarget) = (pan, zoom, null);
        }

        Aim();
        if (close)
        {
            Emphasise(); // fact names appear once close in
        }

        return !close;
    }

    /// <summary>Glides the camera in until node <paramref name="i"/> is the centre of the view.</summary>
    private void FocusOn(int i, double zoom)
    {
        var target = Math.Clamp(Math.Max(_zoom, zoom), MinZoom, MaxZoom);
        var at = _layout!.Positions[i];
        _cameraTarget = (new Vector2((float)(-at.X * target), (float)(-at.Y * target)), target);
        Wake();
    }

    private void Render()
    {
        for (var i = 0; i < _shapes.Count && i < _display.Length; i++)
        {
            var (dot, label) = _shapes[i];
            var radius = dot.Width / 2;
            Canvas.SetLeft(dot, _display[i].X - radius);
            Canvas.SetTop(dot, _display[i].Y - radius);
            Canvas.SetLeft(label, _display[i].X + radius + 4);
            Canvas.SetTop(label, _display[i].Y - 8);
        }

        foreach (var (line, a, b, _) in _lines)
        {
            line.X1 = _display[a].X;
            line.Y1 = _display[a].Y;
            line.X2 = _display[b].X;
            line.Y2 = _display[b].Y;
        }
    }

    /// <summary>
    /// A search lights its matches (in yellow); otherwise a hovered or picked node and its neighbours
    /// stay bright and the rest step back. Fact names show when lit, or once zoomed in close.
    /// </summary>
    private void Emphasise()
    {
        var focus = _hover >= 0 ? _hover : _selected;
        HashSet<int>? lit = _matches.Count > 0 ? [.. _matches, .. focus >= 0 ? [focus] : Array.Empty<int>()]
            : focus >= 0 ? [focus, .. _neighbours[focus]]
            : null;
        var close = _zoom >= 1.5;
        for (var i = 0; i < _shapes.Count; i++)
        {
            var (dot, label) = _shapes[i];
            var bright = lit is null || lit.Contains(i);
            dot.Opacity = bright ? 1 : 0.16;
            var ring = i == _selected ? Microsoft.UI.Colors.White : _matches.Contains(i) ? SearchColor : Color.FromArgb(0x90, 0x0B, 0x10, 0x1C);
            dot.Stroke = new SolidColorBrush(ring);
            dot.StrokeThickness = i == _selected || _matches.Contains(i) ? 3 : 1.5;
            var isFact = _layout?.Nodes[i].Kind == GraphNodeKind.Fact;
            label.Opacity = !bright ? 0.1 : isFact && !close && lit is null ? 0 : 1;
        }

        foreach (var (line, a, b, related) in _lines)
        {
            var bright = lit is null || (lit.Contains(a) && lit.Contains(b) && (a == focus || b == focus || _matches.Count > 0));
            line.Opacity = lit is null ? (related ? 0.7 : 1) : bright ? 1 : 0.07;
        }
    }

    private void Aim()
    {
        _camera.ScaleX = _camera.ScaleY = _zoom;
        _camera.TranslateX = (ActualWidth / 2) + _pan.X;
        _camera.TranslateY = (ActualHeight / 2) + _pan.Y;
    }

    private Vector2 ToWorld(Point point)
        => new((float)((point.X - _camera.TranslateX) / _zoom), (float)((point.Y - _camera.TranslateY) / _zoom));

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging >= 0)
        {
            return; // a node took it
        }

        _panFrom = e.GetCurrentPoint(this).Position;
        _moved = false;
        _cameraTarget = null; // the user takes the camera
        CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        if (_dragging >= 0 && _layout is not null)
        {
            var at = ToWorld(point);
            if (Vector2.Distance(at, _layout.Positions[_dragging]) > 3)
            {
                _moved = true;
            }

            if (_moved)
            {
                _layout.Pinned = _dragging;
                _layout.Positions[_dragging] = at;
                _display[_dragging] = at; // the dragged node follows the pointer exactly
                Wake();
            }
        }
        else if (_panFrom is { } from)
        {
            var delta = new Vector2((float)(point.X - from.X), (float)(point.Y - from.Y));
            if (delta.Length() > 2)
            {
                _moved = true;
            }

            _pan += delta;
            _panFrom = point;
            Aim();
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var wasNode = _dragging;
        var clicked = !_moved;
        EndDrag();
        ReleasePointerCaptures();
        if (!clicked)
        {
            return;
        }

        _selected = wasNode;
        Emphasise();
        if (wasNode >= 0 && _layout is not null)
        {
            FocusOn(wasNode, FocusZoom);
        }

        Picked?.Invoke(wasNode >= 0 && _layout is not null ? _layout.Nodes[wasNode] : null);
    }

    private void EndDrag()
    {
        if (_dragging >= 0 && _layout is not null && _moved)
        {
            _layout.Pinned = -1;
            _layout.Nudge();
            Wake();
        }

        _dragging = -1;
        _panFrom = null;
    }

    /// <summary>Zooms around the pointer: what's under it stays under it.</summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var before = ToWorld(point.Position);
        _cameraTarget = null;
        _zoom = Math.Clamp(_zoom * Math.Pow(1.0015, point.Properties.MouseWheelDelta), MinZoom, MaxZoom);
        _pan = new Vector2(
            (float)(point.Position.X - (before.X * _zoom) - (ActualWidth / 2)),
            (float)(point.Position.Y - (before.Y * _zoom) - (ActualHeight / 2)));
        Aim();
        Emphasise();
        e.Handled = true;
    }
}
