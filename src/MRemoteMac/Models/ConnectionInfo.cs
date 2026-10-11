using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MRemoteMac.Models;

/// <summary>
/// One tree node: a connection, a folder (ContainerInfo) or the root (RootNodeInfo).
/// Property names mirror mRemoteNG so the XML format and the property grid stay compatible.
/// </summary>
public partial class ConnectionInfo : ObservableObject
{
    public const string DefaultIcon = "mRemoteNG";
    public const string DefaultPanel = "General";

    // ----- Display -----
    [property: Category("Display"), DisplayName("Name"), Description("The name shown in the connection tree.")]
    [ObservableProperty] private string _name = "New Connection";
    [property: Category("Display"), DisplayName("Description")]
    [ObservableProperty] private string _description = "";
    [property: Category("Display"), DisplayName("Icon")]
    [ObservableProperty] private string _icon = DefaultIcon;
    [property: Category("Display"), DisplayName("Panel")]
    [ObservableProperty] private string _panel = DefaultPanel;

    // ----- Connection -----
    [property: Category("Connection"), DisplayName("Hostname/IP")]
    [ObservableProperty] private string _hostname = "";
    [property: Category("Connection"), DisplayName("Port")]
    [ObservableProperty] private int _port = 3389;
    [property: Category("Connection"), DisplayName("Username")]
    [ObservableProperty] private string _username = "";
    [property: Category("Connection"), DisplayName("Password"), PasswordPropertyText(true)]
    [ObservableProperty] private string _password = "";
    [property: Category("Connection"), DisplayName("Domain")]
    [ObservableProperty] private string _domain = "";

    // ----- Protocol -----
    [property: Category("Protocol"), DisplayName("Protocol")]
    [ObservableProperty] private ProtocolType _protocol = ProtocolType.RDP;
    [property: Category("Protocol"), DisplayName("RDP Version")]
    [ObservableProperty] private RdpVersion _rdpVersion = RdpVersion.Highest;
    [property: Category("Protocol"), DisplayName("Use Console Session")]
    [ObservableProperty] private bool _useConsoleSession;
    [property: Category("Protocol"), DisplayName("Authentication Level")]
    [ObservableProperty] private AuthenticationLevel _rdpAuthenticationLevel = AuthenticationLevel.NoAuth;
    [property: Category("Protocol"), DisplayName("Load Balance Info")]
    [ObservableProperty] private string _loadBalanceInfo = "";
    [property: Category("Protocol"), DisplayName("Use CredSSP")]
    [ObservableProperty] private bool _useCredSsp = true;
    [property: Category("Protocol"), DisplayName("Use Restricted Admin")]
    [ObservableProperty] private bool _useRestrictedAdmin;
    [property: Category("Protocol"), DisplayName("Use Remote Credential Guard")]
    [ObservableProperty] private bool _useRCG;
    [property: Category("Protocol"), DisplayName("Minutes To Idle Timeout")]
    [ObservableProperty] private int _rdpMinutesToIdleTimeout;
    [property: Category("Protocol"), DisplayName("Alert On Idle Timeout")]
    [ObservableProperty] private bool _rdpAlertIdleTimeout;
    [property: Category("Protocol"), DisplayName("Start Program")]
    [ObservableProperty] private string _rdpStartProgram = "";
    [property: Category("Protocol"), DisplayName("Start Program Working Directory")]
    [ObservableProperty] private string _rdpStartProgramWorkDir = "";
    [property: Category("Protocol"), DisplayName("Use VM ID")]
    [ObservableProperty] private bool _useVmId;
    [property: Category("Protocol"), DisplayName("VM ID")]
    [ObservableProperty] private string _vmId = "";
    [property: Category("Protocol"), DisplayName("Use Enhanced Mode")]
    [ObservableProperty] private bool _useEnhancedMode;

    // ----- RD Gateway -----
    [property: Category("RD Gateway"), DisplayName("Use Gateway")]
    [ObservableProperty] private RdGatewayUsageMethod _rdGatewayUsageMethod = RdGatewayUsageMethod.Never;
    [property: Category("RD Gateway"), DisplayName("Hostname")]
    [ObservableProperty] private string _rdGatewayHostname = "";
    [property: Category("RD Gateway"), DisplayName("Use Connection Credentials")]
    [ObservableProperty] private RdGatewayUseConnectionCredentials _rdGatewayUseConnectionCredentials = RdGatewayUseConnectionCredentials.Yes;
    [property: Category("RD Gateway"), DisplayName("Username")]
    [ObservableProperty] private string _rdGatewayUsername = "";
    [property: Category("RD Gateway"), DisplayName("Password"), PasswordPropertyText(true)]
    [ObservableProperty] private string _rdGatewayPassword = "";
    [property: Category("RD Gateway"), DisplayName("Domain")]
    [ObservableProperty] private string _rdGatewayDomain = "";
    [property: Category("RD Gateway"), DisplayName("Access Token"), PasswordPropertyText(true)]
    [ObservableProperty] private string _rdGatewayAccessToken = "";

    // ----- Appearance -----
    [property: Category("Appearance"), DisplayName("Resolution")]
    [ObservableProperty] private RdpResolutions _resolution = RdpResolutions.SmartSize;
    [property: Category("Appearance"), DisplayName("Automatic Resize")]
    [ObservableProperty] private bool _automaticResize = true;
    [property: Category("Appearance"), DisplayName("Colors")]
    [ObservableProperty] private RdpColors _colors = RdpColors.Colors16Bit;
    [property: Category("Appearance"), DisplayName("Cache Bitmaps")]
    [ObservableProperty] private bool _cacheBitmaps;
    [property: Category("Appearance"), DisplayName("Display Wallpaper")]
    [ObservableProperty] private bool _displayWallpaper;
    [property: Category("Appearance"), DisplayName("Display Themes")]
    [ObservableProperty] private bool _displayThemes;
    [property: Category("Appearance"), DisplayName("Enable Font Smoothing")]
    [ObservableProperty] private bool _enableFontSmoothing;
    [property: Category("Appearance"), DisplayName("Enable Desktop Composition")]
    [ObservableProperty] private bool _enableDesktopComposition;
    [property: Category("Appearance"), DisplayName("Disable Full Window Drag")]
    [ObservableProperty] private bool _disableFullWindowDrag;
    [property: Category("Appearance"), DisplayName("Disable Menu Animations")]
    [ObservableProperty] private bool _disableMenuAnimations;
    [property: Category("Appearance"), DisplayName("Disable Cursor Shadow")]
    [ObservableProperty] private bool _disableCursorShadow;
    [property: Category("Appearance"), DisplayName("Disable Cursor Blinking")]
    [ObservableProperty] private bool _disableCursorBlinking;
    [property: Category("Appearance"), DisplayName("Tab Color")]
    [ObservableProperty] private string _tabColor = "";
    [property: Category("Appearance"), DisplayName("Connection Frame Color")]
    [ObservableProperty] private ConnectionFrameColor _connectionFrameColor = ConnectionFrameColor.None;

    // ----- Redirect -----
    [property: Category("Redirect"), DisplayName("Disk Drives")]
    [ObservableProperty] private RdpDiskDrives _redirectDiskDrives = RdpDiskDrives.None;
    [property: Category("Redirect"), DisplayName("Disk Drives (custom)")]
    [ObservableProperty] private string _redirectDiskDrivesCustom = "";
    [property: Category("Redirect"), DisplayName("Printers")]
    [ObservableProperty] private bool _redirectPrinters;
    [property: Category("Redirect"), DisplayName("Clipboard")]
    [ObservableProperty] private bool _redirectClipboard;
    [property: Category("Redirect"), DisplayName("Ports")]
    [ObservableProperty] private bool _redirectPorts;
    [property: Category("Redirect"), DisplayName("Smart Cards")]
    [ObservableProperty] private bool _redirectSmartCards;
    [property: Category("Redirect"), DisplayName("Sounds")]
    [ObservableProperty] private RdpSounds _redirectSound = RdpSounds.DoNotPlay;
    [property: Category("Redirect"), DisplayName("Sound Quality")]
    [ObservableProperty] private RdpSoundQuality _soundQuality = RdpSoundQuality.Dynamic;
    [property: Category("Redirect"), DisplayName("Audio Capture")]
    [ObservableProperty] private bool _redirectAudioCapture;
    [property: Category("Redirect"), DisplayName("Key Combinations")]
    [ObservableProperty] private bool _redirectKeys;

    // ----- Miscellaneous -----
    [property: Category("Miscellaneous"), DisplayName("Pre-connect External Tool")]
    [ObservableProperty] private string _preExtApp = "";
    [property: Category("Miscellaneous"), DisplayName("Post-connect External Tool")]
    [ObservableProperty] private string _postExtApp = "";
    [property: Category("Miscellaneous"), DisplayName("MAC Address")]
    [ObservableProperty] private string _macAddress = "";
    [property: Category("Miscellaneous"), DisplayName("User Field")]
    [ObservableProperty] private string _userField = "";
    [property: Category("Miscellaneous"), DisplayName("Environment Tags")]
    [ObservableProperty] private string _environmentTags = "";
    [property: Category("Miscellaneous"), DisplayName("Favorite")]
    [ObservableProperty] private bool _favorite;
    [property: Category("Miscellaneous"), DisplayName("External Tool")]
    [ObservableProperty] private string _extApp = "";

    // ----- not shown in the grid -----
    [property: Browsable(false)] [ObservableProperty] private bool _isExpanded = true;
    [property: Browsable(false)] [ObservableProperty] private int _openConnections;
    [property: Browsable(false)] [ObservableProperty] private bool _isSelected;
    [Browsable(false)] public virtual System.Collections.ObjectModel.ObservableCollection<ConnectionInfo>? Children => null;
    [Browsable(false)] public string ConstantId { get; set; } = Guid.NewGuid().ToString();
    [Browsable(false)] public ContainerInfo? Parent { get; set; }
    [Browsable(false)] public bool PleaseConnect { get; set; }
    [Browsable(false)] public bool IsQuickConnect { get; set; }

    /// <summary>Names of properties inherited from the parent folder (mRemoteNG "InheritX" flags).</summary>
    [Browsable(false)] public HashSet<string> Inheritance { get; } = new(StringComparer.Ordinal);

    /// <summary>Attributes that mRemoteNG stores but this port does not model; kept so the file round-trips.</summary>
    [Browsable(false)] public Dictionary<string, string> UnknownAttributes { get; } = new();

    [Browsable(false)] public virtual TreeNodeType NodeType => TreeNodeType.Connection;
    [Browsable(false)] public virtual bool IsContainer => false;

    /// <summary>mRemoteNG rule: nodes directly under the root never inherit.</summary>
    [Browsable(false)]
    public bool InheritanceActive => Parent is not null && Parent is not RootNodeInfo && Parent.Parent is not RootNodeInfo;

    public bool Inherits(string propertyName) => Inheritance.Contains(propertyName) && InheritanceActive;

    /// <summary>Effective value after inheritance resolution.</summary>
    public object? GetEffective(string propertyName)
    {
        var prop = GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                   ?? throw new ArgumentException($"Unknown property {propertyName}");
        if (Inherits(propertyName) && Parent is not null)
            return Parent.GetEffective(propertyName);
        return prop.GetValue(this);
    }

    public T GetEffective<T>(string propertyName) => (T)GetEffective(propertyName)!;

    public static IReadOnlyList<PropertyInfo> GridProperties { get; } =
        typeof(ConnectionInfo).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false
                        && p.GetCustomAttribute<CategoryAttribute>() is not null)
            .ToList();

    /// <summary>All property names that mRemoteNG allows to be inherited.</summary>
    public static readonly string[] InheritableProperties =
    {
        "CacheBitmaps", "Colors", "Description", "DisplayThemes", "DisplayWallpaper", "EnableFontSmoothing",
        "EnableDesktopComposition", "DisableFullWindowDrag", "DisableMenuAnimations", "DisableCursorShadow",
        "DisableCursorBlinking", "Domain", "Icon", "Panel", "TabColor", "ConnectionFrameColor", "Password", "Port",
        "Protocol", "RdpVersion", "SSHTunnelConnectionName", "OpeningCommand", "SSHOptions", "PuttySession",
        "RedirectDiskDrives", "RedirectDiskDrivesCustom", "RedirectKeys", "RedirectPorts", "RedirectPrinters",
        "RedirectClipboard", "RedirectSmartCards", "RedirectSound", "SoundQuality", "RedirectAudioCapture",
        "Resolution", "AutomaticResize", "UseConsoleSession", "UseCredSsp", "RenderingEngine", "Username",
        "RDPAuthenticationLevel", "RDPMinutesToIdleTimeout", "RDPAlertIdleTimeout", "LoadBalanceInfo",
        "PreExtApp", "PostExtApp", "MacAddress", "UserField", "EnvironmentTags", "Favorite", "ExtApp",
        "VNCCompression", "VNCEncoding", "VNCAuthMode", "VNCProxyType", "VNCProxyIP", "VNCProxyPort",
        "VNCProxyUsername", "VNCProxyPassword", "VNCColors", "VNCSmartSizeMode", "VNCViewOnly",
        "RDGatewayUsageMethod", "RDGatewayHostname", "RDGatewayUseConnectionCredentials", "RDGatewayUsername",
        "RDGatewayPassword", "RDGatewayAccessToken", "RDGatewayDomain", "RDGatewayExternalCredentialProvider",
        "RDGatewayUserViaAPI", "VmId", "UseVmId", "UseEnhancedMode", "ExternalCredentialProvider", "UserViaAPI",
        "UseRCG", "UseRestrictedAdmin", "UseRedirectionServerName", "TerminalBackend"
    };

    public ConnectionInfo Clone()
    {
        var c = (ConnectionInfo)MemberwiseClone();
        c.ConstantId = Guid.NewGuid().ToString();
        c.Parent = null;
        // HashSet/Dictionary are reference types: copy them
        var inh = new HashSet<string>(Inheritance, StringComparer.Ordinal);
        var unk = new Dictionary<string, string>(UnknownAttributes);
        c.Inheritance.Clear(); foreach (var i in inh) c.Inheritance.Add(i);
        c.UnknownAttributes.Clear(); foreach (var kv in unk) c.UnknownAttributes[kv.Key] = kv.Value;
        return c;
    }

    public void CopyFrom(ConnectionInfo other)
    {
        foreach (var p in GridProperties)
            p.SetValue(this, p.GetValue(other));
        Inheritance.Clear();
        foreach (var i in other.Inheritance) Inheritance.Add(i);
        UnknownAttributes.Clear();
        foreach (var kv in other.UnknownAttributes) UnknownAttributes[kv.Key] = kv.Value;
    }

    public override string ToString() => Name;
}

public partial class ContainerInfo : ConnectionInfo
{
    public ContainerInfo()
    {
        Name = "New Folder";
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<ConnectionInfo> _children = new();
    [Browsable(false)] public override System.Collections.ObjectModel.ObservableCollection<ConnectionInfo> Children => _children;
    [Browsable(false)] public bool IsRootGroup { get; set; }

    [Browsable(false)] public override TreeNodeType NodeType => TreeNodeType.Container;
    [Browsable(false)] public override bool IsContainer => true;

    public void AddChild(ConnectionInfo child, int index = -1)
    {
        child.Parent?.Children.Remove(child);
        child.Parent = this;
        if (index < 0 || index >= Children.Count) Children.Add(child); else Children.Insert(index, child);
    }

    public void RemoveChild(ConnectionInfo child)
    {
        if (Children.Remove(child)) child.Parent = null;
    }

    public IEnumerable<ConnectionInfo> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            if (c is ContainerInfo ci)
                foreach (var d in ci.Descendants()) yield return d;
        }
    }

    public void Sort(bool ascending)
    {
        var sorted = Children.OrderByDescending(c => c.IsContainer)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!ascending) sorted = Children.OrderByDescending(c => c.IsContainer)
            .ThenByDescending(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Children.Clear();
        foreach (var c in sorted) Children.Add(c);
        foreach (var c in Children.OfType<ContainerInfo>()) c.Sort(ascending);
    }
}

public partial class RootNodeInfo : ContainerInfo
{
    public const string DefaultPassword = "mR3m";

    public RootNodeInfo()
    {
        Name = "Connections";
        Icon = DefaultIcon;
    }

    [Browsable(false)] public override TreeNodeType NodeType => TreeNodeType.Root;

    /// <summary>Custom master password, or null/empty for the default "mR3m".</summary>
    [Browsable(false)] public string? MasterPassword { get; set; }

    [Browsable(false)] public string PasswordString => string.IsNullOrEmpty(MasterPassword) ? DefaultPassword : MasterPassword;
    [Browsable(false)] public bool IsProtected => !string.IsNullOrEmpty(MasterPassword);
}
