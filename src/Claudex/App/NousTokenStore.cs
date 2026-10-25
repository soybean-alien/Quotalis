using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Claudex.Core.Diagnostics;
using Claudex.Core.Hermes;

namespace Claudex;

/// <summary>
/// Claudex's Nous Portal sign-in, encrypted with Windows' per-user data protection (DPAPI) in
/// <see cref="AppPaths.NousSignIn"/>: only this Windows account on this PC can read it. The tokens can run paid models
/// (Nous grants <c>inference:invoke</c>), so they are never logged, never in settings, a build, an installer or the
/// repository. Credential Manager is not used because Nous's tokens are longer than its 2,560-byte limit.
/// The refresh token is single-use: the rotated one is saved before it is used.
/// </summary>
internal sealed class NousTokenStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Claudex Nous sign-in");

    public NousTokens? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path)));
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new NousTokens(r.GetProperty("a").GetString()!, r.GetProperty("r").GetString()!,
                DateTimeOffset.FromUnixTimeSeconds(r.GetProperty("e").GetInt64()));
        }
        catch (Exception ex)
        {
            Log.Warn("nous", "Saved Nous sign-in could not be read", new { error = ex.GetType().Name });
            return null;
        }
    }

    public void Save(NousTokens tokens)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["a"] = tokens.AccessToken,
            ["r"] = tokens.RefreshToken,
            ["e"] = tokens.ExpiresAt.ToUnixTimeSeconds(),
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Protect(Encoding.UTF8.GetBytes(json)));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn("nous", "Nous sign-in file not deleted", new { error = ex.GetType().Name });
        }
    }

    private static byte[] Protect(byte[] data) => Crypt(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Crypt(data, protect: false);

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DATA_BLOB { cbData = data.Length, pbData = input.AddrOfPinnedObject() };
            var entropyBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = entropy.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok)
            {
                throw new IOException($"Windows data protection failed (error {Marshal.GetLastWin32Error()}).");
            }

            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            input.Free();
            entropy.Free();
        }
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
