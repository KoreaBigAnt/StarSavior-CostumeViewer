using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed partial class PartVisibility : IDisposable
    {
        internal sealed class Part
        {
            internal Renderer Renderer;
            internal int Slot;
            internal int GeometrySlot;
            internal Material Original;
            internal string Id, Name;
            internal bool Visible = true;
            internal bool Expanded;
            internal FineMesh Detail;
            internal UvIslandView UvView;
            internal int TextureIndex;
            internal int Page;
        }
        internal readonly List<Part> Parts = new List<Part>();
        internal FineMesh MeshForExport(Renderer renderer){return EnsureMesh(Parts.First(p=>p.Renderer==renderer&&HasGeometry(p)));}
        private readonly Material invisible;
        private readonly Dictionary<Renderer,FineMesh> meshes=new Dictionary<Renderer,FineMesh>();
        private readonly string folder;
        private readonly Transform modelRoot;
        private string error;
        private readonly List<TextureBinding> bindings;
        private Part hoverPart;
        private int hoverPiece=-1;
        private int hoverIsland=-1;
        private float hoverUntil;
        private void Hover(Part part,int piece,Rect rect)
        {
            if(Event.current.type==EventType.Repaint&&rect.Contains(Event.current.mousePosition)){hoverPart=part;hoverPiece=piece;hoverIsland=-1;hoverUntil=Time.realtimeSinceStartup+.18f;}
        }
        internal void TickHover(){UpdateHighlight();}
        internal void ClearHover(){hoverPart=null;hoverUntil=0;UpdateHighlight();}
        internal PartVisibility(GameObject model, string folder,List<TextureBinding> bindings,string viewerRoot)
        {
            this.bindings=bindings;
            this.folder=folder;
            LoadBrushPreferences(viewerRoot);
            modelRoot=model.transform;
            var shader = Shader.Find("UI/Default");
            if (shader != null) { invisible = new Material(shader); invisible.name = "Viewer hidden part"; invisible.SetColor("_Color", Color.clear); }
            var manifest = new JArray();
            foreach (var renderer in AssetExporter.Renderers(model))
            {
                var materials = renderer.sharedMaterials;
                var skin = renderer as SkinnedMeshRenderer;
                var filter = renderer.GetComponent<MeshFilter>();
                var geometry = skin != null ? skin.sharedMesh : filter != null ? filter.sharedMesh : null;
                int geometryCount = geometry == null ? 0 : geometry.subMeshCount;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot]; if (material == null) continue;
                    var part = new Part { Renderer = renderer, Slot = slot, GeometrySlot = Math.Min(slot, geometryCount - 1), Original = material,
                        Id = "P" + Parts.Count.ToString("D3"), Name = renderer.name + " / " + material.name.Replace(" (Instance)", "") };
                    Parts.Add(part);
                    manifest.Add(new JObject { ["Id"] = new JValue(part.Id), ["Renderer"] = new JValue(renderer.name), ["MaterialSlot"] = new JValue(slot), ["Material"] = new JValue(material.name) });
                }
            }
            File.WriteAllText(Path.Combine(folder, "parts.json"), manifest.ToString());
        }
        internal int Analyze(int index)
        {
            var part=Parts[index];FineMesh mesh;
            if(!meshes.TryGetValue(part.Renderer,out mesh))
            {
                mesh=new FineMesh(part.Renderer);meshes[part.Renderer]=mesh;
                foreach(var sibling in Parts.Where(row=>row.Renderer==part.Renderer && row.GeometrySlot<mesh.Slots.Length))sibling.Detail=mesh;
            }
            if(part.GeometrySlot<0||part.GeometrySlot>=mesh.Slots.Length)throw new InvalidOperationException("이 슬롯은 별도 메시가 없는 추가 렌더링 패스입니다.");
            if(part.UvView==null)
            {
                var textures=Textures(part);
                if(textures.Length==0){part.Expanded=true;SavePieces();return mesh.Slots[part.GeometrySlot].Count;}
                var binding=textures[part.TextureIndex%textures.Length];
                part.UvView=new UvIslandView(binding,mesh,part.GeometrySlot);
                ConnectPngEditor(part);
            }
            part.Expanded=true;SavePieces();return mesh.Slots[part.GeometrySlot].Count;
        }
        private TextureBinding[] Textures(Part part)
        {
            return bindings.Where(b=>b.Material==part.Original).OrderBy(b=>b.Property=="_BaseMap"?0:b.Property=="_MainTex"?1:b.Property.ToLowerInvariant().Contains("base")||b.Property.ToLowerInvariant().Contains("albedo")||b.Property.ToLowerInvariant().Contains("diffuse")?2:3).ToArray();
        }
        private IEnumerable<UvRasterLayer> VisibleTextureLayers(string file)
        {
            // Protect visible UVs even on other renderers/material slots sharing this PNG.
            foreach(var part in Parts.Where(p=>p.Visible&&p.Renderer.enabled&&p.Renderer.gameObject.activeInHierarchy&&HasGeometry(p)))
            foreach(var binding in Textures(part).Where(b=>string.Equals(b.File,file,StringComparison.OrdinalIgnoreCase)))
            {
                FineMesh mesh;
                if(!meshes.TryGetValue(part.Renderer,out mesh))
                {
                    mesh=new FineMesh(part.Renderer);meshes.Add(part.Renderer,mesh);
                    foreach(var sibling in Parts.Where(p=>p.Renderer==part.Renderer&&p.GeometrySlot<mesh.Slots.Length))sibling.Detail=mesh;
                }
                yield return new UvRasterLayer(mesh,mesh.VisibleIndices(part.GeometrySlot),binding);
            }
        }
        private void ReloadUvView(int index,bool next)
        {
            var part=Parts[index];if(part.UvView!=null){part.UvView.Dispose();part.UvView=null;}
            if(next)part.TextureIndex++;hoverPart=null;
            try{Analyze(index);error=null;}catch(Exception failure){error=failure.Message;Debug.LogWarning("[Viewer] UV islands: "+failure.Message);}
        }
        private void ExportTransparentPng()
        {
            var targets=Parts.Where(p=>HasGeometry(p)&&(p.UvView!=null||!p.Visible||p.Detail!=null&&p.Detail.Slots[p.GeometrySlot].Any(piece=>!piece.Visible)||Textures(p).Any(b=>RequiresAlpha(b.File)))).ToArray();
            if(targets.Length==0){error="먼저 UV 섬을 숨기거나 Ctrl 붓질로 일부를 지워 주세요.";return;}
            string output=Path.Combine(folder,"TransparentPNGs",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(output);var manifest=new JArray();int saved=0,shared=0;var failures=new List<string>();
            foreach(var part in targets)
            {
                var available=Textures(part);var sources=new List<TextureBinding>();
                if(part.UvView!=null)sources.Add(part.UvView.Binding);
                foreach(var binding in available.Where(b=>RequiresAlpha(b.File)))if(!sources.Any(b=>b.File==binding.File))sources.Add(binding);
                if(sources.Count==0&&available.Length>0)sources.Add(available[part.TextureIndex%available.Length]);
                if(sources.Count==0){failures.Add(part.Id+": 내보낼 텍스처가 없습니다.");continue;}
                foreach(var binding in sources)
                {
                    UvIslandView temporary=null;
                    try
                    {
                        var mesh=EnsureMesh(part);var view=part.UvView!=null&&part.UvView.Binding==binding?part.UvView:(temporary=new UvIslandView(binding,mesh,part.GeometrySlot));
                        string name=part.Id+"_"+AssetExporter.Safe(binding.Property)+"_"+Path.GetFileNameWithoutExtension(binding.File)+"_transparent.png";
                        int skipped;int pixels=view.ExportTransparent(Path.Combine(output,name),!part.Visible,VisibleTextureLayers(binding.File),out skipped,(colors,w,h)=>PadAlphaEdges(binding.File,colors,w,h));saved++;shared+=skipped;
                        manifest.Add(new JObject{["Part"]=new JValue(part.Id),["Source"]=new JValue(binding.File),["Output"]=new JValue(name),["TransparentPixelsAdded"]=new JValue(pixels),["IncludesBrushEdits"]=new JValue(RequiresAlpha(binding.File)),["SharedPixelsPreserved"]=new JValue(skipped)});
                    }
                    catch(Exception failure){failures.Add(part.Id+": "+failure.Message);Debug.LogWarning("[Viewer] Transparent PNG: "+failure.Message);}
                    finally{if(temporary!=null)temporary.Dispose();}
                }
            }
            File.WriteAllText(Path.Combine(output,"export.json"),new JObject{["Format"]=new JValue("TransparentUVExport.v1"),["Files"]=manifest,["Errors"]=new JArray(failures)}.ToString());
            File.WriteAllText(Path.Combine(output,"안내.txt"),"현재 편집 PNG의 Ctrl 붓질 알파와 UV 섬 전체 숨김을 함께 반영한 복사본입니다.\n원본 PNG와 이전 내보내기 파일은 덮어쓰지 않습니다.\n표시 중인 다른 부위와 겹친 UV 픽셀은 보호합니다.\n3D 투명 표시는 재질의 알파클립 지원 여부에 따라 다릅니다.\n");
            error="투명 PNG "+saved+"개 저장\n"+output+(shared>0?"\n표시 면과 겹친 UV 픽셀 "+shared+"개는 유지했습니다.":"")+(failures.Count>0?"\n"+string.Join("\n",failures.ToArray()):"");
            if(saved>0)System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe","\""+output+"\""));
        }
        private void SavePieces()
        {
            var result=new JArray();
            foreach(var part in Parts.Where(row=>row.Detail!=null))
            {
                var pieces=part.Detail.Slots[part.GeometrySlot];
                for(int i=0;i<pieces.Count;i++)result.Add(new JObject{["Id"]=new JValue(part.Id+"."+(i+1).ToString("D3")),["Hint"]=new JValue(pieces[i].Hint),["Basis"]=new JValue("UVIsland"),["IslandId"]=new JValue(pieces[i].IslandId),["TextureFile"]=new JValue(part.UvView==null?"":Path.GetFileName(part.UvView.Binding.File)),["RendererPath"]=new JValue(RendererPath(part.Renderer)),["Mesh"]=new JValue(part.Detail.MeshName),["Material"]=new JValue(part.Original.name),["Parent"]=new JValue(part.Id),["Triangles"]=new JValue(pieces[i].Indices.Length/3),["Center"]=new JArray(new float[]{pieces[i].Center.x,pieces[i].Center.y,pieces[i].Center.z})});
            }
            File.WriteAllText(Path.Combine(folder,"pieces.json"),result.ToString());
        }
        internal void SetPiece(int parent,int piece,bool visible)
        {
            var part=Parts[parent];EnsureMesh(part);var row=part.Detail.Slots[part.GeometrySlot][piece];
            if(row.Visible==visible)return;RecordUndo();SetMembersVisible(Members(row),visible);
            Debug.Log("[Viewer] Piece "+part.Id+"."+(piece+1)+" visible="+visible);
        }
        internal void ShowAll()
        {
            RecordUndo();
            for(int i=0;i<Parts.Count;i++)Set(i,true);
            foreach(var mesh in meshes.Values){foreach(var slot in mesh.Slots)foreach(var piece in slot)piece.Visible=true;mesh.Upload();}RefreshPicking();
        }
        internal int[] ExportIndices(Renderer renderer,int slot)
        {
            FineMesh mesh;return meshes.TryGetValue(renderer,out mesh)&&slot<mesh.Slots.Length?mesh.VisibleIndices(slot):null;
        }
        internal string RendererPath(Renderer renderer)
        {
            var segments=new List<string>();for(var node=renderer.transform;node!=null&&node!=modelRoot;node=node.parent)segments.Add(node.name);
            segments.Reverse();return string.Join("/",segments.ToArray());
        }
        internal JObject ProfileRules()
        {
            foreach(var part in Parts.Where(value=>!value.Visible).ToArray())if(!meshes.ContainsKey(part.Renderer))Analyze(Parts.FindIndex(value=>value.Renderer==part.Renderer && value.Slot==0));
            var geometry=new JArray();var materials=new JArray();
            foreach(var pair in meshes)
            for(int slot=0;slot<pair.Value.Slots.Length;slot++)
            {
                var mesh=pair.Value;var hidden=mesh.Slots[slot].Where(piece=>!piece.Visible).SelectMany(piece=>piece.TriangleIds).OrderBy(index=>index).ToArray();
                if(hidden.Length==0)continue;
                var original=mesh.SourceIndices[slot];var bytes=new byte[original.Length*4];Buffer.BlockCopy(original,0,bytes,0,bytes.Length);
                string hash;using(var digest=SHA256.Create())hash=BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-","");
                geometry.Add(new JObject{["RendererPath"]=new JValue(RendererPath(pair.Key)),["Mesh"]=new JValue(mesh.MeshName),["VertexCount"]=new JValue(mesh.VertexCount),["SubMesh"]=new JValue(slot),["OriginalTriangleCount"]=new JValue(original.Length/3),["OriginalIndexSHA256"]=new JValue(hash),["HiddenTriangleIds"]=new JArray(hidden)});
            }
            foreach(var part in Parts.Where(value=>!value.Visible))
            {
                var mesh=meshes[part.Renderer];int slot=Mathf.Min(part.Slot,mesh.Slots.Length-1);var source=mesh.SourceIndices[slot];var bytes=new byte[source.Length*4];Buffer.BlockCopy(source,0,bytes,0,bytes.Length);
                string hash;using(var digest=SHA256.Create())hash=BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-","");
                materials.Add(new JObject{["Id"]=new JValue(part.Id),["RendererPath"]=new JValue(RendererPath(part.Renderer)),["MaterialSlot"]=new JValue(part.Slot),["Material"]=new JValue(part.Original.name.Replace(" (Instance)","")),["GeometrySubMesh"]=new JValue(slot),["OriginalIndexSHA256"]=new JValue(hash)});
            }
            return new JObject{["HiddenGeometry"]=geometry,["HiddenMaterials"]=materials};
        }
        internal void Set(int index, bool visible)
        {
            var part = Parts[index];
            if (part.Renderer == null || visible == part.Visible) return;
            var materials = part.Renderer.sharedMaterials;
            if (invisible == null) throw new InvalidOperationException("부위 숨김용 셰이더를 불러오지 못했습니다.");
            materials[part.Slot] = visible ? part.Original : invisible;
            part.Renderer.sharedMaterials = materials; part.Visible = visible;
            Debug.Log("[Viewer] Part " + part.Id + " visible=" + visible);
        }
        private static bool HasGeometry(Part part)
        {
            var skinned=part.Renderer as SkinnedMeshRenderer;
            var filter=part.Renderer.GetComponent<MeshFilter>();
            var mesh=skinned!=null?skinned.sharedMesh:filter!=null?filter.sharedMesh:null;
            return mesh!=null&&(skinned!=null||mesh.isReadable)&&part.GeometrySlot>=0&&part.GeometrySlot<mesh.subMeshCount;
        }
        private static void Eye(Rect rect, bool open)
        {
            // Draw a clear eye icon without depending on an installed symbol font.
            var original = GUI.color; GUI.color = open ? new Color(.5f,.85f,1) : new Color(.5f,.5f,.5f);
            for(int x=0;x<24;x++) { float y=6*Mathf.Sqrt(Mathf.Max(0,1-Mathf.Pow((x-11.5f)/11.5f,2))); GUI.DrawTexture(new Rect(rect.x+5+x,rect.y+16-y,1,1.5f),Texture2D.whiteTexture); GUI.DrawTexture(new Rect(rect.x+5+x,rect.y+16+y,1,1.5f),Texture2D.whiteTexture); }
            GUI.DrawTexture(new Rect(rect.x+15,rect.y+12,5,9),Texture2D.whiteTexture);
            if (!open) { GUI.color = new Color(.9f,.5f,.5f); GUI.DrawTexture(new Rect(rect.x+5,rect.y+15,24,2),Texture2D.whiteTexture); }
            GUI.color = original;
        }
        internal void Draw(GUIStyle text, GUIStyle heading)
        {
            GUILayout.Space(16); GUILayout.Label("부위 / 재질", heading);
            DrawSelectionControls(text);
            GUILayout.Label("UV 섬 기준\nUV 경계가 이어진 면끼리 분리합니다.\n목록 / UV 영역 위에서 마우스로 식별합니다.", text);
            if (GUILayout.Button("모든 부위 / 조각 표시")) ShowAll();
            if (GUILayout.Button("숨긴 UV 영역 → 투명 PNG 내보내기")) ExportTransparentPng();
            if(!string.IsNullOrEmpty(error))GUILayout.Label(error,text);
            for (int i = 0; i < Parts.Count; i++)
            {
                var part = Parts[i]; GUILayout.BeginHorizontal();
                var rect = GUILayoutUtility.GetRect(34,32,GUI.skin.button,new GUILayoutOption[]{GUILayout.Width(34),GUILayout.Height(32)});
                if (GUI.Button(rect,"")){RecordUndo();Set(i,!part.Visible);RefreshPicking();}
                Eye(rect,part.Visible);
                Hover(part,-1,rect);
                var labelRect=GUILayoutUtility.GetRect(120,64,text,new GUILayoutOption[]{GUILayout.ExpandWidth(true),GUILayout.Height(64)});GUI.Label(labelRect,new GUIContent(part.Id + " · " + part.Name + (part.Renderer.enabled && part.Renderer.gameObject.activeInHierarchy ? "" : "\n(현재 상태에서 비활성)")), text);Hover(part,-1,labelRect); GUILayout.EndHorizontal();
                if(!HasGeometry(part))continue;
                if(part.Slot!=part.GeometrySlot)GUILayout.Label("앞뒤 조각·부분 지우기 연동",text);
                if(GUILayout.Button(part.Expanded?"▾ UV 섬 접기":"▸ UV 섬 펼치기"))
                {
                    if(part.Expanded)part.Expanded=false;
                    else try{Analyze(i);error=null;}catch(Exception failure){error=failure.Message;Debug.LogWarning("[Viewer] Piece analysis: "+failure.Message);}
                }
                if(part.Expanded && part.Detail!=null)
                {
                    int selected=hoverPart==part&&Time.realtimeSinceStartup<hoverUntil?(hoverIsland>=0?hoverIsland:hoverPiece>=0&&hoverPiece<part.Detail.Slots[part.GeometrySlot].Count?part.Detail.Slots[part.GeometrySlot][hoverPiece].IslandId:-1):-1;
                    int region=part.UvView==null?-1:part.UvView.Draw(text,selected);
                    if(region>=0&&Event.current.type==EventType.Repaint){hoverPart=part;hoverPiece=-1;hoverIsland=region;hoverUntil=Time.realtimeSinceStartup+.18f;}
                    if(GUILayout.Button("PNG 미리보기 다시 읽기"))ReloadUvView(i,false);
                    if(Textures(part).Length>1&&GUILayout.Button("미리보기 텍스처 변경"))ReloadUvView(i,true);
                    var pieces=part.Detail.Slots[part.GeometrySlot];
                    const int pageSize=18;int pages=Mathf.Max(1,(pieces.Count+pageSize-1)/pageSize);part.Page=Mathf.Clamp(part.Page,0,pages-1);
                    GUILayout.BeginHorizontal();if(GUILayout.Button("이전"))part.Page=Mathf.Max(0,part.Page-1);GUILayout.Label((part.Page+1)+" / "+pages,text);if(GUILayout.Button("다음"))part.Page=Mathf.Min(pages-1,part.Page+1);GUILayout.EndHorizontal();
                    for(int piece=part.Page*pageSize;piece<Mathf.Min(pieces.Count,(part.Page+1)*pageSize);piece++)
                    {
                        var row=pieces[piece];GUILayout.BeginHorizontal();GUILayout.Space(14);
                        var eye=GUILayoutUtility.GetRect(34,30,GUI.skin.button,new GUILayoutOption[]{GUILayout.Width(34),GUILayout.Height(30)});
                        if(GUI.Button(eye,""))SetPiece(i,piece,!row.Visible);Eye(eye,row.Visible&&part.Visible);
                        Hover(part,piece,eye);
                        var pieceRect=GUILayoutUtility.GetRect(100,68,text,new GUILayoutOption[]{GUILayout.ExpandWidth(true),GUILayout.Height(68)});GUI.Label(pieceRect,new GUIContent(part.Id+"."+(piece+1).ToString("D3")+" · "+row.Indices.Length/3+"면\n"+GroupLabel(row)+row.Hint),text);Hover(part,piece,pieceRect);
                        if(GUILayout.Button("단독",GUILayout.Width(42)))
                        {
                            Isolate(row);
                        }
                        GUILayout.EndHorizontal();
                    }
                }
            }
        }
        public void Dispose() { foreach(var part in Parts)if(part.UvView!=null)part.UvView.Dispose();DisposeEditor();foreach(var mesh in meshes.Values)mesh.Dispose();meshes.Clear();if (invisible != null) UnityEngine.Object.Destroy(invisible); }
    }
}

