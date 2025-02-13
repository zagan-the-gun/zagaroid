using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Twitcher
{
    public class TwitcherUtil : MonoBehaviour
    {
        public enum LoggingMode
        {
            Silent,
            ErrorsOnly,
            Verbose
        }

        public enum EmoteSize
        {
            Small = 1,
            Medium,
            Large
        }

        public delegate void EmoteDelegate(Texture2D emote);


        private static TwitcherUtil instance;
        public static LoggingMode logging = LoggingMode.ErrorsOnly;

        private static TwitcherUtil Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new GameObject("TwitcherUtil").AddComponent<TwitcherUtil>();
                    instance.hideFlags = HideFlags.HideAndDontSave;
                }
                return instance;
            }
        }

        public static bool LogVerbose
        {
            get { return (logging == LoggingMode.Verbose); } 
        }
        public static bool LogErrors
        {
            get { return (logging != LoggingMode.ErrorsOnly); }
        }
        public static bool LogSilent
        { 
            get { return (logging == LoggingMode.Silent); }
        }



        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }

        /// <summary>
        /// Downloads the image for an emote used in chat.
        /// </summary>
        /// <param name="emoteId">ID of the emote from the message.</param>
        /// <param name="size">Size to download (twitch provides 3 options.</param>
        /// <param name="onSuccess">Callback method to send the resulting image to on success.</param>
        public static void DownloadEmote(string emoteId, EmoteSize size, EmoteDelegate callback)
        {
            if (callback == null)
            {
                if (LogErrors)
                    Debug.LogError($"DownloadEmote must be provided with a callback method, ignoring download request.");

                return;
            }

            // UnityWebRequest needs to wait to finish, so let the util instance handle that with a coroutine.
            Instance.StartCoroutine(EmoteCoroutine(emoteId, size, callback));
        }

        private static IEnumerator EmoteCoroutine(string emoteId, EmoteSize size, EmoteDelegate onSuccess)
        {
            if (onSuccess != null)
            {
                // URL based on twitch api ref: https://dev.twitch.tv/docs/irc/tags
                string emoteString = $"{emoteId}/{(int)size}.0";
                UnityWebRequest www = UnityWebRequestTexture.GetTexture($"http://static-cdn.jtvnw.net/emoticons/v1/{emoteString}");

                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    if (LogErrors)
                        Debug.LogError(www.error);
                }
                else
                {
                    if (LogVerbose)
                        Debug.Log($"Successfully downloaded emote: {emoteId}");

                    Texture2D myTexture = ((DownloadHandlerTexture)www.downloadHandler).texture;
                    myTexture.name = emoteString;
                    onSuccess(myTexture);
                }
            }
        }
    }

}