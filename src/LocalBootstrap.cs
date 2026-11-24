using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace StarSaviorCostumeViewer
{
    public static class LocalBootstrap
    {
        public static string ProfilePath()
        {
            string path=Path.Combine(Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_ROOT")??".","profile","data");
            Directory.CreateDirectory(path);return path;
        }
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static System.Diagnostics.Stopwatch timing;
        private static long last;
        private static JObject timings;
        internal static void Mark(string phase)
        {
            if (timing == null) return;
            long elapsed = timing.ElapsedMilliseconds;
            timings[phase] = new JObject { ["ElapsedMs"] = new JValue(elapsed), ["StageMs"] = new JValue(elapsed - last) };
            Debug.Log("[Viewer timing] " + phase + " stage=" + (elapsed - last) + "ms total=" + elapsed + "ms");
            last = elapsed;
            if (phase == "Viewer ready")
                File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_ROOT"), "startup-native-timing.json"), timings.ToString());
        }
        public static UniTaskVoid Start(Component launcher)
        {
            Run(launcher).Forget();
            return default(UniTaskVoid);
        }
        private static async UniTask Run(Component launcher)
        {
            try
            {
                timing = System.Diagnostics.Stopwatch.StartNew(); last = 0; timings = new JObject();
                Debug.Log("[Viewer local] Starting directly from installed files; no game service or download checks");
                var assembly = launcher.GetType().Assembly;
                // Local settings, audio configuration and table readers used by the native renderer.
                GameSymbols.ResolveMethod(launcher.GetType().Module,0x06000000+32928).Invoke(launcher,null);
                var property = typeof(Addressables).GetProperty("m_Addressables", Flags);
                object implementation = property.GetValue(null, null);
                var initialize = implementation.GetType().GetMethod("InitializeAsync", Flags, null,
                    new[] { typeof(string), typeof(string), typeof(bool) }, null);
                string root = Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_ROOT");
                var operation = (AsyncOperationHandle<IResourceLocator>)initialize.Invoke(implementation,
                    new object[] { Path.Combine(root, "local-addressables-settings.json"), null, false });
                await operation.Task;
                if (operation.Status != AsyncOperationStatus.Succeeded) throw operation.OperationException;
                Debug.Log("[Viewer local] Installed catalog loaded");
                Mark("Catalog");
                StartupAppearance.Phase("설치된 리소스 목록을 확인하는 중…");
                await InstalledBundles.Reconcile();
                Mark("Resource index");
                StartupAppearance.Phase("캐릭터 데이터를 읽는 중…");
                assembly.GetType("ആഇആഅഌഉഇഈഊഊആഋഊഉആ")
                    .GetMethod("ഈഈഊഋഊഋഌഊഅഌഊഋആഊഅ", Flags).Invoke(null, null);
                Debug.Log("[Viewer local] Local tables loaded");
                Mark("Tables");
                StartupAppearance.Phase("모델 표시 환경을 준비하는 중…");
                var scene = (AssetReference)launcher.GetType().GetField("sceneRootObject", Flags).GetValue(launcher);
                var handle = scene.InstantiateAsync(null, false);
                await handle.Task;
                if (handle.Status != AsyncOperationStatus.Succeeded) throw handle.OperationException;
                Debug.Log("[Viewer local] Rendering services loaded");
                Mark("Rendering services");
                launcher.gameObject.SetActive(false);
                await UniTask.Yield();
                Entry.Start(null);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                StartupAppearance.Fail("설치 리소스를 읽지 못했습니다. viewer.log를 확인해 주세요.");
            }
        }
    }
}
