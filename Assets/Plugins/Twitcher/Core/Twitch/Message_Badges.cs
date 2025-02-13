using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Twitcher
{
    /// <summary>
    /// Class representing a message that has been received from Twitch.
    /// </summary>
    public partial class Message
    {
        /// <summary>
        /// Badge data as provided by twitch, badges contain a badge id, and a version.
        /// </summary>
        public struct Badge
        {
            public const string PREDICTION_BADGE = "prediction";
            public const string PREDICTION_BLUE = "blue-1";
            
            public string badge;
            public int version;

            public Badge(string fromString)
            {
                int slash = fromString.IndexOf('/');
                if (slash == -1)
                {
                    badge = fromString;
                    version = 0;
                }
                else
                {
                    badge = fromString.Substring(0, slash);
                    if (badge.Equals(PREDICTION_BADGE))
                    {
                        // Prediction versions are.... not integers like the rest of them. Thanks twitch.
                        version = fromString.Contains(PREDICTION_BLUE) ? 1 : 2;
                    }
                    else
                    {
                        // Standard version data.
                        version = int.Parse(fromString.Substring(slash + 1));
                    }
                }
            }
        }
    }
}