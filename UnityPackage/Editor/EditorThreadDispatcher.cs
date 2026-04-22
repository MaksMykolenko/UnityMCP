// UnityMCP — EditorThreadDispatcher.cs
// Thread-safe dispatcher: routes work from background TCP thread → Unity main thread.
// Uses ConcurrentQueue + EditorApplication.update (main thread callback) pattern.

using System;
using System.Collections.Concurrent;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Routes <see cref="Action"/> delegates from any background thread to Unity's main thread.
    /// Required because the Unity API is NOT thread-safe.
    ///
    /// Usage (from any thread):
    ///   EditorThreadDispatcher.Enqueue(() => Debug.Log("on main thread!"));
    /// </summary>
    [InitializeOnLoad]
    public static class EditorThreadDispatcher
    {
        // ConcurrentQueue is safe to Enqueue from multiple threads simultaneously.
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        static EditorThreadDispatcher()
        {
            // Hook into the Editor update loop — runs on the main thread every frame.
            EditorApplication.update += Flush;

            // Clean up before script reloads to avoid stale callbacks.
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                EditorApplication.update -= Flush;
            };
        }

        /// <summary>Enqueue an action to be executed on Unity's main thread.</summary>
        public static void Enqueue(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _queue.Enqueue(action);
        }

        /// <summary>Number of actions currently waiting to be executed.</summary>
        public static int PendingCount => _queue.Count;

        // Called every Editor frame on the main thread.
        private static void Flush()
        {
            // Process at most 32 items per frame to avoid stalling the Editor.
            int processed = 0;
            while (processed < 32 && _queue.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UnityMCP] EditorThreadDispatcher: unhandled exception in enqueued action:\n{ex}");
                }
                processed++;
            }
        }
    }
}
