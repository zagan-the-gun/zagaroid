using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Twitcher
{
    public class VoteDemo : MonoBehaviour
    {
        [Header("Twitch Settings")]
        [SerializeField, Tooltip("Name of channel to join.")]
        private string channelToJoin;
        [SerializeField, Tooltip("OAuth token to login with")]
        private string authToken;

        [Header("Vote Settings")]
        [SerializeField, Tooltip("Duration in seconds of a vote.")]
        private float voteDuration;
        [SerializeField, Tooltip("Time in seconds bwterrn one vote ending, and the next starting.")]
        private float timeBetweenVotes;
        [SerializeField, Tooltip("This prefix will be required at the start of messages to count them as votes.")]
        private string votePrefix = "!";

        private TwitchController twitch;
        private Vote theVote;

        // These are just example options for voting.
        private readonly string[] voteOptions =
        {
            "Left",
            "Right",
            "Up",
            "Down"
        };

        private void Start()
        {
            // Create a twitcher instance, and build our vote object.
            twitch = TwitchController.Create(authToken, channelToJoin);
            theVote = new TimedVote(votePrefix, voteOptions, voteDuration, OnVoteComplete);

            // This example just repeats votes when they finish, so trigger our first vote.
            twitch.StartVote(theVote);
        }

        private void OnVoteComplete(Vote vote, List<Vote.Result> results)
        {
            // Results are already sorted, so as long as the first vote has more than 0
            // then we have at least 1 winner, this example will just take the first.
            if (results[0].voteCount > 0)
            {
                Debug.LogWarning("Vote Ended, winner is " + results[0].option);
            }
            else
            {
                Debug.Log("No Votes!");
            }

            // NOTE: we only log out debug messages here, the visual display is used as a way to 
            // demonstrate the static events that the Vote class broadcasts when votes begin/end.
            // See VoteDisplay.cs for this example.

            // Start the next vote after a short delay.
            StartCoroutine(DelayedRevote());
        }

        private IEnumerator DelayedRevote()
        {
            yield return new WaitForSecondsRealtime(timeBetweenVotes);

            Debug.LogWarning("Starting Vote");

            twitch.StartVote(theVote);
        }
    }

}