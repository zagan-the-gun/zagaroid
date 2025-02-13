using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Twitcher
{
    public partial class Message
    {
        [Flags]
        public enum Permission
        {
            Viewer, Subscriber, Admin, Moderator, Broadcaster, Staff
        }

        /// <summary>
        /// Tag info as provided by twitch, contains information associated with 
        /// a message such as user flags, badges, timestamp, chat colour, etc.
        /// </summary>
        public struct TagInfo
        {
            public string displayName;
            public Badge[] badges;
            public long userId;
            public Permission permissions;
            [Obsolete("broadcaster flag is deprecated and will be removed in future versions, utilize the permissions flags instead.")]
            public bool broadcaster;
            [Obsolete("admin flag is deprecated and will be removed in future versions, utilize the permissions flags instead.")]
            public bool admin;
            [Obsolete("subscriber flag is deprecated and will be removed in future versions, utilize the permissions flags instead.")]
            public bool subscriber;
            [Obsolete("moderator flag is deprecated and will be removed in future versions, utilize the permissions flags instead.")]
            public bool moderator;
            [Obsolete("staff flag is deprecated and will be removed in future versions, utilize the permissions flags instead.")]
            public bool staff;
            public string colourHex;
            public Color colour;
            public long timestamp;
            public int bits;
            public string id;
            public Dictionary<string, List<Vector2Int>> emotes;

            public TagInfo(string info)
            {
                // Rip out the various information that we need, based flags like admin, mod, etc on the presence of the badges.
                // as the dedicated flags for them in the tag info are obsolete and may not appear in the future.
                string badgeString = ExtractString(info, "badges");
                permissions = Permission.Viewer;
                if (string.IsNullOrEmpty(badgeString))
                {
                    badges = null;
                    admin = staff = moderator = subscriber = broadcaster = false;
                }
                else
                {
                    badges = badgeString.Split(',').Select(x => new Badge(x)).ToArray();
                    
                    // Old flags, deprecated, will be removed in future update, but maintaining for the time being.
                    admin = badgeString.Contains("admin");
                    broadcaster = badgeString.Contains("broadcaster");
                    staff = badgeString.Contains("staff");
                    moderator = badgeString.Contains("moderator");
                    subscriber = badgeString.Contains("subscriber");

                    // Set the new permissions flag.
                    if (admin) permissions |= Permission.Admin;
                    if (broadcaster) permissions |= Permission.Broadcaster;
                    if (staff) permissions |= Permission.Staff;
                    if (moderator) permissions |= Permission.Moderator;
                    if (subscriber) permissions |= Permission.Subscriber;
                }

                // Then let's just pull out the extra data.
                colourHex = ExtractString(info, "color", "#FFFFFF");
                colour = ConvertHexToColor(colourHex);
                displayName = ExtractString(info, "display-name");
                timestamp = ExtractLong(info, "tmi-sent-ts");
                userId = ExtractLong(info, "user-id");
                bits = ExtractInt(info, "bits");
                id = ExtractString(info, "id");

                // Now process emotes from the message, based on twitches docs this emote string will be formatted as below.
                // <emote ID>:<first index>-<last index>,<another first index>-<another last index>/<another emote.....
                emotes = new Dictionary<string, List<Vector2Int>>();
                string emoteString = ExtractString(info, "emotes");
                if (!string.IsNullOrEmpty(emoteString))
                {
                    string[] parts = emoteString.Split('/');
                    foreach (string emoteEntry in parts)
                    {
                        int idEnd = emoteEntry.IndexOf(':');
                        string id = emoteEntry.Substring(0, idEnd);
                        string[] posParts = emoteEntry.Substring(idEnd + 1).Split(',');
                        emotes.Add(id, posParts.Select(x => GetEmotePosition(x)).ToList());
                    }
                }
            }

            /// <summary>
            /// Checks to see if the tag info contains a badge.
            /// </summary>
            /// <param name="badgeId">Id of the badge to check.</param>
            /// <returns>True if matching badge found, false otherwise.</returns>
            public bool HasBadge(string badgeId)
            {
                return badges.Any(x => x.badge == badgeId);
            }

            /// <summary>
            /// Gets the integer version data that is specified for badges.
            /// </summary>
            /// <param name="badgeId">Id of the badge to check.</param>
            /// <returns>Associated version data for provided badge if exists, -1 otherwise.</returns>
            public int GetBadgeVersion(string badgeId)
            {
                foreach (var badge in badges)
                {
                    if (badge.badge == badgeId)
                    {
                        return badge.version;
                    }
                }

                return -1;
            }

            /// <summary>
            /// Check to see if this message has a specific permission.
            /// </summary>
            /// <param name="permission"></param>
            /// <returns>True if permissions contains one or more of the provided permission flags, False otherwise.</returns>
            public bool HasPermission(Permission permission)
            {
                return ((permissions & permission) > 0);
            }

            private static Vector2Int GetEmotePosition(string posString)
            {
                int splitIndex = posString.IndexOf('-');
                return new Vector2Int(int.Parse(posString.Substring(0, splitIndex)), int.Parse(posString.Substring(splitIndex + 1)));
            }
            private static string ExtractString(string source, string key, string defaultValue = "")
            {
                int index = source.IndexOf($";{key}", 0, StringComparison.InvariantCultureIgnoreCase);
                if (index != -1)
                {
                    index += 1; // account for addition of ';' in the search.
                    int valueStart = source.IndexOf('=', index) + 1;
                    int valueEnd = source.IndexOf(';', valueStart);
                    return source.Substring(valueStart, (valueEnd - valueStart));
                }
                else
                {
                    return defaultValue;
                }
            }
            private static long ExtractLong(string source, string key)
            {
                string stringValue = ExtractString(source, key);
                if (string.IsNullOrEmpty(stringValue))
                {
                    return 0;
                }
                else
                {
                    return long.Parse(stringValue);
                }
            }
            private static int ExtractInt(string source, string key)
            {
                string stringValue = ExtractString(source, key);
                if (string.IsNullOrEmpty(stringValue))
                {
                    return 0;
                }
                else
                {
                    return int.Parse(stringValue);
                }
            }
            
            /// <summary>
            /// Utility method for converting the hex colour provided by twitch into a Unity Color format.
            /// </summary>
            /// <param name="hexString">Hex string to convert.</param>
            /// <returns>The hex value as Color type.</returns>
            public static Color ConvertHexToColor(string hexString)
            {
                if (string.IsNullOrEmpty(hexString))
                {
                    return Color.white;
                }

                if (hexString.IndexOf('#') != -1)
                {
                    hexString = hexString.Replace("#", "");
                }

                int r = int.Parse(hexString.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                int g = int.Parse(hexString.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                int b = int.Parse(hexString.Substring(4, 2), NumberStyles.AllowHexSpecifier);

                return new Color(r / 255.0f, g / 255.0f, b / 255.0f);
            }
        }
    }
}