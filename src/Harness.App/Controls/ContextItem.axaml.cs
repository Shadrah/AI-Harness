using Avalonia;
using Avalonia.Controls;

namespace Harness.App.Controls;

public sealed partial class ContextItem : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ContextItem, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> DetailProperty =
        AvaloniaProperty.Register<ContextItem, string>(nameof(Detail), string.Empty);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public ContextItem() => InitializeComponent();
}
