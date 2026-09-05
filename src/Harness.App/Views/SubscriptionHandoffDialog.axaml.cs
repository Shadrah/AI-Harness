using Avalonia.Controls;
using Avalonia.Input;
using Harness.App.Services;

namespace Harness.App.Views;

public sealed partial class SubscriptionHandoffDialog : Window
{
    public SubscriptionHandoffDialog()
    {
        InitializeComponent();
    }

    public SubscriptionHandoffDialog(
        SubscriptionIdentity origin,
        IReadOnlyList<SubscriptionIdentity> destinations)
        : this()
    {
        OriginText.Text = $"{origin.DisplayName} is low on usage. Choose the separately authenticated account that should take over.";
        DestinationList.ItemsSource = destinations;
        DestinationList.SelectedItem = destinations.FirstOrDefault();
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void Continue_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DestinationList.SelectedItem is SubscriptionIdentity identity) Close(identity.Id);
    }

    private void Cancel_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);
}
