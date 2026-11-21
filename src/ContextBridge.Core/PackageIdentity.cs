using System.Security.Cryptography;
using System.Text;

namespace ContextBridge.Core;

/// <summary>Identity of the sparse package (must match package/AppxManifest.xml.in).</summary>
public static class PackageIdentity
{
    public const string Name = "ContextBridge";
    public const string Publisher = "CN=ContextBridge, OID.2.25.311729368913984317654407730594956997722=1";

    public static string FamilyName => $"{Name}_{PublisherId(Publisher)}";

    /// <summary>
    /// Computes the 13-character publisher id used in package family names:
    /// first 8 bytes of SHA-256(UTF-16LE publisher), as base32 with the alphabet below.
    /// </summary>
    public static string PublisherId(string publisher)
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        byte[] hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | hash[i];
        var sb = new StringBuilder(13);
        // 64 bits → 13 groups of 5 bits (the last group is padded with a zero bit).
        for (int i = 0; i < 13; i++)
        {
            int shift = 59 - 5 * i;
            int idx = shift >= 0 ? (int)((v >> shift) & 0x1F) : (int)((v << 1) & 0x1F);
            sb.Append(alphabet[idx]);
        }
        return sb.ToString();
    }

    /// <summary>"1.2.3" → "1.2.3.0" (package versions always have four parts).</summary>
    public static string ToPackageVersion(string version)
    {
        var parts = version.Split('.', '-', '+').Take(4).Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
        while (parts.Count < 4) parts.Add(0);
        return string.Join('.', parts);
    }

    /// <summary>Extracts the version from a package full name: Name_Version_Arch_ResourceId_PublisherId.</summary>
    public static string? VersionFromFullName(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length >= 2 ? parts[1] : null;
    }
}
