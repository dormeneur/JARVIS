namespace Jarvis.Contracts.Capture;

/// <summary>Screenshots of every display, excluding Jarvis's own floating surfaces.</summary>
public interface IScreenCapture
{
    /// <summary>One capture per display, the display under the mouse first.</summary>
    Task<IReadOnlyList<CapturedScreen>> CaptureAsync(CancellationToken cancellationToken);
}

/// <param name="Image">Downscaled JPEG for the model.</param>
/// <param name="Left">Display bounds on the virtual desktop, physical pixels.</param>
/// <param name="Top">Display bounds on the virtual desktop, physical pixels.</param>
/// <param name="Width">Display width in physical pixels.</param>
/// <param name="Height">Display height in physical pixels.</param>
/// <param name="Scale">Display scale (1.0 = 96 DPI), for sizing anything drawn back onto it.</param>
public sealed record CapturedScreen(ScreenImage Image, int Left, int Top, int Width, int Height, double Scale = 1.0)
{
    /// <summary>Maps a point in image pixels back to the virtual desktop.</summary>
    public (int X, int Y) ToDesktop(double imageX, double imageY)
        => (Left + (int)Math.Round(imageX * Width / Image.Width), Top + (int)Math.Round(imageY * Height / Image.Height));
}

/// <param name="Jpeg">Encoded image, already downscaled for the model.</param>
/// <param name="Width">Pixel width of <paramref name="Jpeg"/>; pointing coordinates use this space.</param>
/// <param name="Height">Pixel height of <paramref name="Jpeg"/>.</param>
/// <param name="HasCursor">The user's mouse is on this display.</param>
public sealed record ScreenImage(ReadOnlyMemory<byte> Jpeg, int Width, int Height, bool HasCursor);
