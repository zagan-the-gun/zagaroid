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
    }

    void OnDisable() {
        StopBlinkLoop();
    }

    void Update() {
        UpdateMouthAnimationUnscaled();
    }

    // 口パク：話中のみ mouthFrames を巡回
    private void UpdateMouthAnimationUnscaled() {
        if (!isTalking || mouthImage == null) return;
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

