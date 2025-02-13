using UnityEngine;
using UnityEngine.Video;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;

public class VideoPlayerController : MonoBehaviour
{
    [SerializeField] private RawImage displayImagePrefab;
    private Dictionary<VideoClip, (RawImage image, RectTransform rect)> activeVideos 
        = new Dictionary<VideoClip, (RawImage image, RectTransform rect)>();
    
        private Transform canvasTransform; // CanvasのTransformを格納する変数

        void Awake()
        {
            // Canvasを探してTransformを取得
            canvasTransform = GameObject.Find("Canvas").transform; // "Canvas"はCanvasオブジェクトの名前
        }

    // PlayVideoメソッドの引数を修正
    public void PlayVideo(VideoClip clip, Vector2 position, Vector2 size)
    {
        if (clip == null) return;

        // 既存の再生中の同じ動画があれば停止
        if (activeVideos.ContainsKey(clip))
        {
            StopVideo(clip);
        }

        // 新しいRawImageを作成
        // var newImage = Instantiate(displayImagePrefab, transform);
        var newImage = Instantiate(displayImagePrefab, canvasTransform); // canvasTransformはCanvasのTransform
        var rectTransform = newImage.GetComponent<RectTransform>();
        
        // 位置とサイズを設定
        rectTransform.anchorMin = new Vector2(0, 0);
        rectTransform.anchorMax = new Vector2(0, 0);
        rectTransform.pivot = new Vector2(0, 0);
        rectTransform.anchoredPosition = position;
        rectTransform.sizeDelta = size;

        // VideoPlayerの設定
        var videoPlayer = newImage.gameObject.AddComponent<VideoPlayer>();
        videoPlayer.playOnAwake = false;
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        
        // RenderTextureの作成と設定
        var renderTexture = new RenderTexture((int)size.x, (int)size.y, 24);
        videoPlayer.targetTexture = renderTexture;
        newImage.texture = renderTexture;

        // 再生終了時のイベントを登録
        videoPlayer.loopPointReached += (vp) => OnVideoFinished(clip);

        // 動画を再生
        videoPlayer.clip = clip;
        videoPlayer.Play();

        // 管理用ディクショナリに追加
        activeVideos[clip] = (newImage, rectTransform);

        // デバッグ用のオブジェクトを作成
        // GameObject debugMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        // debugMarker.transform.position = new Vector3(position.x, position.y, 0); // 動画の再生位置にマーカーを配置
        // // debugMarker.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f); // マーカーのサイズを設定
        // debugMarker.transform.localScale = new Vector3(size.x / 100, size.y / 100, 0.5f); // サイズを調整
    }

    // StopVideoメソッドを修正
    public void StopVideo(VideoClip clip)
    {
        if (activeVideos.TryGetValue(clip, out var videoComponents))
        {
            var videoPlayer = videoComponents.image.GetComponent<VideoPlayer>();
            if (videoPlayer != null)
            {
                videoPlayer.Stop();
                Destroy(videoPlayer.targetTexture);
            }
            Destroy(videoComponents.image.gameObject);
            activeVideos.Remove(clip);
        }
    }

    // OnVideoFinishedメソッドを修正
    private void OnVideoFinished(VideoClip clip)
    {
        StopVideo(clip);
    }

    private void OnDestroy()
    {
        // 全ての動画を停止してクリーンアップ
        foreach (var clip in activeVideos.Keys.ToList())
        {
            StopVideo(clip);
        }
    }
}