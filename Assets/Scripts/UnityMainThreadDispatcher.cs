using System;
using System.Collections.Generic;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour {
    private static readonly Queue<Action> _executionQueue = new Queue<Action>();
    private static UnityMainThreadDispatcher _instance = null;

    public static UnityMainThreadDispatcher Instance() {
        if (_instance == null) {
            // シーンでインスタンスを探す
            _instance = FindObjectOfType<UnityMainThreadDispatcher>();
            
            if (_instance == null) {
                // なければ作成
                var go = new GameObject("UnityMainThreadDispatcher");
                _instance = go.AddComponent<UnityMainThreadDispatcher>();
                DontDestroyOnLoad(go);
            }
        }
        return _instance;
    }

    void Update() {
        lock(_executionQueue) {
            while (_executionQueue.Count > 0) {
                var action = _executionQueue.Dequeue();
                try {
                    action.Invoke();
                } catch (Exception e) {
                    Debug.LogError($"UnityMainThreadDispatcher: {e.Message}");
                }
            }
        }
    }

    /// <summary>
    /// メインスレッドでActionを実行するためにキューに追加
    /// </summary>
    public void Enqueue(Action action) {
        lock (_executionQueue) {
            _executionQueue.Enqueue(action);
        }
    }

    /// <summary>
    /// メインスレッドかどうかをチェック
    /// </summary>
    public bool IsMainThread() {
        return System.Threading.Thread.CurrentThread.ManagedThreadId == 1;
    }
} 