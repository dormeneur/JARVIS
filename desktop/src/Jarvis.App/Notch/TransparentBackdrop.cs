using Jarvis.Platform.Shell;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Jarvis.App.Notch;

/// <summary>
/// A backdrop that is fully see-through, so only what the window draws is visible. WinUI ships
/// only Mica and Acrylic; the system compositor's clear brush gives true transparency.
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private static global::Windows.UI.Composition.Compositor? _systemCompositor;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(connectedTarget);
        base.OnTargetConnected(connectedTarget, xamlRoot);
        connectedTarget.SystemBackdrop = SystemCompositor().CreateColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        ArgumentNullException.ThrowIfNull(disconnectedTarget);
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }

    private static global::Windows.UI.Composition.Compositor SystemCompositor()
    {
        if (_systemCompositor is null)
        {
            WindowChrome.EnsureSystemDispatcherQueue();
            _systemCompositor = new global::Windows.UI.Composition.Compositor();
        }

        return _systemCompositor;
    }
}
