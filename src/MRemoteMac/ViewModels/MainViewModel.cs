using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MRemoteMac.Config;
using MRemoteMac.Models;
using MRemoteMac.Services;

namespace MRemoteMac.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public AppSettings Settings { get; }
    public Notifications Notifications => Notifications.Instance;
    public ObservableCollection<ConnectionInfo> TreeRoots { get; } = new();
    public ObservableCollection<ConnectionTab> Tabs { get; } = new();

    [ObservableProperty] private RootNodeInfo _root = new();
    [ObservableProperty] private ConnectionInfo? _selectedNode;
    [ObservableProperty] private ConnectionTab? _selectedTab;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _sortAscending = true;

    public ICryptographyProvider Crypto { get; private set; } = new AeadCryptographyProvider();
    public bool FullFileEncryption { get; private set; }

    public string Title => (IsDirty ? "*" : "") + (FilePath is null ? "mRemoteMac" : $"mRemoteMac - {Path.GetFileName(FilePath)}");

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        TreeRoots.Add(Root);
    }

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Title));
    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(Title));

    // ---------------- file ----------------

    public void NewFile()
    {
        SetRoot(new RootNodeInfo());
        FilePath = null;
        Crypto = new AeadCryptographyProvider();
        FullFileEncryption = false;
        IsDirty = false;
    }

    /// <summary>Loads a file. Throws ConnectionsFile.PasswordRequiredException when a master password is needed.</summary>
    public void LoadFile(string path, string? masterPassword)
    {
        var r = ConnectionsFile.Load(path, masterPassword);
        SetRoot(r.Root);
        Crypto = r.Crypto;
        FullFileEncryption = r.FullFileEncryption;
        FilePath = path;
        IsDirty = false;
        Notifications.Info($"Loaded {r.Root.Descendants().Count(n => !n.IsContainer)} connections from {path}");
    }

    private void SetRoot(RootNodeInfo root)
    {
        Root = root;
        TreeRoots.Clear();
        TreeRoots.Add(root);
        SelectedNode = root;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath ?? AppSettings.DefaultConnectionsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ConnectionsFile.Save(path, Root, Crypto, FullFileEncryption);
        FilePath = path;
        IsDirty = false;
    }

    public void MarkDirty() => IsDirty = true;

    // ---------------- tree editing ----------------

    public ContainerInfo TargetContainerFor(ConnectionInfo? node) => node switch
    {
        ContainerInfo c => c,
        { Parent: not null } n => n.Parent,
        _ => Root
    };

    public ConnectionInfo AddConnection(ConnectionInfo? at)
    {
        var parent = TargetContainerFor(at);
        var c = new ConnectionInfo();
        if (parent is not RootNodeInfo) foreach (var p in parent.Inheritance) c.Inheritance.Add(p);
        var index = at is not null && !at.IsContainer && at.Parent == parent ? parent.Children.IndexOf(at) + 1 : -1;
        parent.AddChild(c, index);
        parent.IsExpanded = true;
        MarkDirty();
        return c;
    }

    public ContainerInfo AddFolder(ConnectionInfo? at)
    {
        var parent = TargetContainerFor(at);
        var f = new ContainerInfo();
        parent.AddChild(f);
        parent.IsExpanded = true;
        MarkDirty();
        return f;
    }

    public void Delete(ConnectionInfo node)
    {
        if (node is RootNodeInfo || node.Parent is null) return;
        var parent = node.Parent;
        parent.RemoveChild(node);
        SelectedNode = parent;
        MarkDirty();
    }

    public ConnectionInfo Duplicate(ConnectionInfo node)
    {
        if (node is RootNodeInfo || node.Parent is null) return node;
        ConnectionInfo copy;
        if (node is ContainerInfo folder)
        {
            var f = new ContainerInfo();
            f.CopyFrom(folder);
            f.Name = folder.Name;
            foreach (var child in folder.Children) f.AddChild(DeepCopy(child));
            copy = f;
        }
        else copy = node.Clone();
        node.Parent.AddChild(copy, node.Parent.Children.IndexOf(node) + 1);
        MarkDirty();
        return copy;
    }

    private static ConnectionInfo DeepCopy(ConnectionInfo n)
    {
        if (n is ContainerInfo c)
        {
            var f = new ContainerInfo();
            f.CopyFrom(c);
            f.Name = c.Name;
            foreach (var child in c.Children) f.AddChild(DeepCopy(child));
            return f;
        }
        return n.Clone();
    }

    public void Move(ConnectionInfo node, int delta)
    {
        if (node.Parent is null) return;
        var list = node.Parent.Children;
        var i = list.IndexOf(node);
        var j = i + delta;
        if (j < 0 || j >= list.Count) return;
        list.Move(i, j);
        MarkDirty();
    }

    public void Sort(bool ascending)
    {
        SortAscending = ascending;
        Root.Sort(ascending);
        MarkDirty();
    }

    public void ExpandAll(bool expand)
    {
        foreach (var c in Root.Descendants().OfType<ContainerInfo>()) c.IsExpanded = expand;
        Root.IsExpanded = true;
    }

    public IEnumerable<ConnectionInfo> Search(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var n in Root.Descendants())
            if (n.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                n.Hostname.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                n.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                yield return n;
    }

    public IEnumerable<ConnectionInfo> Favorites() => Root.Descendants().Where(n => !n.IsContainer && n.Favorite);

    /// <summary>Parses mRemoteNG quick connect syntax: [user@]host[:port].</summary>
    public ConnectionInfo QuickConnect(string input)
    {
        var text = input.Trim();
        var c = new ConnectionInfo { IsQuickConnect = true, Protocol = ProtocolType.RDP, RdpVersion = RdpVersion.Highest };
        var at = text.LastIndexOf('@');
        if (at > 0) { c.Username = text[..at]; text = text[(at + 1)..]; }
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var port) && !text.Contains(']')) { c.Port = port; text = text[..colon]; }
        c.Hostname = text;
        c.Name = text;
        c.Resolution = RdpResolutions.FitToWindow;
        c.AutomaticResize = true;
        c.Colors = RdpColors.Colors32Bit;
        c.RedirectClipboard = true;
        if (!Settings.QuickConnectHistory.Contains(input.Trim()))
        {
            Settings.QuickConnectHistory.Insert(0, input.Trim());
            while (Settings.QuickConnectHistory.Count > 20) Settings.QuickConnectHistory.RemoveAt(Settings.QuickConnectHistory.Count - 1);
        }
        return c;
    }
}
