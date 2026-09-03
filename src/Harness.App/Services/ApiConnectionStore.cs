using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Harness.Providers.Api;

namespace Harness.App.Services;

public sealed record SavedApiConnection(ApiConnection Connection, IReadOnlyList<ApiModelConfiguration> Models);

/// <summary>Only public connection metadata is serialized. Secrets are generic OS credentials.</summary>
public sealed class ApiConnectionStore
{
    private static readonly string CatalogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Harness", "api-connections.json");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<IReadOnlyList<SavedApiConnection>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try { return await ReadAsync(cancellationToken); }
        finally { Gate.Release(); }
    }

    private static async Task<List<SavedApiConnection>> ReadAsync(CancellationToken cancellationToken) => File.Exists(CatalogPath)
        ? JsonSerializer.Deserialize<List<SavedApiConnection>>(await File.ReadAllTextAsync(CatalogPath, cancellationToken))
            ?? throw new IOException("The API connection catalog is invalid.") : [];

    public async Task SaveAsync(SavedApiConnection connection, string? replacementKey, CancellationToken cancellationToken = default)
    {
        _ = connection.Connection.BaseUri;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var connections = await ReadAsync(cancellationToken);
            if (replacementKey is not null) WriteCredential(connection.Connection.Id, replacementKey);
            connections.RemoveAll(item => item.Connection.Id == connection.Connection.Id);
            connections.Add(connection);
            await WriteAsync(connections, cancellationToken);
        }
        finally { Gate.Release(); }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var connections = await ReadAsync(cancellationToken);
            connections.RemoveAll(item => item.Connection.Id == id);
            await WriteAsync(connections, cancellationToken);
            if (OperatingSystem.IsWindows() && !CredDelete("Harness/API/" + id, 1, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Connection removed, but its OS credential could not be removed.");
        }
        finally { Gate.Release(); }
    }

    private static async Task WriteAsync(List<SavedApiConnection> connections, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        var staging = CatalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(connections, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(staging, CatalogPath, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public static string ReadCredential(string id)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Persistent API credentials currently require Windows Credential Manager.");
        if (!CredRead("Harness/API/" + id, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return "";
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the API credential from Windows Credential Manager.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)) ?? "";
        }
        finally { CredFree(pointer); }
    }

    private static void WriteCredential(string id, string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Persistent API credentials currently require Windows Credential Manager.");
        if (secret.Length * 2 > 2560) throw new ArgumentException("API key exceeds the OS credential size limit.");
        var pointer = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new Credential { Type = 1, TargetName = "Harness/API/" + id,
                Blob = pointer, BlobSize = (uint)(secret.Length * 2), Persist = 2, UserName = "Harness" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the API credential in Windows Credential Manager.");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}
