using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.Linq;



/// <summary>
/// VOICEVOXのREST-APIクライアント
/// </summary>
public class VoiceVoxApiClient
{
    /// <summary> 基本 URL </summary>
    private const string BASE = "localhost:50021";
    /// <summary> 音声クエリ取得 URL </summary>
    private const string AUDIO_QUERY_URL = BASE + "/audio_query";
    /// <summary> 音声合成 URL </summary>
    private const string SYNTHESIS_URL = BASE + "/synthesis";
    /// <summary> 話者一覧取得　URL </summary>
    private const string SPEAKERS_URL = BASE + "/speakers";

    /// <summary> 音声クエリ（Byte配列） </summary>
    private byte[] _audioQueryBytes;
    /// <summary> 音声クエリ（Json文字列） </summary>
    private string _audioQuery;
    /// <summary> 音声クリップ </summary>
    private AudioClip _audioClip;

    /// <summary> 音声クエリ（Json文字列） </summary>
    public string AudioQuery { get => _audioQuery; }
    /// <summary> 音声クリップ </summary>
    public AudioClip AudioClip { get => _audioClip; }

    public List<Speaker> speakersList = new List<Speaker>(); // Speakerオブジェクトのリスト

    /// <summary>
    /// 指定したテキストを音声合成、AudioClipとして出力
    /// </summary>
    /// <param name="speakerId">話者ID</param>
    /// <param name="text">テキスト</param>
    /// <returns></returns>
    [Obsolete]
    public IEnumerator TextToAudioClip(int speakerId, string text)
    {
        // 音声クエリを生成
        yield return PostAudioQuery(speakerId, text);

        // 音声クエリから音声合成
        yield return PostSynthesis(speakerId, _audioQueryBytes);
    }

    /// <summary>
    /// 音声合成用のクエリ生成
    /// </summary>
    /// <param name="speakerId">話者ID</param>
    /// <param name="text">テキスト</param>
    /// <returns></returns>
    public IEnumerator PostAudioQuery(int speakerId, string text)
    {
        _audioQuery = "";
        _audioQueryBytes = null;
        // URL
        string webUrl = $"{AUDIO_QUERY_URL}?speaker={speakerId}&text={text}";
        // POST通信
        using (UnityWebRequest request = new UnityWebRequest(webUrl, "POST"))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            // リクエスト（レスポンスがあるまで待機）
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                // 接続エラー
                Debug.Log("AudioQuery:" + request.error);
            }
            else
            {
                if (request.responseCode == 200)
                {
                    // リクエスト成功
                    _audioQuery = request.downloadHandler.text;
                    _audioQueryBytes = request.downloadHandler.data;
                    Debug.Log("AudioQuery:" + request.downloadHandler.text);
                }
                else
                {
                    // リクエスト失敗
                    Debug.Log("AudioQuery:" + request.responseCode);
                }
            }
        }
    }

    /// <summary>
    /// 音声合成
    /// </summary>
    /// <param name="speakerID">話者ID</param>
    /// <param name="audioQuery">音声クエリ</param>
    /// <returns></returns>
    [Obsolete]
    public IEnumerator PostSynthesis(int speakerID, string audioQuery)
    {
        return PostSynthesis(speakerID, Encoding.UTF8.GetBytes(audioQuery));
    }

    /// <summary>
    /// 音声合成
    /// </summary>
    /// <param name="speakerId">話者ID</param>
    /// <param name="audioQuery">音声クエリ(Byte配列)</param>
    /// <returns></returns>
    [Obsolete]
    private IEnumerator PostSynthesis(int speakerId, byte[] audioQuery)
    {
        _audioClip = null;
        // URL
        string webUrl = $"{SYNTHESIS_URL}?speaker={speakerId}";
        // ヘッダー情報
        Dictionary<string, string> headers = new Dictionary<string, string>();
        headers.Add("Content-Type", "application/json");

        using (WWW www = new WWW(webUrl, audioQuery, headers))
        {
            // レスポンスが返るまで待機
            yield return www;

            if (!string.IsNullOrEmpty(www.error))
            {
                // エラー
                Debug.Log("Synthesis : " + www.error);
            }
            else
            {
                // レスポンス結果をAudioClipで取得
                _audioClip = www.GetAudioClip(false, false, AudioType.WAV);
            }
        }
    }

    public IEnumerator GetSpeakerRnd(Action<int> callback)
    {
        // 話者IDを取得
        yield return GetSpeakers();

        // ランダムに話者を選択
        UnityEngine.Random.InitState((int)DateTime.Now.Ticks); // ランダムシードを現在の時間に基づいて設定
        if (speakersList.Count > 0)
        {
            int randomIndex = UnityEngine.Random.Range(0, speakersList.Count);
            Speaker randomSpeaker = speakersList[randomIndex];
            // Debug.Log($"ランダムに選ばれた話者: {randomSpeaker.name} (UUID: {randomSpeaker.speaker_uuid})");

            // スタイルをランダムに選択
            if (randomSpeaker.styles != null && randomSpeaker.styles.Count > 0)
            {
                int randomStyleIndex = UnityEngine.Random.Range(0, randomSpeaker.styles.Count);
                SpeakerStyle randomStyle = randomSpeaker.styles[randomStyleIndex];
                Debug.Log($"name: {randomSpeaker.name}, style: {randomStyle.name}, (ID: {randomStyle.id})");
                // yield return randomStyle.id;
                callback(randomStyle.id); // コールバックを呼び出してスタイルIDを返す
            }
            else
            {
                Debug.Log("選ばれた話者にはスタイルがありません。");
            }

        }
        else
        {
            Debug.Log("話者が見つかりませんでした。");
        }
    }

    /// <summary>
    /// 話者一覧を取得
    /// </summary>
    /// <returns></returns>
    public IEnumerator GetSpeakers()
    {
        string webUrl = SPEAKERS_URL; // 話者一覧取得のURL

        using (UnityWebRequest request = UnityWebRequest.Get(webUrl))
        {
            // リクエスト（レスポンスがあるまで待機）
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                // 接続エラー
                Debug.Log("GetSpeakers:" + request.error);
            }
            else
            {
                if (request.responseCode == 200)
                {
                    // リクエスト成功
                    string jsonResponse = request.downloadHandler.text;
                    // Debug.Log("Speakers: " + jsonResponse);
                    
                    // JSONをパースして話者情報を取得する処理を追加
                    ParseSpeakers(jsonResponse);
                }
                else
                {
                    // リクエスト失敗
                    Debug.Log("GetSpeakers:" + request.responseCode);
                }
            }
        }
    }

    /// <summary>
    /// JSONレスポンスをパースして話者情報を取得
    /// </summary>
    /// <param name="jsonResponse">JSONレスポンス</param>
    private void ParseSpeakers(string jsonResponse)
    {
        // JSONが配列形式であることを確認
        if (jsonResponse.StartsWith("[") && jsonResponse.EndsWith("]"))
        {
            // JSONをラッパークラスを使ってパース
            SpeakerArrayWrapper wrapper = JsonUtility.FromJson<SpeakerArrayWrapper>("{\"speakers\":" + jsonResponse + "}");
            speakersList = wrapper.speakers.ToList();
        }
        else
        {
            Debug.LogError("JSONが配列形式ではありません。");
        }
    }

    [System.Serializable]
    public class SpeakerStyle
    {
        public string name;
        public int id;
        public string type;
    }

    [System.Serializable]
    public class Speaker
    {
        public string name;
        public string speaker_uuid;
        public List<SpeakerStyle> styles;
        public string version;
        public Dictionary<string, string> supported_features;
    }

    // JSONのリストを扱うためのラッパークラス
    [System.Serializable]
    public class SpeakerArrayWrapper
    {
        public Speaker[] speakers;
    }
}
