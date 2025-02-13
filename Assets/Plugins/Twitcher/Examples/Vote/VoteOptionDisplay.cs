using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class VoteOptionDisplay : MonoBehaviour
{
    public enum State
    {
        Active, Winner, Loser
    }

    [SerializeField]
    private Text voteCount;
    [SerializeField]
    private Text voteOption;
    [SerializeField]
    private Image background;
    [SerializeField]
    private Color normalColour;
    [SerializeField]
    private Color winnerColour;
    [SerializeField]
    private Color loserColour;

    private int count;

    public string Option { get; private set; }
    public int Count
    {
        get { return count; }
        set
        {
            count = value;
            voteCount.text = count.ToString();
        }
    }

    public void Init(string option)
    {
        Option = option;
        voteOption.text = option;
        Count = 0;
        SetState(State.Active);
    }

    public void SetState(State state)
    {
        switch (state)
        {
            case State.Active:
                background.color = normalColour;
                break;

            case State.Winner:
                background.color = winnerColour;
                break;

            case State.Loser:
                background.color = loserColour;
                break;
        }
    }
}
