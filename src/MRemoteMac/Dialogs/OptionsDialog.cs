using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MRemoteMac.Rdp;
using MRemoteMac.Services;

namespace MRemoteMac.Dialogs;

public static class OptionsDialog
{
    public static async Task<bool> Show(Window owner, AppSettings s)
    {
        var cmd = new ComboBox { ItemsSource = Enum.GetValues<CommandKeyMode>(), SelectedItem = s.CommandKey, Width = 200 };
        var single = new CheckBox { Content = "Single click on a connection opens it", IsChecked = s.SingleClickOpensConnection };
        var confirm = new CheckBox { Content = "Confirm before closing an open connection", IsChecked = s.ConfirmCloseConnection };
        var autosave = new CheckBox { Content = "Save connection file automatically after changes", IsChecked = s.AutoSaveOnChange };
        var closeTab = new CheckBox { Content = "Close tab when the remote session ends cleanly", IsChecked = s.CloseTabOnDisconnect };
        var gfx = new CheckBox { Content = "Use RDP graphics pipeline (RemoteFX / progressive codec)", IsChecked = s.UseGfx };
        var dark = new CheckBox { Content = "Dark theme (vs2015dark)", IsChecked = s.DarkTheme };

        var body = new StackPanel { Spacing = 8, Width = 460 };
        body.Children.Add(new TextBlock { Text = "Keyboard", FontWeight = Avalonia.Media.FontWeight.Bold });
        body.Children.Add(new DockPanel { Children = { new TextBlock { Text = "⌘ Command key acts as:", Width = 180, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left }, cmd } });
        body.Children.Add(new TextBlock { Text = "Behaviour", FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) });
        body.Children.Add(single);
        body.Children.Add(confirm);
        body.Children.Add(autosave);
        body.Children.Add(closeTab);
        body.Children.Add(new TextBlock { Text = "RDP", FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) });
        body.Children.Add(gfx);
        body.Children.Add(new TextBlock { Text = "Theme", FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) });
        body.Children.Add(dark);
        body.Children.Add(new TextBlock { Text = $"Settings and the default connection file live in:\n{AppSettings.Directory}", Foreground = Avalonia.Media.Brushes.Gray, FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } };
        var w = new Window
        {
            Title = "Options", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(16), Children = { body, buttons } }
        };
        var saved = false;
        ok.Click += (_, _) =>
        {
            s.CommandKey = (CommandKeyMode)(cmd.SelectedItem ?? CommandKeyMode.Control);
            s.SingleClickOpensConnection = single.IsChecked == true;
            s.ConfirmCloseConnection = confirm.IsChecked == true;
            s.AutoSaveOnChange = autosave.IsChecked == true;
            s.CloseTabOnDisconnect = closeTab.IsChecked == true;
            s.UseGfx = gfx.IsChecked == true;
            s.DarkTheme = dark.IsChecked == true;
            saved = true;
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return saved;
    }
}
