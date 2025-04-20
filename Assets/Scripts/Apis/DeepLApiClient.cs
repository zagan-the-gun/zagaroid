using UnityEngine;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine.Networking;
using System.Collections;
using Newtonsoft.Json;


// DeepLのREST-APIクライアント
public class DeepLApiClient : MonoBehaviour {
    private ConfigLoader configLoader;
    // 基本URL
    private const string BASE = "https://api-free.deepl.com";
    // 翻訳 URL
    private const string TRANSLATE_URL = BASE + "/v2/translate";
    // Authorization
    private string AUTHORIZATION; // ここにAPIキーを入力

    private void Start()
    {
        ConfigLoader.Instance.LoadConfig();
        Debug.LogError("ConfigLoader起動");
    }

    // void Start() {
    //     // ConfigLoaderを取得してAPIキーをロード
    //     ConfigLoader configLoader = FindObjectOfType<ConfigLoader>();
    //     if (configLoader != null)
    //     {
    //         AUTHORIZATION = configLoader.GetDeepLApiClientKey();
    //         Debug.Log("DeepLApiClientKeyの取得に成功しました" + AUTHORIZATION);
    //     }
    //     else
    //     {
    //         Debug.LogError("DeepLApiClient 設定ファイルの読み込みに失敗しました");
    //     }
    // }

    // Authorization
    // private string GetAuthorization()
    // {
    //     return AUTHORIZATION; // APIキーを返す
    // }

    public IEnumerator PostTranslate(string text, string toLang, Action<string> onTranslated) {
        // APIキーの読み取り
        AUTHORIZATION = ConfigLoader.Instance.GetDeepLApiClientKey();
        if (string.IsNullOrEmpty(AUTHORIZATION))
        {
            Debug.LogError("でーぷるきー！読み込みエラー！");
        }
        else
        {
            Debug.Log("でーぷるきーをよみこみました！: " + AUTHORIZATION);
        }

        // URL
        string webUrl = $"TRANSLATE_URL";

        // JSON データを作成
        string data = $@"
        {{
            ""text"": [
                ""{text}""
            ],
            ""target_lang"": ""{toLang}""
        }}";

        // UnityWebRequestを使用してPOSTリクエストを送信
        using (UnityWebRequest request = new UnityWebRequest(TRANSLATE_URL, "POST")) {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(data);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization",  "DeepL-Auth-Key " + AUTHORIZATION);
            request.SetRequestHeader("Content-Type", "application/json");
            Debug.Log("DeepLApiClientKey: " + AUTHORIZATION);

            // リクエストを送信し、レスポンスを待つ
            yield return request.SendWebRequest();

            // エラーチェック
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Error: " + request.error);
            }
            else
            {
                // レスポンスの内容を取得
                string responseString = request.downloadHandler.text;

                // JSON をデシリアライズ
                var translationResponse = JsonConvert.DeserializeObject<TranslationResponse>(responseString);

                // text の内容を取得
                string translatedText = translationResponse.translations[0].text;

                // 結果をログに出力
                Debug.Log("Translated Text: " + translatedText);

                // コールバックを呼び出して翻訳結果を返す
                onTranslated?.Invoke(translatedText);
            }
        }
    }
}

// JSON 構造に対応するクラス
public class TranslationResponse
{
    public List<Translation> translations { get; set; }
}

public class Translation
{
    public string detected_source_language { get; set; }
    public string text { get; set; }
}