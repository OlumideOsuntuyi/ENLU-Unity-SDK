#nullable enable
using System;
using System.Text;

namespace Enlu
{
    /// <summary>Helpers for identifiers the API validates.</summary>
    public static class EnluIds
    {
        /// <summary>Longest custom id the API accepts.</summary>
        public const int MaxCustomIdLength = 128;

        /// <summary>Prefix used for device-id logins, so they never collide with your own custom ids.</summary>
        public const string DevicePrefix = "device:";

        /// <summary>True when the text is a valid custom id (letters, digits and <c>_.:@-</c>, 1 to 128).</summary>
        public static bool IsValidCustomId(string? id)
        {
            if (string.IsNullOrEmpty(id) || id!.Length > MaxCustomIdLength) return false;
            foreach (var c in id) if (!IsCustomIdChar(c)) return false;
            return true;
        }

        /// <summary>
        /// Turns a raw device identifier into a valid custom id: <c>device:</c> plus the identifier with
        /// any other characters dropped, cut to 128 characters. Returns null when nothing usable is left.
        /// </summary>
        public static string? DeviceCustomId(string? rawDeviceId)
        {
            if (string.IsNullOrWhiteSpace(rawDeviceId)) return null;
            var sb = new StringBuilder(DevicePrefix, MaxCustomIdLength);
            foreach (var c in rawDeviceId!)
            {
                if (sb.Length >= MaxCustomIdLength) break;
                if (IsCustomIdChar(c)) sb.Append(c);
            }
            return sb.Length > DevicePrefix.Length ? sb.ToString() : null;
        }

        /// <summary>True for a valid data key (letters, digits and <c>_.:-</c>, 1 to 128).</summary>
        public static bool IsValidDataKey(string? key)
        {
            if (string.IsNullOrEmpty(key) || key!.Length > 128) return false;
            foreach (var c in key)
            {
                if (!(IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == ':' || c == '-')) return false;
            }
            return true;
        }

        /// <summary>True for a valid statistic or event name (starts with a letter; letters, digits, <c>_.-</c>; up to 64).</summary>
        public static bool IsValidName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name!.Length > 64) return false;
            if (!((name[0] >= 'a' && name[0] <= 'z') || (name[0] >= 'A' && name[0] <= 'Z'))) return false;
            foreach (var c in name)
            {
                if (!(IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == '-')) return false;
            }
            return true;
        }

        private static bool IsCustomIdChar(char c) =>
            IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == ':' || c == '@' || c == '-';

        private static bool IsAsciiLetterOrDigit(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
    }
}
