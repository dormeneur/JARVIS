using System.Globalization;
using System.Numerics;
using System.Text;
using Jarvis.Contracts.Overlay;

namespace Jarvis.Core.Drawing;

/// <summary>
/// What the brain asked to draw, parsed from a [DRAW]…[/DRAW] block: one command per line,
/// coordinates in the screenshot's pixels. Unknown or malformed lines are skipped so one bad line
/// never costs the whole drawing.
/// <code>
/// box x y w h "label"      a hand-drawn rectangle around a region
/// ring x y w h "label"     a loose circle around a region
/// arrow x1 y1 x2 y2 "label" an arrow from (x1,y1) pointing at (x2,y2)
/// num x y N                a numbered step badge
/// note x y "words"         a label on its own
///
/// figure x y size "title"            a turning 3D figure centred at (x,y), about size pixels wide;
///                                    the solids after it belong to it, in its own -1..1 space:
/// block x y z w h d [colour] "label" a box centred at (x,y,z)
/// ball x y z r [colour] "label"      a sphere
/// cylinder x y z r h [colour] "label" an upright cylinder centred at (x,y,z)
/// cone x y z r h [colour] "label"    an upright cone, point up
/// rod x1 y1 z1 x2 y2 z2 r [colour] "label" a cylinder between two points (a bond, a beam)
/// </code>
/// </summary>
public sealed record DrawingScript(int Screen, IReadOnlyList<DrawCommand> Commands)
{
    public static DrawingScript Parse(string body, int screen)
    {
        ArgumentNullException.ThrowIfNull(body);
        var commands = new List<DrawCommand>();
        foreach (var line in body.Split('\n'))
        {
            if (ParseLine(Tokenize(line)) is { } command)
            {
                commands.Add(command);
            }
        }

        return new DrawingScript(Math.Max(1, screen), commands);
    }

    private static DrawCommand? ParseLine(List<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return null;
        }

        var verb = tokens[0].ToUpperInvariant();
        var numbers = tokens.Skip(1).Select(Number).TakeWhile(value => value is not null).Select(value => value!.Value).ToList();
        var label = tokens.Count > numbers.Count + 1 ? tokens[numbers.Count + 1] : null;

        // A solid's colour comes before its label: "red" alone is a colour, not a label.
        var colour = label is null ? null : Colour(label);
        if (colour is not null)
        {
            label = tokens.Count > numbers.Count + 2 ? tokens[numbers.Count + 2] : null;
        }

        Vector3 At(int index) => new(Space(numbers[index]), Space(numbers[index + 1]), Space(numbers[index + 2]));
        DrawCommand Upright(FigureShape shape)
        {
            var (centre, half) = (At(0), new Vector3(0, Extent(numbers[4]) / 2, 0));
            return new DrawCommand.Solid(shape, centre - half, centre + half, Extent(numbers[3]), colour, label);
        }

        DrawCommand Block()
        {
            var half = new Vector3(Extent(numbers[3]), Extent(numbers[4]), Extent(numbers[5])) / 2;
            return new DrawCommand.Solid(FigureShape.Box, At(0) - half, At(0) + half, 0, colour, label);
        }

        return verb switch
        {
            "FIGURE" when numbers.Count >= 3 && numbers[2] > 0 => new DrawCommand.Figure(numbers[0], numbers[1], numbers[2], label),
            "BLOCK" when numbers.Count >= 6 => Block(),
            "BALL" when numbers.Count >= 4 => new DrawCommand.Solid(FigureShape.Sphere, At(0), At(0), Extent(numbers[3]), colour, label),
            "CYLINDER" when numbers.Count >= 5 => Upright(FigureShape.Cylinder),
            "CONE" when numbers.Count >= 5 => Upright(FigureShape.Cone),
            "ROD" when numbers.Count >= 7 => new DrawCommand.Solid(FigureShape.Cylinder, At(0), At(3), Extent(numbers[6]), colour, label),
            "BOX" when numbers.Count >= 4 => new DrawCommand.Box(numbers[0], numbers[1], numbers[2], numbers[3], label),
            "RING" when numbers.Count >= 4 => new DrawCommand.Ring(numbers[0], numbers[1], numbers[2], numbers[3], label),
            "ARROW" when numbers.Count >= 4 => new DrawCommand.Arrow(numbers[0], numbers[1], numbers[2], numbers[3], label),
            "NUM" when numbers.Count >= 3 => new DrawCommand.Step(numbers[0], numbers[1], (int)numbers[2]),
            "NOTE" when numbers.Count >= 2 && label is not null => new DrawCommand.Note(numbers[0], numbers[1], label),
            _ => null,
        };
    }

    // Figure space is -1..1; a little overhang is fine, wild numbers are not.
    private static float Space(float value) => Math.Clamp(value, -1.5f, 1.5f);

    private static float Extent(float value) => Math.Clamp(Math.Abs(value), 0.01f, 3f);

    /// <summary>A colour name or #rrggbb; null for anything else (then the token is a label).</summary>
    private static uint? Colour(string token) => token.ToUpperInvariant() switch
    {
        "BLUE" => 0x3380FF,
        "GREEN" => 0x26C281,
        "RED" => 0xFF4C4C,
        "YELLOW" or "GOLD" => 0xD4A017,
        "ORANGE" => 0xFFA659,
        "PURPLE" => 0x9B5CFF,
        "PINK" => 0xFF5CA8,
        "GREY" or "GRAY" => 0x9AA0A6,
        "WHITE" => 0xF2F2F2,
        "BLACK" => 0x303030,
        _ when token.Length == 7 && token[0] == '#' && uint.TryParse(token[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb) => rgb,
        _ => null,
    };

    private static float? Number(string token)
        => float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Splits on whitespace, keeping "quoted phrases" together (quotes removed).</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in line.Trim())
        {
            if (c is '"' or '“' or '”')
            {
                if (quoted)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

/// <summary>One mark to draw, in screenshot pixels.</summary>
public abstract record DrawCommand
{
    private DrawCommand()
    {
    }

    public sealed record Box(float X, float Y, float Width, float Height, string? Label) : DrawCommand;

    public sealed record Ring(float X, float Y, float Width, float Height, string? Label) : DrawCommand;

    /// <summary>From the tail (<paramref name="FromX"/>,<paramref name="FromY"/>) to the tip.</summary>
    public sealed record Arrow(float FromX, float FromY, float ToX, float ToY, string? Label) : DrawCommand;

    public sealed record Step(float X, float Y, int Number) : DrawCommand;

    public sealed record Note(float X, float Y, string Text) : DrawCommand;

    /// <summary>Starts a 3D figure; the <see cref="Solid"/>s after it are its parts.</summary>
    public sealed record Figure(float X, float Y, float Size, string? Title) : DrawCommand;

    /// <summary>A part of the current figure, in figure space (see <see cref="FigurePart"/>). No colour = the ink.</summary>
    public sealed record Solid(FigureShape Shape, Vector3 From, Vector3 To, float Radius, uint? Rgb, string? Label) : DrawCommand;
}
