using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Main Canvas 上でユーザーが Avatar 画像をドラッグして位置を操作するコンポーネント
/// - CentralManager.OnActorsChanged を購読
/// - 各 Actor ごとに RawImage + UIDragMove を動的に作成
/// - ドラッグで位置が変更されたら ActorConfig.avatarDisplayPosition を更新
/// - 更新を CentralManager.SetActors() で保存
///
/// Main Canvas 構造:
///   Main Canvas
///     ├─ AvatarControlPanel（本コンポーネント管理）
///     │   ├─ ksk_Avatar_Icon (RawImage)
///     │   │   └─ UIDragMove
///     │   └─ Actor2_Avatar_Icon (RawImage)
///     │       └─ UIDragMove
///     └─ ...
/// </summary>
public class MainCanvasAvatarController : MonoBehaviour
{
    [Header("Avatar Container")]
    [SerializeField] private RectTransform avatarControlPanel; // Avatar 配置先（Main Canvas 直下を推奨）

    [Header("Avatar Icon Size")]
    [SerializeField] private Vector2 avatarIconSize = new Vector2(100, 100); // UI Icon サイズ

    private const string LogPrefix = "[MainCanvasAvatarController]";
    private Dictionary<string, RawImage> actorAvatarUIMap = new Dictionary<string, RawImage>(); // Actor名 → Image
    private List<ActorConfig> cachedActors = new List<ActorConfig>();

    private void OnEnable()
    {
        // CentralManager のイベント購読
        CentralManager.OnActorsChanged += HandleActorsChanged;
    }

    private void OnDisable()
    {
        // イベント購読解除
        CentralManager.OnActorsChanged -= HandleActorsChanged;
    }

    private void Start()
    {
        // avatarControlPanel が未設定ならシーンから自動検出
        if (avatarControlPanel == null)
        {
            var mainCanvas = GameObject.Find("Main Canvas");
            if (mainCanvas != null)
            {
                var controlPanel = mainCanvas.transform.Find("AvatarControlPanel");
                if (controlPanel != null)
                {
                    avatarControlPanel = controlPanel.GetComponent<RectTransform>();
                }
                else
                {
                    // AvatarControlPanel がない場合は作成
                    Debug.LogWarning($"{LogPrefix} AvatarControlPanel が見つかりません。新規作成します。");
                    var go = new GameObject("AvatarControlPanel", typeof(RectTransform));
                    go.transform.SetParent(mainCanvas.transform, false);
                    var rect = go.GetComponent<RectTransform>();
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                    rect.localScale = Vector3.one;  // スケールを 1, 1, 1 に設定
                    avatarControlPanel = rect;
                }
            }
        }

        if (avatarControlPanel == null)
        {
            Debug.LogError($"{LogPrefix} avatarControlPanel が見つかりません");
            return;
        }

        // 初期化：現在の Actor リストから UI を生成
        var actors = CentralManager.Instance.GetActors();
        if (actors != null && actors.Count > 0)
        {
            HandleActorsChanged(actors);
        }
    }

    /// <summary>
    /// Actor リスト変更時のハンドラ
    /// </summary>
    private void HandleActorsChanged(List<ActorConfig> actors)
    {
        if (avatarControlPanel == null) return;

        Debug.Log($"{LogPrefix} Actor リスト変更を検知: {actors.Count} 件");
        cachedActors = new List<ActorConfig>(actors);

        // 現在の UI マップ内の Actor を確認
        var currentActorNames = new HashSet<string>(actorAvatarUIMap.Keys);
        var newActorNames = new HashSet<string>(actors.Select(a => a.actorName).Where(name => !string.IsNullOrEmpty(name)));

        // 削除対象: 新リストに存在しない Actor の UI を削除
        var toRemove = currentActorNames.Except(newActorNames).ToList();
        foreach (var actorName in toRemove)
        {
            if (actorAvatarUIMap.TryGetValue(actorName, out var image))
            {
                Debug.Log($"{LogPrefix} UI を削除: {actorName}");
                Destroy(image.gameObject);
                actorAvatarUIMap.Remove(actorName);
            }
        }

        // 追加対象: 新リストに存在するが、まだ UI がない Actor
        var toAdd = newActorNames.Except(currentActorNames).ToList();
        foreach (var actorName in toAdd)
        {
            var actor = actors.FirstOrDefault(a => a.actorName == actorName);
            if (actor != null)
            {
                CreateAvatarUI(actor);
            }
        }

        // 既存の UI の位置を更新（保存された位置がある場合）
        foreach (var actor in actors)
        {
            if (actorAvatarUIMap.TryGetValue(actor.actorName, out var image))
            {
                var rect = image.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.anchoredPosition = actor.avatarDisplayPosition;
                }
            }
        }
    }

    /// <summary>
    /// 指定の Actor 用 UI を生成
    /// </summary>
    private void CreateAvatarUI(ActorConfig actor)
    {
        if (avatarControlPanel == null) return;

        // アバター画像パスが設定されていない場合はスキップ
        if (actor.avatarAnimePaths == null || actor.avatarAnimePaths.Count == 0)
        {
            Debug.LogWarning($"{LogPrefix} avatarAnimePaths が空のためスキップ: {actor.actorName}");
            return;
        }

        // 最初の画像をロード
        Debug.Log($"{LogPrefix} テクスチャ読み込み開始: {actor.actorName} path={actor.avatarAnimePaths[0]}");
        var texture = LoadTextureFromPath(actor.avatarAnimePaths[0]);
        if (texture == null)
        {
            Debug.LogWarning($"{LogPrefix} テクスチャ読み込み失敗のためスキップ: {actor.actorName} path={actor.avatarAnimePaths[0]}");
            return;
        }

        // Image GameObject を作成
        var go = new GameObject($"{actor.actorName}_Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        go.transform.SetParent(avatarControlPanel, false);
        var image = go.GetComponent<RawImage>();

        // RectTransform を設定
        var rect = go.GetComponent<RectTransform>();
        rect.anchoredPosition = actor.avatarDisplayPosition;

        // Image の色を白色で不透明に設定（コメントと同じ）
        image.color = new Color(1f, 1f, 1f, 1f);
        image.raycastTarget = false;
        image.enabled = true;

        // テクスチャを設定
        image.texture = texture;

        // RectTransform のサイズを画像のナチュラルサイズに設定
        if (texture.width > 0 && texture.height > 0)
        {
            rect.sizeDelta = new Vector2(texture.width, texture.height);
        }

        Debug.Log($"{LogPrefix} テクスチャ読み込み成功: {actor.actorName} size={texture.width}x{texture.height}");

        // 表示スケール（倍率）を適用
        rect.localScale = Vector3.one * actor.avatarDisplayScale;

        // ドラッグ機能を追加
        var drag = image.gameObject.GetComponent<UIDragMove>();
        if (drag == null)
        {
            drag = image.gameObject.AddComponent<UIDragMove>();
        }
        drag.SetTarget(rect);
        drag.SetOnlyDragOnGraphic(false); // Icon 全体でドラッグ可能

        // ドラッグ終了時に位置を保存するコールバック
        AddDragEndListener(actor, drag);

        // マップに登録
        actorAvatarUIMap[actor.actorName] = image;
        Debug.Log($"{LogPrefix} UI を作成: {actor.actorName} texture={image.texture?.name ?? "null"} color={image.color}");
    }

    /// <summary>
    /// ドラッグ終了時に位置情報を保存するコールバックを追加
    /// </summary>
    private void AddDragEndListener(ActorConfig actor, UIDragMove drag)
    {
        // NOTE: UIDragMove に EndDrag イベントがあれば使用
        // 今は Update で定期的に位置をチェック
        // 別途イベントシステムを追加してもよい
    }

    /// <summary>
    /// ドラッグ終了時に位置を保存（毎フレーム監視）
    /// </summary>
    private void Update()
    {
        // 各 Actor の UI 位置が変更されていないか監視
        foreach (var actor in cachedActors)
        {
            if (actorAvatarUIMap.TryGetValue(actor.actorName, out var image))
            {
                var rect = image.GetComponent<RectTransform>();
                if (rect != null && actor.avatarDisplayPosition != rect.anchoredPosition)
                {
                    // 位置が変更されている → 保存
                    actor.avatarDisplayPosition = rect.anchoredPosition;
                    // CentralManager 経由で保存
                    CentralManager.Instance?.SetActors(cachedActors);
                }
            }
        }
    }

    /// <summary>
    /// ファイルパスから Texture2D を読み込み
    /// </summary>
    private Texture2D LoadTextureFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2);
            if (texture.LoadImage(bytes))
            {
                return texture;
            }
            Debug.LogError($"{LogPrefix} テクスチャ読み込み失敗: {path}");
            return null;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"{LogPrefix} ファイル読み込みエラー: {path} - {ex.Message}");
            return null;
        }
    }
}
