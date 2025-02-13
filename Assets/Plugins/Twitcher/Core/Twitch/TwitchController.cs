using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Twitcher
{
    /*
    Token generation: https://twitchapps.com/tmi/
    */
    /// <summary>
    /// Class that manages a connection to twitch.
    /// Allows the triggering of chat votes, and provides access to the TwitchClient.
    /// </summary>
    public class TwitchController : MonoBehaviour
    {
        /// <summary>
        /// Gets the twitch client associated with this Twitcher.
        /// </summary>
        /// <value>TwitchClient.</value>
        public TwitchClient Client { get; private set; }

        private List<Vote> activeVotes;


        /// <summary>
        /// Create a new GameObject with a Twitcher script attached to it.
        /// This will also initialize the connection to twitch, but will not login.
        /// </summary>
        /// <returns>The created twitcher.</returns>
        public static TwitchController Create()
        {
            TwitchController twitch = new GameObject("Twitcher").AddComponent<TwitchController>();
            twitch.Client = new TwitchClient("irc.chat.twitch.tv", 80);
            twitch.activeVotes = new List<Vote>();

            return twitch;
        }

        /// <summary>
        /// Create a new GameObject with a Twitcher script attached to it.
        /// This will also initialize the connection to twitch, attempt to
        /// login with the OAuth token provided, and if specified join a room.
        /// </summary>
        /// <param name="oAuthToken">OAuth token for the desired user to login as.</param>
        /// <param name="joinRoom">Room to try and join.</param>
        /// <returns>The created twitcher</returns>
        public static TwitchController Create(string oAuthToken, string joinRoom)
        {
            TwitchController twitch = Create();
            twitch.Client.Login(oAuthToken);
            if (!string.IsNullOrEmpty(joinRoom))
            {
                twitch.Client.Join(joinRoom);
            }
            return twitch;
        }

        private void OnDestroy()
        {
            if (Client != null)
            {
                Client.Close();
            }
        }

        private void Update()
        {
            if (Client != null && Client.Connected)
            {
                /*
                Since the clients receives messages in a way that may not happen on the main thread,
                we trigger it's update which will process received messages here to ensure that callbacks
                all occur on the main thread to avoid any thread access issues.
                */
                Client.ProcessMessages();

                // Update all votes, and end any votes that have now finished.
                for (int i = activeVotes.Count - 1; i >= 0; i--)
                {
                    if (activeVotes[i].Progress >= 1 || activeVotes[i].ForceEnd)
                    {
                        activeVotes[i].ProcessEndOfVote();
                        activeVotes.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// Start a vote with this twitcher.
        /// </summary>
        /// <param name="vote">Vote to start.</param>
        public void StartVote(Vote vote)
        {
            if (vote.Twitcher != null)
            {
                if (TwitcherUtil.LogErrors)
                {
                    Debug.LogError("Vote still running on a Twitcher instance, vote cannot be started again until it has ended.");
                }
            }
            else
            {
                activeVotes.Add(vote);
                vote.StartVote(this);
            }
        }

        /// <summary>
        /// Ends all votes immediately regardless of their progress state.
        /// </summary>
        /// <param name="broadcastResults">True to call vote complete events like normal, false to end votes silently.</param>
        public void ClearAllVotes(bool broadcastResults)
        {
            if (broadcastResults)
            {
                for (int i = activeVotes.Count - 1; i >= 0; i--)
                {
                    activeVotes[i].ProcessEndOfVote();
                }
            }
            activeVotes.Clear();
        }
    }

}