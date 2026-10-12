using MRemoteMac.Services;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MRemoteMac.Models;

namespace MRemoteMac.Config;

/// <summary>Reads and writes mRemoteNG confCons.xml files (ConfVersion 2.6 .. 2.9, writes 2.9).</summary>
public static class ConnectionsFile
{
    public const string DefaultFileName = "confCons.xml";
    public const string WriteVersion = "2.9";
    public static readonly XNamespace Ns = "http://mremoteng.org";

    private static readonly string[] SecretAttributes = { "Password", "VNCProxyPassword", "RDGatewayPassword", "RDGatewayAccessToken" };

    public sealed class LoadResult
    {
        public required RootNodeInfo Root { get; init; }
        public required ICryptographyProvider Crypto { get; init; }
        public double ConfVersion { get; init; }
        public bool FullFileEncryption { get; init; }
    }

    /// <summary>Thrown when the file is protected with a custom master password that was not supplied.</summary>
    public sealed class PasswordRequiredException : Exception
    {
        public PasswordRequiredException() : base("The connection file is protected by a master password.") { }
    }

    public static LoadResult Load(string path, string? masterPassword = null)
        => LoadFromText(File.ReadAllText(path, Encoding.UTF8), masterPassword);

    public static LoadResult LoadFromText(string text, string? masterPassword)
    {
        if (!text.TrimStart().StartsWith("<?xml", StringComparison.Ordinal))
        {
            // pre-2.6 whole-file legacy encryption
            var legacy = new LegacyRijndaelCryptographyProvider();
            text = legacy.Decrypt(text.Trim(), masterPassword ?? RootNodeInfo.DefaultPassword);
        }

        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var rootEl = doc.DocumentElement ?? throw new InvalidDataException("Empty connections file");

        var versionText = (rootEl.GetAttribute("ConfVersion") is { Length: > 0 } v ? v : "2.6").Replace(',', '.');
        var confVersion = double.Parse(versionText, CultureInfo.InvariantCulture);

        ICryptographyProvider crypto;
        if (confVersion >= 2.6)
        {
            var engine = Attr(rootEl, "EncryptionEngine", "AES");
            var mode = Attr(rootEl, "BlockCipherMode", "GCM");
            var iter = int.TryParse(Attr(rootEl, "KdfIterations", "1000"), out var i) ? i : 1000;
            crypto = new AeadCryptographyProvider(engine, mode, iter);
        }
        else
        {
            crypto = new LegacyRijndaelCryptographyProvider();
        }

        var root = new RootNodeInfo { Name = Attr(rootEl, "Name", "Connections") };

        // Authenticate master password via the Protected attribute
        var protectedAttr = rootEl.GetAttribute("Protected");
        var password = RootNodeInfo.DefaultPassword;
        if (confVersion > 1.3 && !string.IsNullOrEmpty(protectedAttr))
        {
            string? plain = null;
            try { plain = crypto.Decrypt(protectedAttr, RootNodeInfo.DefaultPassword); } catch (EncryptionException) { }
            if (plain != "ThisIsNotProtected")
            {
                if (string.IsNullOrEmpty(masterPassword)) throw new PasswordRequiredException();
                try { crypto.Decrypt(protectedAttr, masterPassword); }
                catch (EncryptionException) { throw new PasswordRequiredException(); }
                password = masterPassword;
                root.MasterPassword = masterPassword;
            }
        }

        var fullFile = bool.TryParse(Attr(rootEl, "FullFileEncryption", "false"), out var ff) && ff;
        if (fullFile && confVersion >= 2.6)
        {
            var inner = crypto.Decrypt(rootEl.InnerText.Trim(), password);
            rootEl.InnerXml = inner;
        }

        foreach (XmlNode child in rootEl.ChildNodes)
        {
            if (child is XmlElement { Name: "Node" } el)
                root.AddChild(ReadNode(el, crypto, password, confVersion));
        }

        return new LoadResult { Root = root, Crypto = crypto, ConfVersion = confVersion, FullFileEncryption = fullFile };
    }

    private static string Attr(XmlElement el, string name, string def)
        => el.HasAttribute(name) ? el.GetAttribute(name) : def;

    private static ConnectionInfo ReadNode(XmlElement el, ICryptographyProvider crypto, string password, double confVersion)
    {
        var node = NewNode(Attr(el, "Type", "Connection"));
        ReadAttributes(node, el.Attributes.Cast<XmlAttribute>().Select(a => new KeyValuePair<string, string>(a.Name, a.Value)),
            v => { try { return crypto.Decrypt(v, password); } catch (EncryptionException) { return ""; } }, confVersion);
        if (node is ContainerInfo container)
            foreach (XmlNode child in el.ChildNodes)
                if (child is XmlElement { Name: "Node" } childEl)
                    container.AddChild(ReadNode(childEl, crypto, password, confVersion));
        return node;
    }

    /// <summary>A node of the given mRemoteNG "Type" attribute ("Connection" or "Container").</summary>
    public static ConnectionInfo NewNode(string typeText)
        => typeText.Equals("Container", StringComparison.OrdinalIgnoreCase) ? new ContainerInfo() : new ConnectionInfo();

    /// <summary>
    /// Applies mRemoteNG attributes to a node: the same code path for a confCons.xml element and for a record from the
    /// database, so both formats stay in lock-step. <paramref name="decryptSecret"/> turns a stored password attribute
    /// into plain text (identity for the database, AES-GCM for the file).
    /// </summary>
    public static void ReadAttributes(ConnectionInfo node, IEnumerable<KeyValuePair<string, string>> attributes,
        Func<string, string> decryptSecret, double confVersion = 2.9)
    {
        var props = ConnectionInfo.FileProperties.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Descr"] = "Description", ["ConnectToConsole"] = "UseConsoleSession",
            ["StartProgram"] = "RdpStartProgram", ["StartProgramWorkDir"] = "RdpStartProgramWorkDir",
        };

        foreach (var a in attributes)
        {
            var name = a.Key;
            var value = a.Value;
            if (name.StartsWith("xmlns", StringComparison.Ordinal)) continue;
            if (name.StartsWith("Inherit", StringComparison.Ordinal) && name.Length > 7)
            {
                if (bool.TryParse(value, out var inh) && inh) node.Inheritance.Add(name[7..]);
                continue;
            }
            switch (name)
            {
                case "Type": continue;
                case "Id": node.ConstantId = string.IsNullOrEmpty(value) ? Guid.NewGuid().ToString() : value; continue;
                case "Expanded": if (node is ContainerInfo c1) c1.IsExpanded = bool.TryParse(value, out var ex) && ex; continue;
                case "IsRootGroup": if (node is ContainerInfo c2) c2.IsRootGroup = bool.TryParse(value, out var rg) && rg; continue;
                case "Connected": node.PleaseConnect = bool.TryParse(value, out var pc) && pc; continue;
                case "Inherit": // pre-1.3: inherit everything
                    if (bool.TryParse(value, out var all) && all) foreach (var p in ConnectionInfo.InheritableProperties) node.Inheritance.Add(p);
                    continue;
            }
            if (aliases.TryGetValue(name, out var alias)) name = alias;

            if (Array.IndexOf(SecretAttributes, a.Key) >= 0)
                value = decryptSecret(value);

            if (name == "RedirectDiskDrives" && confVersion < 2.8)
            {
                value = bool.TryParse(value, out var rd) && rd ? "Local" : "None";
            }
            else if (name == "Colors" && confVersion < 1.3 && int.TryParse(value, out var ci))
            {
                value = ci switch { 0 => "Colors256", 1 => "Colors16Bit", 2 => "Colors24Bit", 3 => "Colors32Bit", _ => "Colors15Bit" };
            }

            if (props.TryGetValue(name, out var prop))
                SetFromString(node, prop, value);
            else
                node.UnknownAttributes[a.Key] = a.Value;
        }

    }

    private static void SetFromString(ConnectionInfo node, PropertyInfo prop, string value)
    {
        try
        {
            object? converted;
            var t = prop.PropertyType;
            if (t == typeof(string)) converted = value;
            else if (t == typeof(bool)) converted = bool.TryParse(value, out var b) && b;
            else if (t == typeof(int)) converted = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
            else if (t.IsEnum)
            {
                if (!Enum.TryParse(t, value, true, out converted))
                    converted = EnumHelper.Values(t).First();
                // removed protocols (e.g. ICA) fall back to RDP
                if (t == typeof(ProtocolType) && !Enum.IsDefined(t, converted!)) converted = ProtocolType.RDP;
            }
            else return;
            prop.SetValue(node, converted);
        }
        catch
        {
            // ignore malformed attribute, keep default
        }
    }

    // ------------------------------------------------------------------ save

    public static void Save(string path, RootNodeInfo root, ICryptographyProvider? crypto = null, bool fullFileEncryption = false)
    {
        var text = SerializeToText(root, crypto, fullFileEncryption);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    public static string SerializeToText(RootNodeInfo root, ICryptographyProvider? crypto = null, bool fullFileEncryption = false)
    {
        var aead = crypto as AeadCryptographyProvider ?? new AeadCryptographyProvider();
        var password = root.PasswordString;
        var protectedText = aead.Encrypt(root.IsProtected ? "ThisIsProtected" : "ThisIsNotProtected", password);

        var rootEl = new XElement(Ns + "Connections",
            new XAttribute(XNamespace.Xmlns + "mrng", Ns),
            new XAttribute("Name", root.Name),
            new XAttribute("Export", "false"),
            new XAttribute("EncryptionEngine", aead.Engine),
            new XAttribute("BlockCipherMode", aead.Mode),
            new XAttribute("KdfIterations", aead.KdfIterations),
            new XAttribute("FullFileEncryption", fullFileEncryption ? "true" : "false"),
            new XAttribute("Protected", protectedText),
            new XAttribute("ConfVersion", WriteVersion));

        foreach (var child in root.Children)
            rootEl.Add(WriteNode(child, aead, password));

        if (fullFileEncryption)
        {
            var inner = string.Concat(rootEl.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));
            rootEl.RemoveNodes();
            rootEl.Value = aead.Encrypt(inner, password);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), rootEl);
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "    ", Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false }))
            doc.Save(w);
        return sb.ToString().Replace("<?xml version=\"1.0\" encoding=\"utf-16\"?>", "<?xml version=\"1.0\" encoding=\"utf-8\"?>");
    }

    private static string B(bool b) => b ? "true" : "false";
    private static string E(Enum e) => e.ToString();

    private static XElement WriteNode(ConnectionInfo n, ICryptographyProvider crypto, string password)
    {
        var el = new XElement("Node");
        foreach (var a in NodeAttributes(n, v => crypto.Encrypt(v, password)))
            el.SetAttributeValue(a.Key, a.Value);
        if (n is ContainerInfo container)
            foreach (var child in container.Children)
                el.Add(WriteNode(child, crypto, password));
        return el;
    }

    /// <summary>
    /// Every attribute mRemoteNG 1.77 writes for a node, in its order: the single source of truth for the XML file and
    /// for the database records. <paramref name="encryptSecret"/> protects password attributes (identity for the
    /// database, which is encrypted as a whole).
    /// </summary>
    public static List<KeyValuePair<string, string>> NodeAttributes(ConnectionInfo n, Func<string, string> encryptSecret)
    {
        var list = new List<KeyValuePair<string, string>>(96);
        var container = n as ContainerInfo;
        void A(string name, string value) => list.Add(new KeyValuePair<string, string>(name, value));
        string Enc(string s) => string.IsNullOrEmpty(s) ? "" : encryptSecret(s);
        string Unk(string name, string def) => n.UnknownAttributes.TryGetValue(name, out var v) ? v : def;

        A("Name", n.Name);
        A("VmId", n.VmId);
        A("UseVmId", B(n.UseVmId));
        A("UseEnhancedMode", B(n.UseEnhancedMode));
        A("Type", container is null ? "Connection" : "Container");
        if (container is not null)
        {
            A("Expanded", B(container.IsExpanded));
            A("IsRootGroup", B(container.IsRootGroup));
        }
        A("Descr", n.Description);
        A("Icon", n.Icon);
        A("Panel", n.Panel);
        A("TabColor", n.TabColor);
        A("ConnectionFrameColor", E(n.ConnectionFrameColor));
        A("Id", n.ConstantId);
        A("Username", n.Username);
        A("Domain", n.Domain);
        A("Password", n.Inherits("Password") ? "" : Enc(n.Password));
        A("Hostname", n.Hostname.Trim());
        A("Protocol", E(n.Protocol));
        A("RdpVersion", n.RdpVersion.ToString().ToLowerInvariant());
        A("SSHTunnelConnectionName", Unk("SSHTunnelConnectionName", ""));
        A("OpeningCommand", Unk("OpeningCommand", ""));
        A("SSHOptions", Unk("SSHOptions", ""));
        A("PuttySession", Unk("PuttySession", "Default Settings"));
        A("Port", n.Port.ToString(CultureInfo.InvariantCulture));
        A("ConnectToConsole", B(n.UseConsoleSession));
        A("UseCredSsp", B(n.UseCredSsp));
        A("RenderingEngine", Unk("RenderingEngine", "EdgeChromium"));
        A("RDPAuthenticationLevel", E(n.RdpAuthenticationLevel));
        A("RDPMinutesToIdleTimeout", Math.Clamp(n.RdpMinutesToIdleTimeout, 0, 240).ToString(CultureInfo.InvariantCulture));
        A("RDPAlertIdleTimeout", B(n.RdpAlertIdleTimeout));
        A("LoadBalanceInfo", n.LoadBalanceInfo);
        A("Colors", E(n.Colors));
        A("Resolution", E(n.Resolution));
        A("AutomaticResize", B(n.AutomaticResize));
        A("DisplayWallpaper", B(n.DisplayWallpaper));
        A("DisplayThemes", B(n.DisplayThemes));
        A("EnableFontSmoothing", B(n.EnableFontSmoothing));
        A("EnableDesktopComposition", B(n.EnableDesktopComposition));
        A("DisableFullWindowDrag", B(n.DisableFullWindowDrag));
        A("DisableMenuAnimations", B(n.DisableMenuAnimations));
        A("DisableCursorShadow", B(n.DisableCursorShadow));
        A("DisableCursorBlinking", B(n.DisableCursorBlinking));
        A("CacheBitmaps", B(n.CacheBitmaps));
        A("RedirectDiskDrives", E(n.RedirectDiskDrives));
        A("RedirectDiskDrivesCustom", n.RedirectDiskDrivesCustom);
        A("RedirectPorts", B(n.RedirectPorts));
        A("RedirectPrinters", B(n.RedirectPrinters));
        A("RedirectClipboard", B(n.RedirectClipboard));
        A("RedirectSmartCards", B(n.RedirectSmartCards));
        A("RedirectSound", E(n.RedirectSound));
        A("SoundQuality", E(n.SoundQuality));
        A("RedirectAudioCapture", B(n.RedirectAudioCapture));
        A("RedirectKeys", B(n.RedirectKeys));
        A("Connected", "false");
        A("PreExtApp", n.PreExtApp);
        A("PostExtApp", n.PostExtApp);
        A("MacAddress", n.MacAddress);
        A("UserField", n.UserField);
        A("EnvironmentTags", n.EnvironmentTags);
        A("Favorite", B(n.Favorite));
        A("ExtApp", n.ExtApp);
        A("StartProgram", n.RdpStartProgram);
        A("StartProgramWorkDir", n.RdpStartProgramWorkDir);
        A("VNCCompression", Unk("VNCCompression", "CompNone"));
        A("VNCEncoding", Unk("VNCEncoding", "EncHextile"));
        A("VNCAuthMode", Unk("VNCAuthMode", "AuthVNC"));
        A("VNCProxyType", Unk("VNCProxyType", "ProxyNone"));
        A("VNCProxyIP", Unk("VNCProxyIP", ""));
        A("VNCProxyPort", Unk("VNCProxyPort", "0"));
        A("VNCProxyUsername", Unk("VNCProxyUsername", ""));
        A("VNCProxyPassword", Unk("VNCProxyPassword", ""));
        A("VNCColors", Unk("VNCColors", "ColNormal"));
        A("VNCSmartSizeMode", Unk("VNCSmartSizeMode", "SmartSAspect"));
        A("VNCViewOnly", Unk("VNCViewOnly", "false"));
        A("RDGatewayUsageMethod", E(n.RdGatewayUsageMethod));
        A("RDGatewayHostname", n.RdGatewayHostname);
        A("RDGatewayUseConnectionCredentials", E(n.RdGatewayUseConnectionCredentials));
        A("RDGatewayExternalCredentialProvider", Unk("RDGatewayExternalCredentialProvider", "None"));
        A("RDGatewayUserViaAPI", Unk("RDGatewayUserViaAPI", ""));
        A("RDGatewayUsername", n.RdGatewayUsername);
        A("RDGatewayPassword", Enc(n.RdGatewayPassword));
        A("RDGatewayAccessToken", Enc(n.RdGatewayAccessToken));
        A("RDGatewayDomain", n.RdGatewayDomain);
        A("UseRCG", B(n.UseRCG));
        A("UseRestrictedAdmin", B(n.UseRestrictedAdmin));
        A("UseRedirectionServerName", Unk("UseRedirectionServerName", "false"));
        A("UserViaAPI", Unk("UserViaAPI", ""));
        A("EC2InstanceId", Unk("EC2InstanceId", ""));
        A("EC2Region", Unk("EC2Region", "eu-central-1"));
        A("ExternalCredentialProvider", Unk("ExternalCredentialProvider", "None"));
        A("ExternalAddressProvider", Unk("ExternalAddressProvider", "None"));
        A("VaultOpenbaoMount", Unk("VaultOpenbaoMount", ""));
        A("VaultOpenbaoRole", Unk("VaultOpenbaoRole", ""));
        A("VaultOpenbaoSecretEngine", Unk("VaultOpenbaoSecretEngine", "Kv"));
        A("TerminalBackend", Unk("TerminalBackend", "PuttyNg"));

        foreach (var inh in n.Inheritance.OrderBy(x => x, StringComparer.Ordinal))
            A("Inherit" + inh, "true");

        return list;
    }
}
