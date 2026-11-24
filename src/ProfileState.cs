using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal static class ProfileFiles
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="MoveFileW")]
        private static extern bool MoveFile(string source,string destination);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="MoveFileExW")]
        private static extern bool MoveFileEx(string source,string destination,uint flags);
        internal static void WriteAtomic(string path,byte[] bytes)
        {
            string temporary=path+".write-"+Guid.NewGuid().ToString("N");File.WriteAllBytes(temporary,bytes);
            if(!MoveFileEx(temporary,path,9)){int code=Marshal.GetLastWin32Error();File.Delete(temporary);throw new IOException("PNG 저장 실패: "+code);}
        }
        internal static void MoveFolder(string source,string destination)
        {if(!MoveFile(Path.GetFullPath(source),Path.GetFullPath(destination)))throw new IOException("폴더 이동 실패: "+Marshal.GetLastWin32Error());}
        internal static string LegacyTime(string path)
        {
            var pieces=Path.GetFileName(path).Split('_');if(pieces.Length<3)return "";string value=pieces[2];
            return value.Length>=15?value.Substring(0,4)+"-"+value.Substring(4,2)+"-"+value.Substring(6,2)+"T"+value.Substring(9,2)+":"+value.Substring(11,2)+":"+value.Substring(13,2):value;
        }
        internal static string Child(string folder,string relative)
        {
            if(string.IsNullOrEmpty(relative)||Path.IsPathRooted(relative))throw new InvalidDataException("프로필 상대 경로가 잘못되었습니다.");
            var full=Path.GetFullPath(Path.Combine(folder,relative));
            if(!full.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("프로필 폴더 밖의 파일은 읽을 수 없습니다.");
            return full;
        }
    }
    internal sealed partial class PartVisibility
    {
        internal JObject ExportEditorState()
        {
            if(!groupsLoaded){foreach(var part in Parts.Where(p=>HasGeometry(p)))EnsureMesh(part);LoadGroups();}
            return new JObject{["Format"]=new JValue("ViewerEditState.v1"),["ForceOutlineSuppression"]=new JValue(forceOutline),["AlphaPreviewTextures"]=new JArray(paintTextures.Keys.Concat(restoredAlphaFiles).Select(file=>Path.GetFileName(file)).Distinct()),["Groups"]=new JArray(Pieces().Where(p=>groups.ContainsKey(p.Piece)).Select(p=>new JObject{
                ["RendererPath"]=new JValue(RendererPath(p.Part.Renderer)),["Slot"]=new JValue(p.Part.Slot),["Island"]=new JValue(p.Piece.IslandId),["Group"]=new JValue(groups[p.Piece])}))};
        }
        internal int ExportProfileTexture(string source,string destination)
        {
            var hidden=new List<UvRasterLayer>();
            foreach(var part in Parts.Where(p=>HasGeometry(p)))
            foreach(var binding in Textures(part).Where(b=>string.Equals(b.File,source,StringComparison.OrdinalIgnoreCase)))
            {
                if(part.Visible&&(part.Detail==null||part.Detail.Slots[part.GeometrySlot].All(p=>p.Visible)))continue;
                var mesh=EnsureMesh(part);var indices=part.Visible?mesh.Slots[part.GeometrySlot].Where(p=>!p.Visible).SelectMany(p=>p.Indices).ToArray():mesh.SourceIndices[part.GeometrySlot];
                hidden.Add(new UvRasterLayer(mesh,indices,binding));
            }
            if(hidden.Count==0){ExportEditorTexture(source,destination);return 0;}
            var copy=new Texture2D(2,2,TextureFormat.RGBA32,false,true);
            try
            {
                if(!ImageConversion.LoadImage(copy,File.ReadAllBytes(source),false))throw new IOException("PNG를 읽을 수 없습니다.");
                int w=copy.width,h=copy.height;if((long)w*h>33554432)throw new InvalidDataException("프로필 PNG가 너무 큽니다.");
                var mask=new byte[w*h];foreach(var layer in hidden)UvRaster.Paint(layer,w,h,p=>mask[p]|=1);
                foreach(var layer in VisibleTextureLayers(source))UvRaster.Paint(layer,w,h,p=>mask[p]|=2);
                var pixels=copy.GetPixels32();int changed=0;
                for(int p=0;p<pixels.Length;p++)if(mask[p]==1){if(pixels[p].a!=0)changed++;pixels[p].a=0;}
                changed+=PadAlphaEdges(source,pixels,w,h);
                copy.SetPixels32(pixels);copy.Apply(false,false);File.WriteAllBytes(destination,AssetExporter.EncodePng(copy));return changed;
            }
            finally{UnityEngine.Object.Destroy(copy);}
        }
        internal void RestoreProfile(JObject document)
        {
            SetSelectionMode(false);
            var geometry=(JArray)document["Visibility"]?["HiddenGeometry"]??new JArray();
            var plans=new List<KeyValuePair<Part,HashSet<int>>>();
            foreach(var entry in geometry.OfType<JObject>())
            {
                var part=Parts.FirstOrDefault(p=>RendererPath(p.Renderer)==(string)entry["RendererPath"]&&p.Slot==(int)entry["SubMesh"]);
                if(part==null)throw new InvalidDataException("숨긴 메시를 찾을 수 없습니다: "+(string)entry["RendererPath"]);
                var mesh=EnsureMesh(part);var original=mesh.SourceIndices[part.GeometrySlot];var bytes=new byte[original.Length*4];Buffer.BlockCopy(original,0,bytes,0,bytes.Length);
                string hash;using(var digest=SHA256.Create())hash=BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-","");
                if(mesh.VertexCount!=(int)entry["VertexCount"]||!string.Equals(hash,(string)entry["OriginalIndexSHA256"],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("원본 메시 버전이 다른 프로필입니다.");
                var hidden=new HashSet<int>(((JArray)entry["HiddenTriangleIds"]).Select(t=>(int)t));
                if(hidden.Any(t=>t<0||t>=original.Length/3))throw new InvalidDataException("잘못된 숨김 삼각형 번호입니다.");
                foreach(var piece in mesh.Slots[part.GeometrySlot])if(piece.TriangleIds.Any(t=>hidden.Contains(t))&&!piece.TriangleIds.All(t=>hidden.Contains(t)))throw new InvalidDataException("이전 분리 방식의 일부 면 숨김은 현재 UV 섬과 일치하지 않습니다.");
                plans.Add(new KeyValuePair<Part,HashSet<int>>(part,hidden));
            }
            var materialPlans=new List<Part>();
            foreach(var entry in ((JArray)document["Visibility"]?["HiddenMaterials"]??new JArray()).OfType<JObject>())
            {
                var part=Parts.FirstOrDefault(p=>RendererPath(p.Renderer)==(string)entry["RendererPath"]&&p.Slot==(int)entry["MaterialSlot"]);
                if(part==null)throw new InvalidDataException("숨긴 재질을 찾을 수 없습니다.");materialPlans.Add(part);
            }
            foreach(var part in Parts)Set(Parts.IndexOf(part),true);
            foreach(var mesh in meshes.Values)foreach(var slot in mesh.Slots)foreach(var piece in slot)piece.Visible=true;
            foreach(var plan in plans)foreach(var piece in plan.Key.Detail.Slots[plan.Key.GeometrySlot])piece.Visible=piece.TriangleIds.Length==0||!plan.Value.Contains(piece.TriangleIds[0]);
            foreach(var part in materialPlans)Set(Parts.IndexOf(part),false);
            foreach(var mesh in meshes.Values)mesh.Upload();
            groups.Clear();groupsLoaded=true;selection.Clear();
            foreach(var entry in ((JArray)document["EditorState"]?["Groups"]??new JArray()).OfType<JObject>())
            {
                var part=Parts.FirstOrDefault(p=>RendererPath(p.Renderer)==(string)entry["RendererPath"]&&p.Slot==(int)entry["Slot"]);if(part==null)continue;
                EnsureMesh(part);var piece=part.Detail.Slots[part.GeometrySlot].FirstOrDefault(p=>p.IslandId==(int)entry["Island"]);int id=(int)entry["Group"];
                if(piece!=null&&id>0){groups[piece]=id;groupSerial=Mathf.Max(groupSerial,id);}
            }
            undo.Clear();redo.Clear();overlayRevision++;hoverPart=null;SaveGroups();
            forceOutline=(bool?)document["EditorState"]?["ForceOutlineSuppression"]??false;if(OutlineModeChanged!=null)OutlineModeChanged(forceOutline);
            foreach(var token in ((JArray)document["EditorState"]?["AlphaPreviewTextures"]??new JArray()))
            foreach(var file in bindings.Where(b=>Path.GetFileName(b.File)==(string)token).Select(b=>b.File).Distinct())
            {restoredAlphaFiles.Add(file);NotifyTexture(file,true);}
        }
    }
}
