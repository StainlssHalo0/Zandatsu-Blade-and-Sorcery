using System;
using System.Collections;
using UnityEngine;

namespace Zandatsu
{
    /// <summary>Persistent host for coroutines (ThunderScript is not a MonoBehaviour).</summary>
    public class ZandatsuRunner : MonoBehaviour
    {
        private static ZandatsuRunner instance;

        public static ZandatsuRunner Instance
        {
            get
            {
                if (!instance)
                {
                    var go = new GameObject("Zandatsu_Runner");
                    DontDestroyOnLoad(go);
                    instance = go.AddComponent<ZandatsuRunner>();
                }
                return instance;
            }
        }

        public void RunAfterRealtime(float seconds, Action action)
        {
            StartCoroutine(After(seconds, action));
        }

        private static IEnumerator After(float seconds, Action action)
        {
            yield return new WaitForSecondsRealtime(seconds);
            try { action(); }
            catch (Exception e) { Debug.LogWarning("[Zandatsu] delayed action: " + e.Message); }
        }
    }
}
