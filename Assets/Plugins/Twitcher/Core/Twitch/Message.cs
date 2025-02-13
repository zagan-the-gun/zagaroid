using System;
using System.Collections.Generic;
using System.Linq;

namespace Twitcher
{
    /// <summary>
    /// Class representing a message that has been received from Twitch.
    /// </summary>
    public partial class Message
    {
        /// <summary>
        /// Meta data associated with the message, such as indicating if the sender is a mod, subscriber, etc.
        /// </summary>
        public TagInfo Info { get; private set; }
        /// <summary>
        /// The raw message as received from twitch.
        /// </summary>
        public string RawMessage { get; private set; }
        /// <summary>
        /// The specific command this message has (Compare to values in TwitchClient.Commands)
        /// </summary>
        public string Command { get; private set; }
        /// <summary>
        /// Name of the sender of the message.
        /// </summary>
        public string Sender { get; private set; }
        /// <summary>
        /// If PRIVMSG this contains the chat message portion of the message.
        /// </summary>
        public string ChatMessage { get; set; }
        /// <summary>
        /// Parameters provided as part of this message.
        /// </summary>
        public string[] Parameters { get; private set; }

        /// <summary>
        /// Conctructs a new Message object from a received raw message.
        /// </summary>
        /// <param name="message">Raw message received from twitch.</param>
        public Message(string message)
        {
            RawMessage = message;

            if (message.StartsWith("@badge-info"))
            {
                // extract badge info, due to possibility of : appearing in emotes list, we have to work a bit to make sure we get the split right.
                int indexOfUserType = message.IndexOf("user-type="); // This seems to always be the last badge info item.
                int splitPoint = message.IndexOf(':', indexOfUserType);
                string badgeString = message.Substring(0, splitPoint);
                Info = new TagInfo(badgeString);
                message = message.Substring(badgeString.Length);
            }

            // Process the sender.
            int spaceIdx = message.IndexOf(' ');
            string prefix = message.Substring(1, spaceIdx - 1);
            int exclaimIndex = prefix.IndexOf("!", StringComparison.InvariantCultureIgnoreCase);
            if (exclaimIndex >= 0)
            {
                Sender = prefix.Substring(0, exclaimIndex);
            }
            else
            {
                Sender = prefix;
            }
            message = message.Substring(spaceIdx + 1);

            // Process the command.
            spaceIdx = message.IndexOf(' ');
            Command = message.Substring(0, spaceIdx);
            message = message.Substring(spaceIdx + 1);

            // Process remaining parameters
            List<string> msgParams = new List<string>();
            while (!string.IsNullOrEmpty(message))
            {
                if (message.StartsWith(":", StringComparison.InvariantCultureIgnoreCase))
                {
                    msgParams.Add(message.Substring(1));
                    break;
                }
                if (!message.Contains(' '))
                {
                    msgParams.Add(message);
                    break;
                }

                spaceIdx = message.IndexOf(' ');
                msgParams.Add(message.Remove(spaceIdx));
                message = message.Substring(spaceIdx + 1);

            }
            Parameters = msgParams.ToArray();

            // If we've got a PRIVMSG just pull out the chat message parameter for convenience.
            if (Command == TwitchClient.Commands.PRIVMSG && Parameters.Length >= 2)
            {
                ChatMessage = Parameters[1];
            }
        }
    }
}