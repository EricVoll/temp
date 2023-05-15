using System;
using System.Collections.Generic;
using UnityEngine;

namespace Microsoft.Azure.SpatialAnchors.Unity
{
    /// <summary>
    /// A helper class for dispatching actions to run on various Unity threads.
    /// </summary>
    public class ASAUnityDispatcher : MonoBehaviour
    {
        static private ASAUnityDispatcher s_instance;
        static private Queue<Action> s_queue = new Queue<Action>(8);
        static private volatile bool s_queued = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static private void Initialize()
        {
            lock (s_queue)
            {
                if (s_instance == null)
                {
                    s_instance = new GameObject("Dispatcher").AddComponent<ASAUnityDispatcher>();
                    DontDestroyOnLoad(s_instance.gameObject);
                }
            }
        }

        protected virtual void Update()
        {
            // Action placeholder
            Action action = null;

            // Do this as long as there's something in the queue
            while (s_queued)
            {
                // Lock only long enough to take an item
                lock (s_queue)
                {
                    // Get the next action
                    action = s_queue.Dequeue();

                    // Have we exhausted the queue?
                    if (s_queue.Count == 0) { s_queued = false; }
                }

                // Execute the action outside of the lock
                action();
            }
        }

        /// <summary>
        /// Schedules the specified action to be run on Unity's main thread.
        /// </summary>
        /// <param name="action">The action to run</param>
        static public void InvokeOnMainThread(Action action)
        {
            // Validate
            if (action == null) throw new ArgumentNullException(nameof(action));

            // Lock to be thread-safe
            lock (s_queue)
            {
                // Add the action
                s_queue.Enqueue(action);

                // Action is in the queue
                s_queued = true;
            }
        }
    }
}