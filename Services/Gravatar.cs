using System;
using System.Security.Cryptography;
using System.Text;

namespace AlcoRats.Services;

/// <summary>Builds Gravatar avatar URLs from an email (same scheme as the bot).</summary>
public static class Gravatar
{
    public static string BuildAvatarUrl(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.IndexOf('@') < 0)
            return string.Empty;

        var normalized = email.Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
        return $"https://www.gravatar.com/avatar/{hash}?d=identicon&s=200";
    }
}
