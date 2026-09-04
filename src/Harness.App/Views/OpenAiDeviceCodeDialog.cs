using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Harness.Providers.Codex;

namespace Harness.App.Views;

internal static class OpenAiDeviceCodeDialog
{
    public static async Task ShowAsync(Window owner, CodexDeviceCodeLoginStart login)
    {
        var copy = new Button { Content = "COPY CODE" };
        var open = new Button { Content = "OPEN SIGN-IN PAGE", Classes = { "primary" } };
        var close = new Button { Content = "CLOSE" };
        var dialog = new Window
        {
            Title = "Connect OpenAI",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "CONNECT OPENAI",
                        Foreground = Brush.Parse("#65C7D0"),
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = "Sign in on the OpenAI page, then enter this one-time code:",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = login.UserCode,
                        FontFamily = "Cascadia Mono, JetBrains Mono, Consolas",
                        FontSize = 26,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brush.Parse("#D8DEE8")
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { close, copy, open }
                    }
                }
            }
        };
        copy.Click += async (_, _) =>
        {
            if (owner.Clipboard is not null) await owner.Clipboard.SetTextAsync(login.UserCode);
        };
        open.Click += async (_, _) => await owner.Launcher.LaunchUriAsync(new Uri(login.VerificationUrl));
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }
}
