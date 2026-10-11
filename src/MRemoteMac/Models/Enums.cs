namespace MRemoteMac.Models;

public enum TreeNodeType { None, Root, Container, Connection, PuttyRoot, PuttySession }

public enum ProtocolType
{
    RDP = 0, VNC = 1, SSH1 = 2, SSH2 = 3, Telnet = 4, Rlogin = 5, RAW = 6, HTTP = 7, HTTPS = 8,
    PowerShell = 10, ARD = 11, Terminal = 12, WSL = 13, AnyDesk = 14, IntApp = 20
}

public enum RdpVersion { Rdc6, Rdc7, Rdc8, Rdc9, Rdc10, Rdc11, Highest = 1000 }

public enum RdpColors { Colors256 = 8, Colors15Bit = 15, Colors16Bit = 16, Colors24Bit = 24, Colors32Bit = 32 }

public enum RdpResolutions { SmartSize, FitToWindow, Fullscreen }

public enum RdpSounds { BringToThisComputer = 0, LeaveAtRemoteComputer = 1, DoNotPlay = 2 }

public enum RdpSoundQuality { Dynamic, Medium, High }

public enum RdpDiskDrives { None, Local, All, Custom }

public enum AuthenticationLevel { NoAuth, AuthRequired, WarnOnFailedAuth }

public enum RdGatewayUsageMethod { Never = 0, Always = 1, Detect = 2 }

public enum RdGatewayUseConnectionCredentials { No = 0, Yes = 1, SmartCard = 2, ExternalCredentialProvider = 3, AccessToken = 4 }

public enum ConnectionFrameColor { None, Red, Yellow, Green, Blue, Purple }

public enum RenderingEngine { IE = 1, EdgeChromium = 2 }
