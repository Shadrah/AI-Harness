using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Harness.App.Views;

public sealed partial class DiffWindow : Window
{
    public DiffWindow() => InitializeComponent();
    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else BeginMoveDrag(e);
    }
    private void Minimize_OnClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_OnClick(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
