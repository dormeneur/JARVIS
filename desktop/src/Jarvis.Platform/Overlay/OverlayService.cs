using System.Collections.Concurrent;
using Jarvis.Contracts.Overlay;
using Jarvis.Core;
using Jarvis.Core.Cursor;
using Jarvis.Platform.Shell;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Jarvis.Platform.Overlay;

/// <summary>
/// Owns the transparent overlay windows and runs their frame loop on a dedicated thread, paced to
/// the desktop compositor. Two layers share it: hand-drawn ink underneath, the companion cursor on
/// top. Commands from other threads are queued and applied at the next frame.
/// </summary>
public sealed partial class OverlayService : ICursorOverlay, IScreenDrawing, IHostedService, IDisposable
{
    private readonly IOptionsMonitor<CursorOptions> _options;
    private readonly PauseSwitch _pause;
    private ScreenHighlight? _highlight;
    private volatile bool _controlBorder;
    private readonly TimeProvider _time;
    private readonly ILogger<OverlayService> _logger;
    private readonly ConcurrentQueue<Action<Layers, TimeSpan>> _commands = new();
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _thread;
    private volatile bool _displaysChanged;

    public OverlayService(IOptionsMonitor<CursorOptions> options, PauseSwitch pause, TimeProvider time, ILogger<OverlayService> logger)
    {
        _options = options;
        _pause = pause;
        _time = time;
        _logger = logger;
    }

    public event Action? Clicked;

    public ScreenHighlight? TakeHighlight() => Interlocked.Exchange(ref _highlight, null);

    public void ShowControlBorder(bool on) => _controlBorder = on;

    public void SetThinking(bool thinking) => _commands.Enqueue((layers, now) => layers.Cursor.SetThinking(thinking, now));

    public void SetListening(bool listening) => _commands.Enqueue((layers, _) => layers.Cursor.SetListening(listening));

    public void ShowLevel(double level) => _commands.Enqueue((layers, _) => layers.Cursor.ShowLevel(level));

    public void PointAt(int x, int y, string? label = null) => _commands.Enqueue((layers, _) =>
    {
        layers.Cursor.PointAt(x, y, label);
        layers.RaiseAll();
    });

    public void ReturnToMouse() => _commands.Enqueue((layers, _) => layers.Cursor.ReturnToMouse());

    public void Show(ScreenDrawing drawing) => _commands.Enqueue((layers, now) =>
    {
        layers.Drawing.Show(drawing, now);
        layers.RaiseAll();
    });

    public void Clear() => _commands.Enqueue((layers, now) => layers.Drawing.Clear(now));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "Jarvis overlay", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    private void RunLoop()
    {
        try
        {
            OverlayWindow.DisplaysChanged += () => _displaysChanged = true;
            using var graphics = new OverlayGraphics();
            using var cursorRenderer = new CursorRenderer(graphics);
            using var drawingRenderer = new DrawingRenderer(graphics, _logger);
            using var pacer = new FramePacer();
            using var catcher = new ClickCatcher(WindowChrome.ExcludeFromCapture);
            using var bubble = new BubbleRenderer(graphics);
            using var highlightRenderer = new HighlightRenderer(graphics);
            using var border = new ControlBorderRenderer(graphics);
            var highlighter = new Jarvis.Core.Gestures.Highlighter();
            var highlighting = false;
            var windows = CreateWindows(graphics);
            var layers = new Layers(new CursorCompanion(), drawingRenderer, () => windows);
            CursorPose? lastDrawn = null;
            var lastPhase = layers.Cursor.Phase;

            while (!_stopping.IsCancellationRequested)
            {
                PumpMessages();
                if (_displaysChanged)
                {
                    _displaysChanged = false;
                    cursorRenderer.AttachTo(null);
                    drawingRenderer.Detach();
                    windows.ForEach(window => window.Dispose());
                    windows = CreateWindows(graphics);
                    lastDrawn = null;
                }

                var now = _time.GetElapsedTime(0);
                while (_commands.TryDequeue(out var command))
                {
                    command(layers, now);
                }

                try
                {
                    drawingRenderer.Render(windows, now);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // One bad drawing must not take the cursor down with it.
                    LogDrawingFailed(exception);
                    drawingRenderer.Abandon();
                }

                var options = _options.CurrentValue;
                layers.Cursor.ShakeToDetachEnabled = options.ShakeToDetach;
                PInvoke.GetCursorPos(out var mouse);
                var host = windows.Find(window => window.Display.Contains(mouse.X, mouse.Y)) ?? windows[0];
                var pose = layers.Cursor.Update(now, mouse.X, mouse.Y, host.Display.Scale);

                if (layers.Cursor.Phase != lastPhase)
                {
                    lastPhase = layers.Cursor.Phase;
                    LogPhase(lastPhase, (int)pose.X, (int)pose.Y, mouse.X, mouse.Y);
                }

                var visible = options.Visible && !_pause.IsPaused
                              && !(options.HideInFullscreen && layers.Cursor.IsIdle && Fullscreen.IsActive);
                cursorRenderer.AttachTo(visible ? HostFor(windows, pose) ?? host : null);
                if (visible && pose != lastDrawn)
                {
                    cursorRenderer.Render(pose, CursorColor.Parse(options.Color));
                    lastDrawn = pose;
                }

                bubble.Show(visible ? HostFor(windows, pose) : null, pose.Bubble, pose.X, pose.Y, CursorColor.Parse(options.Color));

                // Talk keys held: the mouse draws a highlighter trail the turn reads as "this".
                if (layers.Cursor.IsListening && options.Highlighter && visible)
                {
                    if (!highlighting)
                    {
                        highlighting = true;
                        _highlight = null;
                        highlighter.Start(mouse.X, mouse.Y, host.Display.Scale, now);
                    }
                    else
                    {
                        highlighter.Add(mouse.X, mouse.Y, now);
                    }

                    if (highlighter.IsDrawing)
                    {
                        highlightRenderer.Update(host, highlighter.Path, CursorColor.Parse(options.Color));
                    }
                }
                else if (highlighting)
                {
                    highlighting = false;
                    _highlight = highlighter.Finish();
                    highlightRenderer.Release(now);
                }

                highlightRenderer.Render(now);

                border.Set(_controlBorder, now);
                border.Render(windows, now);

                if (visible && layers.Cursor.IsDetached)
                {
                    catcher.ShowAt(pose.X, pose.Y, host.Display.Scale);
                }
                else
                {
                    catcher.Hide();
                }

                if (ClickCatcher.TakeClick() && layers.Cursor.IsDetached)
                {
                    layers.Cursor.Clicked(now);

                    // Off this thread: starting the microphone must not stall a frame.
                    ThreadPool.QueueUserWorkItem(_ => Clicked?.Invoke());
                }

                graphics.Composition.Commit();
                pacer.WaitForNextFrame();
            }

            cursorRenderer.AttachTo(null);
            drawingRenderer.Detach();
            windows.ForEach(window => window.Dispose());
        }
        catch (Exception exception)
        {
            LogOverlayFailed(exception);
        }
    }

    /// <summary>The monitor the cursor itself is on; it can be on a different one than the mouse mid-flight.</summary>
    private static OverlayWindow? HostFor(List<OverlayWindow> windows, CursorPose pose)
        => windows.Find(window => window.Display.Contains((int)pose.X, (int)pose.Y));

    private List<OverlayWindow> CreateWindows(OverlayGraphics graphics)
    {
        var windows = Display.All().Select(display => new OverlayWindow(display, graphics.Composition, WindowChrome.ExcludeFromCapture)).ToList();
        LogDisplays(windows.Count, string.Join(", ", windows.Select(window => window.Display)));
        return windows;
    }

    private static void PumpMessages()
    {
        while (PInvoke.PeekMessage(out var message, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            PInvoke.TranslateMessage(message);
            PInvoke.DispatchMessage(message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Overlay covering {Count} display(s): {Displays}")]
    private partial void LogDisplays(int count, string displays);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cursor {Phase} at {X},{Y} (mouse {MouseX},{MouseY})")]
    private partial void LogPhase(CursorPhase phase, int x, int y, int mouseX, int mouseY);

    [LoggerMessage(Level = LogLevel.Error, Message = "A drawing failed to render and was dropped")]
    private partial void LogDrawingFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Overlay stopped after an unexpected error")]
    private partial void LogOverlayFailed(Exception exception);

    /// <summary>The state the frame loop owns; touched only on the overlay thread.</summary>
    private sealed record Layers(CursorCompanion Cursor, DrawingRenderer Drawing, Func<List<OverlayWindow>> Windows)
    {
        public void RaiseAll() => Windows().ForEach(window => window.RaiseToTop());
    }
}
