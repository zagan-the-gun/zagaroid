using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Twitcher
{
    public class ChatDemo : MonoBehaviour
    {
        [Header("Twitch Settings")]
        [SerializeField, Tooltip("Name of channel to join.")]
        private string channelToJoin;
        [SerializeField, Tooltip("OAuth token to login with")]
        private string authToken;

        [Header("Chat Settings")]
        [SerializeField, Tooltip("Display for the chat output")]
        private ChatDisplay chatDisplay;
        [SerializeField, Tooltip("Input for chat to send.")]
        private InputField chatInput;

        private TwitchController twitch;


        private void Awake()
        {
            // Enable full verbose logging of what's going on.
            TwitcherUtil.logging = TwitcherUtil.LoggingMode.Verbose;

            // Create a twitcher instance, and subscribe to the message event.
            twitch = TwitchController.Create(authToken, channelToJoin);
            twitch.Client.onMessageReceived += OnMessageReceived;
        }

        private void OnMessageReceived(Message message)
        {
            // Pass all chat messages (PRIVMSG) to our chat display.
            if (message.Command == TwitchClient.Commands.PRIVMSG)
            {
                chatDisplay.DisplayMessage(message);
            }
        }

        public void ButtonEvent_Send()
        {
            // We have to send our own messages to the display ourselves, this is because
            // messages we send will not come back through the onMessageReceived event.
            if (!string.IsNullOrEmpty(chatInput.text))
            {
                chatDisplay.DisplayMessage(twitch.Client.Username, chatInput.text);
                twitch.Client.SendPrivMessage(chatInput.text);
                chatInput.text = "";
            }
        }
    }
}