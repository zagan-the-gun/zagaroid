using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;

namespace Twitcher
{
    /// <summary>
    /// Base class for various types of chat votes.
    /// </summary>
    public abstract class Vote
    {
        public delegate void VoteDelegate(Vote vote);
        public delegate void VoteResultsDelegate(Vote vote, List<Result> results);
        public delegate void VoteUpdateDelegate(string voter, string voteCurrent, string votePrevious);

        /// <summary>
        /// Event called any time a vote is started.
        /// </summary>
        public static event VoteDelegate onVoteStarted;
        /// <summary>
        /// Event called any time a vote ends, provides results of the vote.
        /// </summary>
        public static event VoteResultsDelegate onVoteEnded;
        /// <summary>
        /// Event triggered when a vote is made, or changed on this paricular vote.
        /// </summary>
        public event VoteUpdateDelegate onVoteMadeOrChanged;

        private VoteResultsDelegate onComplete;
        private Dictionary<string, string> trackedVotes;
        internal TwitchController Twitcher { get; private set; }
        private string votePrefix;
        internal bool ForceEnd { get; private set; }

        /// <summary>
        /// Gets the available choices for this vote.
        /// </summary>
        /// <value>The options.</value>
        public string[] Options { get; private set; }

        /// <summary>
        /// Current results of the vote, updated throughout the lifetime of the vote.
        /// </summary>
        public List<Result> results;


        /// <summary>
        /// Indicates the progress of the vote as a [0-1] value. Based on duration and elapsed time.
        /// </summary>
        public abstract float Progress { get; }

        /// <summary>
        /// Creates an instance of the Vote class.
        /// </summary>
        /// <param name="twitcher">Twitcher that this vote is assigned to.</param>
        /// <param name="votePrefix">Vote prefix required in front of any chat vote.</param>
        /// <param name="options">Options available to be voted on.</param>
        /// <param name="onComplete">Callback to be provided the results when the vote ends.</param>
        protected Vote(string votePrefix, string[] options, VoteResultsDelegate onComplete)
        {
            this.onComplete = onComplete;
            this.votePrefix = votePrefix;
            trackedVotes = new Dictionary<string, string>();

            // Force all options to lower case.
            Options = options.Select(x => x.ToLower()).ToArray();
        }

        /// <summary>
        /// Called internally to register the twitcher with the vote, and start the vote proper.
        /// </summary>
        /// <param name="twitcher">Twitcher this vote is being assigned to.</param>
        internal void StartVote(TwitchController twitcher)
        {
            // Ensure we start with clean results.
            trackedVotes.Clear();
            results = Options.Select(x => new Result() { option = x, voteCount = 0 }).ToList();

            // Set and register with our twitcher.
            this.Twitcher = twitcher;
            twitcher.Client.onMessageReceived += MessageCallback;

            if (TwitcherUtil.LogVerbose)
                Debug.Log($"Starting vote with options: {string.Join(",", Options)}");

            // Trigger the custom vote start logic, then broadcast the vote start.
            OnVoteStart();
            onVoteStarted?.Invoke(this);
        }

        /// <summary>
        /// Custom logic to run per vote-type when a vote is started.
        /// </summary>
        protected virtual void OnVoteStart() { }

        /// <summary>
        /// Called when the vote ends.
        /// </summary>
        internal void ProcessEndOfVote()
        {
            // Unsubcribe and null our twitcher so that we can be reused on another if necessary.
            Twitcher.Client.onMessageReceived -= MessageCallback;
            Twitcher = null;

            results.Sort((Result a, Result b) => { return b.voteCount.CompareTo(a.voteCount); });

            if (TwitcherUtil.LogVerbose)
                Debug.Log($"Vote complete! {string.Join(",", results)}");

            onComplete?.Invoke(this, results);

            onVoteEnded?.Invoke(this, results);
        }

        /// <summary>
        /// Callback for the assigned twitchers incoming messages.
        /// </summary>
        /// <param name="message">Message received by the twitcher.</param>
        private void MessageCallback(Message message)
        {
            if (message.Command != TwitchClient.Commands.PRIVMSG)
                return;

            if (!message.ChatMessage.StartsWith(votePrefix, StringComparison.InvariantCultureIgnoreCase))
                return;

            string choice = message.ChatMessage.ToLower().Substring(votePrefix.Length);
            if (Options.Contains(choice))
            {
                string prevVote = null;
                if (trackedVotes.ContainsKey(message.Sender))
                {
                    // Reset the existing vote, decreasing rolling result appropriately.
                    prevVote = trackedVotes[message.Sender];
                    EditResultCount(trackedVotes[message.Sender], -1);
                    trackedVotes[message.Sender] = choice;
                }
                else
                {
                    trackedVotes.Add(message.Sender, choice);
                }

                //Increase new vote.
                EditResultCount(choice, 1);

                onVoteMadeOrChanged?.Invoke(message.Sender, choice, prevVote);
            }
        }

        /// <summary>
        /// Called when votes received to edit the results count.
        /// </summary>
        /// <param name="option">Option to amend.</param>
        /// <param name="delta">Change in result count for this option.</param>
        private void EditResultCount(string option, int delta)
        {
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].option == option)
                {
                    results[i] = new Result()
                    {
                        option = option,
                        voteCount = results[i].voteCount + delta
                    };
                }
            }
        }

        /// <summary>
        /// Gets the current tracking vote count for a given option.
        /// </summary>
        /// <returns>The current vote count if option found, -1 otherwise.</returns>
        /// <param name="option">Option.</param>
        public int GetCurrentVoteCount(string option)
        {
            option = option.ToLower();
            foreach (Result result in results)
            {
                if (result.option == option)
                {
                    return result.voteCount;
                }
            }

            return -1;
        }

        /// <summary>
        /// Marks this vote as complete, regardless of it's current progress.
        /// </summary>
        public void EndVote()
        {
            ForceEnd = true;
        }


        /// <summary>
        /// Struct to repreesnt a vote option and its current vote count.
        /// </summary>
        public struct Result
        {
            public string option;
            public int voteCount;

            public override string ToString()
            {
                return $"{option} ({voteCount})";
            }
        }
    }

}