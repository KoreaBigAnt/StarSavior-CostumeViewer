using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Cysharp.Threading.Tasks;
using StarSaviorMods;

namespace StarSaviorCostumeViewer
{
    internal static class AppearanceProfile
    {
        internal static async UniTask<string> Export(string root,Costume costume,GameObject model,PartVisibility parts,AssetExporter exporter,JObject viewState,ImportedMeshSession imported,ChestRig chestRig,ChestSettings chest)
        {
            string profiles=Path.Combine(root,"Profiles");Directory.CreateDirectory(profiles);
            string suffix=Guid.NewGuid().ToString("N").Substring(0,8),path=Path.Combine(profiles,".pending-"+suffix);
            string final=Path.Combine(profiles,costume.Unit+"_"+costume.Skin+"_"+AssetExporter.Safe(costume.Title+"_"+costume.CharacterName)+"_"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+suffix);
            Directory.CreateDirectory(Path.Combine(path,"Textures"));
            Directory.CreateDirectory(Path.Combine(path,"EditorTextures"));
            var edits=new JArray();
            var textureBindings=new JArray();
            var changed=exporter.Bindings.Select(binding=>binding.File).Distinct().Where(file=>exporter.IsEdited(file)).ToArray();
            var masked=new System.Collections.Generic.HashSet<string>();var editorTextures=new JArray();
            var visibility=parts.ProfileRules();var editor=parts.ExportEditorState();
            foreach(var property in viewState.Properties())editor[property.Name]=property.Value.DeepClone();
            foreach(var file in exporter.Bindings.Select(binding=>binding.File).Distinct())
            {
                string name=Path.GetFileName(file);parts.ExportEditorTexture(file,Path.Combine(path,"EditorTextures",name));
                editorTextures.Add(new JObject{["Name"]=new JValue(name),["File"]=new JValue("EditorTextures/"+name)});
                if(parts.ExportProfileTexture(file,Path.Combine(path,"Textures",name))>0)masked.Add(file);
            }
            foreach(var binding in exporter.Bindings)
            {
                var slots=parts.Parts.Where(part=>part.Renderer==binding.Renderer&&part.Original==binding.Material).Select(part=>part.Slot).ToArray();
                var entry=new JObject{["RendererPath"]=new JValue(parts.RendererPath(binding.Renderer)),["MaterialSlots"]=new JArray(slots),["Material"]=new JValue(binding.Material.name.Replace(" (Instance)","")),["Property"]=new JValue(binding.Property),["Linear"]=new JValue(binding.Linear),["AlphaClip"]=new JValue(parts.RequiresAlpha(binding.File)),["File"]=new JValue("Textures/"+Path.GetFileName(binding.File))};
                textureBindings.Add(entry);if(changed.Contains(binding.File)||masked.Contains(binding.File))edits.Add(entry.DeepClone());
            }
            var document=new JObject{
                ["Format"]=new JValue("StarSavior.AppearanceProfile"),["Version"]=new JValue(1),["ClientRevision"]=new JValue(277650),
                ["ExportedUtc"]=new JValue(DateTime.UtcNow.ToString("o")),["EditorState"]=editor,["EditorTextures"]=editorTextures,
                ["Unit"]=new JValue(costume.Unit),["Skin"]=new JValue(costume.Skin),["ModelPrefab"]=new JValue(costume.Body),
                ["Card"]=new JObject{["Kind"]=new JValue("CustomCostume"),["Id"]=new JValue(Guid.NewGuid().ToString("N")),["DisplayName"]=new JValue(costume.CharacterName+" 커스텀"),["BaseSkin"]=new JValue(costume.Skin),["Thumbnail"]=new JValue("thumbnail.png")},
                ["IntegrationStatus"]=new JValue("RequiresCompatibleRuntimeLoader"),["IndexEncoding"]=new JValue("Int32LE"),
                ["Visibility"]=visibility,["TextureOverrides"]=edits,["TextureBindings"]=textureBindings,["Preview"]=new JValue("model-preview.obj")};
            var staticModel=imported==null?null:imported.StaticReplacement;
            if(chest!=null)document[ChestSettings.ProfileKey]=chest.ToJson();
            if(staticModel!=null)document["Preview"]=new JValue("ImportedMeshData/"+(string)staticModel["Obj"]);
            else
            {
                try
                {
                    if(chest!=null)chestRig.Apply(chest,0f,false);
                    exporter.ExportMesh(model,path,"preview",imported==null?null:new Func<Renderer,string>(imported.PreviewAlbedo));
                }
                finally{chestRig?.Suspend();}
            }
            var meshIndex=await EditableMeshExport.Export(model,parts,exporter,path);
            document["MeshEdit"]=new JObject{["Index"]=new JValue("MeshEdit/mesh-index.json"),["Complete"]=meshIndex["Complete"].DeepClone(),["Errors"]=meshIndex["Errors"].DeepClone()};
            document["MeshOverridesFile"]=new JValue("mesh-overrides.json");
            File.WriteAllText(Path.Combine(path,"appearance-profile.json"),document.ToString());
            ModPackage.Write(root,path,document,meshIndex);
            if(imported!=null)imported.Export(path,parts);
            foreach(string file in Directory.GetFiles(path,"*",SearchOption.AllDirectories))
                if(!ModPackage.IsPayload(file))File.Delete(file);
            ProfileFiles.MoveFolder(path,final);Debug.Log("[Viewer] Appearance profile exported "+final);return final;
        }
    }
}
