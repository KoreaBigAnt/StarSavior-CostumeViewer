using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// Data-only runtime library. A BepInEx plugin identifies the unit/skin/model and
// calls Apply BEFORE its ordinary visibility/texture pass. No hooks/install here.
namespace StarSaviorMods
{
    public sealed class SkinnedAttachment : MonoBehaviour
    {
        private Mesh mesh;
        private Texture2D albedo;
        private Material material, edgeMaterial;
        private Renderer sourceRenderer, attachmentRenderer;
        private MaterialPropertyBlock sourceProperties;

        private static float Scalar(BinaryReader reader)
        {
            float value = reader.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Invalid attachment coordinate");
            return value;
        }

        internal static GameObject Load(SkinnedMeshRenderer source, Mesh original, string geometryPath, string texturePath)
        {
            if (source == null) throw new InvalidDataException("Attachment requires a skinned renderer");
            var node = new GameObject("Imported skinned torso");
            node.SetActive(false);
            var owner = node.AddComponent<SkinnedAttachment>();
            try
            {
                node.transform.SetParent(source.transform, false);
                node.layer = source.gameObject.layer;
                var bones = source.bones;
                bool hasOpacity, nativeSurface;
                using (var reader = new BinaryReader(File.OpenRead(geometryPath)))
                {
                    uint version = reader.ReadUInt32();
                    hasOpacity = version == 0x32544D53;
                    nativeSurface = version == 0x33544D53;
                    if (!nativeSurface && !hasOpacity && version != 0x31544D53) throw new InvalidDataException("Unknown attachment mesh");
                    int vertices = reader.ReadInt32(), indices = reader.ReadInt32();
                    if (vertices < 3 || vertices > 2000000 || indices < 3 || indices > 12000000 || indices % 3 != 0 ||
                        reader.BaseStream.Length != 12L + (nativeSurface ? 96L : hasOpacity ? 53L : 52L) * vertices + 4L * indices) throw new InvalidDataException("Invalid attachment dimensions");
                    var positions = new Vector3[vertices];
                    var uv = new Vector2[vertices];
                    var weights = new BoneWeight[vertices];
                    var colors = hasOpacity || nativeSurface ? new Color32[vertices] : null;
                    var normals = nativeSurface ? new Vector3[vertices] : null;
                    var uv2 = nativeSurface ? new Vector2[vertices] : null;
                    var uv7 = nativeSurface ? new List<Vector4>(vertices) : null;
                    var ids = new int[4];
                    var influences = new float[4];
                    for (int v = 0; v < vertices; v++)
                    {
                        positions[v] = new Vector3(Scalar(reader), Scalar(reader), Scalar(reader));
                        uv[v] = new Vector2(Scalar(reader), Scalar(reader));
                        if (hasOpacity) colors[v] = new Color32(255,255,255,reader.ReadByte());
                        float sum = 0;
                        for (int j = 0; j < 4; j++)
                        {
                            ids[j] = reader.ReadInt32();
                            influences[j] = Scalar(reader);
                            if (ids[j] < 0 || ids[j] >= bones.Length || influences[j] < 0) throw new InvalidDataException("Invalid skin weight");
                            sum += influences[j];
                        }
                        if (Math.Abs(sum - 1f) > .002f) throw new InvalidDataException("Attachment weights must sum to one");
                        if(nativeSurface)
                        {
                            normals[v]=new Vector3(Scalar(reader),Scalar(reader),Scalar(reader));
                            uv2[v]=new Vector2(Scalar(reader),Scalar(reader));
                            uv7.Add(new Vector4(Scalar(reader),Scalar(reader),Scalar(reader),Scalar(reader)));
                            colors[v]=new Color32(reader.ReadByte(),reader.ReadByte(),reader.ReadByte(),reader.ReadByte());
                            if(reader.ReadUInt32()!=0)throw new InvalidDataException("Unsupported native surface flags");
                        }
                        weights[v] = new BoneWeight { boneIndex0=ids[0], boneIndex1=ids[1], boneIndex2=ids[2], boneIndex3=ids[3],
                            weight0=influences[0], weight1=influences[1], weight2=influences[2], weight3=influences[3] };
                    }
                    int[] triangles = new int[indices];
                    for (int i = 0; i < indices; i++)
                    {
                        triangles[i] = reader.ReadInt32();
                        if ((uint)triangles[i] >= (uint)vertices) throw new InvalidDataException("Invalid attachment index");
                    }
                    owner.mesh = new Mesh { name="Imported torso", indexFormat=vertices>65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                    owner.mesh.vertices=positions;
                    owner.mesh.uv=uv;
                    if (hasOpacity)
                    {
                        owner.mesh.colors32=colors;
                        var opaque=new List<int>(indices);
                        var edge=new List<int>();
                        for (int i=0;i<indices;i+=3)
                        {
                            int a=triangles[i], b=triangles[i+1], c=triangles[i+2];
                            var dest=colors[a].a==255 && colors[b].a==255 && colors[c].a==255 ? opaque : edge;
                            dest.Add(a);dest.Add(b);dest.Add(c);
                        }
                        owner.mesh.subMeshCount=2;
                        owner.mesh.SetTriangles(opaque,0);
                        owner.mesh.SetTriangles(edge,1);
                    }
                    else owner.mesh.triangles=triangles;
                    owner.mesh.bindposes=original.bindposes;
                    owner.mesh.boneWeights=weights;
                    if(nativeSurface)
                    {
                        owner.mesh.normals=normals;owner.mesh.uv2=uv2;owner.mesh.SetUVs(7,uv7);
                        owner.mesh.colors32=colors;owner.mesh.RecalculateTangents();
                    }
                    else owner.mesh.RecalculateNormals();
                    owner.mesh.RecalculateBounds();
                }
                var renderer = node.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh=owner.mesh;
                if(nativeSurface)
                {
                    renderer.sharedMaterial=source.sharedMaterial;
                    renderer.quality=source.quality;
                    owner.sourceProperties=new MaterialPropertyBlock();
                    source.GetPropertyBlock(owner.sourceProperties);
                    renderer.SetPropertyBlock(owner.sourceProperties);
                }
                else
                {
                owner.albedo = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
                if (!ImageConversion.LoadImage(owner.albedo, File.ReadAllBytes(texturePath), false)) throw new InvalidDataException("Invalid attachment texture");
                var shader = new[]{"Universal Render Pipeline/Unlit","Unlit/Texture","Sprites/Default","UI/Default"}.Select(Shader.Find).FirstOrDefault(s=>s!=null && s.isSupported);
                if (shader==null) throw new InvalidDataException("No textured shader available for attachment");
                owner.material = new Material(shader);
                foreach (string name in new[]{"_MainTex","_BaseMap"}) if(owner.material.HasProperty(name)) owner.material.SetTexture(name,owner.albedo);
                if (hasOpacity)
                {
                    var edgeShader=new[]{"Sprites/Default","UI/Default"}.Select(Shader.Find).FirstOrDefault(s=>s!=null && s.isSupported);
                    if(edgeShader==null)throw new InvalidDataException("No vertex-alpha shader for transition");
                    owner.edgeMaterial=new Material(edgeShader);
                    owner.edgeMaterial.mainTexture=owner.albedo;
                    renderer.sharedMaterials=new[]{owner.material,owner.edgeMaterial};
                }
                else renderer.sharedMaterial=owner.material;
                }
                renderer.bones=bones;
                renderer.rootBone=source.rootBone;
                renderer.localBounds=source.localBounds;
                renderer.updateWhenOffscreen=true;
                renderer.enabled=source.enabled;
                owner.sourceRenderer=source;
                owner.attachmentRenderer=renderer;
                node.SetActive(true);
                Debug.Log("[Mesh] Loaded skinned torso: "+owner.mesh.vertexCount+" vertices, shader "+renderer.sharedMaterial.shader.name);
                return node;
            }
            catch { owner.ReleaseAssets(); UnityEngine.Object.Destroy(node); throw; }
        }

        private void LateUpdate()
        {
            if(sourceRenderer==null || attachmentRenderer==null)return;
            attachmentRenderer.enabled=sourceRenderer.enabled;
            if(sourceProperties!=null)
            {
                sourceRenderer.GetPropertyBlock(sourceProperties);
                attachmentRenderer.SetPropertyBlock(sourceProperties);
            }
        }
        private void ReleaseAssets()
        {
            if(mesh!=null) Destroy(mesh);
            if(material!=null) Destroy(material);
            if(edgeMaterial!=null) Destroy(edgeMaterial);
            if(albedo!=null) Destroy(albedo);
            mesh=null; material=null; edgeMaterial=null; albedo=null;
        }
        private void OnDestroy() { ReleaseAssets(); }
    }


    public sealed class StaticModelReplacement : MonoBehaviour
    {
        private Mesh mesh;
        private Material material;
        private readonly List<Texture2D> maps = new List<Texture2D>();
        private Renderer[] originals = new Renderer[0];
        private bool[] priorVisibility = new bool[0];

        private static float[] ReadFloats(BinaryReader reader, int count)
        {
            var bytes = reader.ReadBytes(checked(count * 4));
            if (bytes.Length != count * 4) throw new EndOfStreamException();
            var values = new float[count];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            foreach (float v in values)
                if (float.IsNaN(v) || float.IsInfinity(v)) throw new InvalidDataException("Invalid OBJ attribute");
            return values;
        }

        private static Vector3[] ReadVectors(BinaryReader reader, int vertices)
        {
            var values = ReadFloats(reader, checked(vertices * 3));
            var result = new Vector3[vertices];
            for (int i = 0; i < vertices; i++)
                result[i] = new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
            return result;
        }

        private Texture2D LoadMap(string path, bool linear)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
            maps.Add(texture);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), true))
                throw new InvalidDataException("Cannot read OBJ texture " + Path.GetFileName(path));
            texture.name = Path.GetFileName(path);
            return texture;
        }

        internal static GameObject Load(GameObject model, Func<string,string> file)
        {
            var node = new GameObject("ZIP source model");
            node.SetActive(false);
            var owner = node.AddComponent<StaticModelReplacement>();
            try
            {
                owner.originals = model.GetComponentsInChildren<Renderer>(true);
                owner.priorVisibility = owner.originals.Select(r => r.forceRenderingOff).ToArray();
                node.transform.SetParent(model.transform, false);
                node.layer = model.layer;
                int sourceVertices, vertices, indices;
                using (var reader = new BinaryReader(File.OpenRead(file("Geometry"))))
                {
                    if (reader.ReadUInt32() != 0x314A424F) throw new InvalidDataException("Unknown static OBJ geometry");
                    vertices = reader.ReadInt32(); indices = reader.ReadInt32(); sourceVertices = reader.ReadInt32();
                    if (vertices < 3 || vertices > 5000000 || indices < 3 || indices > 24000000 ||
                        indices % 3 != 0 || sourceVertices < 3 ||
                        reader.BaseStream.Length != 16L + 32L * vertices + 4L * indices)
                        throw new InvalidDataException("Invalid static OBJ dimensions");
                    owner.mesh = new Mesh { name="ZIP original OBJ", indexFormat=IndexFormat.UInt32 };
                    owner.mesh.vertices = ReadVectors(reader, vertices);
                    owner.mesh.normals = ReadVectors(reader, vertices);
                    var texcoord = ReadFloats(reader, 2 * vertices);
                    var uv = new Vector2[vertices];
                    for (int i=0;i<vertices;i++) uv[i] = new Vector2(texcoord[i*2],texcoord[i*2+1]);
                    owner.mesh.uv = uv;
                    var faces = new int[indices];
                    var bytes = reader.ReadBytes(indices * 4);
                    if (bytes.Length != indices * 4) throw new EndOfStreamException();
                    Buffer.BlockCopy(bytes, 0, faces, 0, bytes.Length);
                    foreach (int index in faces) if ((uint)index >= (uint)vertices) throw new InvalidDataException("Invalid OBJ index");
                    owner.mesh.triangles = faces;
                    owner.mesh.RecalculateBounds();
                }
                var shader = new[]{"Unlit/Texture","Universal Render Pipeline/Unlit","Universal Render Pipeline/Lit","Standard"}.Select(Shader.Find).FirstOrDefault(s => s!=null && s.isSupported);
                if (shader==null) throw new InvalidDataException("No supported OBJ surface shader");
                owner.material = new Material(shader) { name="ZIP original material" };
                var color = owner.LoadMap(file("Albedo"), false);
                foreach (string property in new[]{"_BaseMap","_MainTex"})
                    if (owner.material.HasProperty(property)) owner.material.SetTexture(property, color);
                if (owner.material.HasProperty("_BumpMap"))
                {
                    owner.mesh.RecalculateTangents();
                    owner.material.SetTexture("_BumpMap",owner.LoadMap(file("NormalMap"),true));
                    owner.material.EnableKeyword("_NORMALMAP");
                }
                if (owner.material.HasProperty("_MetallicGlossMap"))
                {
                    owner.material.SetTexture("_MetallicGlossMap", owner.LoadMap(file("PackedMetallic"),true));
                    owner.material.EnableKeyword("_METALLICSPECGLOSSMAP");
                    owner.material.EnableKeyword("_METALLICGLOSSMAP");
                    foreach (string property in new[]{"_Metallic","_Smoothness","_GlossMapScale"})
                        if (owner.material.HasProperty(property)) owner.material.SetFloat(property,1);
                }
                node.AddComponent<MeshFilter>().sharedMesh = owner.mesh;
                var renderer = node.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = owner.material;
                node.SetActive(true);
                Debug.Log("[Mesh] Static OBJ: "+sourceVertices+" source vertices, "+indices/3+" triangles, "+shader.name+" bounds "+renderer.bounds);
                return node;
            }
            catch { UnityEngine.Object.Destroy(node); throw; }
        }

        private void OnEnable()
        {
            foreach (var renderer in originals) if (renderer!=null) renderer.forceRenderingOff=true;
        }

        private void OnDisable()
        {
            for (int i=0;i<originals.Length;i++) if (originals[i]!=null) originals[i].forceRenderingOff=priorVisibility[i];
        }

        private void OnDestroy()
        {
            if (mesh!=null) Destroy(mesh);
            if (material!=null) Destroy(material);
            foreach(var map in maps) if(map!=null) Destroy(map);
        }
    }


    public sealed class ModMeshOverrides : IDisposable
    {
        private sealed class Entry {internal string Path;internal SkinnedMeshRenderer Skin;internal MeshFilter Filter;internal Mesh Original,Replacement;internal Bounds OriginalBounds;}
        private readonly List<GameObject> attachments=new List<GameObject>();
        public GameObject[] TakeAttachments(){var result=attachments.ToArray();attachments.Clear();return result;}
        private readonly List<Entry> entries=new List<Entry>();
        public IEnumerable<string> ReplacedRendererPaths {get{return entries.Select(e=>e.Path);}}
        private static string FileAt(string root,string name)
        {if(string.IsNullOrEmpty(name)||Path.IsPathRooted(name))throw new InvalidDataException("Expected relative mod path");string full=Path.GetFullPath(Path.Combine(root,name));if(!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Path escapes mod folder");return full;}
        private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
        private static int[] OriginalIndices(Renderer renderer,Mesh mesh,int sub)
        {
            var skin=renderer as SkinnedMeshRenderer;if(skin==null)return mesh.GetTriangles(sub);
            var baked=new Mesh();try{skin.BakeMesh(baked,false);return baked.GetTriangles(sub);}finally{UnityEngine.Object.Destroy(baked);}
        }
        private static ushort Half(float value)
        {
            uint bits=BitConverter.ToUInt32(BitConverter.GetBytes(value),0),sign=(bits>>16)&32768;int exp=(int)((bits>>23)&255)-127+15;uint mantissa=bits&0x7fffff;
            if(exp>=31)throw new InvalidDataException("Edited float exceeds Float16 range");
            if(exp<=0){if(exp<-10)return (ushort)sign;mantissa|=0x800000;int shift=14-exp;return (ushort)(sign+((mantissa+(1u<<(shift-1)))>>shift));}
            uint rounded=(mantissa+4096)>>13;if(rounded==1024){exp++;rounded=0;if(exp>=31)throw new InvalidDataException("Edited half overflow");}return (ushort)(sign|((uint)exp<<10)|rounded);
        }
        private static int Size(VertexAttributeFormat f){switch(f){case VertexAttributeFormat.Float32:case VertexAttributeFormat.UInt32:case VertexAttributeFormat.SInt32:return 4;case VertexAttributeFormat.Float16:case VertexAttributeFormat.UNorm16:case VertexAttributeFormat.SNorm16:case VertexAttributeFormat.UInt16:case VertexAttributeFormat.SInt16:return 2;default:return 1;}}
        private static byte[] Encode(float v,VertexAttributeFormat f)
        {
            if(float.IsNaN(v)||float.IsInfinity(v))throw new InvalidDataException("Non-finite edited value");
            switch(f)
            {
                case VertexAttributeFormat.Float32:return BitConverter.GetBytes(v);
                case VertexAttributeFormat.Float16:return BitConverter.GetBytes(Half(v));
                case VertexAttributeFormat.UNorm8:return new[]{(byte)Mathf.RoundToInt(Mathf.Clamp01(v)*255)};
                case VertexAttributeFormat.SNorm8:return new[]{unchecked((byte)(sbyte)Mathf.RoundToInt(Mathf.Clamp(v,-1,1)*127))};
                case VertexAttributeFormat.UNorm16:return BitConverter.GetBytes((ushort)Mathf.RoundToInt(Mathf.Clamp01(v)*65535));
                case VertexAttributeFormat.SNorm16:return BitConverter.GetBytes((short)Mathf.RoundToInt(Mathf.Clamp(v,-1,1)*32767));
                default:throw new InvalidDataException("Editable channel has unsupported integer format: "+f);
            }
        }
        private static void Patch(string folder,JObject rule,string property,VertexAttribute attr,Mesh original,byte[][] streams,int dimensions)
        {
            var value=rule[property] as JObject;if(value==null)return;
            if(!original.HasVertexAttribute(attr)||(int)value["Components"]!=dimensions||original.GetVertexAttributeDimension(attr)<dimensions)throw new InvalidDataException("Vertex channel differs: "+property);
            byte[] data=File.ReadAllBytes(FileAt(folder,(string)value["File"]));if(data.Length!=checked(original.vertexCount*dimensions*4))throw new InvalidDataException("Edited channel length differs");
            int stream=original.GetVertexAttributeStream(attr),offset=original.GetVertexAttributeOffset(attr),stride=original.GetVertexBufferStride(stream);var format=original.GetVertexAttributeFormat(attr);int size=Size(format);
            for(int v=0;v<original.vertexCount;v++)for(int c=0;c<dimensions;c++){var encoded=Encode(BitConverter.ToSingle(data,(v*dimensions+c)*4),format);Buffer.BlockCopy(encoded,0,streams[stream],v*stride+offset+c*size,size);}
        }
        private static SubMeshDescriptor Descriptor(SubMeshDescriptor value,int start,int count)
        {object boxed=value;var flags=BindingFlags.Instance|BindingFlags.NonPublic;typeof(SubMeshDescriptor).GetField("<indexStart>k__BackingField",flags).SetValue(boxed,start);typeof(SubMeshDescriptor).GetField("<indexCount>k__BackingField",flags).SetValue(boxed,count);typeof(SubMeshDescriptor).GetField("<baseVertex>k__BackingField",flags).SetValue(boxed,0);return (SubMeshDescriptor)boxed;}
        public static ModMeshOverrides Apply(GameObject model,string modFolder){return Apply(model,modFolder,null,null);}
        public static ModMeshOverrides Apply(GameObject model,string modFolder,Func<Renderer,Mesh> originalMesh,Func<Renderer,int,int[]> originalIndices)
        {
            var handle=new ModMeshOverrides();try
            {
                var doc=JObject.Parse(File.ReadAllText(FileAt(modFolder,"mesh-overrides.json")));
                if((string)doc["Format"]!="StarSavior.MeshOverrides"||(int?)doc["Version"]!=1)throw new InvalidDataException("Unsupported mesh override format");
                var paths=new HashSet<string>();
                foreach(var rule in ((JArray)doc["Meshes"]).OfType<JObject>())
                {
                    string path=(string)rule["RendererPath"];if(string.IsNullOrEmpty(path)||path.Split('/').Any(p=>p==".."||p=="."||p.Length==0)||!paths.Add(path))throw new InvalidDataException("Invalid/duplicate renderer path");
                    var node=model.transform.Find(path);var renderer=node==null?null:node.GetComponent<Renderer>();if(renderer==null)throw new InvalidDataException("Renderer not found: "+path);
                    var skin=renderer as SkinnedMeshRenderer;var filter=renderer.GetComponent<MeshFilter>();var currentMesh=skin!=null?skin.sharedMesh:filter==null?null:filter.sharedMesh;
                    var original=originalMesh==null?currentMesh:originalMesh(renderer);
                    if(original==null||original.vertexCount!=(int)rule["VertexCount"]||(string)rule["VertexOrder"]!="OriginalVertexId"||(string)rule["CoordinateSpace"]!="UnityRendererLocalLeftHanded")throw new InvalidDataException("Original vertex identity differs");
                    var meta=JObject.Parse(File.ReadAllText(FileAt(modFolder,(string)rule["SourceMesh"])));
                    if((string)meta["Format"]!="StarSavior.EditableMesh"||(int?)meta["Version"]!=1||(int)meta["VertexCount"]!=original.vertexCount||(string)meta["RendererPath"]!=path)throw new InvalidDataException("Invalid reference mesh metadata");
                    var layout=(JArray)meta["VertexLayout"];var nativeLayout=original.GetVertexAttributes();
                    if(layout.Count!=nativeLayout.Length)throw new InvalidDataException("Vertex layout count differs");
                    for(int i=0;i<nativeLayout.Length;i++){var attr=nativeLayout[i];if((string)layout[i]["Attribute"]!=attr.attribute.ToString()||(string)layout[i]["Format"]!=attr.format.ToString()||(int)layout[i]["Dimension"]!=attr.dimension||(int)layout[i]["Stream"]!=attr.stream||(int)layout[i]["Offset"]!=original.GetVertexAttributeOffset(attr.attribute))throw new InvalidDataException("Original vertex layout differs");}
                    var source=new int[original.subMeshCount][];var packed=new List<int>();var descriptors=new SubMeshDescriptor[source.Length];
                    var hashes=((JArray)rule["OriginalSubMeshes"]).OfType<JObject>().ToDictionary(r=>(int)r["SubMesh"]);
                    if(hashes.Count!=source.Length)throw new InvalidDataException("Submesh count differs");
                    for(int sub=0;sub<source.Length;sub++)
                    {
                        source[sub]=originalIndices==null?OriginalIndices(renderer,original,sub):originalIndices(renderer,sub);var bytes=new byte[source[sub].Length*4];Buffer.BlockCopy(source[sub],0,bytes,0,bytes.Length);
                        JObject fingerprint;if(!hashes.TryGetValue(sub,out fingerprint)||!string.Equals(Hash(bytes),(string)fingerprint["OriginalIndexSHA256"],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Original topology fingerprint differs: "+path);
                        var hidden=new HashSet<int>();foreach(var group in ((JArray)rule["Visibility"]).OfType<JObject>().Where(r=>(int)r["SubMesh"]==sub))foreach(var id in (JArray)group["HiddenTriangleIds"])hidden.Add((int)id);
                        if(hidden.Any(i=>i<0||i>=source[sub].Length/3))throw new InvalidDataException("Hidden triangle outside mesh");
                        int start=packed.Count;for(int t=0;t<source[sub].Length/3;t++)if(!hidden.Contains(t)){packed.Add(source[sub][t*3]);packed.Add(source[sub][t*3+1]);packed.Add(source[sub][t*3+2]);}
                        descriptors[sub]=Descriptor(original.GetSubMesh(sub),start,packed.Count-start);
                    }
                    string sourceFolder=Path.GetDirectoryName(FileAt(modFolder,(string)rule["SourceMesh"]));var raw=(JArray)meta["RawStreams"];
                    if(raw.Count!=original.vertexBufferCount)throw new InvalidDataException("Vertex stream count differs");
                    var streams=new byte[raw.Count][];
                    for(int stream=0;stream<raw.Count;stream++)
                    {
                        streams[stream]=File.ReadAllBytes(FileAt(sourceFolder,(string)raw[stream]["File"]));int stride=original.GetVertexBufferStride(stream);
                        if((int)raw[stream]["Stride"]!=stride||streams[stream].Length!=checked(stride*original.vertexCount)||!string.Equals(Hash(streams[stream]),(string)raw[stream]["SHA256"],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Reference vertex stream differs");
                    }
                    Patch(modFolder,rule,"Position",VertexAttribute.Position,original,streams,3);Patch(modFolder,rule,"Normal",VertexAttribute.Normal,original,streams,3);Patch(modFolder,rule,"TexCoord0",VertexAttribute.TexCoord0,original,streams,2);
                    var clone=new Mesh();var entry=new Entry{Path=path,Skin=skin,Filter=filter,Original=currentMesh,Replacement=clone,OriginalBounds=skin==null?original.bounds:skin.localBounds};handle.entries.Add(entry);
                    clone.name=original.name+" [local mod]";clone.indexFormat=original.indexFormat;clone.SetVertexBufferParams(original.vertexCount,original.GetVertexAttributes());
                    for(int stream=0;stream<streams.Length;stream++){var data=new NativeArray<byte>(streams[stream],Allocator.Temp);try{clone.SetVertexBufferData(data,0,0,data.Length,stream,MeshUpdateFlags.DontRecalculateBounds|MeshUpdateFlags.DontValidateIndices);}finally{data.Dispose();}}
                    clone.SetIndexBufferParams(Math.Max(1,packed.Count),original.indexFormat);clone.subMeshCount=source.Length;

                    {
                        if(original.indexFormat==IndexFormat.UInt16){var values=packed.Count==0?new ushort[1]:packed.Select(i=>checked((ushort)i)).ToArray();var data=new NativeArray<ushort>(values,Allocator.Temp);try{clone.SetIndexBufferData(data,0,0,data.Length,MeshUpdateFlags.DontRecalculateBounds|MeshUpdateFlags.DontValidateIndices);}finally{data.Dispose();}}
                        else{var values=packed.Count==0?new int[1]:packed.ToArray();var data=new NativeArray<int>(values,Allocator.Temp);try{clone.SetIndexBufferData(data,0,0,data.Length,MeshUpdateFlags.DontRecalculateBounds|MeshUpdateFlags.DontValidateIndices);}finally{data.Dispose();}}
                    }
                    for(int sub=0;sub<descriptors.Length;sub++)clone.SetSubMesh(sub,descriptors[sub],MeshUpdateFlags.DontRecalculateBounds|MeshUpdateFlags.DontValidateIndices);
                    if(skin!=null){clone.bindposes=original.bindposes;clone.SetBoneWeights(original.GetBonesPerVertex(),original.GetAllBoneWeights());}
                    for(int shape=0;shape<original.blendShapeCount;shape++)for(int frame=0;frame<original.GetBlendShapeFrameCount(shape);frame++)
                    {var dv=new Vector3[original.vertexCount];var dn=new Vector3[original.vertexCount];var dt=new Vector3[original.vertexCount];original.GetBlendShapeFrameVertices(shape,frame,dv,dn,dt);clone.AddBlendShapeFrame(original.GetBlendShapeName(shape),original.GetBlendShapeFrameWeight(shape,frame),dv,dn,dt);}
                    clone.RecalculateBounds();
                    var replacement=rule["StaticReplacement"] as JObject;
                    if(replacement!=null)
                    {
                        if(((JArray)doc["Meshes"]).Count!=1 || rule["SkinnedAttachment"]!=null)
                            throw new InvalidDataException("A static model replaces the whole character");
                        handle.attachments.Add(StaticModelReplacement.Load(model,key=>FileAt(modFolder,(string)replacement[key])));
                    }
                    var attachment=rule["SkinnedAttachment"] as JObject;
                    if(attachment!=null)handle.attachments.Add(SkinnedAttachment.Load(skin,original,FileAt(modFolder,(string)attachment["Geometry"]),FileAt(modFolder,(string)attachment["Albedo"])));
                }
                foreach(var entry in handle.entries)if(entry.Skin!=null){entry.Skin.sharedMesh=entry.Replacement;var bounds=entry.OriginalBounds;bounds.Encapsulate(entry.Replacement.bounds);entry.Skin.localBounds=bounds;}else entry.Filter.sharedMesh=entry.Replacement;
                return handle;
            }
            catch{handle.Dispose();throw;}
        }
        public void Dispose()
        {
            foreach(var entry in entries)
            {
                if(entry.Skin!=null&&entry.Skin.sharedMesh==entry.Replacement){entry.Skin.sharedMesh=entry.Original;entry.Skin.localBounds=entry.OriginalBounds;}
                else if(entry.Filter!=null&&entry.Filter.sharedMesh==entry.Replacement)entry.Filter.sharedMesh=entry.Original;
                if(entry.Replacement!=null)UnityEngine.Object.Destroy(entry.Replacement);
            }
            foreach(var attachment in attachments)if(attachment!=null){attachment.SetActive(false);UnityEngine.Object.Destroy(attachment);}
            attachments.Clear();
            entries.Clear();
        }
    }
}
