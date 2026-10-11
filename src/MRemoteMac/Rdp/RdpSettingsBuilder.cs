using System.Globalization;
using MRemoteMac.Models;

namespace MRemoteMac.Rdp;

public enum CommandKeyMode { Control, Windows, Alt }

public sealed class RdpClientOptions
{
    public CommandKeyMode CommandKey { get; set; } = CommandKeyMode.Control;
    public bool UseGfx { get; set; } = true;
    public int ConnectTimeoutMs { get; set; } = 15000;
    public uint? KeyboardLayout { get; set; }
}

/// <summary>Translates an mRemoteNG ConnectionInfo into FreeRDP settings on an RdpSession.</summary>
public static class RdpSettingsBuilder
{
    public static void Apply(RdpSession s, ConnectionInfo c, int desktopWidth, int desktopHeight, RdpClientOptions opt,
        bool connectToConsoleOverride = false, bool withoutCredentials = false)
    {
        T Eff<T>(string name) => c.GetEffective<T>(name);

        s.SetString("ServerHostname", Eff<string>(nameof(ConnectionInfo.Hostname)).Trim());
        var port = Eff<int>(nameof(ConnectionInfo.Port));
        s.SetUInt32("ServerPort", (uint)(port > 0 ? port : 3389));

        if (!withoutCredentials)
        {
            var user = Eff<string>(nameof(ConnectionInfo.Username));
            var domain = Eff<string>(nameof(ConnectionInfo.Domain));
            var pass = Eff<string>(nameof(ConnectionInfo.Password));
            if (user.Contains('\\') && string.IsNullOrEmpty(domain))
            {
                var parts = user.Split('\\', 2);
                domain = parts[0]; user = parts[1];
            }
            if (!string.IsNullOrEmpty(user)) s.SetString("Username", user);
            if (!string.IsNullOrEmpty(domain)) s.SetString("Domain", domain);
            if (!string.IsNullOrEmpty(pass)) s.SetString("Password", pass);
        }

        s.SetUInt32("DesktopWidth", (uint)Math.Max(200, desktopWidth & ~1));
        s.SetUInt32("DesktopHeight", (uint)Math.Max(200, desktopHeight));
        s.SetUInt32("ColorDepth", (uint)(int)Eff<RdpColors>(nameof(ConnectionInfo.Colors)));

        s.SetBool("ConsoleSession", connectToConsoleOverride || Eff<bool>(nameof(ConnectionInfo.UseConsoleSession)));

        var credSsp = Eff<bool>(nameof(ConnectionInfo.UseCredSsp));
        s.SetBool("NlaSecurity", credSsp);
        s.SetBool("TlsSecurity", true);
        s.SetBool("RdpSecurity", !credSsp);

        // performance flags
        s.SetBool("DisableWallpaper", !Eff<bool>(nameof(ConnectionInfo.DisplayWallpaper)));
        s.SetBool("DisableThemes", !Eff<bool>(nameof(ConnectionInfo.DisplayThemes)));
        s.SetBool("AllowFontSmoothing", Eff<bool>(nameof(ConnectionInfo.EnableFontSmoothing)));
        s.SetBool("AllowDesktopComposition", Eff<bool>(nameof(ConnectionInfo.EnableDesktopComposition)));
        s.SetBool("DisableFullWindowDrag", Eff<bool>(nameof(ConnectionInfo.DisableFullWindowDrag)));
        s.SetBool("DisableMenuAnims", Eff<bool>(nameof(ConnectionInfo.DisableMenuAnimations)));
        s.SetBool("DisableCursorShadow", Eff<bool>(nameof(ConnectionInfo.DisableCursorShadow)));
        s.SetBool("DisableCursorBlinking", Eff<bool>(nameof(ConnectionInfo.DisableCursorBlinking)));
        s.SetBool("BitmapCacheEnabled", Eff<bool>(nameof(ConnectionInfo.CacheBitmaps)));
        s.SetBool("CompressionEnabled", true);
        s.SetUInt32("TcpConnectTimeout", (uint)opt.ConnectTimeoutMs);

        var layout = opt.KeyboardLayout ?? (uint)CultureInfo.CurrentCulture.KeyboardLayoutId;
        if (layout != 0) s.SetUInt32("KeyboardLayout", layout);

        var startProgram = Eff<string>(nameof(ConnectionInfo.RdpStartProgram));
        if (!string.IsNullOrWhiteSpace(startProgram))
        {
            s.SetString("AlternateShell", startProgram);
            var dir = Eff<string>(nameof(ConnectionInfo.RdpStartProgramWorkDir));
            if (!string.IsNullOrWhiteSpace(dir)) s.SetString("ShellWorkingDirectory", dir);
        }

        var lbi = Eff<string>(nameof(ConnectionInfo.LoadBalanceInfo));
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(lbi)) args.Add($"/load-balance-info:{lbi}");

        // auto-detect network, dynamic resolution when requested
        args.Add("/network:auto");
        if (c.GetEffective<bool>(nameof(ConnectionInfo.AutomaticResize)) || Eff<RdpResolutions>(nameof(ConnectionInfo.Resolution)) == RdpResolutions.FitToWindow)
            args.Add("/dynamic-resolution");
        args.Add(opt.UseGfx ? "/gfx:RFX,progressive" : "-gfx");

        // redirection
        switch (Eff<RdpSounds>(nameof(ConnectionInfo.RedirectSound)))
        {
            case RdpSounds.BringToThisComputer:
                args.Add("/sound");
                args.Add("/audio-mode:0");
                break;
            case RdpSounds.LeaveAtRemoteComputer: args.Add("/audio-mode:1"); break;
            default: args.Add("/audio-mode:2"); break;
        }
        if (Eff<bool>(nameof(ConnectionInfo.RedirectAudioCapture))) args.Add("/microphone");
        if (Eff<bool>(nameof(ConnectionInfo.RedirectClipboard))) args.Add("+clipboard"); else args.Add("-clipboard");
        if (Eff<bool>(nameof(ConnectionInfo.RedirectPrinters))) args.Add("/printer");
        if (Eff<bool>(nameof(ConnectionInfo.RedirectSmartCards))) args.Add("/smartcard");
        if (Eff<bool>(nameof(ConnectionInfo.RedirectPorts))) args.Add("/serial");
        switch (Eff<RdpDiskDrives>(nameof(ConnectionInfo.RedirectDiskDrives)))
        {
            case RdpDiskDrives.Local:
            case RdpDiskDrives.All:
                args.Add($"/drive:Home,{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}");
                if (Directory.Exists("/Volumes")) args.Add("/drive:Volumes,/Volumes");
                break;
            case RdpDiskDrives.Custom:
                foreach (var p in Eff<string>(nameof(ConnectionInfo.RedirectDiskDrivesCustom)).Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var path = p.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    if (Directory.Exists(path)) args.Add($"/drive:{Path.GetFileName(path.TrimEnd('/'))},{path}");
                }
                break;
        }

        // gateway
        var gwMethod = Eff<RdGatewayUsageMethod>(nameof(ConnectionInfo.RdGatewayUsageMethod));
        var gwHost = Eff<string>(nameof(ConnectionInfo.RdGatewayHostname));
        if (gwMethod != RdGatewayUsageMethod.Never && !string.IsNullOrWhiteSpace(gwHost))
        {
            var gw = new List<string> { $"g:{gwHost}" };
            var useConn = Eff<RdGatewayUseConnectionCredentials>(nameof(ConnectionInfo.RdGatewayUseConnectionCredentials));
            if (useConn == RdGatewayUseConnectionCredentials.No)
            {
                var gu = Eff<string>(nameof(ConnectionInfo.RdGatewayUsername));
                var gp = Eff<string>(nameof(ConnectionInfo.RdGatewayPassword));
                var gd = Eff<string>(nameof(ConnectionInfo.RdGatewayDomain));
                if (!string.IsNullOrEmpty(gu)) gw.Add($"u:{gu}");
                if (!string.IsNullOrEmpty(gp)) gw.Add($"p:{gp}");
                if (!string.IsNullOrEmpty(gd)) gw.Add($"d:{gd}");
            }
            else if (useConn == RdGatewayUseConnectionCredentials.AccessToken)
            {
                var token = Eff<string>(nameof(ConnectionInfo.RdGatewayAccessToken));
                if (!string.IsNullOrEmpty(token)) gw.Add($"access-token:{token}");
            }
            gw.Add(gwMethod == RdGatewayUsageMethod.Detect ? "usage-method:detect" : "usage-method:direct");
            args.Add("/gateway:" + string.Join(",", gw));
            if (useConn == RdGatewayUseConnectionCredentials.Yes) s.SetBool("GatewayUseSameCredentials", true);
        }

        if (Eff<bool>(nameof(ConnectionInfo.UseRestrictedAdmin))) args.Add("/restricted-admin");
        if (Eff<bool>(nameof(ConnectionInfo.UseRCG))) args.Add("/remote-guard");
        if (c.UseVmId && !string.IsNullOrWhiteSpace(c.VmId)) args.Add($"/vmconnect:{c.VmId}");

        // certificate handling: NoAuth = ignore, others go through the verify callback
        if (Eff<AuthenticationLevel>(nameof(ConnectionInfo.RdpAuthenticationLevel)) == AuthenticationLevel.NoAuth)
            args.Add("/cert:ignore");

        var rc = s.ParseArgs(args);
        if (rc != 0) throw new InvalidOperationException($"FreeRDP rejected arguments ({rc}): {string.Join(' ', args)}");
    }
}
