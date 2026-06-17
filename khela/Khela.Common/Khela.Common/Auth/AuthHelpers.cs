using System;
using System.Security.Cryptography;
using System.Text;

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
            var password = DerivePassword(id);
            return (email, userName, password);
        }

        // DETERMINISTIC from the device id: the same device always derives the same password, so the guest
        // can re-login even after the local save is cleared/lost — instead of a fresh random password that no
        // longer matches the already-registered account (which causes a permanent 401). The "Aa1" prefix
        // guarantees the server policy (>=1 uppercase, >=1 lowercase, >=1 digit); a raw base64 chunk could
        // randomly miss a digit and be rejected at register. Acceptable for non-cashable guest accounts.
        private static string DerivePassword(string deviceId)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes("khela-guest-pw-v1|" + deviceId));
            var b64 = Convert.ToBase64String(hash)
                .Replace("+", string.Empty)
                .Replace("/", string.Empty)
                .TrimEnd('=');
            return "Aa1" + b64.Substring(0, 13);
        }
    }
}
