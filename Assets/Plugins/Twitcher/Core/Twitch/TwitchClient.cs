using System;
using System.Net.Sockets;
using UnityEngine;
using System.Collections.Generic;

namespace Twitcher
{
    public class TwitchClient : TcpMessageClient
    {
        /// <summary>
        /// Message types through twitch, use to compare against Message.Command
        /// </summary>
        public static class Commands
        {
            public const string PRIVMSG = "PRIVMSG";
            public const string PING = "PING";
            public const string PART = "PART";
            public const string JOIN = "JOIN";
            public const string NICK = "NICK";
            public const string PASS = "PASS";
            public const string USER = "USER";
            public const string NOTICE = "NOTICE";
            public const string USERNAME_REPORT = "001";
        }

        private const string TWITCH_SENDER = "tmi.twitch.tv";
        private const string COMMAND_PREFIX = "!";

        public delegate void MessageDelegate(Message message);

        public string Username { get; private set; }
        private string oauth;
        private string currentRoom;
        private Dictionary<string, MessageDelegate> commandHandlers;
        

        /// <summary>
        /// Event called any time a message is received (with the exception of PING messages, which are handled internally)
        /// </summary>
        public event MessageDelegate onMessageReceived;


        /// <summary>
        /// Creates a new instance of a TwitchClient.
        /// </summary>
        /// <param name="host">Host to connect to.</param>
        /// <param name="port">Port to connect to.</param>
        public TwitchClient(string host, int port)
            : base(host, port, new RateLimiter(30, 1.5f))
        {
            onRawMessageReceived += ProcessRawMessage;
            commandHandlers = new Dictionary<string, MessageDelegate>();
        }

        /// <summary>
        /// Overrides the connect action, attempting on connection to login if the username and password are already provided.
        /// </summary>
        protected override void OnConnect()
        {
            base.OnConnect();
            try
            {
                if (!string.IsNullOrEmpty(oauth))
                {
                    if(TwitcherUtil.LogVerbose)
                        Debug.Log("User/oauth already provided, attempting immediate login.");

                    Login(oauth);
                }
            }
            catch (SocketException lException)
            {
                if (TwitcherUtil.LogErrors)
                    Debug.LogException(lException);
            }
        }

        /// <summary>
        /// Closes the current connection, leaving the current room if there was one.
        /// </summary>
        public override void Close()
        {
            if (TwitcherUtil.LogVerbose)
                Debug.Log("Closing TwitchClient");

            Leave();

            base.Close();
        }

        /// <summary>
        /// Attempt to login to twitch.
        /// </summary>
        /// <param name="username">Username to login as.</param>
        /// <param name="oauth">OAuth token for the given username.</param>
        public void Login(string oauth)
        {
            if (oauth.StartsWith("oauth:"))
            {
                this.oauth = oauth;
            }
            else
            {
                this.oauth = $"oauth:{oauth}";
            }

            if (TwitcherUtil.LogVerbose)
                Debug.Log($"Attempting login with token = {oauth}");

            Send(new string[]{
                $"{Commands.PASS} {oauth}\r\n",
                $"{Commands.NICK} unknown\r\n",
            });
        }

        /// <summary>
        /// Leave the current room if you're in one.
        /// </summary>
        public void Leave()
        {
            if (!string.IsNullOrEmpty(currentRoom))
            {
                if (TwitcherUtil.LogVerbose)
                    Debug.Log($"Leaving current room: {currentRoom}");

                Send($"{Commands.PART} #{currentRoom}");
                currentRoom = null;
            }
        }

        /// <summary>
        /// Join a new room, will leave current room if there is one.
        /// </summary>
        /// <param name="room">Room to join.</param>
        public void Join(string room)
        {
            // Just in case we're already in a room.
            Leave();
            room = room.ToLower();

            if (TwitcherUtil.LogVerbose)
                Debug.Log($"Attempting to join room: {room}");

            if (!room.StartsWith("#", StringComparison.InvariantCultureIgnoreCase))
            {
                Send($"{Commands.JOIN} #{room}");
                currentRoom = room;
            }
            else
            {
                Send($"{Commands.JOIN} {room}");
                currentRoom = room.Substring(1);
            }
            Send("CAP REQ :twitch.tv/tags\r\n");
        }

        /// <summary>
        /// Send a PRIVMSG message to twitch (standard chat message)
        /// </summary>
        /// <param name="message">Message to send.</param>
        public void SendPrivMessage(string message)
        {
            if (TwitcherUtil.LogVerbose)
                Debug.Log($"Sending chat message: {message}");

            Send($"{Commands.PRIVMSG} #{currentRoom} : {message}");
        }

        /// <summary>
        /// Processes a raw message, calling onMessageReceived with any message except the PING message.
        /// </summary>
        /// <param name="rawMessage">Raw message.</param>
        private void ProcessRawMessage(string rawMessage)
        {
            try
            {
                // Special handle for ping messages to respond and avoid building a pointless Message object.
                if (rawMessage.StartsWith(Commands.PING, StringComparison.InvariantCultureIgnoreCase))
                {
                    Send($"PONG {rawMessage.Substring(5)}\r\n");
                }
                else
                {
                    Message msg = new Message(rawMessage);
                    if (msg.Sender == TWITCH_SENDER)
                    {
                        if (msg.Command == Commands.USERNAME_REPORT)
                        {
                            Username = msg.Parameters[0];
                        }
                        else if (msg.Command == Commands.NOTICE)
                        {
                            if (TwitcherUtil.LogErrors)
                                Debug.LogError(msg.RawMessage);
                        }
                    }
                    if (TwitcherUtil.LogVerbose)
                        Debug.Log($"Message Received: {rawMessage}");

                    onMessageReceived?.Invoke(msg);
                    CheckMessageForCustomCommands(msg);
                }
            }
            catch (Exception exception)
            {
                if (TwitcherUtil.LogErrors)
                {
                    Debug.LogError($"Exception occurred when processing rawMessage (see next log for exception): {rawMessage}");
                    Debug.LogException(exception);
                }
            }
        }


        public void AddCommandListener(string command, MessageDelegate handler)
        {
            if (commandHandlers.ContainsKey(command))
            {
                commandHandlers[command] += handler;
            }
            else
            {
                commandHandlers.Add(command, handler);
            }
        }

        public void RemoveCommandListener(string command, MessageDelegate handler)
        {
            if (commandHandlers.ContainsKey(command))
            {
                commandHandlers[command] -= handler;
            }
        }

        public void ClearCommandListener(string command)
        {
            commandHandlers.Remove(command);
        }
        
        private void CheckMessageForCustomCommands(Message message)
        {
            if (message.Command == Commands.PRIVMSG &&
                message.ChatMessage.StartsWith(COMMAND_PREFIX))
            {
                string customCommand = ExtractCustomCommand(message.ChatMessage);
                if (commandHandlers.ContainsKey(customCommand))
                {
                    commandHandlers[customCommand]?.Invoke(message);
                }
            }
        }

        private string ExtractCustomCommand(string chatMessage)
        {
            int firstSpace = chatMessage.IndexOf(' ');
            if (firstSpace == -1)
            {
                return chatMessage.Substring(1);
            }
            return chatMessage.Substring(1,firstSpace);
        }
    }
}