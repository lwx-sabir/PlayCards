using System;
using System.Security.Cryptography; 

namespace Khela.Common.Auth
{
    public static class AuthHelpers
    {
        private const string EmailDomain = "khela.game";

        public static (string Email, string Username, string Password) GenerateDeviceUser(string? deviceId = null)
        {
            var id = string.IsNullOrWhiteSpace(deviceId) ? Guid.NewGuid().ToString("N") : deviceId;
            var userName = $"player_{id[..8]}";
            var email = $"{userName}@{EmailDomain}";
            var password = GeneratePassword();
            return (email, userName, password);
        }

        private static string GeneratePassword()
        {
            // 16 chars URL-safe
            Span<byte> bytes = stackalloc byte[12];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes)
                .Replace("+", string.Empty)
                .Replace("/", string.Empty)
                .TrimEnd('=')
                .PadRight(16, 'x')
                .Substring(0, 16);
        }
    }
}
