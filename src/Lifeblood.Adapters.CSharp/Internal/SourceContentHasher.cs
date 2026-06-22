using System.Security.Cryptography;
using System.Text;

namespace Lifeblood.Adapters.CSharp.Internal;

internal static class SourceContentHasher
{
    public static string HashText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
