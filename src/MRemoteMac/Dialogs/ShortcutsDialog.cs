using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MRemoteMac.Converters;

namespace MRemoteMac.Dialogs;

/// <summary>Help ▸ Keyboard Shortcuts.</summary>
public static class ShortcutsDialog
{
    private static readonly (string Group, (string Keys, string Action)[] Items)[] Groups =
    [
        ("Connections", [("⏎ / double-click", "Connect"), ("⇧⌘C", "Connect (tree)"), ("⌘D", "Duplicate"), ("F2", "Rename"), ("⌫ / Del", "Delete"), ("⌘↑ / ⌘↓", "Move up / down"), ("⌘F", "Search")]),
        ("Sessions", [("⌘← / ⌘→", "Previous / next session"), ("⌘1 … ⌘9", "Jump to session"), ("⌘W", "Disconnect current session"), ("F11", "Fullscreen")]),
        ("Inside a session", [("⌘", "Acts as Ctrl (Win or Alt in Options)"), ("⌥", "Alt"), ("Tab menu ▸ Send special keys", "Ctrl+Alt+Del, Ctrl+Esc, Win, Alt+Tab")]),
        ("Files", [("⌘O", "Open connection file"), ("⌘S", "Save"), ("⇧⌘S", "Save as"), ("⌘,", "Options"), ("⌘Q", "Quit")]),
    ];

    public static async Task Show(Window owner)
    {
        var body = new StackPanel { Spacing = 10, Width = 460 };
        foreach (var (group, items) in Groups)
        {
            var title = new TextBlock { Text = group.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Margin = new Thickness(0, 6, 0, 2) };
            title.Classes.Add("secondary");
            body.Children.Add(title);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
            for (var i = 0; i < items.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var keys = new TextBlock { Text = items[i].Keys, FontFamily = new FontFamily("Menlo, Monaco, monospace"), FontSize = 12, Margin = new Thickness(0, 3) };
                var action = new TextBlock { Text = items[i].Action, Margin = new Thickness(8, 3, 0, 3) };
                action.Classes.Add("secondary");
                Grid.SetRow(keys, i); Grid.SetRow(action, i); Grid.SetColumn(action, 1);
                grid.Children.Add(keys); grid.Children.Add(action);
            }
            body.Children.Add(grid);
        }
        var ok = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 88 };
        ok.Classes.Add("dialog"); ok.Classes.Add("primary");
        var w = new Window
        {
            Title = "Keyboard Shortcuts", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = AppIcon.Window,
            Content = new StackPanel { Margin = new Thickness(20, 12, 20, 16), Children = { body, new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok } } } }
        };
        Services.TestMode.Prepare(w);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner).ConfigureAwait(true);
    }
}
