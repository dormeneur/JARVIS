using System.Text.Json;

namespace Jarvis.Platform.Mac.Shell;

/// <summary>
/// A display on the y-down desktop, in points (origin at the main display's top-left), the space
/// Jarvis.Core works in on a Mac. Scale is the backing scale (2 on Retina); Core distances are
/// points, so the overlay passes 1 to Core.
/// </summary>
public sealed record Screen(double Left, double Top, double Width, double Height, double Scale, double NotchWidth, double NotchHeight, double MenuBar)
{
    public bool Contains(double x, double y) => x >= Left && x < Left + Width && y >= Top && y < Top + Height;

    public bool HasNotch => NotchWidth > 0;

    public static IReadOnlyList<Screen> All()
    {
        using var document = JsonDocument.Parse(Native.Take(Native.Screens()) ?? "[]");
        return document.RootElement.EnumerateArray().Select(screen => new Screen(
            screen.GetProperty("left").GetDouble(),
            screen.GetProperty("top").GetDouble(),
            screen.GetProperty("width").GetDouble(),
            screen.GetProperty("height").GetDouble(),
            screen.GetProperty("scale").GetDouble(),
            screen.GetProperty("notch").GetDouble(),
            screen.GetProperty("notchHeight").GetDouble(),
            screen.GetProperty("menuBar").GetDouble())).ToList();
    }
}
