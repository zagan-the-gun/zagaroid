using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System;
using System.Collections.Generic;



public class LogCanvasController : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI logText; // Log CanvasのText要素を指定
    // [SerializeField] private InputField logText; // Log CanvasのText要素を指定
    [SerializeField] private ScrollRect scrollRect; // スクロールを制御するScrollRect
    private List<string> logMessages = new List<string>(); // ログメッセージを保持するリスト
    private const int maxLines = 100; // 最大行数
    private RectTransform contentRectTransform; // ContentのRectTransform

    void OnEnable()
    {
        // ログメッセージを受信するイベントを登録
        Application.logMessageReceived += HandleLog;
    }

    void OnDisable()
    {
        // イベントの登録を解除
        Application.logMessageReceived -= HandleLog;
    }

    void Start()
    {
        // 初期化
        logText.text = ""; // 最初は空にする
        contentRectTransform = scrollRect.content; // ContentのRectTransformを取得
        // UpdateContentSize(); // 初期サイズを更新
    }

    private void HandleLog(string logString, string stackTrace, LogType type) {
        // ログメッセージを追加
        logMessages.Add($"[{DateTime.Now:HH:mm:ss}] {logString}"); // メッセージを追加
        // 最大行数を超えた場合、古い行を削除
        if (logMessages.Count > maxLines) {
            logMessages.RemoveAt(0); // 最初の行を削除
        }

        logText.text = string.Join("\n", logMessages); // Text要素に表示
        UpdateContentSize(); // Contentのサイズを更新
        UpdateScrollPosition(); // スクロール位置を更新
    }

    // ContentのサイズをLog Textに同期させる
    private void UpdateContentSize()
    {
        // Textのサイズに基づいてContentのサイズを更新
        float preferredHeight = logText.GetPreferredValues().y + 10.0f; // Textの推奨高さを取得
        contentRectTransform.sizeDelta = new Vector2(contentRectTransform.sizeDelta.x, preferredHeight); // Contentのサイズを更新
    }

    // スクロールバーの位置の更新
    private void UpdateScrollPosition()
    {
        // スクロールを一番下に移動
        scrollRect.verticalNormalizedPosition = 0; 
    }
}