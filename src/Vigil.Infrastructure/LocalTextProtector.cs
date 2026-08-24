using System.Security.Cryptography;
using System.Text;

namespace Vigil.Infrastructure;

internal static class LocalTextProtector
{
    private const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy = "Vigil.Windows.DatabaseText.v1"u8.ToArray();
    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            try { return Prefix + Convert.ToBase64String(encrypted); }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static string Unprotect(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        try
        {
            var encrypted = Convert.FromBase64String(value[Prefix.Length..]);
            try
            {
                var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        catch (FormatException) { throw new CryptographicException("数据库中的加密文本格式无效。"); }
    }
}
