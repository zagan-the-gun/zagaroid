using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class FaceAnimatorController : MonoBehaviour {
    [Header("Images (uGUI)")]
    [SerializeField] private Image baseFaceImage; // 任意（輪郭）
    [SerializeField] private Image eyesImage;     // 必須（目）
    [SerializeField] private Image mouthImage;    // 必須（口）

    [Header("Frames (Sprites)")]
    [Tooltip("目のフレーム配列（例: Open/Half/Closed の順）")]
    [SerializeField] private Sprite[] eyeFrames;
    [Tooltip("口のフレーム配列（例: 0/1/2... の順で巡回）")]
    [SerializeField] private Sprite[] mouthFrames;

    [Header("Blink Settings")]
    [SerializeField] private float blinkIntervalMinSeconds = 3.0f;
    [SerializeField] private float blinkIntervalMaxSeconds = 6.0f;
    [SerializeField] private float blinkHalfCloseSeconds = 0.05f;
    [SerializeField] private float blinkClosedSeconds = 0.08f;

    [Header("Mouth Settings")]
    [SerializeField] private bool talkingOnStart = false;
    [SerializeField] private float mouthFpsWhenTalking = 10.0f; // 話中のみ巡回
    private const float lipLevelHoldSeconds = 0.10f; // レベル駆動の保持時間（固定）
    private const float lipEpsilon = 0.02f; // レベル駆動の最小閾値（固定）
    private const float mouthGain = 1.8f; // 開口度ゲイン
    private const float speakingMinMouth = 0.12f; // 発話時の最小開口度
    private const float speakingLevelEpsilon = 0.02f; // 発話自己判定用の最小レベル

    [Header("Interaction")]
    [SerializeField] private bool enableDragAtRuntime = true; // 実行中のドラッグ移動を有効化

    private const string LogPrefix = "[FaceAnimator]";

    private bool isTalking;
    private int currentMouthFrameIndex;
    private float mouthFrameDuration;
    private float mouthFrameTimerUnscaled;

    private bool isBlinking;
    private Coroutine blinkLoopCoroutine;

    private bool warnedMissingEyesOnce;
    private bool warnedMissingMouthOnce;

    // リップシンク制御用
    private float mouthOpen01;
    private bool hasLipLevel;
    private float lastLipLevelTimeUnscaled;

    void Awake() {
        // 初期ガード
        if (eyesImage == null) {
            Debug.LogWarning($"{LogPrefix} eyesImage が未設定です。まばたきは無効になります。");
        }
        if (mouthImage == null) {
            Debug.LogWarning($"{LogPrefix} mouthImage が未設定です。口パクは無効になります。");
        }

        // 初期スプライト
        if (eyesImage != null && eyeFrames != null && eyeFrames.Length > 0) {
            eyesImage.sprite = eyeFrames[0];
        }
        if (mouthImage != null && mouthFrames != null && mouthFrames.Length > 0) {
            mouthImage.sprite = mouthFrames[0];
        }

        // Canvasコスト最適化
        if (baseFaceImage != null) baseFaceImage.raycastTarget = false;
        if (eyesImage != null) eyesImage.raycastTarget = false;
        if (mouthImage != null) mouthImage.raycastTarget = false;

        SetTalking(talkingOnStart);
        mouthFrameDuration = mouthFpsWhenTalking > 0 ? 1.0f / mouthFpsWhenTalking : 0f;

        // 実行時ドラッグの自動付与（UI の RectTransform 前提）
        if (enableDragAtRuntime) {
            RectTransform rectTransform = transform as RectTransform;
            if (rectTransform != null) {
                var drag = gameObject.GetComponent<UIDragMove>();
                if (drag == null) {
                    drag = gameObject.AddComponent<UIDragMove>();
                }
                drag.SetTarget(rectTransform);
            } else {
                Debug.LogWarning($"{LogPrefix} RectTransform が見つかりません。UI要素に配置されていないためドラッグは無効です。");
            }
        }
    }

    void OnEnable() {
        StartBlinkLoop();
        // リップシンクイベント購読
        CentralManager.OnLipSyncLevel += HandleLipSyncLevel;
        // 発話状態はレベルから自己判定に変更（SpeakingChangedは購読しない）
    }

    void OnDisable() {
        StopBlinkLoop();
        // リップシンクイベント購読解除
        CentralManager.OnLipSyncLevel -= HandleLipSyncLevel;
        
    }

    void Update() {
        UpdateMouthAnimationUnscaled();
    }

    // 口パク：レベル駆動優先、微小時は話中なら巡回
    private void UpdateMouthAnimationUnscaled() {
        if (mouthImage == null) return;
        bool recentLip = hasLipLevel && (Time.unscaledTime - lastLipLevelTimeUnscaled) <= lipLevelHoldSeconds;
        if (recentLip && mouthOpen01 >= lipEpsilon) {
            UpdateMouthByLevel();
            return;
        }
        // 巡回フォールバック（発話中のみ）。speakingは自己判定
        if (!IsSpeakingByLevel()) {
            // 発話中でない場合は、最近のレベルがあればそれを反映（小さければ閉口のまま）
            if (recentLip) UpdateMouthByLevel();
            return;
        }
        if (mouthFrames == null || mouthFrames.Length == 0) {
            if (!warnedMissingMouthOnce) {
                Debug.LogWarning($"{LogPrefix} mouthFrames が空です。口パクは無効です。");
                warnedMissingMouthOnce = true;
            }
            return;
        }

        if (mouthFrameDuration <= 0f) return;

        mouthFrameTimerUnscaled += Time.unscaledDeltaTime;
        if (mouthFrameTimerUnscaled >= mouthFrameDuration) {
            mouthFrameTimerUnscaled -= mouthFrameDuration;
            currentMouthFrameIndex = (currentMouthFrameIndex + 1) % mouthFrames.Length;
            mouthImage.sprite = mouthFrames[currentMouthFrameIndex];
        }
    }

    // 外部API: 口開き度(0..1)をセット
    public void SetMouthOpen01(float openness01) {
        mouthOpen01 = Mathf.Clamp01(openness01);
        hasLipLevel = true;
        lastLipLevelTimeUnscaled = Time.unscaledTime;
        UpdateMouthByLevel();
    }

    private void UpdateMouthByLevel() {
        if (mouthImage == null) return;
        if (mouthFrames == null || mouthFrames.Length == 0) {
            if (!warnedMissingMouthOnce) {
                Debug.LogWarning($"{LogPrefix} mouthFrames が空です。口パクは無効です。");
                warnedMissingMouthOnce = true;
            }
            return;
        }
        // 開口度を強調（ゲイン+非線形）し、発話時は最小開口を保証
        bool speaking = IsSpeakingByLevel();
        float mapped = Mathf.Clamp01(Mathf.Sqrt(Mathf.Clamp01(mouthOpen01 * mouthGain)));
        if (speaking && mapped < speakingMinMouth) mapped = speakingMinMouth;

        int idx = Mathf.Clamp(Mathf.RoundToInt(mapped * (mouthFrames.Length - 1)), 0, mouthFrames.Length - 1);
        mouthImage.sprite = mouthFrames[idx];
    }

    private void HandleLipSyncLevel(float level01) {
        SetMouthOpen01(level01);
    }

    // 簡易自己判定（Face側）: 最近のレベルが閾値以上
    private bool IsSpeakingByLevel() {
        bool recentLip = hasLipLevel && (Time.unscaledTime - lastLipLevelTimeUnscaled) <= lipLevelHoldSeconds;
        return recentLip && mouthOpen01 >= Mathf.Max(lipEpsilon, speakingLevelEpsilon);
    }

    // 公開API: 話中切替
    public void SetTalking(bool talking) {
        isTalking = talking;
        if (!isTalking) {
            // 停止時は初期フレームへ
            currentMouthFrameIndex = 0;
            mouthFrameTimerUnscaled = 0f;
            if (mouthImage != null && mouthFrames != null && mouthFrames.Length > 0) {
                mouthImage.sprite = mouthFrames[0];
            }
        }
    }

    // 即時まばたきAPIは未使用のため削除

    private void StartBlinkLoop() {
        if (blinkLoopCoroutine != null) return;
        blinkLoopCoroutine = StartCoroutine(BlinkLoopRoutine());
    }

    private void StopBlinkLoop() {
        if (blinkLoopCoroutine != null) {
            StopCoroutine(blinkLoopCoroutine);
            blinkLoopCoroutine = null;
        }
        isBlinking = false;
    }

    private IEnumerator BlinkLoopRoutine() {
        // ランダム間隔で自動まばたき
        while (true) {
            float wait = Random.Range(blinkIntervalMinSeconds, blinkIntervalMaxSeconds);
            if (wait < 0f) wait = 0f;
            float elapsed = 0f;
            while (elapsed < wait) {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            // 実行中のBlinkNowがある場合はそちらを優先
            if (!isBlinking) {
                yield return BlinkOnceRoutine();
            }
        }
    }

    private IEnumerator BlinkOnceRoutine() {
        if (eyesImage == null) yield break;
        if (eyeFrames == null || eyeFrames.Length == 0) {
            if (!warnedMissingEyesOnce) {
                Debug.LogWarning($"{LogPrefix} eyeFrames が空です。まばたきは無効です。");
                warnedMissingEyesOnce = true;
            }
            yield break;
        }

        if (isBlinking) yield break; // 多重防止

        isBlinking = true;

        // Open -> Half -> Closed -> Half -> Open
        // フレーム数が3未満でも可能な範囲で遷移
        SetEyesFrameSafe(0);
        yield return null; // 1フレームだけ確実に描画更新

        if (eyeFrames.Length >= 2) {
            SetEyesFrameSafe(1);
            yield return WaitForSecondsRealtimeSafe(blinkHalfCloseSeconds);
        }

        SetEyesFrameSafe(eyeFrames.Length >= 3 ? 2 : eyeFrames.Length - 1);
        yield return WaitForSecondsRealtimeSafe(blinkClosedSeconds);

        if (eyeFrames.Length >= 2) {
            SetEyesFrameSafe(1);
            yield return WaitForSecondsRealtimeSafe(blinkHalfCloseSeconds * 0.8f);
        }

        SetEyesFrameSafe(0);
        isBlinking = false;
    }

    private void SetEyesFrameSafe(int index) {
        if (eyesImage == null) return;
        if (eyeFrames == null || eyeFrames.Length == 0) return;
        int clamped = Mathf.Clamp(index, 0, eyeFrames.Length - 1);
        eyesImage.sprite = eyeFrames[clamped];
    }

    private WaitForSecondsRealtime WaitForSecondsRealtimeSafe(float seconds) {
        if (seconds <= 0f) return new WaitForSecondsRealtime(0f);
        return new WaitForSecondsRealtime(seconds);
    }
}

// ・政治系
// ドナルド・トランプ（派手なリアクション芸がAIワイプ向き）
// プーチン（怖いのにコメントがユルいと爆笑ギャップ）
// 金正恩（北朝鮮ミームは海外でもネタにされがち）
// 安倍晋三（日本だと即炎上だけど強いインパクト）
// ネタニヤフ（国際ニュース絡みで刺さる人には刺さる）
// 金正恩
// ・歴史系
// ヒトラー（禁断感＋ネタ化されすぎて逆に笑いが取れる）
// スターリン（顔が濃くて表情の圧が強い）
// レーニン（肖像画が堅苦しいのにフランクコメントさせるとシュール）
// 毛沢東（中国関連ネタと絡めやすい）
// ナポレオン（チビいじりネタとの相性抜群）
// 織田信長（日本人なら誰でも知ってるし「魔王」ポジで映える）
// 豊臣秀吉（猿キャラ扱いでボケやすい）
// 徳川家康（「鳴くまで待とう」精神でコメントさせると笑える）
// 聖徳太子（実際に喋らせると誰だよ感MAX）
// ・事件系
// 三島由紀夫の割腹事件の写真 → シュールギャグにするとかなり尖る
// 永田洋子（連合赤軍あさま山荘事件）
// 浅原彰晃（地下鉄サリン事件）
// 明智光秀（裏切りキャラとしてAIが毒舌言うと映える）
// 坂本龍馬（爽やか顔でAIが現代ギャグ言うと違和感MAX）
// 西郷隆盛（犬連れてワイプ出すのもアリ）
// 昭和天皇（きわどいがインパクト抜群）
// ガンジー（絶対平和の顔で毒舌コメント）
// チェ・ゲバラ（Tシャツにされすぎて逆にネタ扱い）
// カストロ（葉巻吸いながら配信に乱入感）
// チャーチル（葉巻＆太っちょキャラでコミカルに寄せやすい）
// ルイ14世（豪華絢爛な肖像画がワイプで喋ると面白い）
// クレオパトラ（美女キャラなのに中身AIでズレ感）
// チンギス・ハーン（威圧感満点で「ノリ軽い」AIだと爆笑）
// ・宗教系
// イエス・キリスト（慈愛フェイスで毒舌はインパクト抜群）
// 仏陀（静かな顔でノリノリ発言するシュールさ）
// ローマ法王（最新の写真が出回ってるから即バレ感ある）
// 麻原彰晃（日本ローカルだが最強にセンシティブ）
// ・ミーム系
// 楽器ケースに隠れた誰か
// 猫ミーム
// 唐澤貴洋弁護士（恒心教ミーム）
// syamu_game（復活後の顔で喋らせると笑い取りやすい）
// ゆっくり茶番劇騒動の霊夢・魔理沙（最近も話題性あり）
// チー牛
// ひろゆき
// 中居（いいべ・・・）
// 松本（とうとうでたね・・・）
// 小室圭（ンだわ）
// ・新興宗教系
// 麻原彰晃（オウム真理教）
// 上祐史浩（オウム幹部→ひかりの輪）
// 池田大作（創価学会名誉会長）
// 大川隆法（幸福の科学）
// 文鮮明（統一教会・世界平和統一家庭連合 創始者）
