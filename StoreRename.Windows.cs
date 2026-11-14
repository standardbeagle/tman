using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tman;

/// <summary>
/// The Windows rename behind <see cref="Store.Save(RunRecord)"/>. File.Move(overwrite) is
/// MoveFileEx(MOVEFILE_REPLACE_EXISTING), which Windows refuses with access denied while anyone has
/// the destination open, whatever sharing that reader asked for. Every tman process lists the store
/// (each queued run polls it), so with several agents running, a runner's save kept meeting a reader,
/// ran out of retries, and the run was killed as one tman "can no longer record". A POSIX-semantics
/// rename (FileRenameInfoEx, Windows 10 1709+ on NTFS) replaces the name even while it is open, as
/// rename(2) does, provided the readers share delete, which <see cref="Store.OpenForRead"/> does: a
/// reader keeps the version it opened. A volume without it (FAT, a network share) answers "not
/// supported" and the save falls back to File.Move.
/// </summary>
static class StoreRename
{
    const uint DeleteAccess = 0x00010000;
    const uint SynchronizeAccess = 0x00100000;
    const uint ShareAll = 0x7;
    const uint OpenExisting = 3;
    const int FileRenameInfoEx = 22;
    const uint ReplaceIfExists = 0x1;
    const uint PosixSemantics = 0x2;

    const int ErrorInvalidFunction = 1;
    const int ErrorAccessDenied = 5;
    const int ErrorNotSupported = 50;
    const int ErrorInvalidParameter = 87;

    /// <summary>Renames <paramref name="from"/> over <paramref name="to"/>.</summary>
    public static void Replace(string from, string to)
    {
        if (!OperatingSystem.IsWindows() || !TryPosixReplace(from, to))
            File.Move(from, to, overwrite: true);
    }

    /// <summary>
    /// False when this volume or Windows has no POSIX rename (the caller falls back); throws the
    /// refusal otherwise, as an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// the way File.Move would, so the save's retry treats both the same.
    /// </summary>
    static bool TryPosixReplace(string from, string to)
    {
        using var handle = CreateFileW(from, DeleteAccess | SynchronizeAccess, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw Refusal(Marshal.GetLastPInvokeError(), from);

        // FILE_RENAME_INFO: { union { BOOLEAN ReplaceIfExists; DWORD Flags; }; HANDLE RootDirectory;
        // DWORD FileNameLength; WCHAR FileName[1]; }, laid out by pointer size
        var name = Path.GetFullPath(to);
        int rootOffset = IntPtr.Size;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        int nameBytes = name.Length * sizeof(char);
        int size = nameOffset + nameBytes + sizeof(char);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, 0, unchecked((int)(ReplaceIfExists | PosixSemantics)));
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, nameBytes);
            Marshal.Copy(name.ToCharArray(), 0, buffer + nameOffset, name.Length);
            if (SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size)) return true;
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction) return false;
            throw Refusal(error, to);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static Exception Refusal(int error, string path)
    {
        var message = $"{new Win32Exception(error).Message} ({path})";
        return error == ErrorAccessDenied ? new UnauthorizedAccessException(message) : new IOException(message, error);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr info, uint size);
}
