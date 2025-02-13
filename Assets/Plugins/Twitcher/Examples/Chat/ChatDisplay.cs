using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Twitcher;

public class ChatDisplay : MonoBehaviour
{
    [SerializeField]
    private ChatItem textTemplate;
    [SerializeField]
    private ScrollRect consoleScroll;
    [SerializeField]
    private int maxLength = 128;


    public void DisplayMessage(Message msg)
    {
        TrimMessages();

        ChatItem log = Instantiate(textTemplate, consoleScroll.content);
        log.Configure(msg);
    }

    public void DisplayMessage(string sender, string msg)
    {
        TrimMessages();
        ChatItem log = Instantiate(textTemplate, consoleScroll.content);
        log.Configure(sender, msg);
    }

    private void TrimMessages()
    {
        int trimCount = consoleScroll.content.childCount - maxLength;

        if (trimCount > 0)
        {
            List<GameObject> toKill = new List<GameObject>();

            for (int i = 0; i < trimCount; i++)
                toKill.Add(consoleScroll.content.GetChild(i).gameObject);

            foreach (GameObject target in toKill)
                Destroy(target);
        }
    }
}
