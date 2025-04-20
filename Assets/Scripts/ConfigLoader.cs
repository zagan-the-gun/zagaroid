using System.IO;
using Newtonsoft.Json;
using UnityEngine;

public class ConfigLoader : MonoBehaviour
{
    private static ConfigLoader instance;
    private Config config;

    [System.Serializable]
    public class Config
    {
        public string DeepLApiClientKey; // APIキーを格納するプロパティ
    }

    public static ConfigLoader Instance
    {
        get
        {
            if (instance == null)
            {
                instance = FindObjectOfType<ConfigLoader>();
                if (instance == null)
                {
                    GameObject obj = new GameObject("ConfigLoader");
                    instance = obj.AddComponent<ConfigLoader>();
                    instance.LoadConfig(); // 初回アクセス時に設定を読み込む
                }
            }
            return instance;
        }
    }

    public void LoadConfig()
    {
        Debug.Log("Loading config...");
        string path = Path.Combine(Application.streamingAssetsPath, "config.json");

        if (File.Exists(path))
        {
            string json = File.ReadAllText(path);
            config = JsonConvert.DeserializeObject<Config>(json);
            Debug.Log("Config loaded: " + json);
        }
        else
        {
            Debug.LogError("Config file not found: " + path);
        }
    }

    public string GetDeepLApiClientKey()
    {
        return config?.DeepLApiClientKey; // APIキーを返す
    }
}