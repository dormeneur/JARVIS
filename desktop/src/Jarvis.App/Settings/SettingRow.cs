using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings;

/// <summary>
/// One line of a settings card: title and explanation on the left, the control on the right.
/// Its look lives in Design/Controls.xaml.
/// </summary>
public sealed partial class SettingRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public SettingRow()
    {
        DefaultStyleKey = typeof(SettingRow);
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
