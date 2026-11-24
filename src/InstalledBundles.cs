using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace StarSaviorCostumeViewer
{
    // Resolve patched bundles from the files themselves, without a downloaded catalog.
    internal static class InstalledBundles
    {
        private sealed class Bundle
        {
            internal string Name, File;
            internal uint Crc;
            internal readonly HashSet<string> Assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        internal static async UniTask Reconcile()
        {
            string eb = Path.Combine(Application.dataPath, "eb");
            string[] files = Directory.GetFiles(eb, "*.bundle");
            string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
            string root = Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_ROOT");
            string source = File.ReadAllText(Path.Combine(root, "client-path.txt")).Trim();
            string fingerprint = ResourceIndexFingerprint.Build(catalogPath, source, files);
            string cachePath = Path.Combine(root, "installed-resource-index.json");
            var hashes = ReadBundleInfos(catalogPath).OfType<JObject>().ToDictionary(
                item => (string)item["Name"], item => (uint)item["Hash"], StringComparer.OrdinalIgnoreCase);
            var installedHashes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                uint crc;
                if (stem.Length > 32 && uint.TryParse(stem.Substring(32), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out crc))
                    installedHashes[stem.Substring(0, 32)] = crc;
            }
            var crcField = Addressables.ResourceManager.GetType().GetField("GetCrcFunc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (crcField == null) throw new InvalidDataException("Unsupported resource checksum resolver");
            foreach (var pair in hashes)
                if (File.Exists(Path.Combine(eb, pair.Key + pair.Value.ToString("x") + ".bundle"))) installedHashes[pair.Key] = pair.Value;
            var originalCrc = (Func<string, uint>)crcField.GetValue(Addressables.ResourceManager);
            Func<string, uint> resolveCrc = id =>
            {
                uint crc;
                return installedHashes.TryGetValue(Path.GetFileNameWithoutExtension(id.Replace('\\', '/')), out crc) ? crc :
                    originalCrc == null ? 0 : originalCrc(id);
            };
            crcField.SetValue(Addressables.ResourceManager, resolveCrc);
            if (Restore(cachePath, fingerprint, installedHashes)) return;
            var locations = new HashSet<IResourceLocation>();
            foreach (var locator in Addressables.ResourceLocators)
                foreach (var key in locator.Keys)
                {
                    IList<IResourceLocation> found;
                    if (locator.Locate(key, null, out found)) foreach (var location in found) Collect(location, locations);
                }
            var replacements = new JArray();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<IResourceLocation>();
            foreach (var location in locations)
            {
                var options = location.Data as AssetBundleRequestOptions;
                if (options == null) continue;
                string name = Path.GetFileNameWithoutExtension(location.InternalId.Replace('\\', '/'));
                if (name.Contains("unitybuiltinassets") || name.Contains("monoscripts")) continue;
                uint hash;
                if (!hashes.TryGetValue(name, out hash)) hash = options.Crc;
                string file = name + hash.ToString("x") + ".bundle";
                known.Add(file);
                if (!File.Exists(Path.Combine(eb, file))) missing.Add(location);
            }
            if (missing.Count == 0) { Save(cachePath, fingerprint, replacements); return; }
            Debug.Log("[Viewer local] Checking replaced bundles=" + missing.Count);
            // Traverse dependency edges once instead of scanning every asset for each missing bundle.
            var consumersByBundle = missing.ToDictionary(location => location, location => new List<IResourceLocation>());
            foreach (var asset in locations)
            {
                if (asset.Dependencies == null) continue;
                foreach (var dependency in asset.Dependencies.Distinct())
                {
                    List<IResourceLocation> consumers;
                    if (consumersByBundle.TryGetValue(dependency, out consumers)) consumers.Add(asset);
                }
            }
            var candidates = new List<Bundle>();
            foreach (string file in files.Where(file => !known.Contains(Path.GetFileName(file))))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                uint crc;
                if (stem.Length <= 32 || !uint.TryParse(stem.Substring(32), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out crc)) continue;
                var candidate = new Bundle { Name = stem.Substring(0, 32), File = file, Crc = crc };
                for (int pack = 2; pack >= 0; pack--)
                {
                    try
                    {
                        using (var raw = File.OpenRead(file))
                        using (var decoded = Open(raw, pack, candidate.Name))
                        {
                            byte[] signature = new byte[7];
                            if (decoded.Read(signature, 0, signature.Length) != 7 ||
                                System.Text.Encoding.ASCII.GetString(signature) != "UnityFS") continue;
                            decoded.Position = 0;
                            var bundle = AssetBundle.LoadFromStream(decoded, 0);
                            if (bundle == null) continue;
                            try {
                                foreach (string asset in bundle.GetAllAssetNames()) candidate.Assets.Add(asset);
                                var scenePaths = typeof(AssetBundle).GetMethod("GetAllScenePaths");
                                if (scenePaths != null) foreach (string asset in (string[])scenePaths.Invoke(bundle, null)) candidate.Assets.Add(asset);
                            }
                            finally { bundle.Unload(true); }
                            Debug.Log("[Viewer local] Indexed installed replacement " + candidate.Name + " assets=" + candidate.Assets.Count);
                            candidates.Add(candidate);
                            break;
                        }
                    }
                    catch (Exception error) { Debug.LogWarning("[Viewer local] Bundle index " + candidate.Name + " pack=" + pack + ": " + error.Message); }
                }
                await UniTask.Yield();
            }
            int repaired = 0;
            foreach (var location in missing)
            {
                var consumers = consumersByBundle[location];
                var scores = candidates.Select(bundle => new { Bundle = bundle, Count = consumers.Count(asset => bundle.Assets.Contains(asset.InternalId)) })
                    .Where(score => score.Count > 0).OrderByDescending(score => score.Count).ToArray();
                var matches = scores.Length == 0 ? new Bundle[0] : scores.Where(score => score.Count == scores[0].Count).Select(score => score.Bundle).ToArray();
                if (matches.Length != 1)
                {
                    Debug.LogWarning("[Viewer local] Unavailable bundle " + location.InternalId + " matches=" + matches.Length +
                        " candidates=" + string.Join(",", scores.Select(score => score.Bundle.Name + ":" + score.Count)));
                    continue;
                }
                var replacement = matches[0];
                installedHashes[replacement.Name] = replacement.Crc;
                var entry = new JObject { ["Key"] = new JValue(location.PrimaryKey), ["OldId"] = new JValue(location.InternalId),
                    ["Name"] = new JValue(replacement.Name), ["Crc"] = new JValue(replacement.Crc), ["Size"] = new JValue(new FileInfo(replacement.File).Length) };
                Apply(location, entry);
                replacements.Add(entry);
                repaired++;
            }
            Save(cachePath, fingerprint, replacements);
            Debug.Log("[Viewer local] Reconciled installed bundles " + repaired + "/" + missing.Count);
        }
        private static JArray ReadBundleInfos(string path)
        {
            // Bundle metadata precedes the much larger address/key arrays. Do not parse them twice.
            using (var stream = File.OpenText(path))
            using (var reader = new JsonTextReader(stream))
                while (reader.Read())
                    if (reader.TokenType == JsonToken.PropertyName && (string)reader.Value == "m_BundleInfos")
                    { reader.Read(); return JArray.Load(reader, null); }
            throw new InvalidDataException("Missing installed bundle metadata");
        }
        private static bool Restore(string path, string fingerprint, Dictionary<string, uint> hashes)
        {
            if (!File.Exists(path)) return false;
            try
            {
                var cache = JObject.Parse(File.ReadAllText(path));
                if ((string)cache["Fingerprint"] != fingerprint) return false;
                var entries = cache["Replacements"] as JArray;
                if (entries == null) return false;
                var resolved = new List<KeyValuePair<IResourceLocation, JObject>>();
                foreach (var entry in entries.OfType<JObject>())
                {
                    IResourceLocation match = null;
                    foreach (var locator in Addressables.ResourceLocators)
                    {
                        IList<IResourceLocation> found;
                        if (!locator.Locate((string)entry["Key"], null, out found)) continue;
                        match = found.FirstOrDefault(location => location.InternalId == (string)entry["OldId"] && location.Data is AssetBundleRequestOptions);
                        if (match != null) break;
                    }
                    string name = (string)entry["Name"];
                    uint crc;
                    if (match == null || !hashes.TryGetValue(name, out crc) || crc != (uint)entry["Crc"]) return false;
                    resolved.Add(new KeyValuePair<IResourceLocation, JObject>(match, entry));
                }
                if (resolved.Count != entries.Count) return false;
                foreach (var pair in resolved) Apply(pair.Key, pair.Value);
                Debug.Log("[Viewer local] Reused installed resource index; replacements=" + resolved.Count);
                return true;
            }
            catch (Exception error) { Debug.LogWarning("[Viewer local] Rebuilding resource index: " + error.Message); return false; }
        }
        private static void Save(string path, string fingerprint, JArray replacements)
        {
            try
            {
                string temporary = path + ".new";
                File.WriteAllText(temporary, new JObject { ["Fingerprint"] = new JValue(fingerprint), ["Replacements"] = replacements }.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch (Exception error) { Debug.LogWarning("[Viewer local] Resource index could not be saved: " + error.Message); }
        }
        private static void Apply(IResourceLocation location, JObject entry)
        {
            var field = location.GetType().GetField("m_InternalId", BindingFlags.Instance | BindingFlags.NonPublic) ??
                location.GetType().GetField("m_Id", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidDataException("Unsupported resource location: " + location.GetType());
            field.SetValue(location, Path.Combine(Application.dataPath, "installed-bundles", (string)entry["Name"] + ".bundle"));
            var options = (AssetBundleRequestOptions)location.Data;
            options.BundleName = (string)entry["Name"];
            options.Crc = 0; // Filename checksum is supplied by GetCrcFunc, not Unity's content CRC.
            options.BundleSize = (long)entry["Size"];
        }
        private static Stream Open(Stream stream, int pack, string name)
        {
            if (pack == 0) return stream;
            var type = typeof(AssetBundleRequestOptions).Assembly.GetType("UnityEngine.ResourceManagement.ResourceProviders.IDataConverter");
            var factory = type.GetMethod("GetInstance", BindingFlags.Static | BindingFlags.Public);
            object converter = factory.Invoke(null, new[] { Enum.ToObject(factory.GetParameters()[0].ParameterType, pack), (object)name });
            return (Stream)type.GetMethod("CreateReadStream").Invoke(converter, new object[] { stream });
        }
        private static void Collect(IResourceLocation location, HashSet<IResourceLocation> all)
        {
            if (!all.Add(location) || location.Dependencies == null) return;
            foreach (var dependency in location.Dependencies) Collect(dependency, all);
        }
    }
}
