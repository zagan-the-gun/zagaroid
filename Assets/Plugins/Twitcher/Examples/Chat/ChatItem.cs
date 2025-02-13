using UnityEngine;
using UnityEngine.UI;
using Twitcher;

public class ChatItem : MonoBehaviour
{
	[SerializeField]
	private Text label;

	public void Configure(Message message)
	{
		label.text = $"<b><color={message.Info.colourHex}>{message.Sender}:</color></b> <color=white>{message.ChatMessage}</color>";
	}

	public void Configure(string sender, string message)
	{
		label.text = $"<b><color=green>{sender}:</color></b> <color=white>{message}</color>";
	}

}
