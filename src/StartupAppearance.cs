using System;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    // Presentation shared by the local resource bootstrap and the viewer.
    public sealed class StartupAppearance : MonoBehaviour
    {
        private static StartupAppearance instance;
        private float started;
        private AudioClip startupMusic;
        private string phase = "자료를 준비하는 중…";
        private UnityEngine.UI.Text statusText;
        private RectTransform indicator;

        public static void Begin(Component launcher)
        {
            if (instance != null) return;
            AudioListener.pause = true;
            var host = new GameObject("Costume Viewer loading screen");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<StartupAppearance>();
            var musicField = launcher.GetType().GetField("audioSourceBgm",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (musicField != null) instance.startupMusic = musicField.GetValue(launcher) as AudioClip;
            var canvas = host.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32767;
            var cover = new GameObject("Loading background", typeof(RectTransform));
            cover.transform.SetParent(host.transform, false);
            var panel = cover.AddComponent<UnityEngine.UI.Image>();
            panel.color = new Color(.055f, .065f, .085f);
            var rect = (RectTransform)cover.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var font = Font.CreateDynamicFontFromOSFont("Malgun Gothic", 24);
            AddLabel(host.transform, font, "의상 편집 뷰어", 30, -45);
            instance.statusText = AddLabel(host.transform, font, "자료를 준비하는 중…", 16, 10);
            var track = AddBar(host.transform, 300, 3, new Color(.15f, .18f, .22f));
            instance.indicator = AddBar(host.transform, 55, 3, new Color(.45f, .7f, .9f));
            instance.started = Time.realtimeSinceStartup;
            instance.Log("Loading screen opened");
        }

        public static void LoadingModel()
        {
            if (instance != null) instance.phase = "캐릭터를 불러오는 중…";
        }

        public static void Phase(string message)
        {
            if (instance != null) instance.phase = message;
        }

        public static void Fail(string message)
        {
            if (instance != null) { instance.phase = message; instance.started = Time.realtimeSinceStartup + 100000000f; }
        }

        public static void Finish()
        {
            if (instance != null) instance.Reveal().Forget();
        }

        private async UniTask Reveal()
        {
            // Let the viewer camera render before removing the cover.
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            Log("Viewer ready; loading screen closed");
            LocalBootstrap.Mark("Viewer ready");
            StopStartupMusic();
            AudioListener.pause = false;
            instance = null;
            Destroy(gameObject);
        }

        private void Log(string message)
        {
            Debug.Log("[Viewer startup] " + message);
        }

        private void StopStartupMusic()
        {
            foreach (var source in Resources.FindObjectsOfTypeAll<AudioSource>())
                if (source.clip != null && (source.clip == startupMusic ||
                    source.clip.name.StartsWith("MUSIC_", StringComparison.OrdinalIgnoreCase) ||
                    source.clip.name.StartsWith("BGM_", StringComparison.OrdinalIgnoreCase)))
                    source.Stop();
        }

        private void Update()
        {
            StopStartupMusic();
            statusText.text = phase;
            indicator.anchoredPosition = new Vector2(-122.5f + 245 * Mathf.PingPong(Time.realtimeSinceStartup * .32f, 1), -55);
            if (Time.realtimeSinceStartup - started > 150)
                phase = "자료 확인이 지연되고 있습니다. viewer.log를 확인해 주세요.";
        }

        private static UnityEngine.UI.Text AddLabel(Transform parent, Font font, string message, int size, float y)
        {
            var obj = new GameObject("Loading label", typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var text = obj.AddComponent<UnityEngine.UI.Text>();
            text.font = font;
            text.fontSize = size;
            text.text = message;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            var rect = (RectTransform)obj.transform;
            rect.sizeDelta = new Vector2(900, 70);
            rect.anchoredPosition = new Vector2(0, -y);
            return text;
        }

        private static RectTransform AddBar(Transform parent, float width, float height, Color color)
        {
            var obj = new GameObject("Loading indicator", typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var image = obj.AddComponent<UnityEngine.UI.Image>();
            image.color = color;
            image.raycastTarget = false;
            var rect = (RectTransform)obj.transform;
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(0, -55);
            return rect;
        }
    }
}
