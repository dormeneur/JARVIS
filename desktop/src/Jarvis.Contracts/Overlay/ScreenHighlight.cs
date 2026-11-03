namespace Jarvis.Contracts.Overlay;

/// <summary>
/// What the user marked with the mouse while talking, in desktop pixels: the bounds of a circled
/// area, or a single spot they held the mouse on (<paramref name="IsPoint"/>; left = right, top = bottom).
/// </summary>
public sealed record ScreenHighlight(double Left, double Top, double Right, double Bottom, bool IsPoint);
