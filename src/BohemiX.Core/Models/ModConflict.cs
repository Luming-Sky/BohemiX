using System.Security.Cryptography;
using System.Text;

namespace BohemiX.Core.Models;

public sealed record ModConflict(
    string NormalizedVirtualPath,
    IReadOnlyList<string> ModIds,
    IReadOnlyList<string> LoadOrderModIds,
    string WinningModId)
{
    public string Fingerprint
    {
        get
        {
            var source = string.Join(
                "\n",
                NormalizedVirtualPath,
                string.Join(">", LoadOrderModIds),
                WinningModId);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        }
    }
}
