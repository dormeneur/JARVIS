using CommunityToolkit.Mvvm.ComponentModel;
using Jarvis.Core.Cursor;
using Microsoft.Extensions.Options;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class CursorViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    /// <summary>False while the constructor loads current values, so loading isn't mistaken for a change.</summary>
    private readonly bool _ready;

    public CursorViewModel(SettingsStore store, IOptionsMonitor<CursorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        var current = options.CurrentValue;
        var rgb = CursorColor.Parse(current.Color);
        SelectedColor = Colors.FirstOrDefault(color => color.Rgb == rgb) ?? Colors[0];
        Visible = current.Visible;
        ShakeToDetach = current.ShakeToDetach;
        HideInFullscreen = current.HideInFullscreen;
        Highlighter = current.Highlighter;
        _ready = true;
    }

    /// <summary>The cursor colours; drawings and highlights use the same ink.</summary>
    public IReadOnlyList<ColorChoice> Colors { get; } =
    [
        new("Blue", CursorColor.Blue),
        new("Green", CursorColor.Green),
        new("Yellow", CursorColor.Yellow),
        new("Red", CursorColor.Red),
    ];

    [ObservableProperty]
    public partial ColorChoice SelectedColor { get; set; }

    [ObservableProperty]
    public partial bool Visible { get; set; }

    [ObservableProperty]
    public partial bool ShakeToDetach { get; set; }

    partial void OnSelectedColorChanged(ColorChoice value)
    {
        if (_ready)
        {
            _store.Set(CursorOptions.SectionName, nameof(CursorOptions.Color), $"#{value.Rgb:X6}");
        }
    }

    partial void OnVisibleChanged(bool value)
    {
        if (_ready)
        {
            _store.Set(CursorOptions.SectionName, nameof(CursorOptions.Visible), value);
        }
    }

    [ObservableProperty]
    public partial bool HideInFullscreen { get; set; }

    [ObservableProperty]
    public partial bool Highlighter { get; set; }

    partial void OnHighlighterChanged(bool value)
    {
        if (_ready)
        {
            _store.Set(CursorOptions.SectionName, nameof(CursorOptions.Highlighter), value);
        }
    }

    partial void OnHideInFullscreenChanged(bool value)
    {
        if (_ready)
        {
            _store.Set(CursorOptions.SectionName, nameof(CursorOptions.HideInFullscreen), value);
        }
    }

    partial void OnShakeToDetachChanged(bool value)
    {
        if (_ready)
        {
            _store.Set(CursorOptions.SectionName, nameof(CursorOptions.ShakeToDetach), value);
        }
    }
}

public sealed record ColorChoice(string Name, uint Rgb)
{
    public SolidColorBrush Brush { get; } = new(Color.FromArgb(255, (byte)(Rgb >> 16), (byte)(Rgb >> 8), (byte)Rgb));

    public override string ToString() => Name;
}
