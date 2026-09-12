using System;
using System.Security.Cryptography;

namespace NetworkPlugin.Network.Security;

public static class SecurityUtils
{
    public static string GenerateSecureKey(int length = 16)
    {
        byte[] bytes = new byte[length];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static string GenerateNonce()
    {
        byte[] bytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static string GenerateReconnectToken()
    {
        byte[] bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static string MaskToken(string? token)
    {
        if (token == null)
        {
            return "[null]";
        }
        if (string.IsNullOrEmpty(token))
        {
            return "[empty]";
        }
        if (token.Length <= 8)
        {
            return "****";
        }

        return $"{token[..4]}***{token[^4..]}";
    }

    public static string SanitizeLogString(string? input, int maxLength = 256)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        if (input.Length <= maxLength)
        {
            return input;
        }

        return input.Substring(0, maxLength) + "...[TRUNCATED]";
    }
}
