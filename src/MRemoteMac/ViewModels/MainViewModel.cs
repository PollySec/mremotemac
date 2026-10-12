using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MRemoteMac.Config;
using MRemoteMac.Models;
using MRemoteMac.Services;

namespace MRemoteMac.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public AppSettings Settings { get; }
    public static Notifications Notifications => Notifications.Instance;
    public ObservableCollection<ConnectionInfo> TreeRoots { get; } = new();
    public ObservableCollection<ConnectionTab> Tabs { get; } = new();

    [ObservableProperty] private RootNodeInfo _root = new();
    [ObservableProperty] private ConnectionInfo? _selectedNode;
    [ObservableProperty] private ConnectionTab? _selectedTab;
    [ObservableProperty] private IConnectionStore? _store;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _sortAscending = true;

    /// <summary>Path of the current store (database or XML file).</summary>
    public string? FilePath => Store?.Location;

    public string Title => (IsDirty ? "*" : "") + (Store is null ? "mRemoteMac – no connection store open" : $"mRemoteMac - {Store.DisplayName}");

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        TreeRoots.Add(Root);
    }

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Title));
    partial void OnStoreChanged(IConnectionStore? value) { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(FilePath)); }

    // ---------------- store ----------------

    /// <summary>Makes <paramref name="store"/> current and shows its tree. The previous store is disposed.</summary>
    public void UseStore(IConnectionStore store)
    {
        RootNodeInfo root;
        try { root = store.Load(); }
        catch { store.Dispose(); throw; }
        var old = Store;
        Store = store;
        SetRoot(root);
        IsDirty = false;
        old?.Dispose();
        Notifications.Info($"Loaded {root.Descendants().Count(n => !n.IsContainer)} connections from {store.Location}");
    }

    /// <summary>Replaces the tree with a revision from the store's history and records the restore as a new revision.</summary>
    public void RestoreRevision(long id)
    {
        if (Store is null) return;
        var root = Store.LoadRevision(id);
        Store.Save(root, $"Restored revision {id}"); // persist first; the UI only switches once the restore is on disk
        SetRoot(root);
        IsDirty = false;
    }

    /// <summary>Closes the store (checkpoints and zeroes keys). The tree stays visible but can no longer be saved.</summary>
    public void CloseStore()
    {
        var old = Store;
        Store = null;
        old?.Dispose();
    }

    private void SetRoot(RootNodeInfo root)
    {
        Root = root;
        TreeRoots.Clear();
        TreeRoots.Add(root);
        SelectedNode = root;
    }

    public void Save(string? summary = null)
    {
        if (Store is null) throw new InvalidOperationException("No connection store is open; use File ▸ Open or New.");
        Store.Save(Root, summary);
        IsDirty = false;
    }

    /// <summary>
    /// Writes an mRemoteNG confCons.xml copy of the current tree. <paramref name="masterPassword"/> protects the
    /// passwords in the file; null/empty means mRemoteNG's default key, which anyone with mRemoteNG can read.
    /// </summary>
    public void ExportXml(string path, string? masterPassword)
    {
        var crypto = Store is XmlConnectionStore xml ? xml.Crypto : new AeadCryptographyProvider();
        var fullFile = Store is XmlConnectionStore x2 && x2.FullFileEncryption;
        var previous = Root.MasterPassword;
        Root.MasterPassword = string.IsNullOrEmpty(masterPassword) ? null : masterPassword;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ConnectionsFile.Save(path, Root, crypto, fullFile);
        }
        finally { Root.MasterPassword = previous; }
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
        var c = new ConnectionInfo { RedirectClipboard = true }; // what people expect from a desktop RDP client
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
        // select the parent first so the TreeView has nothing to re-select when the item disappears (mRemoteNG behaviour)
        SelectedNode = parent;
        parent.RemoveChild(node);
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

    /// <summary>A node may move anywhere except onto itself, into its own subtree, or away from the root node.</summary>
    public static bool CanMoveTo(ConnectionInfo node, ContainerInfo target)
    {
        if (node is RootNodeInfo || node.Parent is null || ReferenceEquals(node, target)) return false;
        for (ContainerInfo? p = target; p is not null; p = p.Parent)
            if (ReferenceEquals(p, node)) return false;
        return true;
    }

    /// <summary>
    /// Moves <paramref name="node"/> under <paramref name="target"/> at <paramref name="index"/> (in the target's
    /// current child list, i.e. before the node is taken out of its old place). Returns false when nothing changed.
    /// </summary>
    public bool MoveTo(ConnectionInfo node, ContainerInfo target, int index)
    {
        if (!CanMoveTo(node, target)) return false;
        var old = node.Parent!;
        var oldIndex = old.Children.IndexOf(node);
        index = Math.Clamp(index, 0, target.Children.Count);
        if (ReferenceEquals(old, target))
        {
            if (index > oldIndex) index--; // taking the node out shifts what follows
            if (index == oldIndex) return false;
            old.Children.Move(oldIndex, index);
        }
        else
            target.AddChild(node, index);
        target.IsExpanded = true;
        MarkDirty();
        return true;
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
        var c = new ConnectionInfo { IsQuickConnect = true, Protocol = ProtocolType.RDP, RdpVersion = RdpVersion.Highest, RedirectClipboard = true };
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
