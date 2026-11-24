using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using StarSaviorMods;

namespace StarSaviorCostumeViewer
{
    internal sealed class ImportedMeshSession
    {
        private GameObject[] attachments = new GameObject[0];
        private static readonly string[] StaticFiles = {"Obj","Mtl","Albedo","NormalMap","Roughness","Metallic","Geometry","PackedMetallic"};
        internal JObject StaticReplacement { get { return ((JArray)Document["Meshes"]).OfType<JObject>().Select(r=>r["StaticReplacement"] as JObject).FirstOrDefault(r=>r!=null); } }
        internal string Folder;
        internal JObject Document,BaseVisibility;
        internal int Count{get{return ((JArray)Document["Meshes"]).Count;}}
        private static void Copy(string source,string destination)
        {Directory.CreateDirectory(Path.GetDirectoryName(destination));File.Copy(source,destination,false);}
        internal static ImportedMeshSession Snapshot(string source,string workspace,Costume costume)
        {
            source=Path.GetFullPath(source);if(File.Exists(source))source=Path.GetDirectoryName(source);
            var manifest=JObject.Parse(File.ReadAllText(ProfileFiles.Child(source,"mod-manifest.json")));
            if((string)manifest["Format"]!="StarSavior.AppearanceMod"||(int?)manifest["Version"]!=1||(int?)manifest["ClientRevision"]!=277650||(int?)manifest["Target"]?["Unit"]!=costume.Unit||(int?)manifest["Target"]?["Skin"]!=costume.Skin||(string)manifest["Target"]?["ModelPrefab"]!=costume.Body)throw new InvalidDataException("수정 메시의 캐릭터/의상/클라이언트 버전이 현재 모델과 다릅니다.");
            var doc=JObject.Parse(File.ReadAllText(ProfileFiles.Child(source,"mesh-overrides.json")));
            if((string)doc["Format"]!="StarSavior.MeshOverrides"||(int?)doc["Version"]!=1||!(doc["Meshes"] is JArray)||((JArray)doc["Meshes"]).Count==0)throw new InvalidDataException("수정 메시가 없습니다. Blender의 StarSavior Modified Mesh로 저장한 폴더를 선택하세요.");
            var recipe=JObject.Parse(File.ReadAllText(ProfileFiles.Child(source,"appearance-profile.json")));
            var result=new ImportedMeshSession{Folder=Path.Combine(workspace,"ImportedMeshes",Guid.NewGuid().ToString("N")),Document=(JObject)doc.DeepClone(),BaseVisibility=(JObject)(recipe["Visibility"]??new JObject()).DeepClone()};
            Directory.CreateDirectory(result.Folder);int index=0;
            foreach(var rule in ((JArray)result.Document["Meshes"]).OfType<JObject>())
            {
                string originalMeta=ProfileFiles.Child(source,(string)rule["SourceMesh"]),originalDir=Path.GetDirectoryName(originalMeta),relative="References/"+index+"/mesh-edit.json";
                var meta=JObject.Parse(File.ReadAllText(originalMeta));Copy(originalMeta,ProfileFiles.Child(result.Folder,relative));var files=new HashSet<string>();
                foreach(var row in ((JArray)meta["RawStreams"]).OfType<JObject>())files.Add((string)row["File"]);
                foreach(var row in ((JObject)meta["Channels"]).Properties())files.Add((string)row.Value["File"]);
                foreach(var row in ((JArray)meta["SubMeshes"]).OfType<JObject>()){files.Add((string)row["OriginalIndices"]);files.Add((string)row["VisibleIndices"]);}
                if((bool?)meta["Skinned"]==true){files.Add((string)meta["BoneCountsFile"]);files.Add((string)meta["BoneWeightsFile"]);}
                foreach(string file in files)Copy(ProfileFiles.Child(originalDir,file),ProfileFiles.Child(Path.GetDirectoryName(ProfileFiles.Child(result.Folder,relative)),file));
                rule["SourceMesh"]=new JValue(relative);
                foreach(string property in new[]{"Position","Normal","TexCoord0"})
                {
                    var channel=rule[property] as JObject;if(channel==null)continue;
                    string target="Payload/"+index+"/"+property+".f32le";Copy(ProfileFiles.Child(source,(string)channel["File"]),ProfileFiles.Child(result.Folder,target));channel["File"]=new JValue(target);
                }
                var attachment=rule["SkinnedAttachment"] as JObject;
                if(attachment!=null)foreach(string key in new[]{"Geometry","Albedo"})
                {
                    string input=ProfileFiles.Child(source,(string)attachment[key]);
                    string target="Payload/"+index+"/"+key+Path.GetExtension(input);
                    Copy(input,ProfileFiles.Child(result.Folder,target));
                    attachment[key]=new JValue(target);
                }
                var modelFile=rule["StaticReplacement"] as JObject;
                if(modelFile!=null)foreach(string key in StaticFiles)
                {
                    string input=ProfileFiles.Child(source,(string)modelFile[key]);
                    string target="Payload/"+index+"/SourceOBJ/"+Path.GetFileName(input);
                    Copy(input,ProfileFiles.Child(result.Folder,target));
                    modelFile[key]=new JValue(target);
                }
                index++;
            }
            File.WriteAllText(Path.Combine(result.Folder,"mesh-overrides.json"),result.Document.ToString());return result;
        }
        private static Renderer RendererAt(PartVisibility parts,string path)
        {var part=parts.Parts.FirstOrDefault(p=>parts.RendererPath(p.Renderer)==path);if(part==null)throw new InvalidDataException("현재 모델에 수정 렌더러가 없습니다: "+path);return part.Renderer;}
        private HashSet<int>[] ExtraHidden(JObject rule,FineMesh mesh)
        {
            string path=(string)rule["RendererPath"];var result=new HashSet<int>[mesh.SourceIndices.Length];
            for(int sub=0;sub<result.Length;sub++)
            {
                var hidden=new HashSet<int>();foreach(var row in ((JArray)rule["Visibility"]).OfType<JObject>().Where(r=>(int)r["SubMesh"]==sub))foreach(var id in (JArray)row["HiddenTriangleIds"])hidden.Add((int)id);
                bool materialHidden=((JArray)BaseVisibility["HiddenMaterials"]??new JArray()).OfType<JObject>().Any(r=>(string)r["RendererPath"]==path&&(int)r["MaterialSlot"]==sub);
                if(materialHidden)hidden.Clear();
                else foreach(var row in ((JArray)BaseVisibility["HiddenGeometry"]??new JArray()).OfType<JObject>().Where(r=>(string)r["RendererPath"]==path&&(int)r["SubMesh"]==sub))foreach(var id in (JArray)row["HiddenTriangleIds"])hidden.Remove((int)id);
                result[sub]=hidden;
            }
            return result;
        }
        internal void Apply(GameObject model,PartVisibility parts)
        {
            using(var handle=ModMeshOverrides.Apply(model,Folder,r=>parts.MeshForExport(r).OriginalMesh,(r,sub)=>parts.MeshForExport(r).SourceIndices[sub]))
            {
            foreach(var rule in ((JArray)Document["Meshes"]).OfType<JObject>())
            {
                var renderer=RendererAt(parts,(string)rule["RendererPath"]);var skin=renderer as SkinnedMeshRenderer;var filter=renderer.GetComponent<MeshFilter>();var geometry=skin!=null?skin.sharedMesh:filter.sharedMesh;
                var fine=parts.MeshForExport(renderer);fine.ExternalGeometry(geometry,ExtraHidden(rule,fine));
            }
            attachments=handle.TakeAttachments();
            }
        }
        internal string PreviewAlbedo(Renderer renderer)
        {
            if(StaticReplacement!=null)return "ImportedMeshData/"+(string)StaticReplacement["Albedo"];
            int index=Array.FindIndex(attachments,node=>node==renderer.gameObject);
            if(index<0)return null;
            var attachment=((JArray)Document["Meshes"]).OfType<JObject>().Select(rule=>rule["SkinnedAttachment"] as JObject).Where(item=>item!=null).ElementAt(index);
            return "ImportedMeshData/"+(string)attachment["Albedo"];
        }
        internal void Clear(PartVisibility parts)
        {foreach(var attachment in attachments)if(attachment!=null){attachment.SetActive(false);UnityEngine.Object.Destroy(attachment);}
         attachments=new GameObject[0];
         foreach(var path in ((JArray)Document["Meshes"]).OfType<JObject>().Select(r=>(string)r["RendererPath"]))parts.MeshForExport(RendererAt(parts,path)).ExternalGeometry(null,null);}
        internal void Export(string target,PartVisibility parts)
        {
            string data=Path.Combine(target,"ImportedMeshData");Directory.CreateDirectory(data);
            foreach(string file in Directory.GetFiles(Folder,"*",SearchOption.AllDirectories))
            {string relative=file.Substring(Folder.Length).TrimStart(Path.DirectorySeparatorChar);Copy(file,ProfileFiles.Child(data,relative));}
            var doc=(JObject)Document.DeepClone();
            foreach(var rule in ((JArray)doc["Meshes"]).OfType<JObject>())
            {
                string path=(string)rule["RendererPath"];var fine=parts.MeshForExport(RendererAt(parts,path));var extra=ExtraHidden(rule,fine);var visibility=new JArray();
                for(int sub=0;sub<fine.SourceIndices.Length;sub++)
                {
                    var hidden=new HashSet<int>(extra[sub]);foreach(var piece in fine.Slots[sub].Where(p=>!p.Visible))foreach(int id in piece.TriangleIds)hidden.Add(id);
                    visibility.Add(new JObject{["SubMesh"]=new JValue(sub),["HiddenTriangleIds"]=new JArray(hidden.OrderBy(i=>i))});
                }
                var attachment=rule["SkinnedAttachment"] as JObject;
                if(attachment!=null)foreach(string key in new[]{"Geometry","Albedo"}) attachment[key]=new JValue("ImportedMeshData/"+(string)attachment[key]);
                var modelFile=rule["StaticReplacement"] as JObject;
                if(modelFile!=null)foreach(string key in StaticFiles)modelFile[key]=new JValue("ImportedMeshData/"+(string)modelFile[key]);
                rule["Visibility"]=visibility;rule["SourceMesh"]=new JValue("ImportedMeshData/"+(string)rule["SourceMesh"]);
                foreach(string name in new[]{"Position","Normal","TexCoord0"}){var channel=rule[name] as JObject;if(channel!=null)channel["File"]=new JValue("ImportedMeshData/"+(string)channel["File"]);}
            }
            File.WriteAllText(Path.Combine(target,"mesh-overrides.json"),doc.ToString());
            string manifestPath=Path.Combine(target,"mod-manifest.json");var manifest=JObject.Parse(File.ReadAllText(manifestPath));manifest["ImportedMeshCount"]=new JValue(Count);File.WriteAllText(manifestPath,manifest.ToString());
        }
    }
    internal sealed partial class PartVisibility
    {
        internal void ExternalGeometryChanged()
        {
            ClearHover();alphaUvCoverage.Clear();foreach(var surface in overlays.Values)surface.Dispose();overlays.Clear();
            for(int i=0;i<Parts.Count;i++){var part=Parts[i];if(part.UvView!=null){part.UvView.Dispose();part.UvView=null;if(part.Expanded)Analyze(i);}}
            overlayRevision++;
        }
        internal void ResetExternalGeometry(){foreach(var mesh in meshes.Values)mesh.ExternalGeometry(null,null);}
    }
}
