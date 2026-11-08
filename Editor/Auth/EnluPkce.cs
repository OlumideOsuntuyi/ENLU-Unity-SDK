#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace Enlu.Editor
{
    /// <summary>
    /// PKCE (RFC 7636) and OAuth state helpers for the Editor sign-in.
    /// </summary>
    public static class EnluPkce
    {
        /// <summary>A new code verifier: 32 random bytes, base64url (43 characters).</summary>
        public static string CreateVerifier() => Base64Url(RandomBytes(32));

        /// <summary>A new state value: 16 random bytes, base64url (22 characters).</summary>
        public static string CreateState() => Base64Url(RandomBytes(16));

        /// <summary>The S256 code challenge: base64url(SHA-256(ASCII(verifier))).</summary>
        public static string ChallengeFor(string verifier)
        {
            if (string.IsNullOrEmpty(verifier)) throw new ArgumentException("The verifier is empty.", nameof(verifier));
            using (var sha = SHA256.Create())
            {
                return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            }
        }

        /// <summary>Base64url without padding (RFC 4648 §5).</summary>
        public static string Base64Url(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Cryptographically random bytes.</summary>
        public static byte[] RandomBytes(int count)
        {
            var b = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        /// <summary>
        /// Compares two strings in time that depends only on their lengths, so an attacker cannot
        /// learn a secret (such as the state) one character at a time. Null never equals anything.
        /// </summary>
        public static bool ConstantTimeEquals(string? a, string? b)
        {
            if (a == null || b == null) return false;
            var diff = a.Length ^ b.Length;
            var n = Math.Max(a.Length, b.Length);
            for (var i = 0; i < n; i++)
            {
                var ca = i < a.Length ? a[i] : 0;
                var cb = i < b.Length ? b[i] : 0;
                diff |= ca ^ cb;
            }
            return diff == 0;
        }
    }
}
