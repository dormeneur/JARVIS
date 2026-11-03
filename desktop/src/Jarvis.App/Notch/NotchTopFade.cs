using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Jarvis.App.Notch;

/// <summary>
/// An overlay for the top of scrolling notch content: text dissolves into the notch's black instead
/// of being cut off at a hard edge. Alpha falls along an inverse smoothstep (DESIGN-FACTS.md §2.2).
/// </summary>
internal static class NotchTopFade
{
    private const int Steps = 8;

    public static Border Create(double height = 52)
    {
        var gradient = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        for (var i = 0; i <= Steps; i++)
        {
            var t = (double)i / Steps;
            var grey = (byte)Math.Round(Math.Min(1, t * height / 56) * 30);
            var alpha = 1 - (t * t * (3 - (2 * t)));
            gradient.GradientStops.Add(new GradientStop { Offset = t, Color = Color.FromArgb((byte)Math.Round(alpha * 255), grey, grey, grey) });
        }

        return new Border
        {
            Height = height,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Background = gradient,
        };
    }
}
