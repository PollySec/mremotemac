using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MRemoteMac.Services;

/// <summary>
/// Generic credentials in Windows Credential Manager, the counterpart of the macOS login keychain: encrypted for the
/// signed-in user and kept on this PC. Unlike a keychain item, a credential is not tied to the app that wrote it, so
/// any program running as the same user can read it; a master password protects against that (docs/storage.md).
/// </summary>
internal static partial class WindowsCredentials
{
    private const uint TypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    public const int MaxCommentLength = 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);

    /// <summary>Returns the stored bytes, or null when there is no such credential.</summary>
    public static unsafe byte[]? Read(string target)
    {
        if (!CredRead(target, TypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }
        try
        {
            ref readonly var credential = ref *(Credential*)pointer;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { CredFree(pointer); }
    }

    public static unsafe void Write(string target, ReadOnlySpan<byte> value, string comment)
    {
        var targetName = Marshal.StringToHGlobalUni(target);
        var commentText = Marshal.StringToHGlobalUni(comment.Length > MaxCommentLength ? comment[..MaxCommentLength] : comment);
        try
        {
            fixed (byte* blob = value)
            {
                var credential = new Credential
                {
                    Type = TypeGeneric,
                    TargetName = targetName,
                    Comment = commentText,
                    CredentialBlobSize = (uint)value.Length,
                    CredentialBlob = (IntPtr)blob,
                    Persist = PersistLocalMachine,
                };
                if (!CredWrite(credential, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(targetName);
            Marshal.FreeHGlobal(commentText);
        }
    }

    /// <summary>Deleting a credential that does not exist is not an error.</summary>
    public static void Delete(string target)
    {
        if (CredDelete(target, TypeGeneric, 0)) return;
        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorNotFound) throw new Win32Exception(error);
    }
}
