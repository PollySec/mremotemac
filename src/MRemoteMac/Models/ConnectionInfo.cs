using System.ComponentModel;
using System.Collections.Frozen;
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
    [property: Category("Display"), DisplayName("Description"), Description("Free text about the connection; the search box matches it.")]
    [ObservableProperty] private string _description = "";
    [property: Category("Display"), DisplayName("Icon"), Description("Icon shown in the tree and on the session tab.")]
    [ObservableProperty] private string _icon = DefaultIcon;
    [property: Category("Display"), DisplayName("Panel"), Browsable(false)] // kept in the file for mRemoteNG, no equivalent here
    [ObservableProperty] private string _panel = DefaultPanel;

    // ----- Connection -----
    [property: Category("Connection"), DisplayName("Hostname/IP"), Description("Host name or IP address of the remote computer.")]
    [ObservableProperty] private string _hostname = "";
    [property: Category("Connection"), DisplayName("Port"), Description("TCP port of the RDP service, 3389 by default.")]
    [ObservableProperty] private int _port = 3389;
    [property: Category("Connection"), DisplayName("Username"), Description("User name for the logon; DOMAIN\\user is accepted.")]
    [ObservableProperty] private string _username = "";
    [property: Category("Connection"), DisplayName("Password"), Description("Logon password, stored AES-GCM encrypted in the connection file."), PasswordPropertyText(true)]
    [ObservableProperty] private string _password = "";
    [property: Category("Connection"), DisplayName("Domain"), Description("Windows domain for the logon; leave empty for local accounts.")]
    [ObservableProperty] private string _domain = "";

    // ----- Protocol -----
    [property: Category("Protocol"), DisplayName("Protocol"),
     Description("mRemoteMac supports RDP only. Other protocols are kept for compatibility with mRemoteNG files but cannot be opened.")]
    [ObservableProperty] private ProtocolType _protocol = ProtocolType.RDP;
    [property: Category("Protocol"), DisplayName("RDP Version"), Browsable(false)] // kept in the file for mRemoteNG, no equivalent here
    [ObservableProperty] private RdpVersion _rdpVersion = RdpVersion.Highest;
    [property: Category("Protocol"), DisplayName("Use Console Session"), Description("Connect to the console (admin) session instead of starting a new one.")]
    [ObservableProperty] private bool _useConsoleSession;
    [property: Category("Protocol"), DisplayName("Authentication Level"), Description("Certificate policy: AuthRequired refuses servers whose certificate cannot be verified; the other levels ask you.")]
    [ObservableProperty] private AuthenticationLevel _rdpAuthenticationLevel = AuthenticationLevel.NoAuth;
    [property: Category("Protocol"), DisplayName("Load Balance Info"), Description("Load-balancing cookie sent to a connection broker or RD Connection Broker farm.")]
    [ObservableProperty] private string _loadBalanceInfo = "";
    [property: Category("Protocol"), DisplayName("Use CredSSP"), Description("Network Level Authentication (CredSSP). Off: the server shows its own logon screen and the credentials are typed into it.")]
    [ObservableProperty] private bool _useCredSsp = true;
    [property: Category("Protocol"), DisplayName("Use Restricted Admin"), Description("Restricted Admin mode: the password never reaches the host (needs admin rights there).")]
    [ObservableProperty] private bool _useRestrictedAdmin;
    [property: Category("Protocol"), DisplayName("Use Remote Credential Guard"), Description("Remote Credential Guard: credentials stay on this Mac (Kerberos environments only).")]
    [ObservableProperty] private bool _useRCG;
    [property: Category("Protocol"), DisplayName("Minutes To Idle Timeout"), Description("Disconnect after this many minutes without mouse or keyboard input in the session. 0 = never.")]
    [ObservableProperty] private int _rdpMinutesToIdleTimeout;
    [property: Category("Protocol"), DisplayName("Alert On Idle Timeout"), Description("Show a message when a session is disconnected for inactivity.")]
    [ObservableProperty] private bool _rdpAlertIdleTimeout;
    [property: Category("Protocol"), DisplayName("Start Program"), Description("Program to run instead of the desktop shell (alternate shell).")]
    [ObservableProperty] private string _rdpStartProgram = "";
    [property: Category("Protocol"), DisplayName("Start Program Working Directory"), Description("Working directory for the start program.")]
    [ObservableProperty] private string _rdpStartProgramWorkDir = "";
    [property: Category("Protocol"), DisplayName("Use VM ID"), Description("Connect to a Hyper-V virtual machine through its host (VMConnect); the hostname is the Hyper-V host.")]
    [ObservableProperty] private bool _useVmId;
    [property: Category("Protocol"), DisplayName("VM ID"), Description("GUID of the Hyper-V virtual machine.")]
    [ObservableProperty] private string _vmId = "";
    [property: Category("Protocol"), DisplayName("Use Enhanced Mode"), Browsable(false)] // kept in the file for mRemoteNG, no equivalent here
    [ObservableProperty] private bool _useEnhancedMode;

    // ----- RD Gateway -----
    [property: Category("RD Gateway"), DisplayName("Use Gateway"), Description("Remote Desktop Gateway: Never, Always, or Detect (try direct first, then the gateway).")]
    [ObservableProperty] private RdGatewayUsageMethod _rdGatewayUsageMethod = RdGatewayUsageMethod.Never;
    [property: Category("RD Gateway"), DisplayName("Hostname"), Description("Gateway server, host or host:port.")]
    [ObservableProperty] private string _rdGatewayHostname = "";
    [property: Category("RD Gateway"), DisplayName("Use Connection Credentials"), Description("Yes reuses the connection's credentials; No uses the gateway fields below; AccessToken uses the token field.")]
    [ObservableProperty] private RdGatewayUseConnectionCredentials _rdGatewayUseConnectionCredentials = RdGatewayUseConnectionCredentials.Yes;
    [property: Category("RD Gateway"), DisplayName("Username"), Description("User name for the gateway when Use Connection Credentials is No.")]
    [ObservableProperty] private string _rdGatewayUsername = "";
    [property: Category("RD Gateway"), DisplayName("Password"), Description("Gateway password, stored encrypted like the logon password."), PasswordPropertyText(true)]
    [ObservableProperty] private string _rdGatewayPassword = "";
    [property: Category("RD Gateway"), DisplayName("Domain"), Description("Domain for the gateway credentials.")]
    [ObservableProperty] private string _rdGatewayDomain = "";
    [property: Category("RD Gateway"), DisplayName("Access Token"), Description("Bearer token for gateways that authenticate with Azure AD / RD Web tokens."), PasswordPropertyText(true)]
    [ObservableProperty] private string _rdGatewayAccessToken = "";

    // ----- Appearance -----
    [property: Category("Appearance"), DisplayName("Resolution"), Description("SmartSize: fixed desktop scaled to the panel. FitToWindow: the desktop follows the panel size. Fullscreen: the screen size.")]
    [ObservableProperty] private RdpResolutions _resolution = RdpResolutions.SmartSize;
    [property: Category("Appearance"), DisplayName("Automatic Resize"), Description("Ask the server to change its desktop size whenever the panel is resized.")]
    [ObservableProperty] private bool _automaticResize = true;
    [property: Category("Appearance"), DisplayName("Colors"), Description("Color depth for the legacy codec path; the graphics pipeline always runs at 32-bit.")]
    [ObservableProperty] private RdpColors _colors = RdpColors.Colors16Bit;
    [property: Category("Appearance"), DisplayName("Cache Bitmaps"), Description("Bitmap, glyph and offscreen caches on the legacy codec path.")]
    [ObservableProperty] private bool _cacheBitmaps;
    [property: Category("Appearance"), DisplayName("Display Wallpaper"), Description("Let the remote desktop show its wallpaper (costs bandwidth).")]
    [ObservableProperty] private bool _displayWallpaper;
    [property: Category("Appearance"), DisplayName("Display Themes"), Description("Let the remote desktop use visual themes.")]
    [ObservableProperty] private bool _displayThemes;
    [property: Category("Appearance"), DisplayName("Enable Font Smoothing"), Description("ClearType-style text in the session.")]
    [ObservableProperty] private bool _enableFontSmoothing;
    [property: Category("Appearance"), DisplayName("Enable Desktop Composition"), Description("Allow desktop composition (Aero) on the remote side.")]
    [ObservableProperty] private bool _enableDesktopComposition;
    [property: Category("Appearance"), DisplayName("Disable Full Window Drag"), Description("Show only the outline while windows are moved.")]
    [ObservableProperty] private bool _disableFullWindowDrag;
    [property: Category("Appearance"), DisplayName("Disable Menu Animations"), Description("Turn off menu and tooltip animations on the remote side.")]
    [ObservableProperty] private bool _disableMenuAnimations;
    [property: Category("Appearance"), DisplayName("Disable Cursor Shadow"), Description("No shadow under the remote mouse pointer.")]
    [ObservableProperty] private bool _disableCursorShadow;
    [property: Category("Appearance"), DisplayName("Disable Cursor Blinking"), Description("Stop the text cursor from blinking in the session.")]
    [ObservableProperty] private bool _disableCursorBlinking;
    [property: Category("Appearance"), DisplayName("Tab Color"), Description("Colors the session tab's marker, for example #D9534F or Red. Empty for the accent color.")]
    [ObservableProperty] private string _tabColor = "";
    [property: Category("Appearance"), DisplayName("Connection Frame Color"), Description("Draws a colored frame around the session view, handy for telling production systems apart.")]
    [ObservableProperty] private ConnectionFrameColor _connectionFrameColor = ConnectionFrameColor.None;

    // ----- Redirect -----
    [property: Category("Redirect"), DisplayName("Disk Drives"), Description("Share Mac folders as drives in the session: Local / All share your home folder and /Volumes, Custom the paths below.")]
    [ObservableProperty] private RdpDiskDrives _redirectDiskDrives = RdpDiskDrives.None;
    [property: Category("Redirect"), DisplayName("Disk Drives (custom)"), Description("Folders to share when Disk Drives is Custom, separated by ; (~ is your home folder).")]
    [ObservableProperty] private string _redirectDiskDrivesCustom = "";
    [property: Category("Redirect"), DisplayName("Printers"), Description("Make the Mac printers available in the session.")]
    [ObservableProperty] private bool _redirectPrinters;
    [property: Category("Redirect"), DisplayName("Clipboard"), Description("Share the clipboard (text and files) in both directions.")]
    [ObservableProperty] private bool _redirectClipboard = true; // what people expect from a desktop RDP client
    [property: Category("Redirect"), DisplayName("Ports"), Description("Redirect serial ports into the session.")]
    [ObservableProperty] private bool _redirectPorts;
    [property: Category("Redirect"), DisplayName("Smart Cards"), Description("Redirect smart card readers for logon and applications.")]
    [ObservableProperty] private bool _redirectSmartCards;
    [property: Category("Redirect"), DisplayName("Sounds"), Description("Play remote audio on this Mac, leave it on the remote computer, or mute it.")]
    [ObservableProperty] private RdpSounds _redirectSound = RdpSounds.DoNotPlay;
    [property: Category("Redirect"), DisplayName("Sound Quality"), Description("Audio quality when sound is brought to this Mac: Dynamic adapts to the link, Medium, High.")]
    [ObservableProperty] private RdpSoundQuality _soundQuality = RdpSoundQuality.Dynamic;
    [property: Category("Redirect"), DisplayName("Audio Capture"), Description("Send the Mac microphone to the session.")]
    [ObservableProperty] private bool _redirectAudioCapture;
    [property: Category("Redirect"), DisplayName("Key Combinations"), Browsable(false)] // kept in the file for mRemoteNG, no equivalent here
    [ObservableProperty] private bool _redirectKeys;

    // ----- Miscellaneous -----
    [property: Category("Miscellaneous"), DisplayName("Pre-connect External Tool"), Browsable(false)] // kept in the file for mRemoteNG, no equivalent here
    [ObservableProperty] private string _preExtApp = "";
    [property: Category("Miscellaneous"), DisplayName("Post-connect External Tool"), Browsable(false)] // kept in the file for mRemoteNG, no equivalent here
    [ObservableProperty] private string _postExtApp = "";
    [property: Category("Miscellaneous"), DisplayName("MAC Address"), Description("Informational: the host's MAC address (mRemoteNG uses it for Wake-on-LAN tools).")]
    [ObservableProperty] private string _macAddress = "";
    [property: Category("Miscellaneous"), DisplayName("User Field"), Description("Free text kept in the connection file; mRemoteNG passes it to external tools.")]
    [ObservableProperty] private string _userField = "";
    [property: Category("Miscellaneous"), DisplayName("Environment Tags"), Description("Free text tags kept in the connection file.")]
    [ObservableProperty] private string _environmentTags = "";
    [property: Category("Miscellaneous"), DisplayName("Favorite"), Description("Show this connection in the Favorites menu on the toolbar.")]
    [ObservableProperty] private bool _favorite;
    [property: Category("Miscellaneous"), DisplayName("External Tool"), Browsable(false)] // kept in the file for mRemoteNG, no equivalent here
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

    /// <summary>Protocol after inheritance, i.e. the one a connection would actually be opened with.</summary>
    [Browsable(false)] public ProtocolType EffectiveProtocol => GetEffective<ProtocolType>(nameof(Protocol));
    /// <summary>True for folders and RDP connections; false for protocols mRemoteMac cannot open.</summary>
    [Browsable(false)] public bool IsSupported => IsContainer || EffectiveProtocol == ProtocolType.RDP;
    /// <summary>Tooltip for unsupported nodes in the tree; null when supported.</summary>
    [Browsable(false)] public string? UnsupportedHint => IsSupported ? null : $"{EffectiveProtocol} is not supported – mRemoteMac is RDP only";

    partial void OnProtocolChanged(ProtocolType value) => NotifySupportChanged();

    /// <summary>
    /// Re-evaluates <see cref="IsSupported"/> for this node and everything below it. Called when the protocol or its
    /// inheritance changes, or when the node is moved, because the effective protocol depends on the ancestors.
    /// </summary>
    public void NotifySupportChanged()
    {
        OnPropertyChanged(nameof(EffectiveProtocol));
        OnPropertyChanged(nameof(IsSupported));
        OnPropertyChanged(nameof(UnsupportedHint));
        if (this is ContainerInfo c)
            foreach (var d in c.Descendants()) d.NotifySupportChanged();
    }

    /// <summary>mRemoteNG rule: nodes directly under the root never inherit.</summary>
    [Browsable(false)]
    public bool InheritanceActive => Parent is not null && Parent is not RootNodeInfo && Parent.Parent is not RootNodeInfo;

    public bool Inherits(string propertyName) => Inheritance.Contains(propertyName) && InheritanceActive;

    /// <summary>Effective value after inheritance resolution.</summary>
    private static readonly System.Collections.Frozen.FrozenDictionary<string, PropertyInfo> PropertyMap =
        typeof(ConnectionInfo).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, StringComparer.Ordinal)
            .ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Public instance property by name (trim-safe: the map is built from a statically known type).</summary>
    public static PropertyInfo? FindProperty(string propertyName) => PropertyMap.GetValueOrDefault(propertyName);

    public object? GetEffective(string propertyName)
    {
        var prop = FindProperty(propertyName) ?? throw new ArgumentException($"Unknown property {propertyName}");
        if (Inherits(propertyName) && Parent is not null)
            return Parent.GetEffective(propertyName);
        return prop.GetValue(this);
    }

    public T GetEffective<T>(string propertyName) => (T)GetEffective(propertyName)!;

    /// <summary>Every property stored in the connection file (has a Category), including ones hidden from the grid.</summary>
    public static IReadOnlyList<PropertyInfo> FileProperties { get; } =
        typeof(ConnectionInfo).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<CategoryAttribute>() is not null)
            .ToList();

    /// <summary>Properties shown in the config panel: the file properties that have a function in mRemoteMac.</summary>
    public static IReadOnlyList<PropertyInfo> GridProperties { get; } =
        FileProperties.Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false).ToList();

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
        child.NotifySupportChanged(); // the effective protocol depends on the new ancestors
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
