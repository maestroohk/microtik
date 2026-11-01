using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Kibo.Web.Security;

public class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("Kibo.RouterCredentials.v1");

    public string Protect(string plainText) => protector.Protect(plainText);

    public string Unprotect(string protectedText) => protector.Unprotect(protectedText);

    public static string NewSecret(int bytes = 18) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace("+", "").Replace("/", "").Replace("=", "");

    public static string NewCode(int length, string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789")
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }
}
