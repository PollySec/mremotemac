using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MRemoteMac.Config;
using MRemoteMac.Converters;
using MRemoteMac.Dialogs;
using MRemoteMac.Models;
using MRemoteMac.Services;
using MRemoteMac.ViewModels;

namespace MRemoteMac.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _autoSaveTimer;
    private List<ConnectionInfo> _searchMatches = new();
    private int _searchIndex = -1;
    private bool _closing;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _vm = new MainViewModel(_settings);
        DataContext = _vm;
        InitializeComponent();

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowX is int x && _settings.WindowY is int y) Position = new PixelPoint(x, y);
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        MainGrid.ColumnDefinitions[0].Width = new GridLength(_settings.TreePaneWidth);
        LeftGrid.RowDefinitions[2].Height = new GridLength(_settings.ConfigPaneHeight);
        MessagesList.Height = _settings.NotificationsHeight;
        ApplyTheme();
        QuickConnectBar.IsVisible = _settings.ShowQuickConnectToolbar;
        MenuViewQuickConnect.IsChecked = _settings.ShowQuickConnectToolbar;
        SetNotificationsVisible(_settings.ShowNotificationsPanel);
        QuickConnectBox.ItemsSource = _settings.QuickConnectHistory.ToList();

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); AutoSave(); };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedNode)) OnSelectedNodeChanged();
        };
        ConfigGrid.PropertyChanged += (node, prop) =>
        {
            _vm.MarkDirty();
            if (prop is nameof(ConnectionInfo.Icon) or nameof(ConnectionInfo.Name)) UpdateConfigHeader();
            ScheduleAutoSave();
        };
        Notifications.Instance.MessageAdded += m =>
        {
            NotificationsTitle.Text = $"Notifications ({Notifications.Instance.Messages.Count})";
            if (m.Class == MessageClass.Error && !MessagesList.IsVisible) NotificationsTitle.Text += "  —  " + m.Text;
        };

        Opened += OnOpened;
        Closing += OnClosing;
        UpdateEmptyHint();
    }

    // ------------------------------------------------------------------ startup / shutdown

    private async void OnOpened(object? sender, EventArgs e)
    {
        var path = _settings.ConnectionsFilePath ?? AppSettings.DefaultConnectionsPath;
        if (File.Exists(path)) await LoadFileWithPrompt(path);
        else
        {
            _vm.NewFile();
            _vm.FilePath = path;
            Notifications.Instance.Info($"No connection file found, a new one will be created at {path}");
        }
        _vm.SelectedNode = _vm.Root;
        Tree.Focus();
        if (Environment.GetEnvironmentVariable("MRM_SELECT") is { Length: > 0 } selName &&
            _vm.Root.Descendants().FirstOrDefault(n => n.Name == selName) is { } selNode)
            SelectNode(selNode);
        if (Environment.GetEnvironmentVariable("MRM_CONNECT") is { Length: > 0 } target)
        {
            QuickConnectBox.Text = target;
            await QuickConnect();
            if (int.TryParse(Environment.GetEnvironmentVariable("MRM_CONNECT_WAIT"), out var wait)) await Task.Delay(wait);
        }
        if (Environment.GetEnvironmentVariable("MRM_SCREENSHOT") is { Length: > 0 } shot)
        {
            await Task.Delay(1500);
            SaveScreenshot(shot);
            if (Environment.GetEnvironmentVariable("MRM_EXIT") == "1") { _closing = true; Close(); }
        }
    }

    /// <summary>Debug helper: renders the window content to a PNG (used for headless UI checks).</summary>
    public void SaveScreenshot(string path)
    {
        try
        {
            var content = (Control)Content!;
            var size = new PixelSize((int)content.Bounds.Width, (int)content.Bounds.Height);
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96, 96));
            rtb.Render(content);
            rtb.Save(path);
        }
        catch (Exception ex) { Notifications.Instance.Error($"Screenshot failed: {ex.Message}"); }
    }

    private async Task<bool> LoadFileWithPrompt(string path)
    {
        string? password = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                _vm.LoadFile(path, password);
                _settings.ConnectionsFilePath = path;
                _settings.Save();
                return true;
            }
            catch (ConnectionsFile.PasswordRequiredException)
            {
                password = await Dialogs.Dialogs.Password(this, attempt == 0
                    ? $"The connection file {Path.GetFileName(path)} is protected. Enter the master password:"
                    : "Wrong password. Try again:", "Master password");
                if (password is null) return false;
            }
            catch (Exception ex)
            {
                Notifications.Instance.Error($"Could not load {path}: {ex.Message}");
                await Dialogs.Dialogs.Message(this, "Load failed", $"Could not load {path}:\n{ex.Message}");
                return false;
            }
        }
        return false;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        var open = _vm.Tabs.Where(t => t.IsConnected).ToList();
        if (open.Count > 0 && _settings.ConfirmCloseConnection)
        {
            var r = await Dialogs.Dialogs.Message(this, "Close mRemoteMac", $"{open.Count} connection(s) are still open. Close anyway?", Dialogs.Dialogs.MsgButtons.YesNo);
            if (r != Dialogs.Dialogs.MsgResult.Yes) return;
        }
        if (_vm.IsDirty)
        {
            try { _vm.Save(); }
            catch (Exception ex)
            {
                var r = await Dialogs.Dialogs.Message(this, "Save failed", $"Could not save the connection file:\n{ex.Message}\n\nQuit anyway?", Dialogs.Dialogs.MsgButtons.YesNo);
                if (r != Dialogs.Dialogs.MsgResult.Yes) return;
            }
        }
        foreach (var t in _vm.Tabs.ToList()) await t.DisposeSessionAsync();
        SaveWindowSettings();
        _closing = true;
        Close();
    }

    private void SaveWindowSettings()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = (int)Width;
            _settings.WindowHeight = (int)Height;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }
        _settings.TreePaneWidth = MainGrid.ColumnDefinitions[0].Width.Value;
        _settings.ConfigPaneHeight = LeftGrid.RowDefinitions[2].Height.Value;
        _settings.ShowNotificationsPanel = MessagesList.IsVisible;
        _settings.ShowQuickConnectToolbar = QuickConnectBar.IsVisible;
        try { _settings.Save(); } catch { /* ignore */ }
    }

    private void ApplyTheme()
    {
        Application.Current!.RequestedThemeVariant = _settings.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    // ------------------------------------------------------------------ file menu

    private async void OnNewFile(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsDirty) { try { _vm.Save(); } catch (Exception ex) { Notifications.Instance.Error(ex.Message); } }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "New Connection File", SuggestedFileName = "confCons.xml", DefaultExtension = "xml",
            FileTypeChoices = new[] { new FilePickerFileType("mRemoteNG connection file") { Patterns = new[] { "*.xml" } } }
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        _vm.NewFile();
        _vm.Save(path);
        _settings.ConnectionsFilePath = path;
        _settings.Save();
    }

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Connection File", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("mRemoteNG connection file") { Patterns = new[] { "*.xml" } }, FilePickerFileTypes.All }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        if (_vm.IsDirty) { try { _vm.Save(); } catch (Exception ex) { Notifications.Instance.Error(ex.Message); } }
        await LoadFileWithPrompt(path);
    }

    private async void OnSaveFile(object? sender, RoutedEventArgs e)
    {
        try { _vm.Save(); Notifications.Instance.Info($"Saved {_vm.FilePath}"); }
        catch (Exception ex) { await Dialogs.Dialogs.Message(this, "Save failed", ex.Message); }
    }

    private async void OnSaveFileAs(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Connection File As", SuggestedFileName = Path.GetFileName(_vm.FilePath ?? "confCons.xml"), DefaultExtension = "xml",
            FileTypeChoices = new[] { new FilePickerFileType("mRemoteNG connection file") { Patterns = new[] { "*.xml" } } }
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            _vm.Save(path);
            _settings.ConnectionsFilePath = path;
            _settings.Save();
            Notifications.Instance.Info($"Saved {path}");
        }
        catch (Exception ex) { await Dialogs.Dialogs.Message(this, "Save failed", ex.Message); }
    }

    private async void OnSetMasterPassword(object? sender, RoutedEventArgs e)
    {
        var pw = await Dialogs.Dialogs.Password(this, "Set a master password for the connection file. Leave empty to remove the password (mRemoteNG default encryption).", "Master password", confirm: true);
        if (pw is null) return;
        _vm.Root.MasterPassword = string.IsNullOrEmpty(pw) ? null : pw;
        _vm.MarkDirty();
        try { _vm.Save(); Notifications.Instance.Info(_vm.Root.IsProtected ? "Master password set." : "Master password removed."); }
        catch (Exception ex) { await Dialogs.Dialogs.Message(this, "Save failed", ex.Message); }
    }

    private async void OnOptions(object? sender, RoutedEventArgs e)
    {
        if (await OptionsDialog.Show(this, _settings))
        {
            _settings.Save();
            ApplyTheme();
            foreach (var t in _vm.Tabs) t.View.CommandKey = _settings.CommandKey;
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenFileFolder(object? sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(_vm.FilePath ?? AppSettings.DefaultConnectionsPath);
        if (dir is not null && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("open", dir) { UseShellExecute = false });
    }

    private async void OnImportFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import connections from file", AllowMultiple = false });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        string? password = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var r = ConnectionsFile.Load(path, password);
                var folder = new ContainerInfo { Name = Path.GetFileNameWithoutExtension(path) };
                foreach (var child in r.Root.Children.ToList()) folder.AddChild(child);
                _vm.TargetContainerFor(_vm.SelectedNode).AddChild(folder);
                _vm.MarkDirty();
                ScheduleAutoSave();
                Notifications.Instance.Info($"Imported {folder.Descendants().Count(n => !n.IsContainer)} connections from {path}");
                return;
            }
            catch (ConnectionsFile.PasswordRequiredException)
            {
                password = await Dialogs.Dialogs.Password(this, "The imported file is protected. Enter its master password:", "Import");
                if (password is null) return;
            }
            catch (Exception ex)
            {
                await Dialogs.Dialogs.Message(this, "Import failed", ex.Message);
                return;
            }
        }
    }

    private void ScheduleAutoSave()
    {
        if (!_settings.AutoSaveOnChange) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSave()
    {
        if (!_vm.IsDirty || !_settings.AutoSaveOnChange) return;
        try { _vm.Save(); }
        catch (Exception ex) { Notifications.Instance.Error($"Auto-save failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ tree

    private ConnectionInfo? Selected => _vm.SelectedNode;

    private void OnSelectedNodeChanged()
    {
        ConfigGrid.Target = Selected;
        UpdateConfigHeader();
    }

    private void UpdateConfigHeader()
    {
        ConfigTitle.Text = Selected?.Name ?? "";
        ConfigIcon.Source = Selected is null ? null : NodeIconConverter.Instance.Convert(Selected, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture) as Avalonia.Media.IImage;
    }

    private void OnShowProperties(object? sender, RoutedEventArgs e)
    {
        PropertiesButton.IsChecked = true;
        InheritanceButton.IsChecked = false;
        ConfigGrid.InheritanceMode = false;
    }

    private void OnShowInheritance(object? sender, RoutedEventArgs e)
    {
        PropertiesButton.IsChecked = false;
        InheritanceButton.IsChecked = true;
        ConfigGrid.InheritanceMode = true;
    }

    private static ConnectionInfo? NodeFromEvent(RoutedEventArgs e)
    {
        var v = e.Source as Visual;
        while (v is not null && v is not TreeViewItem) v = v.GetVisualParent();
        return (v as TreeViewItem)?.DataContext as ConnectionInfo;
    }

    private async void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        var node = NodeFromEvent(e) ?? Selected;
        if (node is null) return;
        if (node.IsContainer) { node.IsExpanded = !node.IsExpanded; return; }
        await OpenConnection(node);
    }

    private async void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (Selected is null) return;
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Enter when !Selected.IsContainer: await OpenConnection(Selected); e.Handled = true; break;
            case Key.Enter: Selected.IsExpanded = !Selected.IsExpanded; e.Handled = true; break;
            case Key.Delete or Key.Back when !cmd: OnDelete(sender, e); e.Handled = true; break;
            case Key.F2: OnRename(sender, e); e.Handled = true; break;
            case Key.D when cmd: OnDuplicate(sender, e); e.Handled = true; break;
            case Key.Up when cmd: OnMoveUp(sender, e); e.Handled = true; break;
            case Key.Down when cmd: OnMoveDown(sender, e); e.Handled = true; break;
            case Key.C when cmd && e.KeyModifiers.HasFlag(KeyModifiers.Shift): OnConnect(sender, e); e.Handled = true; break;
            case Key.F when cmd: SearchBox.Focus(); e.Handled = true; break;
            default:
                if (!cmd && e.KeySymbol is { Length: 1 } s && char.IsLetterOrDigit(s[0]))
                {
                    SearchBox.Focus();
                    SearchBox.Text = s;
                    SearchBox.CaretIndex = 1;
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnTreeMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var n = Selected;
        var isRoot = n is RootNodeInfo;
        var isFolder = n is ContainerInfo && !isRoot;
        var isConn = n is not null && !n.IsContainer;
        MiConnect.IsEnabled = isConn || isFolder;
        MiConnectOptions.IsEnabled = isConn;
        MiDisconnect.IsEnabled = n is not null && n.OpenConnections > 0 || isFolder;
        MiDuplicate.IsEnabled = !isRoot && n is not null;
        MiRename.IsEnabled = n is not null;
        MiDelete.IsEnabled = !isRoot && n is not null;
        MiCopyHostname.IsEnabled = isConn;
        MiInheritance.IsEnabled = !isRoot && n is not null;
        MiMoveUp.IsEnabled = !isRoot && n?.Parent is not null && n.Parent.Children.IndexOf(n) > 0;
        MiMoveDown.IsEnabled = !isRoot && n?.Parent is not null && n.Parent.Children.IndexOf(n) < n.Parent.Children.Count - 1;
    }

    private void OnNewConnection(object? sender, RoutedEventArgs e)
    {
        var c = _vm.AddConnection(Selected);
        SelectNode(c);
        ScheduleAutoSave();
    }

    private void OnNewFolder(object? sender, RoutedEventArgs e)
    {
        var f = _vm.AddFolder(Selected);
        SelectNode(f);
        ScheduleAutoSave();
    }

    private void SelectNode(ConnectionInfo node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        Dispatcher.UIThread.Post(() => { _vm.SelectedNode = node; Tree.Focus(); }, DispatcherPriority.Loaded);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        var n = Selected;
        if (n is null || n is RootNodeInfo) return;
        var text = n is ContainerInfo c
            ? $"Delete folder '{n.Name}' and its {c.Descendants().Count()} items?"
            : $"Delete connection '{n.Name}'?";
        var r = await Dialogs.Dialogs.Message(this, "Delete", text, Dialogs.Dialogs.MsgButtons.YesNo);
        if (r != Dialogs.Dialogs.MsgResult.Yes) return;
        _vm.Delete(n);
        ScheduleAutoSave();
    }

    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        var n = Selected;
        if (n is null) return;
        var name = await Dialogs.Dialogs.Input(this, "Rename", "New name:", n.Name);
        if (string.IsNullOrWhiteSpace(name) || name == n.Name) return;
        n.Name = name;
        _vm.MarkDirty();
        UpdateConfigHeader();
        ConfigGrid.RefreshValues();
        ScheduleAutoSave();
    }

    private void OnDuplicate(object? sender, RoutedEventArgs e)
    {
        if (Selected is null || Selected is RootNodeInfo) return;
        var copy = _vm.Duplicate(Selected);
        SelectNode(copy);
        ScheduleAutoSave();
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) { if (Selected is not null) { _vm.Move(Selected, -1); SelectNode(Selected); ScheduleAutoSave(); } }
    private void OnMoveDown(object? sender, RoutedEventArgs e) { if (Selected is not null) { _vm.Move(Selected, +1); SelectNode(Selected); ScheduleAutoSave(); } }
    private void OnSortAsc(object? sender, RoutedEventArgs e) { _vm.Sort(true); UpdateSortIcon(); ScheduleAutoSave(); }
    private void OnSortDesc(object? sender, RoutedEventArgs e) { _vm.Sort(false); UpdateSortIcon(); ScheduleAutoSave(); }
    private void OnSortToggle(object? sender, RoutedEventArgs e) { _vm.Sort(!_vm.SortAscending); UpdateSortIcon(); ScheduleAutoSave(); }

    private void UpdateSortIcon()
    {
        SortImage.Source = ImageCache.Image(_vm.SortAscending ? "SortAscending_16x" : "SortDescending_16x");
        ToolTip.SetTip(SortButton, _vm.SortAscending ? "Sort A-Z" : "Sort Z-A");
    }

    private void OnExpandAll(object? sender, RoutedEventArgs e) => _vm.ExpandAll(true);
    private void OnCollapseAll(object? sender, RoutedEventArgs e) => _vm.ExpandAll(false);

    private async void OnCopyHostname(object? sender, RoutedEventArgs e)
    {
        if (Selected is null || Selected.IsContainer || Clipboard is null) return;
        await Clipboard.SetTextAsync(Selected.GetEffective<string>(nameof(ConnectionInfo.Hostname)));
    }

    private void OnApplyInheritanceToChildren(object? sender, RoutedEventArgs e)
    {
        if (Selected is not ContainerInfo c) return;
        foreach (var d in c.Descendants())
        {
            d.Inheritance.Clear();
            foreach (var p in c.Inheritance) d.Inheritance.Add(p);
        }
        _vm.MarkDirty();
        ScheduleAutoSave();
    }

    private void OnApplyDefaultInheritance(object? sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        Selected.Inheritance.Clear();
        _vm.MarkDirty();
        ConfigGrid.RefreshValues();
        ScheduleAutoSave();
    }

    private void OnFavorites(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var f in _vm.Favorites())
        {
            var item = new MenuItem { Header = f.Name, Icon = new Image { Source = ImageCache.Image(f.OpenConnections > 0 ? "Stop_16x" : "Run_16x"), Width = 16, Height = 16 } };
            var target = f;
            item.Click += async (_, _) => await OpenConnection(target);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no favorites)", IsEnabled = false });
        menu.ShowAt(FavoritesButton);
    }

    private void OnConnectionsMenu(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var child in _vm.Root.Children) menu.Items.Add(BuildNodeMenu(child));
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no connections)", IsEnabled = false });
        menu.ShowAt(ConnectionsMenuButton);
    }

    private MenuItem BuildNodeMenu(ConnectionInfo n)
    {
        var item = new MenuItem
        {
            Header = n.Name,
            Icon = new Image { Source = NodeIconConverter.Instance.Convert(n, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture) as Avalonia.Media.IImage, Width = 16, Height = 16 }
        };
        if (n is ContainerInfo c)
        {
            foreach (var child in c.Children) item.Items.Add(BuildNodeMenu(child));
            if (item.Items.Count == 0) item.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
        }
        else item.Click += async (_, _) => await OpenConnection(n);
        return item;
    }

    // ------------------------------------------------------------------ search

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchMatches = _vm.Search(SearchBox.Text ?? "").ToList();
        _searchIndex = _searchMatches.Count > 0 ? 0 : -1;
        if (_searchIndex >= 0) SelectNode(_searchMatches[0]);
    }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _searchMatches.Count > 0:
                _searchIndex = (_searchIndex + 1) % _searchMatches.Count; SelectNode(_searchMatches[_searchIndex]); SearchBox.Focus(); e.Handled = true; break;
            case Key.Up when _searchMatches.Count > 0:
                _searchIndex = (_searchIndex - 1 + _searchMatches.Count) % _searchMatches.Count; SelectNode(_searchMatches[_searchIndex]); SearchBox.Focus(); e.Handled = true; break;
            case Key.Escape: SearchBox.Text = ""; Tree.Focus(); e.Handled = true; break;
            case Key.Enter when Selected is not null && !Selected.IsContainer: await OpenConnection(Selected); e.Handled = true; break;
        }
    }

    // ------------------------------------------------------------------ connections / tabs

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        if (Selected is ContainerInfo c && Selected is not RootNodeInfo)
        {
            foreach (var n in c.Children.Where(n => !n.IsContainer)) await OpenConnection(n);
            return;
        }
        if (!Selected.IsContainer) await OpenConnection(Selected);
    }

    private async void OnConnectConsole(object? sender, RoutedEventArgs e) { if (Selected is { IsContainer: false }) await OpenConnection(Selected, console: true); }
    private async void OnConnectFullscreen(object? sender, RoutedEventArgs e) { if (Selected is { IsContainer: false }) { await OpenConnection(Selected, fullscreen: true); EnterFullscreen(true); } }
    private async void OnConnectNoCredentials(object? sender, RoutedEventArgs e) { if (Selected is { IsContainer: false }) await OpenConnection(Selected, noCredentials: true); }
    private async void OnConnectViewOnly(object? sender, RoutedEventArgs e) { if (Selected is { IsContainer: false }) await OpenConnection(Selected, viewOnly: true); }

    private async void OnDisconnectNode(object? sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        var nodes = Selected is ContainerInfo c ? c.Descendants().ToHashSet() : new HashSet<ConnectionInfo> { Selected };
        foreach (var t in _vm.Tabs.Where(t => nodes.Contains(t.Info)).ToList()) await CloseTab(t);
    }

    private async Task OpenConnection(ConnectionInfo info, bool console = false, bool fullscreen = false, bool noCredentials = false, bool viewOnly = false)
    {
        if (info.Protocol != ProtocolType.RDP)
        {
            Notifications.Instance.Warn($"{info.Name}: protocol {info.Protocol} is not supported by mRemoteMac (RDP only).");
            return;
        }
        if (string.IsNullOrWhiteSpace(info.GetEffective<string>(nameof(ConnectionInfo.Hostname))))
        {
            Notifications.Instance.Warn($"{info.Name}: no hostname set.");
            return;
        }
        var tab = new ConnectionTab(info, _settings, this)
        {
            ConnectToConsole = console, Fullscreen = fullscreen, WithoutCredentials = noCredentials, ViewOnly = viewOnly
        };
        tab.CloseRequested += async t => await CloseTab(t);
        tab.StateChanged += t => UpdateTabHeader(t);
        _vm.Tabs.Add(tab);
        var item = new TabItem { Content = tab.Content, Tag = tab, Header = BuildTabHeader(tab) };
        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
        UpdateEmptyHint();
        await tab.ConnectAsync();
    }

    private Control BuildTabHeader(ConnectionTab tab)
    {
        var icon = new Image { Source = ImageCache.ConnectionIcon(tab.Info.Icon), Width = 16, Height = 16, Margin = new Thickness(0, 0, 5, 0) };
        var text = new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = "✕", FontSize = 10, Padding = new Thickness(4, 0), Margin = new Thickness(8, 0, 0, 0), Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 0, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        close.Click += async (_, _) => await CloseTab(tab);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, text, close }, Background = Avalonia.Media.Brushes.Transparent };
        panel.ContextMenu = BuildTabContextMenu(tab);
        panel.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(panel).Properties.IsMiddleButtonPressed) _ = CloseTab(tab);
        };
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ConnectionTab.Title)) text.Text = tab.Title; };
        return panel;
    }

    private ContextMenu BuildTabContextMenu(ConnectionTab tab)
    {
        var menu = new ContextMenu();
        MenuItem Mk(string header, Action action, string? icon = null)
        {
            var mi = new MenuItem { Header = header };
            if (icon is not null) mi.Icon = new Image { Source = ImageCache.Image(icon), Width = 16, Height = 16 };
            mi.Click += (_, _) => action();
            return mi;
        }
        menu.Items.Add(Mk("Reconnect", async () => await tab.ConnectAsync(), "Refresh_16x"));
        menu.Items.Add(Mk("Disconnect", async () => await CloseTab(tab), "Stop_16x"));
        menu.Items.Add(Mk("Disconnect others", async () => { foreach (var t in _vm.Tabs.Where(t => t != tab).ToList()) await CloseTab(t); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mk("Rename tab", async () =>
        {
            var name = await Dialogs.Dialogs.Input(this, "Rename tab", "Tab title:", tab.Title);
            if (!string.IsNullOrWhiteSpace(name)) tab.Title = name;
        }, "Rename_16x"));
        menu.Items.Add(Mk("Duplicate tab", async () => await OpenConnection(tab.Info), "Copy_16x"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mk("Fullscreen", () => EnterFullscreen(!_fullscreen), "FullScreen_16x"));
        var smart = new MenuItem { Header = "SmartSize", ToggleType = MenuItemToggleType.CheckBox, IsChecked = tab.SmartSize };
        smart.Click += (_, _) => { tab.SmartSize = !tab.SmartSize; smart.IsChecked = tab.SmartSize; };
        menu.Items.Add(smart);
        var viewOnly = new MenuItem { Header = "View only", ToggleType = MenuItemToggleType.CheckBox, IsChecked = tab.ViewOnly };
        viewOnly.Click += (_, _) => { tab.ViewOnly = !tab.ViewOnly; viewOnly.IsChecked = tab.ViewOnly; };
        menu.Items.Add(viewOnly);
        menu.Items.Add(new Separator());
        var special = new MenuItem { Header = "Send special keys" };
        special.Items.Add(Mk("Ctrl+Alt+Del", tab.SendCtrlAltDel));
        special.Items.Add(Mk("Ctrl+Esc", tab.SendCtrlEsc));
        special.Items.Add(Mk("Windows key", tab.SendWin));
        special.Items.Add(Mk("Alt+Tab", tab.SendAltTab));
        menu.Items.Add(special);
        return menu;
    }

    private void UpdateTabHeader(ConnectionTab tab)
    {
        var item = Tabs.Items.OfType<TabItem>().FirstOrDefault(i => i.Tag == tab);
        if (item?.Header is StackPanel { Children: [Image icon, TextBlock text, ..] })
        {
            text.Text = tab.Title;
            text.Opacity = tab.State == TabState.Connected ? 1.0 : 0.6;
            icon.Source = ImageCache.ConnectionIcon(tab.Info.Icon);
        }
    }

    private async Task CloseTab(ConnectionTab tab)
    {
        var item = Tabs.Items.OfType<TabItem>().FirstOrDefault(i => i.Tag == tab);
        if (item is null) return;
        if (tab.IsConnected && _settings.ConfirmCloseConnection)
        {
            var r = await Dialogs.Dialogs.Message(this, "Disconnect", $"Disconnect from {tab.Info.Name}?", Dialogs.Dialogs.MsgButtons.YesNo);
            if (r != Dialogs.Dialogs.MsgResult.Yes) return;
        }
        await tab.DisposeSessionAsync();
        Tabs.Items.Remove(item);
        _vm.Tabs.Remove(tab);
        tab.View.DisposeBitmap();
        UpdateEmptyHint();
        if (Tabs.Items.Count == 0 && _fullscreen) EnterFullscreen(false);
    }

    private ConnectionTab? CurrentTab => (Tabs?.SelectedItem as TabItem)?.Tag as ConnectionTab;

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Tabs is null || _vm is null) return;
        var tab = CurrentTab;
        _vm.SelectedTab = tab;
        if (tab is not null)
        {
            Title = _vm.Title + " - " + tab.Title;
            Dispatcher.UIThread.Post(() => tab.View.Focus(), DispatcherPriority.Loaded);
        }
    }

    private void UpdateEmptyHint() => EmptyHint.IsVisible = Tabs.Items.Count == 0;

    private void OnNextSession(object? sender, RoutedEventArgs e) { if (Tabs.Items.Count > 0) Tabs.SelectedIndex = (Tabs.SelectedIndex + 1) % Tabs.Items.Count; }
    private void OnPreviousSession(object? sender, RoutedEventArgs e) { if (Tabs.Items.Count > 0) Tabs.SelectedIndex = (Tabs.SelectedIndex - 1 + Tabs.Items.Count) % Tabs.Items.Count; }
    private async void OnTabReconnect(object? sender, RoutedEventArgs e) { if (CurrentTab is { } t) await t.ConnectAsync(); }
    private async void OnTabDisconnect(object? sender, RoutedEventArgs e) { if (CurrentTab is { } t) await CloseTab(t); }
    private async void OnDisconnectAll(object? sender, RoutedEventArgs e) { foreach (var t in _vm.Tabs.ToList()) await CloseTab(t); }
    private async void OnReconnectAll(object? sender, RoutedEventArgs e) { foreach (var t in _vm.Tabs.ToList()) await t.ConnectAsync(); }
    private void OnSendCtrlAltDel(object? sender, RoutedEventArgs e) => CurrentTab?.SendCtrlAltDel();
    private void OnSendCtrlEsc(object? sender, RoutedEventArgs e) => CurrentTab?.SendCtrlEsc();
    private void OnSendWin(object? sender, RoutedEventArgs e) => CurrentTab?.SendWin();
    private void OnSendAltTab(object? sender, RoutedEventArgs e) => CurrentTab?.SendAltTab();

    // ------------------------------------------------------------------ quick connect

    private async void OnQuickConnect(object? sender, RoutedEventArgs e) => await QuickConnect();

    private async void OnQuickConnectKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await QuickConnect(); }
    }

    private async Task QuickConnect()
    {
        var text = QuickConnectBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var info = _vm.QuickConnect(text);
        QuickConnectBox.ItemsSource = _settings.QuickConnectHistory.ToList();
        _settings.Save();
        await OpenConnection(info);
    }

    // ------------------------------------------------------------------ view

    private void OnToggleNotifications(object? sender, RoutedEventArgs e) => SetNotificationsVisible(!MessagesList.IsVisible);

    private void SetNotificationsVisible(bool visible)
    {
        MessagesList.IsVisible = visible;
        MenuViewNotifications.IsChecked = visible;
        if (visible) NotificationsTitle.Text = $"Notifications ({Notifications.Instance.Messages.Count})";
    }

    private void OnToggleQuickConnect(object? sender, RoutedEventArgs e)
    {
        QuickConnectBar.IsVisible = !QuickConnectBar.IsVisible;
        MenuViewQuickConnect.IsChecked = QuickConnectBar.IsVisible;
    }

    private void OnResetLayout(object? sender, RoutedEventArgs e)
    {
        MainGrid.ColumnDefinitions[0].Width = new GridLength(280);
        LeftGrid.RowDefinitions[2].Height = new GridLength(330);
        MessagesList.Height = 140;
        if (_fullscreen) EnterFullscreen(false);
    }

    private void OnToggleFullscreen(object? sender, RoutedEventArgs e) => EnterFullscreen(!_fullscreen);

    private void EnterFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        if (on)
        {
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else WindowState = _stateBeforeFullscreen == WindowState.FullScreen ? WindowState.Normal : _stateBeforeFullscreen;
        MainMenu.IsVisible = !on;
        QuickConnectBar.IsVisible = !on && _settings.ShowQuickConnectToolbar;
        LeftGrid.IsVisible = !on;
        NotificationsPanel.IsVisible = !on;
        MainGrid.ColumnDefinitions[0].Width = on ? new GridLength(0) : new GridLength(_settings.TreePaneWidth);
        MainGrid.ColumnDefinitions[1].Width = on ? new GridLength(0) : new GridLength(4);
        if (!on) _settings.TreePaneWidth = Math.Max(120, _settings.TreePaneWidth);
        CurrentTab?.View.Focus();
    }

    // ------------------------------------------------------------------ notifications

    private async void OnCopyMessage(object? sender, RoutedEventArgs e)
    {
        if (MessagesList.SelectedItem is NotificationMessage m && Clipboard is not null) await Clipboard.SetTextAsync(m.Display);
    }

    private void OnDeleteMessage(object? sender, RoutedEventArgs e)
    {
        if (MessagesList.SelectedItem is NotificationMessage m) Notifications.Instance.Messages.Remove(m);
    }

    private void OnClearMessages(object? sender, RoutedEventArgs e)
    {
        Notifications.Instance.Messages.Clear();
        NotificationsTitle.Text = "Notifications";
    }

    // ------------------------------------------------------------------ help

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
    private void OnHelp(object? sender, RoutedEventArgs e) => OpenUrl("https://mremoteng.readthedocs.io/");
    private void OnWebsite(object? sender, RoutedEventArgs e) => OpenUrl("https://mremoteng.org/");
    private void OnReportIssue(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/mRemoteNG/mRemoteNG/issues");
    private async void OnAbout(object? sender, RoutedEventArgs e) => await Dialogs.Dialogs.About(this);
}
