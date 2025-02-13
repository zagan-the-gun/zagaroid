using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Twitcher;

public class VoteDisplay : MonoBehaviour
{
    [SerializeField]
    private VoteOptionDisplay optionTemplate;
    [SerializeField]
    private Transform optionsRoot;
    [SerializeField]
    private Slider countdown;

    private Vote displayedVote;
    private List<VoteOptionDisplay> optionDisplays;


    private void Awake()
    {
        // Register to the vote events.
        Vote.onVoteStarted += OnVoteStarted;
        Vote.onVoteEnded += OnVoteEnded;

        // Create a container for our vote displays.
        optionDisplays = new List<VoteOptionDisplay>();
    }

    private void Update()
    {
        // Update the vote timer if one is active.
        if (displayedVote != null)
        {
            countdown.value = 1.0f - displayedVote.Progress;
        }
    }

    public void OnVoteStarted(Vote vote)
    {
        // Obtain the vote to display, and register to its update event.
        displayedVote = vote;
        vote.onVoteMadeOrChanged += VoteUpdated;

        // Remove previous display elements and then create new displays.
        // Note: In a real scenario you would probably want to reuse displays elemnts.
        ClearPrevious();
        foreach (string option in vote.Options)
        {
            VoteOptionDisplay display = Instantiate(optionTemplate, optionsRoot);
            display.Init(option);
            optionDisplays.Add(display);
        }
    }

    private void OnVoteEnded(Vote vote, List<Vote.Result> results)
    {
        // Update the displays to indicate which one was the winner.
        foreach (VoteOptionDisplay display in optionDisplays)
        {
            bool isWinner = (results[0].voteCount > 0 && display.Option == results[0].option);
            display.SetState(isWinner ? VoteOptionDisplay.State.Winner : VoteOptionDisplay.State.Loser);
        }

        displayedVote = null;
    }

    private void VoteUpdated(string voter, string voteCurrent, string votePrevious)
    {
        // Update displays to show the latest vote counts.
        foreach (VoteOptionDisplay display in optionDisplays)
        {
            display.Count = displayedVote.GetCurrentVoteCount(display.Option);
        }
    }

    private void ClearPrevious()
    {
        for (int i = optionsRoot.childCount - 1; i >= 0; i--)
        {
            Destroy(optionsRoot.GetChild(i).gameObject);
        }
        optionDisplays.Clear();
    }
}
