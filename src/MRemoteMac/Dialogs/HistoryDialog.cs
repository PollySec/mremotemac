using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MRemoteMac.Config;
using MRemoteMac.Services;

namespace MRemoteMac.Dialogs;

/// <summary>Revision history of the connection database with restore.</summary>
public static class HistoryDialog
{
    /// <summary>Shows the history; returns the revision id the user chose to restore, or null.</summary>
    public static async Task<long?> Show(Window owner, IConnectionStore store)
    {
        var revisions = store.History();
        long? chosen = null;

        var list = new ListBox { Height = 320, Width = 620, ItemsSource = revisions };
        list.Classes.Add("history");
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RevisionInfo>((r, _) =>
        {
            var when = new TextBlock { Text = r.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture), Width = 150, VerticalAlignment = VerticalAlignment.Center };
            var counts = new TextBlock { Text = $"{r.Connections} connections · {r.Folders} folders", Width = 170, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            counts.Classes.Add("caption");
            var summary = new TextBlock { Text = r.Summary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(summary, r.Summary);
            return new DockPanel { Children = { Dock(when, Avalonia.Controls.Dock.Left), Dock(counts, Avalonia.Controls.Dock.Right), summary }, Margin = new Thickness(2) };
        });
        if (revisions.Count > 0) list.SelectedIndex = 0;

        var hint = new TextBlock
        {
            Text = revisions.Count == 0 ? "No revisions yet." : "Every save keeps a snapshot. Restoring replaces the current tree with the selected revision (the restore itself is recorded, so nothing is lost).",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 620, Margin = new Thickness(0, 8, 0, 0),
        };
        hint.Classes.Add("caption");

        var restore = new Button { Content = "Restore", IsDefault = true, Classes = { "dialog", "primary" }, IsEnabled = revisions.Count > 0 };
        var close = new Button { Content = "Close", IsCancel = true, Classes = { "dialog" } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0), Children = { close, restore } };
        var body = new StackPanel { Children = { list, hint } };
        var w = new Window
        {
            Title = $"History – {store.DisplayName}", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = Converters.AppIcon.Window,
            Content = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Children = { body, buttons } },
        };
        TestMode.Prepare(w);
        restore.Click += (_, _) => { if (list.SelectedItem is RevisionInfo r) { chosen = r.Id; w.Close(); } };
        close.Click += (_, _) => w.Close();
        list.SelectionChanged += (_, _) => restore.IsEnabled = list.SelectedItem is not null;
        await w.ShowDialog(owner).ConfigureAwait(true);
        return chosen;
    }

    private static Control Dock(Control c, Avalonia.Controls.Dock dock)
    {
        DockPanel.SetDock(c, dock);
        return c;
    }
}
