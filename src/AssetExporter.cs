using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed class TextureBinding
    {
        internal string File, Property;
        internal Material Material;
        internal Renderer Renderer;
        internal Texture Original;
        internal bool Linear;
    }

    internal sealed class AssetExporter : IDisposable
    {
        internal readonly List<TextureBinding> Bindings = new List<TextureBinding>();
        internal PartVisibility PartFilter;
        private readonly Dictionary<string, Texture2D> replacements = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> stamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> originalHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> pending = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        private float nextScan;
        private readonly List<Material> ownedMaterials=new List<Material>();
        private sealed class AlphaSettings
        {
            internal string Enable,Cutoff;
            internal float EnabledValue,CutoffValue;
            internal bool Keyword;
            internal bool Active;
            internal readonly Dictionary<string,float> OutlineFloats=new Dictionary<string,float>();
            internal readonly Dictionary<string,bool> OutlinePasses=new Dictionary<string,bool>();
            internal readonly Dictionary<string,bool> OutlineKeywords=new Dictionary<string,bool>();
        }
        private readonly Dictionary<Material,AlphaSettings> alphaSettings=new Dictionary<Material,AlphaSettings>();
        private readonly HashSet<string> paintPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal AssetExporter(){UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering+=BeforePaintRender;}
        private void BeforePaintRender(UnityEngine.Rendering.ScriptableRenderContext context,Camera camera)
        {foreach(var pair in alphaSettings)if(pair.Key!=null&&pair.Value.Active)ApplyAlpha(pair.Key,pair.Value,true);}
        private static void SetFloat(Material material,string name,float value)
        {if(Mathf.Abs(material.GetFloat(name)-value)>1e-6f)material.SetFloat(name,value);}
        private static void SetKeyword(Material material,string name,bool enabled)
        {if(material.IsKeywordEnabled(name)==enabled)return;if(enabled)material.EnableKeyword(name);else material.DisableKeyword(name);}
        private bool forceOutlineSuppression;
        private static bool IsOutline(string name){return !string.IsNullOrEmpty(name)&&name.IndexOf("outline",StringComparison.OrdinalIgnoreCase)>=0;}
        private static bool OutlineClipProperty(string name){string s=name.ToLowerInvariant();return IsOutline(s)&&s.Contains("alpha")&&(s.Contains("clip")||s.Contains("cutoff")||s.Contains("test"))&&!s.Contains("channel")&&!s.Contains("uv")&&!s.Contains("remap");}
        private static void CaptureOutline(Material material,AlphaSettings settings)
        {
            var shader=material.shader;
            for(int i=0;i<shader.GetPropertyCount();i++)
            {
                string name=shader.GetPropertyName(i);var type=shader.GetPropertyType(i);
                if(OutlineClipProperty(name)&&(type==UnityEngine.Rendering.ShaderPropertyType.Float||type==UnityEngine.Rendering.ShaderPropertyType.Range||type==UnityEngine.Rendering.ShaderPropertyType.Int))
                {
                    settings.OutlineFloats[name]=material.GetFloat(name);
                    foreach(string attribute in shader.GetPropertyAttributes(i))
                    {
                        if(!attribute.StartsWith("Toggle(",StringComparison.OrdinalIgnoreCase))continue;int end=attribute.IndexOf(')');if(end<8)continue;
                        string keyword=attribute.Substring(7,end-7);settings.OutlineKeywords[keyword]=material.IsKeywordEnabled(keyword);
                    }
                }
            }
            for(int i=0;i<material.passCount;i++)
            {
                string name=material.GetPassName(i);if(IsOutline(name))settings.OutlinePasses[name]=material.GetShaderPassEnabled(name);
                string tag=shader.FindPassTagValue(i,new UnityEngine.Rendering.ShaderTagId("LightMode")).name;
                if(IsOutline(tag))settings.OutlinePasses[tag]=material.GetShaderPassEnabled(tag);
            }
        }
        private void ApplyAlpha(Material material,AlphaSettings settings,bool enabled)
        {
            settings.Active=enabled;SetFloat(material,settings.Enable,enabled?1:settings.EnabledValue);SetFloat(material,settings.Cutoff,enabled?.5f:settings.CutoffValue);
            SetKeyword(material,"_ALPHATEST_ON",enabled||settings.Keyword);
            foreach(var pair in settings.OutlineFloats)
            {
                string name=pair.Key.ToLowerInvariant();bool threshold=name.Contains("cutoff")||name.Contains("threshold");
                SetFloat(material,pair.Key,enabled?(threshold?.5f:1):pair.Value);
            }
            bool suppress=enabled&&(forceOutlineSuppression||settings.OutlineFloats.Count==0);
            foreach(var pair in settings.OutlineKeywords)SetKeyword(material,pair.Key,enabled||pair.Value);
            foreach(var pair in settings.OutlinePasses){bool value=suppress?false:pair.Value;if(material.GetShaderPassEnabled(pair.Key)!=value)material.SetShaderPassEnabled(pair.Key,value);}
        }
        internal void SetOutlineSuppression(bool force)
        {forceOutlineSuppression=force;foreach(var pair in alphaSettings)ApplyAlpha(pair.Key,pair.Value,pair.Value.Active);}
        internal void ShowPaintPreview(string path,Texture2D texture)
        {texture.filterMode=FilterMode.Bilinear;texture.anisoLevel=1;foreach(var binding in Bindings.Where(b=>string.Equals(b.File,path,StringComparison.OrdinalIgnoreCase)))binding.Material.SetTexture(binding.Property,texture);}
        internal bool EnablePaintAlpha(string path,bool enabled)
        {
            if(enabled)paintPaths.Add(path);else paintPaths.Remove(path);
            bool supported=false;
            foreach(var binding in Bindings.Where(b=>string.Equals(b.File,path,StringComparison.OrdinalIgnoreCase)&&
                (b.Property=="_BaseMap"||b.Property=="_MainTex"||b.Property=="_BaseColorMap")))
            {
                var material=binding.Material;AlphaSettings settings;
                if(!alphaSettings.TryGetValue(material,out settings))
                {
                    string enable=new[]{"_AlphaClip","_UseAlphaClipping","_AlphaCutoffEnable","_UseAlphaClip"}.FirstOrDefault(p=>material.HasProperty(p));
                    string cutoff=new[]{"_Cutoff","_AlphaClipThreshold","_AlphaCutoff","_AlphaCutoffValue"}.FirstOrDefault(p=>material.HasProperty(p));
                    if(enable==null||cutoff==null)continue;
                    settings=new AlphaSettings{Enable=enable,Cutoff=cutoff,EnabledValue=material.GetFloat(enable),CutoffValue=material.GetFloat(cutoff),Keyword=material.IsKeywordEnabled("_ALPHATEST_ON")};CaptureOutline(material,settings);alphaSettings.Add(material,settings);
                }
                ApplyAlpha(material,settings,enabled);supported=true;
            }
            Reload(path);return supported;
        }
        private struct Attributes { public uint Flags, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, SizeHigh, SizeLow; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileAttributesEx(string path, int level, out Attributes attributes);
        private static DateTime Stamp(string path) { Attributes value; return GetFileAttributesEx(path, 0, out value) ? new DateTime((long)(((ulong)value.WriteHigh << 32) | value.WriteLow)) : DateTime.MinValue; }
        internal string Folder { get; private set; }
        internal string LastReload { get; private set; }
        internal int ReloadCount { get; private set; }
        internal int VertexCount { get; private set; }
        internal static Renderer[] Renderers(GameObject model)
        {
            var meshRoot=model.transform.Find("UNIT/MESH");
            return meshRoot != null ? meshRoot.GetComponentsInChildren<Renderer>(true) : model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Cast<Renderer>().ToArray();
        }
        internal static string Safe(string name)
        {
            return new string((name ?? "asset").Select(value => char.IsLetterOrDigit(value) || value == '_' || value == '-' ? value : '_').ToArray());
        }
        internal void Export(GameObject model, string folder, string state)
        {
            Folder = folder;
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, "Textures"));
            var exported = new Dictionary<int, string>();
            var materials = new List<Material>();
            var manifest = new JArray();
            foreach (var renderer in Renderers(model))
            foreach (var material in renderer.materials)
            {
                if (material == null || materials.Contains(material)) continue;
                materials.Add(material);
                ownedMaterials.Add(material);
                foreach (string property in material.GetTexturePropertyNames())
                {
                    var texture = material.GetTexture(property) as Texture2D;
                    if (texture == null || texture.width <= 1 || texture.height <= 1) continue;
                    string relative;
                    if (!exported.TryGetValue(texture.GetInstanceID(), out relative))
                    {
                        byte[] png = Png(texture);
                        string hash;
                        using (var digest = SHA256.Create()) hash = BitConverter.ToString(digest.ComputeHash(png)).Replace("-", "").Substring(0, 12);
                        relative = "Textures/" + Safe(texture.name) + "_" + hash + ".png";
                        string path = Path.Combine(folder, relative);
                        if (!File.Exists(path)) File.WriteAllBytes(path, png);
                        originalHashes[Path.GetFullPath(path)]=hash;
                        exported[texture.GetInstanceID()] = relative;
                    }
                    bool linear = !texture.graphicsFormat.ToString().EndsWith("_SRGB",StringComparison.OrdinalIgnoreCase);
                    Bindings.Add(new TextureBinding { File = Path.GetFullPath(Path.Combine(folder, relative)), Material = material, Renderer=renderer, Original = texture, Property = property, Linear = linear });
                    manifest.Add(new JObject { ["Material"] = new JValue(material.name), ["Shader"] = new JValue(material.shader.name),
                        ["Property"] = new JValue(property), ["File"] = new JValue(relative), ["Linear"] = new JValue(linear) });
                }
            }
            File.WriteAllText(Path.Combine(folder, "materials.json"), manifest.ToString());
            ExportMesh(model, folder, state);
            foreach (string path in Bindings.Select(binding => binding.File).Distinct()) Reload(path);
        }
        private void Mark(string path) { lock (gate) pending[path] = DateTime.UtcNow.AddMilliseconds(250); }
        internal void Tick()
        {
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + .5f;
                foreach (string path in Bindings.Select(binding => binding.File).Distinct())
                {
                    DateTime previous;
                    if (stamps.TryGetValue(path, out previous) && Stamp(path) != previous) Mark(path);
                }
            }
            string[] paths;
            lock (gate)
            {
                paths = pending.Where(pair => pair.Value <= DateTime.UtcNow).Select(pair => pair.Key).ToArray();
                foreach (string path in paths) pending.Remove(path);
            }
            foreach (string path in paths)
            {
                try { Reload(path); }
                catch (IOException) { Mark(path); }
                catch (UnauthorizedAccessException) { Mark(path); }
                catch (Exception error) { LastReload = "PNG 확인 필요 · " + Path.GetFileName(path); Debug.LogWarning(error.Message); }
            }
        }
        internal bool IsEdited(string path)
        {
            string original;if(!originalHashes.TryGetValue(path,out original))return false;
            using(var digest=SHA256.Create())return BitConverter.ToString(digest.ComputeHash(File.ReadAllBytes(path))).Replace("-","").Substring(0,12)!=original;
        }
        internal void RestoreEditorTextures(string profile,JArray files)
        {
            if(files==null)return;
            var plans=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in files.OfType<JObject>())
            {
                string name=(string)entry["Name"],source=ProfileFiles.Child(profile,(string)entry["File"]);
                if(Path.GetFileName(name)!=name||!File.Exists(source)||Path.GetExtension(source).ToLowerInvariant()!=".png")throw new InvalidDataException("프로필 텍스처가 누락되었거나 경로가 잘못되었습니다.");
                plans.Add(name,source);
            }
            foreach(var binding in Bindings)if(!plans.ContainsKey(Path.GetFileName(binding.File)))throw new InvalidDataException("편집 복원용 텍스처가 누락되었습니다.");
            string restored=Path.Combine(Folder,"Restored",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(Path.Combine(restored,"Textures"));
            foreach(var old in Bindings.Select(b=>b.File).Distinct().ToArray())
            {
                string name=Path.GetFileName(old),target=Path.Combine(restored,"Textures",name);File.Copy(plans[name],target,false);
                string hash;if(originalHashes.TryGetValue(old,out hash))originalHashes[target]=hash;
                foreach(var binding in Bindings.Where(b=>b.File==old).ToArray())binding.File=target;
            }
            Folder=restored;
            foreach(var path in Bindings.Select(b=>b.File).Distinct())Reload(path);
        }
        private void Reload(string path)
        {
            var targets = Bindings.Where(binding => string.Equals(binding.File, path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (targets.Length == 0 || !File.Exists(path)) return;
            byte[] bytes;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new MemoryStream()) { input.CopyTo(output); bytes = output.ToArray(); }
            string hash;
            using(var digest=SHA256.Create()) hash=BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-","").Substring(0,12);
            string originalHash;
            if(originalHashes.TryGetValue(path,out originalHash) && hash==originalHash)
            {
                foreach(var binding in targets) binding.Material.SetTexture(binding.Property,binding.Original);
                Texture2D old; if(replacements.TryGetValue(path,out old)) {replacements.Remove(path);UnityEngine.Object.Destroy(old);ReloadCount++;LastReload=DateTime.Now.ToString("HH:mm:ss")+" · 원본 복원 · "+Path.GetFileName(path);}
                stamps[path]=Stamp(path); return;
            }
            bool painted=paintPaths.Contains(path);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, !painted, targets[0].Linear);
            if (!ImageConversion.LoadImage(texture, bytes, false)) { UnityEngine.Object.Destroy(texture); throw new IOException("PNG 저장이 완료되지 않았습니다."); }
            if(painted&&PartFilter!=null){var pixels=texture.GetPixels32();if(PartFilter.PadAlphaEdges(path,pixels,texture.width,texture.height)>0){texture.SetPixels32(pixels);texture.Apply(false,false);}}
            texture.name = Path.GetFileNameWithoutExtension(path);
            texture.wrapMode = targets[0].Original.wrapMode;
            texture.filterMode = painted?FilterMode.Bilinear:targets[0].Original.filterMode;
            texture.anisoLevel = painted?1:targets[0].Original.anisoLevel;
            Texture2D previous;
            replacements.TryGetValue(path, out previous);
            replacements[path] = texture;
            foreach (var binding in targets) binding.Material.SetTexture(binding.Property, texture);
            foreach(var material in targets.Select(b=>b.Material).Distinct()){AlphaSettings settings;if(alphaSettings.TryGetValue(material,out settings)&&settings.Active)ApplyAlpha(material,settings,true);}
            if (previous != null) UnityEngine.Object.Destroy(previous);
            stamps[path] = Stamp(path); ReloadCount++;
            LastReload = DateTime.Now.ToString("HH:mm:ss") + " · " + Path.GetFileName(path);
            Debug.Log("[Viewer] Texture reloaded " + path);
        }
        internal static byte[] Png(Texture texture)
        {
            var previous = RenderTexture.active;
            bool linear=!texture.graphicsFormat.ToString().EndsWith("_SRGB",StringComparison.OrdinalIgnoreCase);
            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            Texture2D readable = null;
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, linear);
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                readable.Apply(false, false);
                return EncodePng(readable);
            }
            finally
            {
                RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target);
                if (readable != null) UnityEngine.Object.Destroy(readable);
            }
        }
        private static void UInt(Stream stream, uint value) { stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16)); stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static readonly uint[] crcTable=CreateCrcTable();
        private static uint[] CreateCrcTable() {var table=new uint[256]; for(uint i=0;i<256;i++){uint c=i;for(int bit=0;bit<8;bit++)c=(c&1)!=0?0xedb88320^(c>>1):c>>1;table[i]=c;}return table;}
        private static void Chunk(Stream stream, string name, byte[] bytes)
        {
            UInt(stream, (uint)bytes.Length); byte[] tag = Encoding.ASCII.GetBytes(name); stream.Write(tag, 0, 4); stream.Write(bytes, 0, bytes.Length);
            uint crc = 0xffffffff;
            foreach(byte b in tag) crc=crcTable[(crc^b)&255]^(crc>>8);
            foreach(byte b in bytes) crc=crcTable[(crc^b)&255]^(crc>>8);
            UInt(stream, ~crc);
        }
        internal static byte[] EncodePng(Texture2D texture)
        {
            var pixels = texture.GetPixels32(); int width = texture.width, height = texture.height;
            byte[] scan = new byte[height * (width * 4 + 1)]; int offset = 0;
            for (int y = height - 1; y >= 0; y--) { scan[offset++] = 0; for (int x = 0; x < width; x++) { var p = pixels[y * width + x]; scan[offset++] = p.r; scan[offset++] = p.g; scan[offset++] = p.b; scan[offset++] = p.a; } }
            using (var output = new MemoryStream())
            using (var header = new MemoryStream())
            using (var compressed = new MemoryStream())
            {
                byte[] signature = {137,80,78,71,13,10,26,10}; output.Write(signature,0,signature.Length);
                UInt(header,(uint)width); UInt(header,(uint)height); byte[] format = {8,6,0,0,0}; header.Write(format,0,5); Chunk(output,"IHDR",header.ToArray());
                compressed.WriteByte(0x78); compressed.WriteByte(0x9c);
                using (var deflate = new DeflateStream(compressed, CompressionMode.Compress, true)) deflate.Write(scan,0,scan.Length);
                uint a = 1, b = 0; foreach(byte value in scan) { a = (a + value) % 65521; b = (b + a) % 65521; } UInt(compressed,(b << 16) | a);
                Chunk(output,"IDAT",compressed.ToArray()); Chunk(output,"IEND",new byte[0]); return output.ToArray();
            }
        }
        internal static Texture2D Crop(Texture texture, Rect rect)
        {
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                Graphics.Blit(texture, target); RenderTexture.active = target;
                var result = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false);
                result.ReadPixels(rect, 0, 0, false); result.Apply(false, false); return result;
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
        }
        internal void ExportMesh(GameObject model, string folder, string state, Func<Renderer,string> previewAlbedo=null)
        {
            string name = "model-" + state;
            var materialText = new StringBuilder();
            var obj = new StringBuilder("mtllib " + name + ".mtl\n");
            var manifest = new JArray();
            int offset = 1, part = 0;
            foreach (var renderer in Renderers(model))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                Mesh mesh = null; bool baked = false;
                var skinned = renderer as SkinnedMeshRenderer;
                try
                {
                    if (skinned != null) { mesh = new Mesh(); skinned.BakeMesh(mesh, true); baked = true; }
                    else { var filter = renderer.GetComponent<MeshFilter>(); if (filter != null) mesh = filter.sharedMesh; }
                    if (mesh == null || mesh.vertexCount == 0) continue;
                    string partName = Safe(renderer.name) + "_" + part++;
                    obj.AppendLine("g " + partName);
                    foreach (var vertex in mesh.vertices)
                    {
                        var v = model.transform.InverseTransformPoint(renderer.transform.TransformPoint(vertex));
                        obj.Append(string.Format(CultureInfo.InvariantCulture, "v {0:R} {1:R} {2:R}\n", -v.x, v.y, v.z));
                    }
                    var uv = mesh.uv;
                    for (int index = 0; index < mesh.vertexCount; index++)
                    {
                        var value = index < uv.Length ? uv[index] : Vector2.zero;
                        obj.Append(string.Format(CultureInfo.InvariantCulture, "vt {0:R} {1:R}\n", value.x, value.y));
                    }
                    var normals = mesh.normals;
                    for (int index = 0; index < mesh.vertexCount; index++)
                    {
                        var value = index < normals.Length ? model.transform.InverseTransformDirection(renderer.transform.TransformDirection(normals[index])) : Vector3.up;
                        obj.Append(string.Format(CultureInfo.InvariantCulture, "vn {0:R} {1:R} {2:R}\n", -value.x, value.y, value.z));
                    }
                    var mats = renderer.sharedMaterials;
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        string matName = partName + "_" + sub;
                        obj.AppendLine("usemtl " + matName);
                        materialText.AppendLine("newmtl " + matName + "\nKd 1 1 1");
                        if (sub < mats.Length && mats[sub] != null)
                        {
                            if(mats[sub].name=="Viewer hidden part") continue;
                            var binding = Bindings.Where(row => row.Material == mats[sub])
                                .OrderBy(row => row.Property.IndexOf("BaseMap", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : row.Property == "_MainTex" ? 1 : 2).FirstOrDefault();
                            if (binding != null) materialText.AppendLine("map_Kd Textures/" + Path.GetFileName(binding.File));
                            else if(previewAlbedo!=null)
                            {
                                string texture=previewAlbedo(renderer);
                                if(texture!=null)materialText.AppendLine("map_Kd "+texture);
                            }
                        }
                        var indices = PartFilter==null?mesh.GetTriangles(sub):PartFilter.ExportIndices(renderer,sub)??mesh.GetTriangles(sub);
                        for (int index = 0; index + 2 < indices.Length; index += 3)
                        {
                            int a = indices[index] + offset, b = indices[index + 2] + offset, c = indices[index + 1] + offset;
                            obj.Append(string.Format(CultureInfo.InvariantCulture, "f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\n", a, b, c));
                        }
                    }
                    manifest.Add(new JObject { ["Part"] = new JValue(partName), ["Vertices"] = new JValue(mesh.vertexCount),
                        ["Active"] = new JValue(renderer.gameObject.activeInHierarchy && renderer.enabled) });
                    offset += mesh.vertexCount;
                }
                finally { if (baked && mesh != null) UnityEngine.Object.Destroy(mesh); }
            }
            VertexCount = offset - 1;
            File.WriteAllText(Path.Combine(folder, name + ".obj"), obj.ToString());
            File.WriteAllText(Path.Combine(folder, name + ".mtl"), materialText.ToString());
            File.WriteAllText(Path.Combine(folder, name + ".json"), manifest.ToString());
        }
        public void Dispose()
        {
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering-=BeforePaintRender;
            foreach (var texture in replacements.Values) if (texture != null) UnityEngine.Object.Destroy(texture);
            foreach(var material in ownedMaterials) if(material!=null) UnityEngine.Object.Destroy(material);
            ownedMaterials.Clear();
            replacements.Clear(); Bindings.Clear();
        }
    }
}
