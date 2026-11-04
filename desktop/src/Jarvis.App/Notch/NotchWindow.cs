using Jarvis.App.Hosting;
using Jarvis.Core.Notch;
using Jarvis.Platform.Shell;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Jarvis.App.Notch;

/// <summary>
/// A borderless, transparent, always-on-top window pinned to the top centre of the primary display.
/// It is as big as the notch can ever get; everything outside the drawn shape is see-through and
/// click-through.
/// </summary>
internal sealed class NotchWindow : Window
{
    /// <summary>Tallest panel plus room for the floating sliver's gap.</summary>
    /// <summary>Tall enough for the tallest panel (Settings); outside the panel it stays click-through.</summary>
    public const double EnvelopeHeight = NotchLayout.SettingsHeight + 8;

    private nint _returnFocusTo;

    public NotchWindow()
    {
        SystemBackdrop = new TransparentBackdrop();
        SurfaceHost = new Grid();
        ContentArea = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var root = new Grid();
        root.Children.Add(SurfaceHost);
        root.Children.Add(ContentArea);
        Content = root;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        PlaceAtTopCentre();
    }

    /// <summary>Hosts the composition visuals that draw the notch.</summary>
    public Grid SurfaceHost { get; }

    /// <summary>XAML content shown inside the notch, centred under the top edge.</summary>
    public Grid ContentArea { get; }

    /// <summary>Shows the window without taking focus from whatever the user is doing.</summary>
    public void ShowPassively()
    {
        AppWindow.Show(false);
        WindowChrome.MakeFloatingSurface(Handle);
    }

    /// <summary>Takes clicks and keyboard focus until <see cref="EndTyping"/>.</summary>
    public void BeginTyping()
    {
        var self = Handle;
        if (WindowChrome.ForegroundWindow() is var previous && previous != self)
        {
            _returnFocusTo = previous;
        }

        WindowChrome.SetInteractive(self, true);
        WindowChrome.Activate(self);
    }

    /// <summary>Click-through again, and focus goes back to the app the user was in.</summary>
    public void EndTyping()
    {
        var self = Handle;
        WindowChrome.SetInteractive(self, false);
        if (_returnFocusTo != 0 && WindowChrome.ForegroundWindow() == self)
        {
            WindowChrome.Activate(_returnFocusTo);
        }

        _returnFocusTo = 0;
    }

    /// <summary>Takes clicks over a top-centred panel of this size (DIPs); <see langword="null"/> for none.</summary>
    public void SetClickablePanel(double? width, double height = 0)
    {
        System.Drawing.Rectangle? area = null;
        if (width is { } w)
        {
            var scale = Content.XamlRoot?.RasterizationScale ?? 1;
            var panelWidth = (int)Math.Ceiling(w * scale);
            area = new System.Drawing.Rectangle((AppWindow.Size.Width - panelWidth) / 2, 0, panelWidth, (int)Math.Ceiling(height * scale));
        }

        WindowChrome.SetClickableArea(Handle, area);
    }

    public void RaiseToTop() => WindowChrome.RaiseToTop(Handle);

    /// <summary>Whether a desktop point (physical pixels) is on the top-centred panel of this size (DIPs).</summary>
    public bool IsOnPanel(int x, int y, double width, double height)
    {
        var scale = Content.XamlRoot?.RasterizationScale ?? 1;
        var origin = AppWindow.Position;
        var left = origin.X + ((AppWindow.Size.Width - (width * scale)) / 2);
        return x >= left && x <= left + (width * scale) && y >= origin.Y && y <= origin.Y + (height * scale);
    }

    /// <summary>The window handle (file pickers need an owner).</summary>
    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private void PlaceAtTopCentre()
    {
        this.ResizeDips(NotchLayout.EnvelopeWidth, EnvelopeHeight);
        var screen = DisplayArea.Primary.OuterBounds;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(screen.X + ((screen.Width - size.Width) / 2), screen.Y));
    }
}
