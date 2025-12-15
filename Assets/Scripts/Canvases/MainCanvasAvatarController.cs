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
///     │   ├─ Actor_Avatar_Icon (RawImage)
///     │   │   └─ UIDragMove
///     │   └─ ...
///     └─ ...
/// </summary>
public class MainCanvasAvatarController : MonoBehaviour
{
    [Header("Avatar Container")]
    [SerializeField] private RectTransform avatarControlPanel; // Avatar 配置先（Main Canvas 直下を推奨）
    
    [Header("UI Event Camera")]
    [Tooltip("UIの描画/レイキャストに使うカメラ。未設定ならシーンの 'Main Camera' を探します。")]
    [SerializeField] private Camera uiEventCamera;

    [Header("Avatar Icon Size")]
    [SerializeField] private Vector2 avatarIconSize = new Vector2(100, 100); // UI Icon サイズ

    private const string LogPrefix = "[MainCanvasAvatarController]";
    private Dictionary<string, RawImage> actorAvatarUIMap = new Dictionary<string, RawImage>(); // Actor名 → Image
    private Dictionary<string, MeshRenderer> avatarMeshRendererMap = new Dictionary<string, MeshRenderer>(); // Actor名 → 基本メッシュ
    private List<ActorConfig> cachedActors = new List<ActorConfig>();
    
    /// <summary>
    /// アクターごとのアニメーション状態
    /// </summary>
    private class AvatarAnimationState {
        public List<Texture2D> textures = new List<Texture2D>(); // ロード済みテクスチャリスト
        public float elapsedTime = 0f; // 経過時間
        public int currentIndex = 0; // 現在の画像インデックス
        public int direction = 1; // 進行方向（1=前進、-1=後退）
        public MeshRenderer meshRenderer; // メッシュレンダラー参照
        public Material material; // マテリアル参照
        public bool isInCycleDelay = false; // 1サイクル完了後の待機フェーズ中かどうか
        public float cycleDelayElapsedTime = 0f; // 待機時間の経過時間
        public int previousDirection = 1; // 前フレームの進行方向（サイクル完了判定用）
    }
    
    /// <summary>
    /// リップシンク制御用の状態管理
    /// FaceAnimatorControllerを参考に実装
    /// 3D Mesh（Quad）を使用して、アニメーション画像の上にリップシンク口を重ねる設計
    /// NDI カメラで映るようにするため、必ず 3D Mesh を使用
    /// </summary>
    private class AvatarLipSyncState {
        public List<Texture2D> lipSyncTextures = new List<Texture2D>(); // リップシンク用テクスチャ
        public float mouthOpen01 = 0f;  // 開口度 (0..1)
        public bool hasLipLevel = false;
        public float lastLipLevelTimeUnscaled = 0f;
        public Material lipSyncMaterial; // リップシンク用マテリアル（3D Mesh レンダリング用）
        public GameObject lipSyncGameObject; // リップシンク用 3D GameObject（デバッグ用）
    }
    
    private Dictionary<string, AvatarAnimationState> animationStates = new Dictionary<string, AvatarAnimationState>();
    private Dictionary<string, AvatarLipSyncState> lipSyncStates = new Dictionary<string, AvatarLipSyncState>();
    
    [Header("Position Save")]
    [Tooltip("RectTransform の anchoredPosition がこの閾値以上変化した時だけ保存します（微小誤差による毎フレーム保存を防止）")]
    [SerializeField] private float positionSaveEpsilon = 0.05f;
    
    [Tooltip("保存の最短間隔（秒）。ドラッグ中の連打を防止します")]
    [SerializeField] private float positionSaveMinIntervalSeconds = 0.20f;
    
    private float lastActorsSaveTimeUnscaled;

    private void OnEnable() {
        // CentralManager のイベント購読
        CentralManager.OnActorsChanged += HandleActorsChanged;
        // リップシンクイベント購読（アバター表示開始）
        CentralManager.OnLipSyncLevel += HandleLipSyncLevel;
        // 字幕終了イベント購読（アバター表示終了）
        SubtitleController.OnSubtitleEnded += HandleSubtitleEnded;
    }

    private void OnDisable() {
        // イベント購読解除
        CentralManager.OnActorsChanged -= HandleActorsChanged;
        // リップシンクイベント購読解除
        CentralManager.OnLipSyncLevel -= HandleLipSyncLevel;
        // 字幕終了イベント購読解除
        SubtitleController.OnSubtitleEnded -= HandleSubtitleEnded;
    }

    private void Start() {
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
        
        // 重要:
        // Gameビューで見えている描画カメラと、UIのレイキャスト(eventCamera)が一致していないと
        // 「上半分だけクリック/ドラッグできない」ようなズレが発生する。
        // ここでは UI Canvas の worldCamera を "Main Camera" に寄せて、見た目と入力を一致させる。
        var canvas = avatarControlPanel.GetComponentInParent<Canvas>();
        if (canvas != null) {
            if (uiEventCamera == null) {
                var go = GameObject.Find("Main Camera");
                uiEventCamera = go != null ? go.GetComponent<Camera>() : null;
            }
            if (uiEventCamera != null) {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = uiEventCamera;
            }
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
    private void HandleActorsChanged(List<ActorConfig> actors) {
        if (avatarControlPanel == null) return;

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
                
                // リップシンク GameObject も削除
                if (lipSyncStates.TryGetValue(actorName, out var lipState) && lipState.lipSyncGameObject != null) {
                    Destroy(lipState.lipSyncGameObject);
                }
                
                Destroy(image.gameObject);
                actorAvatarUIMap.Remove(actorName);
                avatarMeshRendererMap.Remove(actorName);
                animationStates.Remove(actorName);
                lipSyncStates.Remove(actorName);
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

        // 既存の UI の位置とスケールを更新（保存された値がある場合）
        foreach (var actor in actors)
        {
            if (actorAvatarUIMap.TryGetValue(actor.actorName, out var image))
            {
                var rect = image.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.anchoredPosition = actor.avatarDisplayPosition;
                    // スケールを更新
                    rect.localScale = Vector3.one * actor.avatarDisplayScale;
                }
            }
        }
    }

    /// <summary>
    /// 指定の Actor 用 UI を生成
    /// </summary>
    private void CreateAvatarUI(ActorConfig actor) {
        if (avatarControlPanel == null) return;

        // アバター画像パスが設定されていない場合はスキップ
        if (actor.avatarAnimePaths == null || actor.avatarAnimePaths.Count == 0)
        {
            return;
        }

        // すべての画像をロード
        var textures = new List<Texture2D>();
        foreach (var path in actor.avatarAnimePaths)
        {
            Debug.Log($"{LogPrefix} テクスチャ読み込み開始: {actor.actorName} path={path}");
            var texture = LoadTextureFromPath(path);
            if (texture == null)
            {
                Debug.LogWarning($"{LogPrefix} テクスチャ読み込み失敗: {actor.actorName} path={path}");
                continue;
            }
            textures.Add(texture);
        }

        if (textures.Count == 0)
        {
            return;
        }

        // 最初のテクスチャを使用（初期表示用）
        var initialTexture = textures[0];

        // Image GameObject を作成
        var go = new GameObject($"{actor.actorName}_Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        go.transform.SetParent(avatarControlPanel, false);
        var image = go.GetComponent<RawImage>();

        // RectTransform を設定
        var rect = go.GetComponent<RectTransform>();
        rect.anchoredPosition = actor.avatarDisplayPosition;
        // 念のため明示（pivotズレがあると Mesh(中心原点) と Rect(Raycast) がズレるため）
        // ※ Mesh は pivot を考慮して生成するが、UI側も一般的な中心pivotで固定しておく
        rect.pivot = new Vector2(0.5f, 0.5f);

        // Image の設定（ドラッグ検知のためにraycastTargetをtrueに設定）
        image.color = new Color(1f, 1f, 1f, 1f);
        image.raycastTarget = true; // ドラッグ検知のために必要
        image.enabled = true;

        // テクスチャを設定
        image.texture = initialTexture;

        // RectTransform のサイズを画像のナチュラルサイズに設定
        if (initialTexture.width > 0 && initialTexture.height > 0)
        {
            rect.sizeDelta = new Vector2(initialTexture.width, initialTexture.height);
        }

        // 表示スケール（倍率）を適用
        rect.localScale = Vector3.one * actor.avatarDisplayScale;

        // 3D空間で表示するためにMeshRendererとMeshFilterを追加（PCとOBS両方に表示されるように）
        MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
        MeshFilter meshFilter = go.AddComponent<MeshFilter>();

        // Quadメッシュを作成（RectTransformのサイズに基づく）
        float meshWidth = rect.sizeDelta.x > 0 ? rect.sizeDelta.x : initialTexture.width;
        float meshHeight = rect.sizeDelta.y > 0 ? rect.sizeDelta.y : initialTexture.height;
        if (meshWidth <= 0) meshWidth = 100f;
        if (meshHeight <= 0) meshHeight = 100f;

        // NOTE:
        // ここで表示しているのは uGUI の RawImage ではなく、同一 GameObject 上の MeshRenderer(Quad) です。
        // ドラッグ判定は透明 RawImage の RectTransform 上で行われるため、
        // Mesh(原点中心) と RectTransform(pivot基準) がズレると「ドラッグ領域が半個分ズレる」症状になります。
        // pivot を考慮した Quad を生成して、判定と見た目を一致させます。
        Mesh quadMesh = CreateQuadMesh(meshWidth, meshHeight, rect.pivot);
        meshFilter.mesh = quadMesh;

        // Unlit/Transparentシェーダーでマテリアルを作成（透過対応）
        Material mat = new Material(Shader.Find("Unlit/Transparent"));
        mat.mainTexture = initialTexture;
        meshRenderer.material = mat;

        // レンダリング設定
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        avatarMeshRendererMap[actor.actorName] = meshRenderer;

        // RawImageは表示しないが、ドラッグ検知のために有効のままにする
        // 色を完全透明にして、raycastTargetをtrueに設定（UIDragMoveがドラッグを検知するため）
        image.color = new Color(1f, 1f, 1f, 0f); // 完全透明
        image.raycastTarget = true; // ドラッグ検知のために必要
        image.enabled = true; // 有効のまま（raycastTargetが機能するため）

        // レイヤーを設定（OBS画面に表示されるように、Nico Text (TMP)と同じレイヤー）
        go.layer = LayerMask.NameToLayer("Default");

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
        
        // アニメーション状態を初期化（複数画像の場合）
        if (textures.Count > 1)
        {
            var animState = new AvatarAnimationState
            {
                textures = textures,
                elapsedTime = 0f,
                currentIndex = 0,
                direction = 1,
                meshRenderer = meshRenderer,
                material = mat,
                isInCycleDelay = false,
                cycleDelayElapsedTime = 0f,
                previousDirection = 1
            };
            animationStates[actor.actorName] = animState;
            Debug.Log($"{LogPrefix} アニメーション状態を初期化: {actor.actorName} count={textures.Count}");
        }
        else
        {
            // 1枚の場合はアニメーション不要
            animationStates.Remove(actor.actorName);
        }

        // リップシンク状態を初期化（AvatarLipSyncPathsがある場合）
        Debug.Log($"{LogPrefix} CreateAvatarUI: {actor.actorName} lipSyncPaths.Count={actor.avatarLipSyncPaths?.Count ?? 0}");
        InitializeLipSyncState(actor);
        
        // 初期表示状態を設定
        // avatarShowWithTalk が OFF なら常に表示、ON なら最初は非表示（リップシンク時に表示）
        bool shouldDisplay = !actor.avatarShowWithTalk;
        image.enabled = shouldDisplay;
        meshRenderer.enabled = shouldDisplay;
        
        Debug.Log($"{LogPrefix} UI を作成: {actor.actorName} texture={image.texture?.name ?? "null"} color={image.color} shouldDisplay={shouldDisplay} (ShowWithTalk={actor.avatarShowWithTalk})");
    }

    /// <summary>
    /// ドラッグ終了時に位置情報を保存するコールバックを追加
    /// </summary>
    private void AddDragEndListener(ActorConfig actor, UIDragMove drag) {
        // NOTE: UIDragMove に EndDrag イベントがあれば使用
        // 今は Update で定期的に位置をチェック
        // 別途イベントシステムを追加してもよい
    }

    /// <summary>
    /// ドラッグ終了時に位置を保存（毎フレーム監視）、アニメーション更新、リップシンク更新
    /// </summary>
    private void Update() {
        // Save のスロットリング
        bool canSaveNow = (Time.unscaledTime - lastActorsSaveTimeUnscaled) >= positionSaveMinIntervalSeconds;

        // 各 Actor の UI 位置が変更されていないか監視
        foreach (var actor in cachedActors)
        {
            if (actorAvatarUIMap.TryGetValue(actor.actorName, out var image))
            {
                var rect = image.GetComponent<RectTransform>();
                if (rect != null)
                {
                    // 微小誤差で毎フレーム保存されないようにする
                    float dist = Vector2.Distance(actor.avatarDisplayPosition, rect.anchoredPosition);
                    if (dist >= positionSaveEpsilon && canSaveNow)
                    {
                        // 位置が変更されている → 保存
                        actor.avatarDisplayPosition = rect.anchoredPosition;
                        // CentralManager 経由で保存（OnActorsChanged が飛ぶので、ここは頻度制限必須）
                        lastActorsSaveTimeUnscaled = Time.unscaledTime;
                        CentralManager.Instance?.SetActors(cachedActors);
                        // 1フレームで複数回保存しない（OnActorsChanged連鎖を抑える）
                        break;
                    }
                }
            }
        }
        
        // アニメーション更新
        UpdateAvatarAnimations();
        
        // リップシンク更新
        UpdateLipSync();
    }

    /// <summary>
    /// リップシンク更新
    /// FaceAnimatorControllerを参考に実装
    /// リップシンク用テクスチャを、音声レベルに応じて更新
    /// 注意：リップシンク画像がない場合はこのメソッドは実行されない
    /// </summary>
    private void UpdateLipSync() {
        if (lipSyncStates.Count == 0) {
            return; // リップシンク用テクスチャがない場合はスキップ
        }

        const float lipLevelHoldSeconds = 0.10f;    // レベル駆動の保持時間（FaceAnimatorControllerと同じ）
        const float lipEpsilon = 0.02f;             // レベル駆動の最小閾値
        const float mouthGain = 1.8f;               // 開口度ゲイン
        
        foreach (var kvp in lipSyncStates) {
            string actorName = kvp.Key;
            AvatarLipSyncState lipState = kvp.Value;
            
            // リップシンクテクスチャが有効か確認
            if (lipState.lipSyncTextures == null || lipState.lipSyncTextures.Count == 0) {
                Debug.LogWarning($"{LogPrefix} リップシンク画像が空: {actorName}");
                continue;
            }
            
            // リップシンク GameObject が有効か確認
            if (lipState.lipSyncMaterial == null || lipState.lipSyncGameObject == null) {
                Debug.LogWarning($"{LogPrefix} リップシンク GameObject が無効: {actorName}");
                continue;
            }
            
            // リップシンクレベルが有効か判定（保持時間内かどうか）
            bool recentLip = lipState.hasLipLevel && 
                             (Time.unscaledTime - lipState.lastLipLevelTimeUnscaled) <= lipLevelHoldSeconds;
            
            if (!recentLip || lipState.mouthOpen01 < lipEpsilon) {
                // レベルが無い、または微小 → デフォルト（先頭画像）に戻す
                UpdateLipSyncTexture(actorName, 0);
                continue;
            }
            
            // 開口度を強調（ゲイン+非線形）
            float mapped = Mathf.Clamp01(Mathf.Sqrt(Mathf.Clamp01(lipState.mouthOpen01 * mouthGain)));
            
            // マッピング値をテクスチャインデックスに変換
            int idx = Mathf.Clamp(
                Mathf.RoundToInt(mapped * (lipState.lipSyncTextures.Count - 1)),
                0,
                lipState.lipSyncTextures.Count - 1
            );
            
            UpdateLipSyncTexture(actorName, idx);
        }
    }

    /// <summary>
    /// 指定アクターのリップシンク画像を更新
    /// リップシンク用マテリアルのテクスチャを設定
    /// </summary>
    private void UpdateLipSyncTexture(string actorName, int textureIndex) {
        if (!lipSyncStates.TryGetValue(actorName, out var lipState)) {
            return;
        }
        
        if (lipState.lipSyncTextures == null || lipState.lipSyncTextures.Count == 0) {
            return;
        }
        
        if (textureIndex < 0 || textureIndex >= lipState.lipSyncTextures.Count) {
            return;
        }
        
        // リップシンク用マテリアルを更新
        if (lipState.lipSyncMaterial != null) {
            var lipTexture = lipState.lipSyncTextures[textureIndex];
            lipState.lipSyncMaterial.mainTexture = lipTexture;
        } else {
            Debug.LogWarning($"{LogPrefix} リップシンク Material が null: {actorName}");
        }
    }

    /// <summary>
    /// アバターアニメーション（ペンデュラム）を更新
    /// </summary>
    private void UpdateAvatarAnimations() {
        float deltaTime = Time.deltaTime;

        foreach (var kvp in animationStates) {
            string actorName = kvp.Key;
            AvatarAnimationState state = kvp.Value;

            // アクター設定を取得
            var actor = cachedActors.FirstOrDefault(a => a.actorName == actorName);
            if (actor == null) continue;

            // 待機フェーズ中か判定
            if (state.isInCycleDelay) {
                state.cycleDelayElapsedTime += deltaTime;

                // 待機時間を超過したらアニメーションフェーズに戻る
                if (state.cycleDelayElapsedTime >= actor.avatarAnimationWaitSeconds) {
                    state.isInCycleDelay = false;
                    state.cycleDelayElapsedTime = 0f;
                    state.elapsedTime = 0f;
                }
                continue;  // ← 待機フェーズ中は処理をスキップ
            } else {
                // アニメーション（フレーム進行）フェーズ
                state.elapsedTime += deltaTime;

                // フレーム時間に達したかチェック（msから秒に変換）
                float frameDurationSeconds = actor.avatarAnimationIntervalMs / 1000f;
                if (state.elapsedTime >= frameDurationSeconds) {
                    state.elapsedTime = 0f;

                    // 次のインデックスを計算
                    int nextIndex = state.currentIndex + state.direction;

                    // 境界チェックと方向変更
                    bool cycleCompleted = false;
                    if (nextIndex >= state.textures.Count) {
                        // 終端を超過 → 逆方向に + インデックスを戻す
                        nextIndex = state.textures.Count - 2;
                        state.direction = -1;
                    } else if (nextIndex < 0) {
                        // 最初より前 → サイクル完了
                        nextIndex = 0;
                        state.direction = 1;
                        cycleCompleted = true;  // 最初に戻った時点でサイクル完了
                        if (actor.avatarAnimationWaitSeconds > 0f) {
                            state.isInCycleDelay = true;
                            state.cycleDelayElapsedTime = 0f;
                        }
                    }

                    state.currentIndex = nextIndex;

                    // テクスチャを更新
                    if (state.currentIndex >= 0 && state.currentIndex < state.textures.Count) {
                        var texture = state.textures[state.currentIndex];
                        if (state.material != null) {
                            state.material.mainTexture = texture;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// リップシンク状態を初期化
    /// 3D Mesh を使用してアニメーション画像の上にリップシンク口を重ねる
    /// NDI カメラで映るようにするため 3D Mesh は必須
    /// </summary>
    private void InitializeLipSyncState(ActorConfig actor) {
        if (actor.avatarLipSyncPaths == null || actor.avatarLipSyncPaths.Count == 0) {
            lipSyncStates.Remove(actor.actorName);
            return;
        }

        // すべてのリップシンク画像をロード
        var lipSyncTextures = new List<Texture2D>();
        foreach (var path in actor.avatarLipSyncPaths) {
            var texture = LoadTextureFromPath(path);
            if (texture != null) {
                lipSyncTextures.Add(texture);
            }
        }

        if (lipSyncTextures.Count == 0) {
            Debug.LogWarning($"{LogPrefix} リップシンク画像がロードできません: {actor.actorName}");
            lipSyncStates.Remove(actor.actorName);
            return;
        }

        // リップシンク用 3D GameObject を作成
        if (actorAvatarUIMap.TryGetValue(actor.actorName, out var avatarImage)) {
            var avatarGo = avatarImage.gameObject;
            var avatarRect = avatarGo.GetComponent<RectTransform>();
            
            // アバターのサイズを取得
            Vector2 avatarSize = avatarRect.sizeDelta;
            if (avatarSize.x <= 0) avatarSize.x = 100f;
            if (avatarSize.y <= 0) avatarSize.y = 100f;
            
            // リップシンク用 3D GameObject を作成
            var lipSyncGo = new GameObject($"{actor.actorName}_LipSync_3D");
            lipSyncGo.transform.SetParent(avatarGo.transform);
            
            // アバター画像と同じワールド位置に配置
            var lipSyncTransform = lipSyncGo.transform;
            lipSyncTransform.localPosition = new Vector3(0, 0, -0.1f); // Z を手前に設定
            lipSyncTransform.localRotation = Quaternion.identity;
            lipSyncTransform.localScale = Vector3.one;
            
            // MeshFilter と MeshRenderer を追加
            var meshFilter = lipSyncGo.AddComponent<MeshFilter>();
            var meshRenderer = lipSyncGo.AddComponent<MeshRenderer>();
            
            // Quad メッシュを作成（アバターと同じサイズ）
            Mesh lipSyncMesh = CreateQuadMesh(avatarSize.x, avatarSize.y, avatarRect.pivot);
            meshFilter.mesh = lipSyncMesh;
            
            // マテリアルを作成（最初のリップシンク画像を表示）
            Material lipSyncMat = new Material(Shader.Find("Unlit/Transparent"));
            lipSyncMat.mainTexture = lipSyncTextures[0];
            lipSyncMat.renderQueue = 3001; // 手前に描画
            meshRenderer.material = lipSyncMat;
            
            // レンダリング設定
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            
            // リップシンク状態を作成
            var lipSyncState = new AvatarLipSyncState {
                lipSyncTextures = lipSyncTextures,
                mouthOpen01 = 0f,
                hasLipLevel = false,
                lastLipLevelTimeUnscaled = 0f,
                lipSyncMaterial = lipSyncMat,
                lipSyncGameObject = lipSyncGo
            };
            lipSyncStates[actor.actorName] = lipSyncState;
            
            // リップシンク GameObject の初期表示状態を主アバター画像と同じにする
            // avatarShowWithTalk が OFF なら常に表示、ON なら最初は非表示（リップシンク時に表示）
            //
            // 注意:
            // - 以降の表示切替は SetAvatarVisibility() で GameObject の active を切り替える前提なので、
            //   ここで MeshRenderer.enabled を弄ると「発話しても表示されない」状態になる。
            // - 初期状態は GameObject 側で統一して管理する。
            bool shouldDisplayLipSync = !actor.avatarShowWithTalk;
            if (meshRenderer != null) {
                meshRenderer.enabled = true; // 常に有効化（可視性は GameObject 側で制御）
            }
            lipSyncGo.SetActive(shouldDisplayLipSync);
            
            Debug.Log($"{LogPrefix} リップシンク状態を初期化: {actor.actorName} count={lipSyncTextures.Count} go={lipSyncGo.name} shouldDisplay={shouldDisplayLipSync} (ShowWithTalk={actor.avatarShowWithTalk})") ;
        } else {
            Debug.LogWarning($"{LogPrefix} アバター UI が見つかりません: {actor.actorName}");
            lipSyncStates.Remove(actor.actorName);
        }
    }

    /// <summary>
    /// ファイルパスから Texture2D を読み込み
    /// </summary>
    private Texture2D LoadTextureFromPath(string path) {
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

    /// <summary>
    /// リップシンクレベルを受信（CentralManagerから通知、複数話者対応）
    /// </summary>
    private void HandleLipSyncLevel(float level01, string actorName) {
        // actorNameが指定されている場合は該当アクターを優先（リップシンク画像が無くても表示させる）
        if (!string.IsNullOrEmpty(actorName)) {
            if (lipSyncStates.TryGetValue(actorName, out var lipState)) {
                lipState.mouthOpen01 = Mathf.Clamp01(level01);
                lipState.hasLipLevel = true;
                lipState.lastLipLevelTimeUnscaled = Time.unscaledTime;
                Debug.Log($"{LogPrefix} HandleLipSyncLevel: {actorName} level={level01:F4}");
            }

            var actor = CentralManager.Instance?.GetActorByName(actorName);
            if (actor == null) {
                Debug.LogWarning($"{LogPrefix} HandleLipSyncLevel: actorName={actorName} に対応するActorが見つかりません");
                return; // 存在しない場合は他アクターを巻き込まず終了
            }

            // 該当アクターのアバターを表示（発話中）。リップシンク画像がなくても表示だけは行う。
            ShowAvatarIfNeeded(actor);
            return;
        }

        // actorNameが未指定の場合は全アクターに適用（後方互換）
        if (lipSyncStates.Count > 0) {
            foreach (var kvp in lipSyncStates) {
                kvp.Value.mouthOpen01 = Mathf.Clamp01(level01);
                kvp.Value.hasLipLevel = true;
                kvp.Value.lastLipLevelTimeUnscaled = Time.unscaledTime;
            }
        }
        // 旧仕様（DiscordTargetUser依存）は廃止。speaker特定できない場合は表示制御しない。
    }
    
    /// <summary>
    /// 字幕終了時のハンドラ（アバター非表示）
    /// </summary>
    private void HandleSubtitleEnded(string channel) {
        Debug.Log($"{LogPrefix} 字幕表示終了: {channel}");
        var actor = ResolveActorBySubtitleChannel(channel);
        if (actor == null) {
            Debug.LogWarning($"{LogPrefix} 字幕チャンネルからアクターを特定できません: {channel}");
            return; // 不明なチャンネルでは何もしない（他アクターを巻き込まない）
        }
        HideAvatarsIfNeeded(actor);
    }
    
    /// <summary>
    /// 指定アクターを表示（Show with Talk ON のみ対象）
    /// 複数話者対応のため、他アクターの表示状態は変更しない
    /// </summary>
    private void ShowAvatarIfNeeded(ActorConfig targetActor) {
        if (targetActor == null) return;
        if (!targetActor.avatarShowWithTalk) return; // OFF なら対象外

        if (SetAvatarVisibility(targetActor, true)) {
            Debug.Log($"{LogPrefix} アバター表示: {targetActor.actorName}");
        }
    }
    
    /// <summary>
    /// avatarShowWithTalk が ON なアクターのアバターを非表示
    /// 顔画像とリップシンク画像を同期して非表示
    /// </summary>
    private void HideAvatarsIfNeeded(ActorConfig targetActor = null) {
        foreach (var actor in cachedActors) {
            if (!actor.avatarShowWithTalk) continue; // OFF なら対象外

            // targetActor が指定されていればそのアクターのみを非表示
            if (targetActor != null && actor.actorName != targetActor.actorName) continue;

            if (SetAvatarVisibility(actor, false)) {
                Debug.Log($"{LogPrefix} アバター非表示: {actor.actorName}");
            }
        }
    }

    /// <summary>
    /// 字幕チャンネル名から対応するアクターを特定
    /// 既定: 「{actorName}_subtitle」の命名規則
    /// </summary>
    private ActorConfig ResolveActorBySubtitleChannel(string channel) {
        if (string.IsNullOrEmpty(channel)) return null;

        const string suffix = "_subtitle";
        if (channel.EndsWith(suffix)) {
            string actorName = channel.Substring(0, channel.Length - suffix.Length);
            return cachedActors.FirstOrDefault(a => a.actorName == actorName);
        }

        // 命名規則外は未対応（将来の拡張余地）
        return null;
    }

    /// <summary>
    /// 3D空間で表示するためのQuadメッシュを作成
    /// </summary>
    private Mesh CreateQuadMesh(float width, float height) {
        Mesh mesh = new Mesh();
        mesh.name = "AvatarQuad";
        
        // 頂点を定義（中心を原点として）
        float halfWidth = width * 0.5f;
        float halfHeight = height * 0.5f;
        
        Vector3[] vertices = new Vector3[4] {
            new Vector3(-halfWidth, -halfHeight, 0), // 左下
            new Vector3(halfWidth, -halfHeight, 0),  // 右下
            new Vector3(-halfWidth, halfHeight, 0),  // 左上
            new Vector3(halfWidth, halfHeight, 0)     // 右上
        };
        
        // UV座標を定義
        Vector2[] uv = new Vector2[4] {
            new Vector2(0, 0), // 左下
            new Vector2(1, 0), // 右下
            new Vector2(0, 1), // 左上
            new Vector2(1, 1)  // 右上
        };
        
        // 三角形を定義（2つの三角形でQuadを作成）
        int[] triangles = new int[6] {
            0, 2, 1, // 最初の三角形
            2, 3, 1  // 2番目の三角形
        };
        
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        
        return mesh;
    }

    /// <summary>
    /// RectTransform の pivot を考慮して Quad を生成します（uGUI の表示領域と Mesh を一致させる）。
    /// pivot=(0.5,0.5) なら従来通り中心原点。
    /// pivot がズレていても、RawImage の Rect と Mesh の表示が一致するため、ドラッグ領域ズレが出ません。
    /// </summary>
    private Mesh CreateQuadMesh(float width, float height, Vector2 pivot) {
        Mesh mesh = new Mesh();
        mesh.name = "AvatarQuad(pivoted)";

        // RectTransform のローカル座標系に合わせる:
        // 左 = -pivot.x * width, 右 = (1 - pivot.x) * width
        // 下 = -pivot.y * height, 上 = (1 - pivot.y) * height
        float left = -pivot.x * width;
        float right = (1f - pivot.x) * width;
        float bottom = -pivot.y * height;
        float top = (1f - pivot.y) * height;

        Vector3[] vertices = new Vector3[4] {
            new Vector3(left,  bottom, 0), // 左下
            new Vector3(right, bottom, 0), // 右下
            new Vector3(left,  top,    0), // 左上
            new Vector3(right, top,    0)  // 右上
        };

        Vector2[] uv = new Vector2[4] {
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(0, 1),
            new Vector2(1, 1)
        };

        int[] triangles = new int[6] {
            0, 2, 1,
            2, 3, 1
        };

        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        return mesh;
    }

    /// <summary>
    /// アバター表示用 MeshRenderer を取得（複数枚/単枚を意識せずに扱うためのヘルパー）
    /// </summary>
    private bool TryGetAvatarRenderer(string actorName, out MeshRenderer renderer) {
        renderer = null;
        if (animationStates.TryGetValue(actorName, out var animState) && animState.meshRenderer != null) {
            renderer = animState.meshRenderer;
            return true;
        }
        if (avatarMeshRendererMap.TryGetValue(actorName, out var baseRenderer) && baseRenderer != null) {
            renderer = baseRenderer;
            return true;
        }
        return false;
    }

    /// <summary>
    /// アバター（顔＋リップシンク）の表示・非表示を一括で切り替える
    /// </summary>
    private bool SetAvatarVisibility(ActorConfig actor, bool visible) {
        bool changed = false;
        
        if (actorAvatarUIMap.TryGetValue(actor.actorName, out var image)) {
            if (image.enabled != visible) {
                image.enabled = visible;
                changed = true;
            }
        }
        
        if (TryGetAvatarRenderer(actor.actorName, out var renderer)) {
            if (renderer.enabled != visible) {
                renderer.enabled = visible;
                changed = true;
            }
        }
        
        if (lipSyncStates.TryGetValue(actor.actorName, out var lipState) && lipState.lipSyncGameObject != null) {
            if (lipState.lipSyncGameObject.activeSelf != visible) {
                lipState.lipSyncGameObject.SetActive(visible);
                changed = true;
            }
            // 念のため MeshRenderer.enabled も同期（過去状態で無効になっていると表示されないため）
            var mr = lipState.lipSyncGameObject.GetComponent<MeshRenderer>();
            if (mr != null && mr.enabled != visible) {
                mr.enabled = visible;
                changed = true;
            }
        }
        
        return changed;
    }
    
}
