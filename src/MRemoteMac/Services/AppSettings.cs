using System.Text.Json;
using System.Text.Json.Serialization;
using MRemoteMac.Rdp;

namespace MRemoteMac.Services;

public sealed class AppSettings
{
    public static string Directory { get; } = Environment.GetEnvironmentVariable("MRM_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "mRemoteMac");

    public static string DefaultConnectionsPath => Path.Combine(Directory, "confCons.xml");
    private static string SettingsPath => Path.Combine(Directory, "settings.json");

    public string? ConnectionsFilePath { get; set; }
    public CommandKeyMode CommandKey { get; set; } = CommandKeyMode.Control;
    public bool ShowQuickConnectToolbar { get; set; } = true;
    public bool ShowNotificationsPanel { get; set; }
    public bool ShowFileMenu { get; set; } = true;
    public bool DarkTheme { get; set; }
    public bool SingleClickOpensConnection { get; set; }
    public bool ConfirmCloseConnection { get; set; } = true;
    public bool AutoSaveOnChange { get; set; } = true;
    public bool CloseTabOnDisconnect { get; set; } = true;
    public bool UseGfx { get; set; } = true;
    public bool StaticWhileConnecting { get; set; } = true;
    public string QuickConnectProtocol { get; set; } = "RDP";
    public List<string> QuickConnectHistory { get; set; } = new();
    public double TreePaneWidth { get; set; } = 300;
    public double ConfigPaneHeight { get; set; } = 280;
    public double NotificationsHeight { get; set; } = 150;
    public int WindowWidth { get; set; } = 1180;
    public int WindowHeight { get; set; } = 720;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
        }
        catch { /* corrupt settings: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
    }
}
