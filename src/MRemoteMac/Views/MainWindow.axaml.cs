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

public sealed partial class MainWindow : Window
{
    private static readonly FilePickerFileType ConnectionFileType = new("mRemoteNG connection file") { Patterns = ["*.xml"] };

    private readonly AppSettings _settings;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _autoSaveTimer;
    private List<ConnectionInfo> _searchMatches = [];
    private int _searchIndex = -1;
    private bool _closing;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen;

    public MainViewModel ViewModel => _vm;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _vm = new MainViewModel(_settings);
        DataContext = _vm;
        InitializeComponent();
        Icon = AppIcon.Window;

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowX is { } x && _settings.WindowY is { } y) Position = new PixelPoint(x, y);
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        TestMode.Prepare(this);
        MainGrid.ColumnDefinitions[0].Width = new GridLength(_settings.TreePaneWidth);
        LeftGrid.RowDefinitions[2].Height = new GridLength(_settings.ConfigPaneHeight);
        NotificationsBody.Height = _settings.NotificationsHeight;
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
        ConfigGrid.ValueChanged += (_, prop) =>
        {
            _vm.MarkDirty();
            if (prop is nameof(ConnectionInfo.Icon) or nameof(ConnectionInfo.Name)) UpdateConfigHeader();
            ScheduleAutoSave();
        };
        Notifications.Instance.MessageAdded += m =>
        {
            UpdateNotificationsTitle();
            if (m.Class == MessageClass.Error && !NotificationsBody.IsVisible) NotificationsTitle.Text += "   ·   " + m.Text;
        };

        // Sessions ▸ Jump to session 1..9 (Cmd+1..9), like mRemoteNG
        for (var i = 1; i <= 9; i++)
        {
            var index = i - 1;
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.D0 + i, KeyModifiers.Meta),
                Command = new RelaySimpleCommand(() => { if (index < Tabs.Items.Count) Tabs.SelectedIndex = index; })
            });
        }

        // Cmd+Up/Down must win over the TreeView's own Up/Down navigation, so intercept them while tunnelling
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDownTunnel, RoutingStrategies.Tunnel);

        Opened += (_, _) => AsyncHelper.Fire(OnOpenedAsync, "Startup");
        Closing += OnClosing;
        UpdateEmptyHint();
    }

    // ------------------------------------------------------------------ startup / shutdown

    private async Task OnOpenedAsync()
    {
        var path = _settings.ConnectionsFilePath ?? AppSettings.DefaultConnectionsPath;
        if (File.Exists(path)) await LoadFileWithPromptAsync(path);
        else
        {
            // first start: create a default (empty, mRemoteNG-compatible) connection file right away
            _vm.NewFile();
            try
            {
                _vm.Save(path);
                _settings.ConnectionsFilePath = path;
                _settings.Save();
                Notifications.Instance.Info($"Created a new connection file at {path}. Use File ▸ Import mRemoteNG File... to bring in an existing confCons.xml.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _vm.FilePath = path;
                Notifications.Instance.Error($"Could not create {path}: {ex.Message}");
            }
        }
        _vm.SelectedNode = _vm.Root;
        FocusTree();
        await RunDevHooksAsync();
    }

    /// <summary>Environment-driven hooks used by the automated audit and icon generation (debug builds only).</summary>
    private async Task RunDevHooksAsync()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("MRM_RENDER_ICON") is { Length: > 0 } iconPath)
        {
            IconRenderer.Render(iconPath);
            Notifications.Instance.Info($"Icon rendered to {iconPath}");
            if (Environment.GetEnvironmentVariable("MRM_EXIT") == "1") { ForceClose(); return; }
        }
        if (Environment.GetEnvironmentVariable("MRM_SCRIPT") is { Length: > 0 } script)
        {
            await Task.Delay(800);
            await UiTestRunner.Run(this, script, Environment.GetEnvironmentVariable("MRM_OUT") ?? Path.Combine(Path.GetDirectoryName(script)!, "out"));
            return;
        }
        if (Environment.GetEnvironmentVariable("MRM_SELECT") is { Length: > 0 } selName &&
            _vm.Root.Descendants().FirstOrDefault(n => n.Name == selName) is { } selNode)
            SelectNode(selNode);
        if (Environment.GetEnvironmentVariable("MRM_CONNECT_NODE") is { Length: > 0 } nodeName &&
            _vm.Root.Descendants().FirstOrDefault(n => n.Name == nodeName) is { } connNode)
        {
            await OpenConnectionAsync(connNode);
            if (int.TryParse(Environment.GetEnvironmentVariable("MRM_CONNECT_WAIT"), out var wait)) await Task.Delay(wait);
        }
        if (Environment.GetEnvironmentVariable("MRM_SCREENSHOT") is { Length: > 0 } shot)
        {
            await Task.Delay(1500);
            SaveScreenshot(shot);
            if (Environment.GetEnvironmentVariable("MRM_EXIT") == "1") ForceClose();
        }
#else
        await Task.CompletedTask;
#endif
    }

    public void SaveScreenshot(string path)
    {
        var content = (Control)Content!;
        var size = new PixelSize((int)content.Bounds.Width, (int)content.Bounds.Height);
        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96, 96));
        rtb.Render(content);
        rtb.Save(path);
    }

    public void ForceClose()
    {
        _closing = true;
        Close();
    }

    private async Task<bool> LoadFileWithPromptAsync(string path)
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException or EncryptionException)
            {
                Notifications.Instance.Error($"Could not load {path}: {ex.Message}");
                await Dialogs.Dialogs.Message(this, "Load failed", $"Could not load {Path.GetFileName(path)}", detail: ex.Message);
                return false;
            }
        }
        return false;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        AsyncHelper.Fire(CloseGracefullyAsync, "Close");
    }

    private async Task CloseGracefullyAsync()
    {
        var open = _vm.Tabs.Where(t => t.IsConnected).ToList();
        if (open.Count > 0 && _settings.ConfirmCloseConnection)
        {
            var r = await Dialogs.Dialogs.Message(this, "Quit mRemoteMac", $"{open.Count} connection(s) are still open. Quit anyway?", Dialogs.Dialogs.MsgButtons.YesNo);
            if (r != Dialogs.Dialogs.MsgResult.Yes) return;
        }
        if (_vm.IsDirty)
        {
            try { _vm.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var r = await Dialogs.Dialogs.Message(this, "Save failed", "Could not save the connection file. Quit anyway?", Dialogs.Dialogs.MsgButtons.YesNo, ex.Message);
                if (r != Dialogs.Dialogs.MsgResult.Yes) return;
            }
        }
        foreach (var t in _vm.Tabs.ToList())
        {
            try { await t.DisposeAsync(); }
            catch (Exception ex) { CrashLog.Write("DisposeSession", ex); }
        }
        SaveWindowSettings();
        ForceClose();
    }

    private void SaveWindowSettings()
    {
        if (TestMode.IsActive) return;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = (int)Width;
            _settings.WindowHeight = (int)Height;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }
        if (!_fullscreen)
        {
            _settings.TreePaneWidth = MainGrid.ColumnDefinitions[0].Width.Value;
            _settings.ConfigPaneHeight = LeftGrid.RowDefinitions[2].Height.Value;
        }
        _settings.ShowNotificationsPanel = NotificationsBody.IsVisible;
        _settings.NotificationsHeight = NotificationsBody.Height;
        _settings.ShowQuickConnectToolbar = _fullscreen ? _settings.ShowQuickConnectToolbar : QuickConnectBar.IsVisible;
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CrashLog.Write("SaveSettings", ex); }
    }

    private void ApplyTheme()
        => Application.Current!.RequestedThemeVariant = _settings.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

    // ------------------------------------------------------------------ file menu

    private void OnNewFile(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(NewFileAsync, "New file");

    private async Task NewFileAsync()
    {
        SaveIfDirty();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "New Connection File", SuggestedFileName = "confCons.xml", DefaultExtension = "xml", FileTypeChoices = [ConnectionFileType]
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        _vm.NewFile();
        _vm.Save(path);
        _settings.ConnectionsFilePath = path;
        _settings.Save();
        FocusTree();
    }

    private void OnOpenFile(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(OpenFileAsync, "Open file");

    private async Task OpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Connection File", AllowMultiple = false, FileTypeFilter = [ConnectionFileType, FilePickerFileTypes.All]
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null) return;
        SaveIfDirty();
        await LoadFileWithPromptAsync(path);
        FocusTree();
    }

    private void SaveIfDirty()
    {
        if (!_vm.IsDirty) return;
        try { _vm.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notifications.Instance.Error($"Save failed: {ex.Message}"); }
    }

    private void OnSaveFile(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(SaveFileAsync, "Save");

    private async Task SaveFileAsync()
    {
        try
        {
            _vm.Save();
            Notifications.Instance.Info($"Saved {_vm.FilePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Dialogs.Message(this, "Save failed", "The connection file could not be saved.", detail: ex.Message);
        }
    }

    private void OnSaveFileAs(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(SaveFileAsAsync, "Save as");

    private async Task SaveFileAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Connection File As", SuggestedFileName = Path.GetFileName(_vm.FilePath ?? "confCons.xml"), DefaultExtension = "xml", FileTypeChoices = [ConnectionFileType]
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Dialogs.Message(this, "Save failed", "The connection file could not be saved.", detail: ex.Message);
        }
    }

    private void OnSetMasterPassword(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(SetMasterPasswordAsync, "Master password");

    private async Task SetMasterPasswordAsync()
    {
        var pw = await Dialogs.Dialogs.Password(this, "Set a master password for the connection file. Leave empty to remove the password (mRemoteNG default encryption).", "Master password", confirm: true);
        if (pw is null) return;
        _vm.Root.MasterPassword = string.IsNullOrEmpty(pw) ? null : pw;
        _vm.MarkDirty();
        await SaveFileAsync();
        Notifications.Instance.Info(_vm.Root.IsProtected ? "Master password set." : "Master password removed.");
    }

    private void OnOptions(object? sender, RoutedEventArgs e) => ShowOptions();

    public void ShowOptions() => AsyncHelper.Fire(ShowOptionsAsync, "Options");

    private async Task ShowOptionsAsync()
    {
        if (!await OptionsDialog.Show(this, _settings)) return;
        _settings.Save();
        ApplyTheme();
        foreach (var t in _vm.Tabs) t.View.CommandKey = _settings.CommandKey;
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenFileFolder(object? sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(_vm.FilePath ?? AppSettings.DefaultConnectionsPath);
        if (dir is not null && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("open", dir) { UseShellExecute = false });
    }

    private void OnImportFile(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(ImportFileAsync, "Import");

    private async Task ImportFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import connections from file", AllowMultiple = false, FileTypeFilter = [ConnectionFileType, FilePickerFileTypes.All] });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
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
                SelectNode(folder);
                Notifications.Instance.Info($"Imported {folder.Descendants().Count(n => !n.IsContainer)} connections from {path}");
                return;
            }
            catch (ConnectionsFile.PasswordRequiredException)
            {
                password = await Dialogs.Dialogs.Password(this, "The imported file is protected. Enter its master password:", "Import");
                if (password is null) return;
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or InvalidDataException or EncryptionException)
            {
                await Dialogs.Dialogs.Message(this, "Import failed", "The file could not be imported.", detail: ex.Message);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notifications.Instance.Error($"Auto-save failed: {ex.Message}"); }
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
        ConfigIcon.Glyph = Selected is null ? IconNames.Default : IconNames.ForNode(Selected);
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

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        var node = NodeFromEvent(e) ?? Selected;
        if (node is null) return;
        if (node.IsContainer) { node.IsExpanded = !node.IsExpanded; return; }
        AsyncHelper.Fire(() => OpenConnectionAsync(node), $"Connect {node.Name}");
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (Selected is null) return;
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Enter when !Selected.IsContainer: AsyncHelper.Fire(() => OpenConnectionAsync(Selected), "Connect"); e.Handled = true; break;
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

    private void OnTreeKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Meta) || Selected is null) return;
        switch (e.Key)
        {
            case Key.Up: OnMoveUp(sender, e); e.Handled = true; break;
            case Key.Down: OnMoveDown(sender, e); e.Handled = true; break;
        }
    }

    /// <summary>Tree shortcuts also work when focus sits on a toolbar button or nothing in particular (like WinForms).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var focused = FocusManager?.GetFocusedElement() as Control;
        if (focused is TextBox or AutoCompleteBox or Rdp.RdpView or ComboBox) return;
        if (focused is not null && focused.FindAncestorOfType<TabControl>() == Tabs) return;
        if (e.Key is Key.F2 or Key.Delete or Key.Back || (e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key is Key.D or Key.Up or Key.Down or Key.F))
            OnTreeKeyDown(Tree, e);
    }

    private void OnTreeMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var n = Selected;
        var isRoot = n is RootNodeInfo;
        var isFolder = n is ContainerInfo && !isRoot;
        var isConn = n is not null && !n.IsContainer;
        MiConnect.IsEnabled = isConn || isFolder;
        MiConnectOptions.IsEnabled = isConn;
        MiDisconnect.IsEnabled = n is not null && (n.OpenConnections > 0 || isFolder);
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

    private void SelectNode(ConnectionInfo node, bool focus = true)
    {
        for (var p = node.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            _vm.SelectedNode = node;
            if (focus) FocusTree();
        }, DispatcherPriority.Loaded);
    }

    private void FocusTree()
    {
        var container = Selected is null ? null : Tree.TreeContainerFromItem(Selected);
        if (container is not null) container.Focus(); else Tree.Focus();
    }

    private void OnDelete(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(DeleteSelectedAsync, "Delete");

    private async Task DeleteSelectedAsync()
    {
        var n = Selected;
        if (n is null or RootNodeInfo) return;
        var text = n is ContainerInfo c
            ? $"Delete folder '{n.Name}' and its {c.Descendants().Count()} items?"
            : $"Delete connection '{n.Name}'?";
        var r = await Dialogs.Dialogs.Message(this, "Delete", text, Dialogs.Dialogs.MsgButtons.YesNo);
        if (r != Dialogs.Dialogs.MsgResult.Yes) return;
        var parent = n.Parent;
        _vm.Delete(n);
        ScheduleAutoSave();
        // the TreeView may re-select a neighbour while it tears down the removed container; settle on the parent afterwards
        Dispatcher.UIThread.Post(() => { if (parent is not null) _vm.SelectedNode = parent; FocusTree(); }, DispatcherPriority.Background);
    }

    private void OnRename(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(RenameSelectedAsync, "Rename");

    private async Task RenameSelectedAsync()
    {
        var n = Selected;
        if (n is null) return;
        var name = await Dialogs.Dialogs.Input(this, "Rename", "New name:", n.Name);
        if (string.IsNullOrWhiteSpace(name) || name == n.Name) return;
        n.Name = name.Trim();
        _vm.MarkDirty();
        UpdateConfigHeader();
        ConfigGrid.RefreshValues();
        ScheduleAutoSave();
        FocusTree();
    }

    private void OnDuplicate(object? sender, RoutedEventArgs e)
    {
        if (Selected is null or RootNodeInfo) return;
        var copy = _vm.Duplicate(Selected);
        SelectNode(copy);
        ScheduleAutoSave();
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => MoveSelected(-1);
    private void OnMoveDown(object? sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        var node = Selected;
        if (node is null) return;
        _vm.Move(node, delta);
        ScheduleAutoSave();
        // the TreeView re-creates the moved container on the next layout pass and may drop the selection meanwhile
        SelectNode(node);
        Dispatcher.UIThread.Post(() => { _vm.SelectedNode = node; FocusTree(); }, DispatcherPriority.Background);
    }
    private void OnSortAsc(object? sender, RoutedEventArgs e) { _vm.Sort(true); UpdateSortIcon(); ScheduleAutoSave(); }
    private void OnSortDesc(object? sender, RoutedEventArgs e) { _vm.Sort(false); UpdateSortIcon(); ScheduleAutoSave(); }
    private void OnSortToggle(object? sender, RoutedEventArgs e) { _vm.Sort(!_vm.SortAscending); UpdateSortIcon(); ScheduleAutoSave(); }

    private void UpdateSortIcon()
    {
        SortIcon.Glyph = _vm.SortAscending ? "arrow-down-a-z" : "arrow-up-z-a";
        ToolTip.SetTip(SortButton, _vm.SortAscending ? "Sort A–Z" : "Sort Z–A");
    }

    private void OnExpandAll(object? sender, RoutedEventArgs e) => _vm.ExpandAll(true);
    private void OnCollapseAll(object? sender, RoutedEventArgs e) => _vm.ExpandAll(false);

    private void OnCopyHostname(object? sender, RoutedEventArgs e)
    {
        if (Selected is null || Selected.IsContainer || Clipboard is null) return;
        var host = Selected.GetEffective<string>(nameof(ConnectionInfo.Hostname));
        AsyncHelper.Fire(() => Clipboard.SetTextAsync(host), "Copy hostname");
        Notifications.Instance.Info($"Copied '{host}' to the clipboard.");
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
        ConfigGrid.RefreshValues();
        ScheduleAutoSave();
        Notifications.Instance.Info($"Inheritance of '{c.Name}' applied to {c.Descendants().Count()} items.");
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
            var item = new MenuItem { Header = f.Name, Icon = new Controls.Icon { Glyph = IconNames.ForNode(f) } };
            var target = f;
            item.Click += (_, _) => AsyncHelper.Fire(() => OpenConnectionAsync(target), $"Connect {target.Name}");
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no favorites — set Favorite = True on a connection)", IsEnabled = false });
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
        var item = new MenuItem { Header = n.Name, Icon = new Controls.Icon { Glyph = IconNames.ForNode(n) } };
        if (n is ContainerInfo c)
        {
            foreach (var child in c.Children) item.Items.Add(BuildNodeMenu(child));
            if (item.Items.Count == 0) item.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
        }
        else item.Click += (_, _) => AsyncHelper.Fire(() => OpenConnectionAsync(n), $"Connect {n.Name}");
        return item;
    }

    // ------------------------------------------------------------------ search

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchMatches = _vm.Search(SearchBox.Text ?? "").ToList();
        _searchIndex = _searchMatches.Count > 0 ? 0 : -1;
        if (_searchIndex >= 0) SelectNode(_searchMatches[0], focus: false);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _searchMatches.Count > 0:
                _searchIndex = (_searchIndex + 1) % _searchMatches.Count; SelectNode(_searchMatches[_searchIndex], focus: false); e.Handled = true; break;
            case Key.Up when _searchMatches.Count > 0:
                _searchIndex = (_searchIndex - 1 + _searchMatches.Count) % _searchMatches.Count; SelectNode(_searchMatches[_searchIndex], focus: false); e.Handled = true; break;
            case Key.Escape: SearchBox.Text = ""; FocusTree(); e.Handled = true; break;
            case Key.Enter when Selected is { IsContainer: false }: AsyncHelper.Fire(() => OpenConnectionAsync(Selected), "Connect"); e.Handled = true; break;
        }
    }

    // ------------------------------------------------------------------ connections / tabs

    private void OnConnect(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(ConnectSelectedAsync, "Connect");

    private async Task ConnectSelectedAsync()
    {
        if (Selected is null) return;
        if (Selected is ContainerInfo c and not RootNodeInfo)
        {
            foreach (var n in c.Children.Where(n => !n.IsContainer).ToList()) await OpenConnectionAsync(n);
            return;
        }
        if (!Selected.IsContainer) await OpenConnectionAsync(Selected);
    }

    private void OnConnectConsole(object? sender, RoutedEventArgs e) { if (Selected is { IsContainer: false } n) AsyncHelper.Fire(() => OpenConnectionAsync(n, console: true), "Connect"); }
    private void OnConnectFullscreen(object? sender, RoutedEventArgs e) { if (Selected is { IsContainer: false } n) AsyncHelper.Fire(async () => { await OpenConnectionAsync(n, fullscreen: true); EnterFullscreen(true); }, "Connect"); }
    private void OnConnectNoCredentials(object? sender, RoutedEventArgs e) { if (Selected is { IsContainer: false } n) AsyncHelper.Fire(() => OpenConnectionAsync(n, noCredentials: true), "Connect"); }
    private void OnConnectViewOnly(object? sender, RoutedEventArgs e) { if (Selected is { IsContainer: false } n) AsyncHelper.Fire(() => OpenConnectionAsync(n, viewOnly: true), "Connect"); }

    private void OnDisconnectNode(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(DisconnectSelectedAsync, "Disconnect");

    private async Task DisconnectSelectedAsync()
    {
        if (Selected is null) return;
        var nodes = Selected is ContainerInfo c ? c.Descendants().ToHashSet() : [Selected];
        foreach (var t in _vm.Tabs.Where(t => nodes.Contains(t.Info)).ToList()) await CloseTabAsync(t);
    }

    private async Task OpenConnectionAsync(ConnectionInfo info, bool console = false, bool fullscreen = false, bool noCredentials = false, bool viewOnly = false)
    {
        if (info.Protocol != ProtocolType.RDP)
        {
            Notifications.Instance.Warn($"{info.Name}: protocol {info.Protocol} is not supported by mRemoteMac (RDP only).");
            return;
        }
        if (string.IsNullOrWhiteSpace(info.GetEffective<string>(nameof(ConnectionInfo.Hostname))))
        {
            Notifications.Instance.Warn($"{info.Name}: no hostname set.");
            await Dialogs.Dialogs.Message(this, "Cannot connect", $"'{info.Name}' has no hostname. Set Hostname/IP in the Config panel first.");
            return;
        }
        var tab = new ConnectionTab(info, _settings, this)
        {
            ConnectToConsole = console, Fullscreen = fullscreen, WithoutCredentials = noCredentials, ViewOnly = viewOnly
        };
        tab.CloseRequested += t => AsyncHelper.Fire(() => CloseTabAsync(t), "Close tab");
        tab.StateChanged += UpdateTabHeader;
        _vm.Tabs.Add(tab);
        var item = new TabItem { Content = tab.Content, Tag = tab, Header = BuildTabHeader(tab), ContextMenu = BuildTabContextMenu(tab) };
        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
        UpdateEmptyHint();
        await tab.ConnectAsync();
    }

    private StackPanel BuildTabHeader(ConnectionTab tab)
    {
        var icon = new Controls.Icon { Glyph = IconNames.ForConnectionIcon(tab.Info.Icon), Size = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        var text = new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = new Controls.Icon { Glyph = "x", Size = 12 }, Classes = { "tabClose" } };
        ToolTip.SetTip(close, "Disconnect (⌘W)");
        close.Click += (_, _) => AsyncHelper.Fire(() => CloseTabAsync(tab), "Close tab");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, dot, text, close }, Background = Avalonia.Media.Brushes.Transparent };
        panel.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(panel).Properties.IsMiddleButtonPressed) AsyncHelper.Fire(() => CloseTabAsync(tab), "Close tab");
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
            if (icon is not null) mi.Icon = new Controls.Icon { Glyph = icon };
            mi.Click += (_, _) => action();
            return mi;
        }
        menu.Items.Add(Mk("Reconnect", () => AsyncHelper.Fire(tab.ConnectAsync, "Reconnect"), "refresh-cw"));
        menu.Items.Add(Mk("Disconnect", () => AsyncHelper.Fire(() => CloseTabAsync(tab), "Disconnect"), "circle-stop"));
        menu.Items.Add(Mk("Disconnect others", () => AsyncHelper.Fire(async () => { foreach (var t in _vm.Tabs.Where(t => t != tab).ToList()) await CloseTabAsync(t); }, "Disconnect others")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mk("Rename tab", () => AsyncHelper.Fire(async () =>
        {
            var name = await Dialogs.Dialogs.Input(this, "Rename tab", "Tab title:", tab.Title);
            if (!string.IsNullOrWhiteSpace(name)) tab.Title = name.Trim();
        }, "Rename tab"), "pencil"));
        menu.Items.Add(Mk("Duplicate tab", () => AsyncHelper.Fire(() => OpenConnectionAsync(tab.Info), "Duplicate tab"), "copy"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mk("Fullscreen", () => EnterFullscreen(!_fullscreen), "maximize"));
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
        menu.Opening += (_, _) => { smart.IsChecked = tab.SmartSize; viewOnly.IsChecked = tab.ViewOnly; };
        return menu;
    }

    private void UpdateTabHeader(ConnectionTab tab)
    {
        var item = Tabs.Items.OfType<TabItem>().FirstOrDefault(i => i.Tag == tab);
        if (item?.Header is StackPanel { Children: [Controls.Icon icon, Avalonia.Controls.Shapes.Ellipse dot, TextBlock text, ..] })
        {
            text.Text = tab.Title;
            text.Opacity = tab.State == TabState.Connected ? 1.0 : 0.65;
            icon.Glyph = IconNames.ForConnectionIcon(tab.Info.Icon);
            dot.IsVisible = tab.State is TabState.Connected or TabState.Connecting;
            dot.Fill = tab.State == TabState.Connected ? this.FindResource("StatusSuccessBrush") as Avalonia.Media.IBrush : this.FindResource("StatusWarningBrush") as Avalonia.Media.IBrush;
        }
    }

    private async Task CloseTabAsync(ConnectionTab tab)
    {
        var item = Tabs.Items.OfType<TabItem>().FirstOrDefault(i => i.Tag == tab);
        if (item is null) return;
        if (tab.IsConnected && _settings.ConfirmCloseConnection)
        {
            var r = await Dialogs.Dialogs.Message(this, "Disconnect", $"Disconnect from {tab.Info.Name}?", Dialogs.Dialogs.MsgButtons.YesNo);
            if (r != Dialogs.Dialogs.MsgResult.Yes) return;
        }
        await tab.DisposeAsync();
        Tabs.Items.Remove(item);
        _vm.Tabs.Remove(tab);
        UpdateEmptyHint();
        if (Tabs.Items.Count == 0 && _fullscreen) EnterFullscreen(false);
    }

    private ConnectionTab? CurrentTab => (Tabs?.SelectedItem as TabItem)?.Tag as ConnectionTab;
    public ConnectionTab? CurrentTabForTest => CurrentTab;

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Tabs is null || _vm is null) return;
        var tab = CurrentTab;
        _vm.SelectedTab = tab;
        if (tab is not null) Dispatcher.UIThread.Post(() => tab.View.Focus(), DispatcherPriority.Loaded);
    }

    private void UpdateEmptyHint() => EmptyHint.IsVisible = Tabs.Items.Count == 0;

    private void OnNextSession(object? sender, RoutedEventArgs e) { if (Tabs.Items.Count > 0) Tabs.SelectedIndex = (Tabs.SelectedIndex + 1) % Tabs.Items.Count; }
    private void OnPreviousSession(object? sender, RoutedEventArgs e) { if (Tabs.Items.Count > 0) Tabs.SelectedIndex = (Tabs.SelectedIndex - 1 + Tabs.Items.Count) % Tabs.Items.Count; }
    private void OnTabReconnect(object? sender, RoutedEventArgs e) { if (CurrentTab is { } t) AsyncHelper.Fire(t.ConnectAsync, "Reconnect"); }
    private void OnTabDisconnect(object? sender, RoutedEventArgs e) { if (CurrentTab is { } t) AsyncHelper.Fire(() => CloseTabAsync(t), "Disconnect"); }
    private void OnDisconnectAll(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(async () => { foreach (var t in _vm.Tabs.ToList()) await CloseTabAsync(t); }, "Disconnect all");
    private void OnReconnectAll(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(async () => { foreach (var t in _vm.Tabs.ToList()) await t.ConnectAsync(); }, "Reconnect all");
    private void OnSendCtrlAltDel(object? sender, RoutedEventArgs e) => CurrentTab?.SendCtrlAltDel();
    private void OnSendCtrlEsc(object? sender, RoutedEventArgs e) => CurrentTab?.SendCtrlEsc();
    private void OnSendWin(object? sender, RoutedEventArgs e) => CurrentTab?.SendWin();
    private void OnSendAltTab(object? sender, RoutedEventArgs e) => CurrentTab?.SendAltTab();

    // ------------------------------------------------------------------ quick connect

    private void OnQuickConnect(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(QuickConnectAsync, "Quick connect");

    private void OnQuickConnectKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AsyncHelper.Fire(QuickConnectAsync, "Quick connect");
    }

    private async Task QuickConnectAsync()
    {
        var text = QuickConnectBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var info = _vm.QuickConnect(text);
        QuickConnectBox.ItemsSource = _settings.QuickConnectHistory.ToList();
        _settings.Save();
        await OpenConnectionAsync(info);
    }

    // ------------------------------------------------------------------ view

    private void OnToggleNotifications(object? sender, RoutedEventArgs e) => SetNotificationsVisible(!NotificationsBody.IsVisible);

    private void SetNotificationsVisible(bool visible)
    {
        NotificationsBody.IsVisible = visible;
        MenuViewNotifications.IsChecked = visible;
        NotificationsChevron.Glyph = visible ? "chevron-down" : "chevron-right";
        UpdateNotificationsTitle();
    }

    private void UpdateNotificationsTitle()
    {
        var n = Notifications.Instance.Messages.Count;
        NotificationsTitle.Text = n == 0 ? "NOTIFICATIONS" : $"NOTIFICATIONS ({n})";
    }

    private void OnToggleQuickConnect(object? sender, RoutedEventArgs e)
    {
        QuickConnectBar.IsVisible = !QuickConnectBar.IsVisible;
        MenuViewQuickConnect.IsChecked = QuickConnectBar.IsVisible;
        _settings.ShowQuickConnectToolbar = QuickConnectBar.IsVisible;
    }

    private void OnResetLayout(object? sender, RoutedEventArgs e)
    {
        if (_fullscreen) EnterFullscreen(false);
        MainGrid.ColumnDefinitions[0].Width = new GridLength(300);
        LeftGrid.RowDefinitions[2].Height = new GridLength(280);
        NotificationsBody.Height = 150;
        SetNotificationsVisible(false);
        QuickConnectBar.IsVisible = true;
        MenuViewQuickConnect.IsChecked = true;
    }

    private void OnToggleFullscreen(object? sender, RoutedEventArgs e) => EnterFullscreen(!_fullscreen);

    private void EnterFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        if (on)
        {
            _settings.TreePaneWidth = MainGrid.ColumnDefinitions[0].Width.Value;
            _settings.ShowQuickConnectToolbar = QuickConnectBar.IsVisible;
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else WindowState = _stateBeforeFullscreen == WindowState.FullScreen ? WindowState.Normal : _stateBeforeFullscreen;
        _fullscreen = on;
        MainMenu.IsVisible = !on;
        QuickConnectBar.IsVisible = !on && _settings.ShowQuickConnectToolbar;
        LeftGrid.IsVisible = !on;
        NotificationsPanel.IsVisible = !on;
        MainGrid.ColumnDefinitions[0].Width = on ? new GridLength(0) : new GridLength(Math.Max(120, _settings.TreePaneWidth));
        MainGrid.ColumnDefinitions[1].Width = on ? new GridLength(0) : new GridLength(4);
        CurrentTab?.View.Focus();
    }

    // ------------------------------------------------------------------ notifications

    private void OnCopyMessage(object? sender, RoutedEventArgs e)
    {
        if (MessagesList.SelectedItem is NotificationMessage m && Clipboard is not null)
            AsyncHelper.Fire(() => Clipboard.SetTextAsync(m.Display), "Copy");
    }

    private void OnDeleteMessage(object? sender, RoutedEventArgs e)
    {
        if (MessagesList.SelectedItem is NotificationMessage m) Notifications.Instance.Messages.Remove(m);
        UpdateNotificationsTitle();
    }

    private void OnClearMessages(object? sender, RoutedEventArgs e)
    {
        Notifications.Instance.Messages.Clear();
        UpdateNotificationsTitle();
    }

    // ------------------------------------------------------------------ help

    private const string ProjectUrl = "https://github.com/PollySec/mremotemac";
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
    private void OnShortcuts(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(() => ShortcutsDialog.Show(this), "Shortcuts");
    private void OnHelp(object? sender, RoutedEventArgs e) => OpenUrl(ProjectUrl + "#using-it");
    private void OnWebsite(object? sender, RoutedEventArgs e) => OpenUrl(ProjectUrl);
    private void OnReportIssue(object? sender, RoutedEventArgs e) => OpenUrl(ProjectUrl + "/issues/new/choose");
    private void OnOpenLogFolder(object? sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(CrashLog.Path)!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("open", dir) { UseShellExecute = false });
    }
    private void OnAbout(object? sender, RoutedEventArgs e) => AsyncHelper.Fire(() => Dialogs.Dialogs.About(this), "About");

    /// <summary>Minimal ICommand for key bindings.</summary>
    private sealed class RelaySimpleCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
