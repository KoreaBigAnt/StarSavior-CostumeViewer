using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;

namespace StarSaviorCostumeViewer
{
    internal static class ModPackage
    {
        internal static void Write(string root,string folder,JObject profile,JObject meshIndex)
        {
            string id=(string)profile["Card"]["Id"];
            var manifest=new JObject{["Format"]=new JValue("StarSavior.AppearanceMod"),["Version"]=new JValue(1),["Id"]=new JValue(id),["Name"]=profile["Card"]["DisplayName"].DeepClone(),["ClientRevision"]=profile["ClientRevision"].DeepClone(),["Target"]=new JObject{["Mode"]=new JValue("ReplaceOriginalCostume"),["Unit"]=profile["Unit"].DeepClone(),["Skin"]=profile["Skin"].DeepClone(),["ModelPrefab"]=profile["ModelPrefab"].DeepClone()},["AppearanceProfile"]=new JValue("appearance-profile.json"),["MeshOverrides"]=new JValue("mesh-overrides.json"),["MeshEditIndex"]=new JValue("MeshEdit/mesh-index.json"),["MeshEditingComplete"]=meshIndex["Complete"].DeepClone(),["RuntimeLoader"]=new JObject{["Kind"]=new JValue("BepInExUnityMonoPlugin"),["Contract"]=new JValue("StarSavior.AppearanceMod.v1"),["Required"]=new JValue(true),["Included"]=new JValue(false)},["PreviewOnly"]=new JArray(new string[]{"model-preview.obj","model-preview.mtl"}),["RigPolicy"]=new JValue("ReuseOriginalBonesBindposesWeightsBlendShapes")};
            string preview=(string)profile["Preview"];
            if(preview!=null && preview!="model-preview.obj"){
                manifest["PreviewOnly"]=new JArray(new JValue(preview));
                manifest["RigPolicy"]=new JValue("StaticOriginalObjPose");
            }
            File.WriteAllText(Path.Combine(folder,"mod-manifest.json"),manifest.ToString());
            File.WriteAllText(Path.Combine(folder,"mesh-overrides.json"),new JObject{["Format"]=new JValue("StarSavior.MeshOverrides"),["Version"]=new JValue(1),["Meshes"]=new JArray()}.ToString());
            string tools=Path.Combine(root,"mod-tools");if(!Directory.Exists(tools))throw new IOException("모드 편집 도구 폴더가 없습니다. 뷰어 전체를 업데이트하세요.");
            string destination=Path.Combine(folder,"Tools");Directory.CreateDirectory(destination);
            foreach(string file in Directory.GetFiles(tools))if(IsPayload(file))File.Copy(file,Path.Combine(destination,Path.GetFileName(file)),false);
        }
        internal static bool IsPayload(string file)
        {
            string ext = Path.GetExtension(file);
            return !ext.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".rst", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(file).Equals("LICENSE", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(file).Equals("README", StringComparison.OrdinalIgnoreCase);
        }
        internal static string Zip(string folder)
        {
            string path=folder+".zip",prefix=Path.GetFileName(folder)+"/";
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write))using(var archive=new ZipArchive(file,ZipArchiveMode.Create,false,Encoding.UTF8))
            foreach(string source in Directory.GetFiles(folder,"*",SearchOption.AllDirectories))
            {
                if(!IsPayload(source))continue;
                string relative=source.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\','/');
                var entry=archive.CreateEntry(prefix+relative,CompressionLevel.Optimal);
                using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=entry.Open())input.CopyTo(output);
            }
            return path;
        }
    }
}
