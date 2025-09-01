using UnityEngine;
using System.Collections;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Canvas 配下の任意の RectTransform をドラッグで移動させるコンポーネント。
/// - 対象に Raycast を受ける Graphic が無い場合、極薄の Image を自動付与可能。
/// - 実行時にドラッグして位置を直感的に変更できます。
/// 使い方:
/// 1) FaceAnime ルート(親)の GameObject に本コンポーネントを追加
/// 2) Target Rect に移動させたい RectTransform(通常は同じ GameObject) を指定
/// 3) Canvas 指定は不要（親 RectTransform 基準で動作）
/// </summary>
public class UIDragMove : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerDownHandler, IPointerUpHandler, IInitializePotentialDragHandler {
    [Header("Target")]
    [SerializeField] private RectTransform targetRectTransform; // 未指定なら自身

    [Header("Options")]
    [SerializeField] private bool autoAddRaycastImage = true; // 受け皿Graphicが無い時に自動で薄いImageを付与
    [SerializeField] private bool enableDebugLogs = true; // 詳細ログ出力（切り分け用）

    [Header("Persistence")]
    [Tooltip("保存キーを明示。未指定なら階層パスから自動生成")]
    [SerializeField] private string positionKey = string.Empty;
    [Tooltip("親Rectに対する正規化(UV)位置で保存（解像度差に強い）")]
    [SerializeField] private bool useNormalizedPosition = true;

    [Header("Cross Canvas (optional)")]
    [Tooltip("ターゲットの親RectTransformを明示。跨ぎCanvas制御時に使用（原則、Targetの直親を指定）")]
    [SerializeField] private RectTransform referenceParentOverride;
    [Tooltip("ドラッグ入力を受ける元のRectTransform（例: MainCanvasのプレビュー枠）。未指定なら自身。")]
    [SerializeField] private RectTransform inputSourceRect;

    private const string LogPrefix = "[ZAGARO][UIDragMove]";

    private Vector2 pointerOffsetLocal; // 親ローカル空間での、ポインタ位置と anchoredPosition の差分
    private RectTransform cachedParentRect;
    private bool initialPositionApplied;

    void Awake() {
        if (targetRectTransform == null) {
            targetRectTransform = transform as RectTransform;
        }

        if (targetRectTransform == null) {
            Debug.LogWarning($"{LogPrefix} RectTransform が見つかりません。UI要素に追加してください。");
            enabled = false;
            return;
        }

        // ドラッグイベントを受けるための Graphic を確保
        Graphic graphic = GetComponent<Graphic>();
        if (graphic == null && autoAddRaycastImage) {
            Image img = gameObject.AddComponent<Image>();
            // 完全透明は端末によって Raycast を拾わないことがあるため、極薄のアルファを入れる
            img.color = new Color(0f, 0f, 0f, 0.001f);
            img.raycastTarget = true;
            if (enableDebugLogs) Debug.Log($"{LogPrefix} Added thin Image raycast holder on {name}");
        } else if (graphic != null) {
            graphic.raycastTarget = true;
            if (enableDebugLogs) Debug.Log($"{LogPrefix} Using existing Graphic for raycast on {name}: {graphic.GetType().Name}");
        }

        cachedParentRect = referenceParentOverride != null ? referenceParentOverride : targetRectTransform.parent as RectTransform;
        if (inputSourceRect == null) {
            inputSourceRect = transform as RectTransform;
        }

        if (enableDebugLogs) {
            var parentCanvas = cachedParentRect != null ? cachedParentRect.GetComponentInParent<Canvas>() : null;
            var srcCanvas = inputSourceRect != null ? inputSourceRect.GetComponentInParent<Canvas>() : null;
            var parentRay = parentCanvas != null ? parentCanvas.GetComponent<GraphicRaycaster>() : null;
            var srcRay = srcCanvas != null ? srcCanvas.GetComponent<GraphicRaycaster>() : null;
            Debug.Log($"{LogPrefix} Awake target={SafeName(targetRectTransform)} parent={SafeName(cachedParentRect)} src={SafeName(inputSourceRect)} parentCanvas={(parentCanvas!=null?parentCanvas.renderMode.ToString():"null")} parentCam={(parentCanvas!=null?SafeName(parentCanvas.worldCamera):"null")} srcCanvas={(srcCanvas!=null?srcCanvas.renderMode.ToString():"null")} srcCam={(srcCanvas!=null?SafeName(srcCanvas.worldCamera):"null")} parentRaycaster={(parentRay!=null)} srcRaycaster={(srcRay!=null)} eventSystem={(EventSystem.current!=null)}");
            if (parentCanvas != null && parentCanvas.renderMode != RenderMode.ScreenSpaceOverlay && parentCanvas.worldCamera == null) {
                Debug.LogWarning($"{LogPrefix} Parent canvas requires worldCamera but is null: {parentCanvas.name}");
            }
            if (srcCanvas != null && srcCanvas.renderMode != RenderMode.ScreenSpaceOverlay && srcCanvas.worldCamera == null) {
                Debug.LogWarning($"{LogPrefix} Source canvas requires worldCamera but is null: {srcCanvas.name}");
            }
            if (parentCanvas != null && parentRay == null) {
                Debug.LogWarning($"{LogPrefix} Missing GraphicRaycaster on parent canvas: {parentCanvas.name}");
            }
            if (srcCanvas != null && srcRay == null) {
                Debug.LogWarning($"{LogPrefix} Missing GraphicRaycaster on source canvas: {srcCanvas.name}");
            }
        }
    }

    IEnumerator Start() {
        // 親Rectサイズが確定するまで待機（最大10フレーム）
        RectTransform parentRect = cachedParentRect != null ? cachedParentRect : targetRectTransform.parent as RectTransform;
        int safety = 0;
        while ((parentRect == null) || parentRect.rect.width < 2f || parentRect.rect.height < 2f) {
            if (safety++ > 10) break;
            yield return null;
            Canvas.ForceUpdateCanvases();
            parentRect = cachedParentRect != null ? cachedParentRect : targetRectTransform.parent as RectTransform;
        }

        LoadPosition();

        // 復元後にレイアウトと描画を確実に更新
        if (parentRect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(parentRect);
        LayoutRebuilder.ForceRebuildLayoutImmediate(targetRectTransform);
        Canvas.ForceUpdateCanvases();
        var graphic = targetRectTransform.GetComponent<Graphic>();
        if (graphic != null) graphic.SetAllDirty();
    }

    public void OnBeginDrag(PointerEventData eventData) {
        if (targetRectTransform == null) return;

        RectTransform parentRect = cachedParentRect != null ? cachedParentRect : targetRectTransform.parent as RectTransform;
        if (parentRect == null) return;

        if (inputSourceRect != null) {
            // 入力元Rect上のローカル座標 → UV → 親Rectローカル座標へ写像
            Camera srcCam = GetCanvasEventCameraFor(inputSourceRect);
            Vector2 localOnSrc;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(inputSourceRect, eventData.position, srcCam, out localOnSrc)) {
                Vector2 uv = LocalPointToUv(inputSourceRect, localOnSrc);
                Vector2 mappedParentLocal = UvToParentLocal(parentRect, uv);
                pointerOffsetLocal = mappedParentLocal - targetRectTransform.anchoredPosition;
                if (enableDebugLogs) Debug.Log($"{LogPrefix} BeginDrag screen={eventData.position} srcLocal={localOnSrc} uv={uv} parentLocal={mappedParentLocal} offset={pointerOffsetLocal}");
            } else {
                pointerOffsetLocal = Vector2.zero;
                if (enableDebugLogs) Debug.Log($"{LogPrefix} BeginDrag map failed: screen -> src local. screen={eventData.position}");
            }
        } else {
            // 従来: 親Rectローカルへ直接変換
            Camera cam = GetCanvasEventCameraFor(parentRect);
            Vector2 localPointOnParent;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, eventData.position, cam, out localPointOnParent)) {
                pointerOffsetLocal = localPointOnParent - targetRectTransform.anchoredPosition;
                if (enableDebugLogs) Debug.Log($"{LogPrefix} BeginDrag direct parentLocal={localPointOnParent} offset={pointerOffsetLocal}");
            } else {
                pointerOffsetLocal = Vector2.zero;
                if (enableDebugLogs) Debug.Log($"{LogPrefix} BeginDrag map failed: screen -> parent local. screen={eventData.position}");
            }
        }
    }

    public void OnDrag(PointerEventData eventData) {
        if (targetRectTransform == null) return;

        RectTransform parentRect = cachedParentRect != null ? cachedParentRect : targetRectTransform.parent as RectTransform;
        if (parentRect == null) return;

        Vector2 desiredParentLocal;
        if (inputSourceRect != null) {
            // 入力元Rect: UVで親へマッピング
            Camera srcCam = GetCanvasEventCameraFor(inputSourceRect);
            Vector2 localOnSrc;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(inputSourceRect, eventData.position, srcCam, out localOnSrc)) {
                if (enableDebugLogs) Debug.Log($"{LogPrefix} Drag map failed: screen -> src local. screen={eventData.position}");
                return;
            }
            Vector2 uv = LocalPointToUv(inputSourceRect, localOnSrc);
            desiredParentLocal = UvToParentLocal(parentRect, uv) - pointerOffsetLocal;
            if (enableDebugLogs) Debug.Log($"{LogPrefix} Drag srcLocal={localOnSrc} uv={uv} desiredParentLocal(beforeClamp)={desiredParentLocal}");
        } else {
            // 従来: 親Rectローカルへ直接変換
            Camera cam = GetCanvasEventCameraFor(parentRect);
            Vector2 worldToLocalOnParent;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, eventData.position, cam, out worldToLocalOnParent)) {
                if (enableDebugLogs) Debug.Log($"{LogPrefix} Drag map failed: screen -> parent local. screen={eventData.position}");
                return;
            }
            desiredParentLocal = worldToLocalOnParent - pointerOffsetLocal;
            if (enableDebugLogs) Debug.Log($"{LogPrefix} Drag desiredParentLocal(beforeClamp)={desiredParentLocal}");
        }

        Vector2 newAnchoredPos = ClampToParent(parentRect, targetRectTransform, desiredParentLocal);
        targetRectTransform.anchoredPosition = newAnchoredPos;
    }

    public void OnEndDrag(PointerEventData eventData) {
        SavePosition();
    }

    public void OnPointerDown(PointerEventData eventData) {
        if (!enableDebugLogs) return;
        Debug.Log($"{LogPrefix} PointerDown screen={eventData.position} button={eventData.button} over={name}");
    }

    public void OnPointerUp(PointerEventData eventData) {
        if (!enableDebugLogs) return;
        Debug.Log($"{LogPrefix} PointerUp screen={eventData.position} button={eventData.button} over={name}");
    }

    public void OnInitializePotentialDrag(PointerEventData eventData) {
        // 微小移動でも BeginDrag が発火するようにする
        eventData.useDragThreshold = false;
        if (enableDebugLogs) Debug.Log($"{LogPrefix} InitPotentialDrag thresholdOff over={name}");
    }

    private static Vector2 ClampToParent(RectTransform parent, RectTransform child, Vector2 desiredAnchoredPos) {
        // 前提: 子のアンカーは固定 (anchorMin == anchorMax)。一般的なUI配置で安定。
        // 計算: 親ローカル座標において、子の矩形が親矩形からはみ出さないように anchoredPosition をクランプ。
        Vector2 parentSize = parent.rect.size;
        Vector2 childSize = child.rect.size;

        Vector2 parentPivot = parent.pivot;
        Vector2 childPivot = child.pivot;
        Vector2 anchor = child.anchorMin; // 固定アンカー前提

        // アンカー基準点（親ローカル座標）
        Vector2 anchorRef = new Vector2(
            (anchor.x - parentPivot.x) * parentSize.x,
            (anchor.y - parentPivot.y) * parentSize.y
        );

        // 親矩形のローカル境界
        float parentLeft = -parentPivot.x * parentSize.x;
        float parentRight = (1f - parentPivot.x) * parentSize.x;
        float parentBottom = -parentPivot.y * parentSize.y;
        float parentTop = (1f - parentPivot.y) * parentSize.y;

        // 子矩形のピボットから各辺までのオフセット
        float leftFromPivot = childPivot.x * childSize.x;
        float rightFromPivot = (1f - childPivot.x) * childSize.x;
        float bottomFromPivot = childPivot.y * childSize.y;
        float topFromPivot = (1f - childPivot.y) * childSize.y;

        // anchoredPosition は anchorRef からの差分（= 子ピボット位置）。
        // 子左辺 >= 親左辺, 子右辺 <= 親右辺 となる範囲を算出。
        float minX = parentLeft + leftFromPivot - anchorRef.x;
        float maxX = parentRight - rightFromPivot - anchorRef.x;
        float minY = parentBottom + bottomFromPivot - anchorRef.y;
        float maxY = parentTop - topFromPivot - anchorRef.y;

        float clampedX = Mathf.Clamp(desiredAnchoredPos.x, minX, maxX);
        float clampedY = Mathf.Clamp(desiredAnchoredPos.y, minY, maxY);
        return new Vector2(clampedX, clampedY);
    }

    private static Vector2 LocalPointToUv(RectTransform rect, Vector2 localPoint) {
        Vector2 size = rect.rect.size;
        Vector2 pivot = rect.pivot;
        float u = (localPoint.x + pivot.x * size.x) / Mathf.Max(size.x, 0.0001f);
        float v = (localPoint.y + pivot.y * size.y) / Mathf.Max(size.y, 0.0001f);
        return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
    }

    private static Vector2 UvToParentLocal(RectTransform parent, Vector2 uv) {
        Vector2 parentSize = parent.rect.size;
        Vector2 parentPivot = parent.pivot;
        float x = uv.x * parentSize.x - parentPivot.x * parentSize.x;
        float y = uv.y * parentSize.y - parentPivot.y * parentSize.y;
        return new Vector2(x, y);
    }

    private static Camera GetCanvasEventCameraFor(RectTransform rect) {
        if (rect == null) return null;
        Canvas c = rect.GetComponentInParent<Canvas>();
        if (c == null) return null;
        if (c.renderMode == RenderMode.ScreenSpaceOverlay) return null;
        return c.worldCamera;
    }

    public void SetTarget(RectTransform target) {
        targetRectTransform = target;
        cachedParentRect = referenceParentOverride != null ? referenceParentOverride : (targetRectTransform != null ? targetRectTransform.parent as RectTransform : null);
    }

    private static string SafeName(Object obj) {
        return obj != null ? obj.name : "null";
    }

    // === Persistence ===
    private string GetPositionKey() {
        if (!string.IsNullOrEmpty(positionKey)) return positionKey;
        return Application.productName + ".UIDragMove." + GetHierarchyPath(transform);
    }

    private static string GetHierarchyPath(Transform t) {
        if (t == null) return "null";
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        Transform current = t;
        // 末尾が自身になるよう後ろから組み立て
        while (current != null) {
            if (sb.Length == 0) sb.Insert(0, current.name);
            else sb.Insert(0, current.name + "/");
            current = current.parent;
        }
        return sb.ToString();
    }

    public void SavePosition() {
        if (targetRectTransform == null) return;
        RectTransform parentRect = cachedParentRect != null ? cachedParentRect : targetRectTransform.parent as RectTransform;
        if (parentRect == null) return;

        string key = GetPositionKey();

        if (useNormalizedPosition) {
            // 子ピボットの親ローカル座標
            Vector2 anchorRef = CalculateAnchorReference(parentRect, targetRectTransform);
            Vector2 pivotLocalOnParent = anchorRef + targetRectTransform.anchoredPosition;
            // 親ローカル -> 親UV
            Vector2 uv = LocalPointToUv(parentRect, pivotLocalOnParent);
            PlayerPrefs.SetFloat(key + ":u", uv.x);
            PlayerPrefs.SetFloat(key + ":v", uv.y);
            if (enableDebugLogs) Debug.Log($"{LogPrefix} Saved normalized position key={key} uv={uv}");
        } else {
            Vector2 ap = targetRectTransform.anchoredPosition;
            PlayerPrefs.SetFloat(key + ":x", ap.x);
            PlayerPrefs.SetFloat(key + ":y", ap.y);
            if (enableDebugLogs) Debug.Log($"{LogPrefix} Saved anchored position key={key} pos={ap}");
        }
        PlayerPrefs.Save();
    }

    public void LoadPosition() {
        if (targetRectTransform == null) return;
        RectTransform parentRect = cachedParentRect != null ? cachedParentRect : targetRectTransform.parent as RectTransform;
        if (parentRect == null) return;

        string key = GetPositionKey();

        if (useNormalizedPosition) {
            string ku = key + ":u";
            string kv = key + ":v";
            if (!PlayerPrefs.HasKey(ku) || !PlayerPrefs.HasKey(kv)) return;
            float u = PlayerPrefs.GetFloat(ku);
            float v = PlayerPrefs.GetFloat(kv);
            Vector2 uv = new Vector2(u, v);
            // 親UV -> 親ローカル（子ピボット）
            Vector2 pivotLocalOnParent = UvToParentLocal(parentRect, uv);
            Vector2 anchorRef = CalculateAnchorReference(parentRect, targetRectTransform);
            Vector2 desiredAnchored = pivotLocalOnParent - anchorRef;
            Vector2 clamped = ClampToParent(parentRect, targetRectTransform, desiredAnchored);
            targetRectTransform.anchoredPosition = clamped;
            if (enableDebugLogs) Debug.Log($"{LogPrefix} Loaded normalized position key={key} uv={uv} anchored={clamped}");
        } else {
            string kx = key + ":x";
            string ky = key + ":y";
            if (!PlayerPrefs.HasKey(kx) || !PlayerPrefs.HasKey(ky)) return;
            float x = PlayerPrefs.GetFloat(kx);
            float y = PlayerPrefs.GetFloat(ky);
            Vector2 desiredAnchored = new Vector2(x, y);
            Vector2 clamped = ClampToParent(parentRect, targetRectTransform, desiredAnchored);
            targetRectTransform.anchoredPosition = clamped;
            if (enableDebugLogs) Debug.Log($"{LogPrefix} Loaded anchored position key={key} anchored={clamped}");
        }
        // 復元直後のレイアウト更新で非表示/ズレを防止
        LayoutRebuilder.ForceRebuildLayoutImmediate(targetRectTransform);
        Canvas.ForceUpdateCanvases();
        initialPositionApplied = true;
    }

    void OnRectTransformDimensionsChange() {
        // レイアウト変更後、まだ適用していなければ復元を試みる
        if (initialPositionApplied) return;
        if (targetRectTransform == null) return;
        RectTransform parentRect = cachedParentRect != null ? cachedParentRect : targetRectTransform.parent as RectTransform;
        if (parentRect == null) return;

        string key = GetPositionKey();
        bool hasSaved = useNormalizedPosition
            ? (PlayerPrefs.HasKey(key + ":u") && PlayerPrefs.HasKey(key + ":v"))
            : (PlayerPrefs.HasKey(key + ":x") && PlayerPrefs.HasKey(key + ":y"));
        if (!hasSaved) return;
        if (parentRect.rect.width < 2f || parentRect.rect.height < 2f) return;
        LoadPosition();
    }

    private static Vector2 CalculateAnchorReference(RectTransform parent, RectTransform child) {
        Vector2 parentSize = parent.rect.size;
        Vector2 parentPivot = parent.pivot;
        Vector2 anchor = child.anchorMin; // 固定アンカー前提
        return new Vector2(
            (anchor.x - parentPivot.x) * parentSize.x,
            (anchor.y - parentPivot.y) * parentSize.y
        );
    }
}

