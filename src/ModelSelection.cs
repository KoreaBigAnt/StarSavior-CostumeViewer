using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed partial class PartVisibility
    {
        internal bool SelectionMode {get;private set;}
        private readonly HashSet<MeshPiece> selection=new HashSet<MeshPiece>();
        private readonly Dictionary<MeshPiece,int> groups=new Dictionary<MeshPiece,int>();
        private readonly Stack<EditSnapshot> undo=new Stack<EditSnapshot>();
        private readonly Dictionary<Animator,float> speeds=new Dictionary<Animator,float>();
        private readonly List<PickSurface> picking=new List<PickSurface>();
        private readonly Dictionary<Renderer,HoverSurface> overlays=new Dictionary<Renderer,HoverSurface>();
        private MeshPiece lastTarget;
        private Part lastPart;
        private int groupSerial,overlayRevision,lastOverlayRevision=-1;
        private bool groupsLoaded;
        private bool pointerDown,boxDragging,boxSelect;
        private Vector2 boxStart,boxEnd;
        private PieceRef pressedPiece;
        private sealed class EditSnapshot
        {
            internal Part[] Parts;
            internal bool[] Visible;
            internal MeshPiece[] Pieces,Selected;
            internal bool[] PieceVisible;
            internal Dictionary<MeshPiece,int> Groups;
            internal Dictionary<string,byte[]> Textures;
        }
        private sealed class PieceRef
        {
            internal Part Part;
            internal MeshPiece Piece;
        }
        private FineMesh EnsureMesh(Part part)
        {
            FineMesh mesh;
            if(!meshes.TryGetValue(part.Renderer,out mesh))
            {
                mesh=new FineMesh(part.Renderer);meshes.Add(part.Renderer,mesh);
                foreach(var sibling in Parts.Where(p=>p.Renderer==part.Renderer&&p.GeometrySlot<mesh.Slots.Length))sibling.Detail=mesh;
            }
            return mesh;
        }
        private IEnumerable<PieceRef> Pieces()
        {
            foreach(var part in Parts)if(part.Detail!=null&&part.Slot==part.GeometrySlot)
                foreach(var piece in part.Detail.Slots[part.GeometrySlot])yield return new PieceRef{Part=part,Piece=piece};
        }
        private readonly Stack<EditSnapshot> redo=new Stack<EditSnapshot>();
        private EditSnapshot CaptureEdit()
        {
            var rows=Pieces().Select(p=>p.Piece).ToArray();
            return new EditSnapshot{Parts=Parts.ToArray(),Visible=Parts.Select(p=>p.Visible).ToArray(),Pieces=rows,PieceVisible=rows.Select(p=>p.Visible).ToArray(),Selected=selection.ToArray(),Groups=new Dictionary<MeshPiece,int>(groups),Textures=new Dictionary<string,byte[]>(paintTextures,StringComparer.OrdinalIgnoreCase)};
        }
        private void RecordUndo()
        {
            if(undo.Count>=60){var recent=undo.ToArray();undo.Clear();for(int i=58;i>=0;i--)undo.Push(recent[i]);}
            undo.Push(CaptureEdit());redo.Clear();
        }
        private void ApplyEdit(EditSnapshot state)
        {
            RestorePaintTextures(state.Textures);
            for(int i=0;i<state.Parts.Length;i++)Set(Parts.IndexOf(state.Parts[i]),state.Visible[i]);
            for(int i=0;i<state.Pieces.Length;i++)state.Pieces[i].Visible=state.PieceVisible[i];
            groups.Clear();foreach(var group in state.Groups)groups.Add(group.Key,group.Value);
            selection.Clear();foreach(var piece in state.Selected)selection.Add(piece);
            foreach(var mesh in meshes.Values)mesh.Upload();overlayRevision++;RefreshPicking();SaveGroups();
        }
        internal void Undo()
        {
            if(brush!=null){CancelPaint();error="진행 중인 붓질을 취소했습니다.";return;}
            pointerDown=false;boxDragging=false;pressedPiece=null;
            if(undo.Count==0){error="되돌릴 작업이 없습니다.";return;}
            try{var current=CaptureEdit();ApplyEdit(undo.Peek());undo.Pop();redo.Push(current);error="되돌리기 완료 · Ctrl+Y로 다시 실행";}catch(Exception e){error="되돌리기 실패: "+e.Message;}
        }
        internal void Redo()
        {
            if(brush!=null){CancelPaint();error="진행 중인 붓질을 취소했습니다.";return;}
            pointerDown=false;boxDragging=false;pressedPiece=null;
            if(redo.Count==0){error="다시 실행할 작업이 없습니다.";return;}
            try{var current=CaptureEdit();ApplyEdit(redo.Peek());redo.Pop();undo.Push(current);error="다시 실행 완료";}catch(Exception e){error="다시 실행 실패: "+e.Message;}
        }
        private IEnumerable<MeshPiece> Members(MeshPiece piece)
        {
            int id;return groups.TryGetValue(piece,out id)?groups.Where(g=>g.Value==id).Select(g=>g.Key).ToArray():new[]{piece};
        }
        private string GroupLabel(MeshPiece piece){int id;return (selection.Contains(piece)?"[선택] ":"")+(groups.TryGetValue(piece,out id)?"묶음 "+id+" · ":"");}
        private void SetMembersVisible(IEnumerable<MeshPiece> members,bool visible)
        {
            var set=new HashSet<MeshPiece>(members);var dirty=new HashSet<FineMesh>();
            foreach(var row in Pieces())if(set.Contains(row.Piece)&&row.Piece.Visible!=visible){row.Piece.Visible=visible;dirty.Add(row.Part.Detail);}
            foreach(var mesh in dirty)mesh.Upload();overlayRevision++;RefreshPicking();
        }
        private void GroupSelection()
        {
            var members=new HashSet<MeshPiece>(selection.SelectMany(p=>Members(p)));
            if(members.Count<2){error="Shift+클릭으로 두 조각 이상 선택하세요.";return;}
            RecordUndo();int id=++groupSerial;foreach(var piece in members)groups[piece]=id;selection.Clear();overlayRevision++;SaveGroups();error="묶음 "+id+" 생성: "+members.Count+"개 조각";
        }
        private void SaveGroups()
        {
            if(!groupsLoaded)return;
            var rows=Pieces().Where(p=>groups.ContainsKey(p.Piece)).Select(p=>new JObject{["Part"]=new JValue(p.Part.Id),["Island"]=new JValue(p.Piece.IslandId),["Group"]=new JValue(groups[p.Piece])});
            File.WriteAllText(Path.Combine(folder,"uv-groups.json"),new JObject{["Format"]=new JValue("UVGroups.v1"),["Members"]=new JArray(rows)}.ToString());
        }
        private void LoadGroups()
        {
            if(groupsLoaded)return;groupsLoaded=true;string path=Path.Combine(folder,"uv-groups.json");if(!File.Exists(path))return;
            try
            {
                var data=JObject.Parse(File.ReadAllText(path));if((string)data["Format"]!="UVGroups.v1")return;
                var rows=Pieces().ToArray();foreach(var entry in ((JArray)data["Members"]).OfType<JObject>())
                {
                    var row=rows.FirstOrDefault(p=>p.Part.Id==(string)entry["Part"]&&p.Piece.IslandId==(int)entry["Island"]);int id=(int)entry["Group"];
                    if(row!=null&&id>0){groups[row.Piece]=id;groupSerial=Mathf.Max(groupSerial,id);}
                }
            }catch(Exception e){Debug.LogWarning("[Viewer] UV groups: "+e.Message);}
        }
        internal Action BeforeSelection;
        internal void SetSelectionMode(bool enabled)
        {
            if(SelectionMode==enabled)return;
            if(!enabled)
            {
                CancelPaint();SelectionMode=false;pointerDown=false;boxDragging=false;pressedPiece=null;foreach(var pair in speeds)if(pair.Key!=null)pair.Key.speed=pair.Value;speeds.Clear();
                foreach(var proxy in picking)proxy.Dispose();picking.Clear();hoverPart=null;overlayRevision++;return;
            }
            try
            {
                BeforeSelection?.Invoke();
                SelectionMode=true;
                foreach(var animator in modelRoot.GetComponentsInChildren<Animator>(true)){speeds[animator]=animator.speed;animator.speed=0;}
                foreach(var part in Parts.Where(p=>HasGeometry(p)))EnsureMesh(part);
                LoadGroups();
                foreach(var renderer in meshes.Keys)if(renderer!=null&&renderer.enabled&&renderer.gameObject.activeInHierarchy)
                    picking.Add(new PickSurface(renderer,meshes[renderer],Parts.Where(p=>p.Renderer==renderer&&p.GeometrySlot<meshes[renderer].Slots.Length).ToArray()));
                error="선택 모드: 클릭 숨김 / Shift+클릭 묶음 선택";
            }
            catch(Exception e){SetSelectionMode(false);error="선택 모드 준비 실패: "+e.Message;Debug.LogException(e);}
        }
        private void RefreshPicking(){foreach(var proxy in picking)proxy.Refresh();}
        private void Isolate(MeshPiece piece)
        {
            RecordUndo();var wanted=new HashSet<MeshPiece>(Members(piece));var rows=Pieces().ToArray();
            foreach(var part in Parts)Set(Parts.IndexOf(part),rows.Any(r=>r.Part==part&&wanted.Contains(r.Piece)));
            foreach(var row in rows)row.Piece.Visible=wanted.Contains(row.Piece);
            foreach(var mesh in meshes.Values)mesh.Upload();overlayRevision++;RefreshPicking();
        }
        internal void UpdateModelSelection(Camera camera)
        {
            if((Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl))&&Input.GetKeyDown(KeyCode.Z))Undo();
            if((Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl))&&Input.GetKeyDown(KeyCode.Y))Redo();
            if(PngEditorOpen)
            {
                if(Input.GetKeyDown(KeyCode.Escape))ClosePngEditor();
                else if(brush!=null&&brush.Png&&!Input.GetMouseButton(0))
                {try{FinishPaint();}catch(Exception e){CancelPaint();error="PNG 저장 실패: "+e.Message;}}
                return;
            }
            if(!SelectionMode||camera==null)return;
            if(Input.GetKeyDown(KeyCode.Escape)){SetSelectionMode(false);return;}
            if(UpdatePaint(camera))return;
            bool over=Input.mousePosition.x>280&&Input.mousePosition.x<Viewer.ViewportRight;
            if(Input.GetMouseButton(1)||Input.GetMouseButton(2)){pointerDown=false;boxDragging=false;return;}
            if(pointerDown)
            {
                boxEnd=new Vector2(Mathf.Clamp(Input.mousePosition.x,280,Viewer.ViewportRight),Mathf.Clamp(Input.mousePosition.y,0,Screen.height));
                if((boxEnd-boxStart).sqrMagnitude>=36)boxDragging=true;
                if(Input.GetMouseButtonUp(0))
                {
                    var targets=new HashSet<MeshPiece>();
                    if(boxDragging)
                    {
                        var rect=BoxRect();foreach(var proxy in picking)foreach(var piece in proxy.BoxHits(camera,rect))foreach(var member in Members(piece))targets.Add(member);
                    }
                    else if(pressedPiece!=null&&over)foreach(var member in Members(pressedPiece.Piece))targets.Add(member);
                    if(targets.Count>0)
                    {
                        if(boxSelect)
                        {
                            bool remove=!boxDragging&&targets.All(p=>selection.Contains(p));foreach(var piece in targets){if(remove)selection.Remove(piece);else selection.Add(piece);}
                            error="묶을 조각 "+selection.Count+"개 선택";
                        }
                        else{RecordUndo();SetMembersVisible(targets,false);error=targets.Count+"개 조각 숨김 · Ctrl+Z로 한 번에 되돌리기";}
                    }
                    pointerDown=false;boxDragging=false;pressedPiece=null;hoverPart=null;return;
                }
                if(!Input.GetMouseButton(0)){pointerDown=false;boxDragging=false;pressedPiece=null;}
                if(boxDragging){hoverPart=null;return;}
            }
            if(!over)return;
            var ray=camera.ScreenPointToRay(Input.mousePosition);float distance=10000;PieceRef hit=null;
            foreach(var proxy in picking){PieceRef row;float d;if(proxy.Hit(ray,out row,out d)&&d<distance){distance=d;hit=row;}}
            if(Input.GetMouseButtonDown(0))
            {
                pointerDown=true;boxDragging=false;pressedPiece=hit;boxStart=new Vector2(Input.mousePosition.x,Input.mousePosition.y);boxEnd=boxStart;
                boxSelect=Input.GetKey(KeyCode.LeftShift)||Input.GetKey(KeyCode.RightShift);
            }
            if(hit==null){hoverPart=null;return;}
            hoverPart=hit.Part;hoverPiece=hit.Part.Detail.Slots[hit.Part.GeometrySlot].IndexOf(hit.Piece);hoverIsland=-1;hoverUntil=Time.realtimeSinceStartup+.18f;
        }
        private Rect BoxRect(){return new Rect(Mathf.Min(boxStart.x,boxEnd.x),Mathf.Min(boxStart.y,boxEnd.y),Mathf.Abs(boxStart.x-boxEnd.x),Mathf.Abs(boxStart.y-boxEnd.y));}
        internal void DrawSelectionBox()
        {
            if(!SelectionMode||!pointerDown||!boxDragging)return;
            var area=BoxRect();var rect=new Rect(area.x,Screen.height-area.yMax,area.width,area.height);var color=GUI.color;
            GUI.color=new Color(.05f,1,1,.15f);GUI.DrawTexture(rect,Texture2D.whiteTexture);GUI.color=new Color(.05f,1,1,.95f);
            GUI.DrawTexture(new Rect(rect.x,rect.y,rect.width,2),Texture2D.whiteTexture);GUI.DrawTexture(new Rect(rect.x,rect.yMax-2,rect.width,2),Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x,rect.y,2,rect.height),Texture2D.whiteTexture);GUI.DrawTexture(new Rect(rect.xMax-2,rect.y,2,rect.height),Texture2D.whiteTexture);GUI.color=color;
        }
        private void DrawSelectionControls(GUIStyle text)
        {
            if(GUILayout.Button(SelectionMode?"선택 모드 끄기":"모델에서 클릭해 숨기기 · 선택 모드"))SetSelectionMode(!SelectionMode);
            GUILayout.Label("좌클릭: 숨김 / 드래그: 영역 일괄 숨김\n영역에 걸친 앞뒤 조각 전체를 포함합니다.\nShift+클릭/드래그: 묶을 조각 선택\n우클릭 드래그: 회전 / 휠 드래그: 이동\nCtrl+Z: 되돌리기 / Esc: 선택 모드 종료",text);
            DrawPaintControls(text);
            if(GUILayout.Button("되돌리기 (Ctrl+Z)"))Undo();
            if(GUILayout.Button("다시 실행 (Ctrl+Y)"))Redo();
            GUILayout.Label("묶을 조각: "+selection.Count+"개",text);
            if(GUILayout.Button("선택 조각을 하나로 묶기"))GroupSelection();
            if(GUILayout.Button("선택 조각 숨기기")){if(selection.Count>0){RecordUndo();SetMembersVisible(selection.SelectMany(p=>Members(p)).ToArray(),false);selection.Clear();}}
            if(GUILayout.Button("선택 묶음 해제")){if(selection.Count>0){RecordUndo();foreach(var p in selection.SelectMany(v=>Members(v)).ToArray())groups.Remove(p);overlayRevision++;SaveGroups();}}
            if(GUILayout.Button("조각 선택 해제"))selection.Clear();
        }
        private void UpdateHighlight()
        {
            bool active=brush==null&&hoverPart!=null&&Time.realtimeSinceStartup<hoverUntil&&hoverPart.Renderer!=null&&hoverPart.Renderer.gameObject.activeInHierarchy;
            MeshPiece target=null;
            if(active&&hoverPart.Detail!=null)
            {
                var slot=hoverPart.Detail.Slots[hoverPart.GeometrySlot];target=hoverIsland>=0?slot.FirstOrDefault(p=>p.IslandId==hoverIsland):hoverPiece>=0&&hoverPiece<slot.Count?slot[hoverPiece]:null;
            }
            bool single=Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl);
            bool changed=lastTarget!=target||lastPart!=(active?hoverPart:null)||lastOverlayRevision!=overlayRevision||lastHoverSingle!=single;
            if(changed)
            {
                lastTarget=target;lastPart=active?hoverPart:null;lastOverlayRevision=overlayRevision;lastHoverSingle=single;
                foreach(var overlay in overlays.Values)overlay.Active=false;
                if(active)
                {
                    var wanted=target==null?new HashSet<MeshPiece>():new HashSet<MeshPiece>(single?new[]{target}:Members(target));
                    var rows=Pieces().Where(p=>target==null?p.Part==hoverPart:wanted.Contains(p.Piece)).GroupBy(p=>p.Part.Renderer);
                    foreach(var group in rows)
                    {
                        HoverSurface surface;if(!overlays.TryGetValue(group.Key,out surface)){surface=new HoverSurface(group.Key);overlays.Add(group.Key,surface);}
                        var first=group.First();bool painted=Textures(first.Part).Any(b=>RequiresAlpha(b.File));
                        surface.Set(group.SelectMany(p=>p.Part.Detail.PieceIndices(p.Part.GeometrySlot,p.Piece)).ToArray(),meshes[group.Key],painted?first.Part.Original:null);
                    }
                }
            }
            foreach(var surface in overlays.Values)surface.Tick(active,SelectionMode);
        }
        private void DisposeEditor()
        {
            CancelPaint();DisposePngEditor();SetSelectionMode(false);foreach(var surface in overlays.Values)surface.Dispose();overlays.Clear();undo.Clear();redo.Clear();paintTextures.Clear();paintOriginals.Clear();
        }
        private sealed class PickSurface:IDisposable
        {
            private readonly Renderer renderer;
            private readonly FineMesh fine;
            private readonly Part[] parts;
            private readonly Mesh mesh;
            private readonly GameObject host;
            private readonly MeshCollider collider;
            private PieceRef[] map;
            private bool[] visibility;
            private int[] triangleIndices;
            private readonly Vector3[] localVertices;
            private readonly Vector3[] worldVertices;
            private static readonly System.Reflection.FieldInfo faceField=typeof(RaycastHit).GetField("m_FaceID",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public);
            internal PickSurface(Renderer renderer,FineMesh fine,Part[] parts)
            {
                this.renderer=renderer;this.fine=fine;this.parts=parts;mesh=fine.Snapshot();localVertices=mesh.vertices;mesh.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;
                host=new GameObject("Viewer picking surface");host.layer=2;host.transform.position=renderer.transform.position;host.transform.rotation=renderer.transform.rotation;host.transform.localScale=renderer.transform.lossyScale;
                worldVertices=new Vector3[localVertices.Length];for(int i=0;i<worldVertices.Length;i++)worldVertices[i]=host.transform.TransformPoint(localVertices[i]);
                collider=host.AddComponent<MeshCollider>();Refresh();
            }
            internal void Refresh()
            {
                var current=parts.SelectMany(part=>fine.Slots[part.GeometrySlot].Select(piece=>part.Visible&&piece.Visible)).ToArray();
                if(visibility!=null&&visibility.SequenceEqual(current))return;visibility=current;
                var indices=new List<int>();var hits=new List<PieceRef>();
                foreach(var part in parts.Where(p=>p.Visible).GroupBy(p=>p.GeometrySlot).Select(g=>g.First()))foreach(var piece in fine.Slots[part.GeometrySlot].Where(p=>p.Visible))
                {var triangles=fine.PieceIndices(part.GeometrySlot,piece);indices.AddRange(triangles);var row=new PieceRef{Part=part,Piece=piece};for(int i=0;i<triangles.Length/3;i++)hits.Add(row);}
                collider.sharedMesh=null;map=hits.ToArray();triangleIndices=indices.ToArray();mesh.subMeshCount=1;mesh.triangles=triangleIndices;collider.enabled=indices.Count>0;if(collider.enabled)collider.sharedMesh=mesh;
            }
            internal IEnumerable<MeshPiece> BoxHits(Camera camera,Rect rect)
            {
                var found=new HashSet<MeshPiece>();
                if(renderer==null||!renderer.enabled||!renderer.gameObject.activeInHierarchy)return found;
                var points=new Vector3[localVertices.Length];for(int i=0;i<points.Length;i++)points[i]=camera.WorldToScreenPoint(host.transform.TransformPoint(localVertices[i]));
                for(int t=0;t<map.Length;t++)
                {
                    if(found.Contains(map[t].Piece))continue;
                    var a=points[triangleIndices[t*3]];var b=points[triangleIndices[t*3+1]];var c=points[triangleIndices[t*3+2]];
                    if(a.z<=camera.nearClipPlane||b.z<=camera.nearClipPlane||c.z<=camera.nearClipPlane)continue;
                    if(TriangleOverlaps(rect,new Vector2(a.x,a.y),new Vector2(b.x,b.y),new Vector2(c.x,c.y)))found.Add(map[t].Piece);
                }
                return found;
            }
            private static float Cross(Vector2 a,Vector2 b,Vector2 c){return (b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x);}
            private static bool Inside(Vector2 p,Vector2 a,Vector2 b,Vector2 c)
            {float u=Cross(a,b,p),v=Cross(b,c,p),w=Cross(c,a,p);return u>=0&&v>=0&&w>=0||u<=0&&v<=0&&w<=0;}
            private static bool Crosses(Vector2 a,Vector2 b,Vector2 c,Vector2 d)
            {
                if(Mathf.Max(a.x,b.x)<Mathf.Min(c.x,d.x)||Mathf.Max(c.x,d.x)<Mathf.Min(a.x,b.x)||Mathf.Max(a.y,b.y)<Mathf.Min(c.y,d.y)||Mathf.Max(c.y,d.y)<Mathf.Min(a.y,b.y))return false;
                return Cross(a,b,c)*Cross(a,b,d)<=0&&Cross(c,d,a)*Cross(c,d,b)<=0;
            }
            private static bool TriangleOverlaps(Rect rect,Vector2 a,Vector2 b,Vector2 c)
            {
                if(Mathf.Max(a.x,Mathf.Max(b.x,c.x))<rect.xMin||Mathf.Min(a.x,Mathf.Min(b.x,c.x))>rect.xMax||Mathf.Max(a.y,Mathf.Max(b.y,c.y))<rect.yMin||Mathf.Min(a.y,Mathf.Min(b.y,c.y))>rect.yMax)return false;
                if(rect.Contains(a)||rect.Contains(b)||rect.Contains(c))return true;
                var p=new Vector2(rect.xMin,rect.yMin);var q=new Vector2(rect.xMax,rect.yMin);var r=new Vector2(rect.xMax,rect.yMax);var s=new Vector2(rect.xMin,rect.yMax);
                if(Mathf.Abs(Cross(a,b,c))>1e-5f&&(Inside(p,a,b,c)||Inside(q,a,b,c)||Inside(r,a,b,c)||Inside(s,a,b,c)))return true;
                return Crosses(a,b,p,q)||Crosses(a,b,q,r)||Crosses(a,b,r,s)||Crosses(a,b,s,p)||Crosses(b,c,p,q)||Crosses(b,c,q,r)||Crosses(b,c,r,s)||Crosses(b,c,s,p)||Crosses(c,a,p,q)||Crosses(c,a,q,r)||Crosses(c,a,r,s)||Crosses(c,a,s,p);
            }
            internal bool Hit(Ray ray,out PieceRef row,out float distance)
            {
                row=null;distance=0;RaycastHit hit;
                if(!collider.enabled||renderer==null||!renderer.enabled||!renderer.gameObject.activeInHierarchy||!collider.Raycast(ray,out hit,10000))return false;
                if(faceField==null)return false;int triangle=unchecked((int)Convert.ToUInt32(faceField.GetValue(hit)));if(triangle<0||triangle>=map.Length)return false;row=map[triangle];distance=hit.distance;return true;
            }
            internal bool Sample(Camera camera,Vector2 screen,out PaintHit sample,out float distance)
            {
                sample=null;distance=0;RaycastHit hit;var ray=camera.ScreenPointToRay(new Vector3(screen.x,screen.y,0));
                if(!collider.enabled||renderer==null||!renderer.enabled||!renderer.gameObject.activeInHierarchy||!collider.Raycast(ray,out hit,10000)||faceField==null)return false;
                int triangle=unchecked((int)Convert.ToUInt32(faceField.GetValue(hit)));if(triangle<0||triangle>=map.Length)return false;
                int ia=triangleIndices[triangle*3],ib=triangleIndices[triangle*3+1],ic=triangleIndices[triangle*3+2];
                Vector3 a=worldVertices[ia],b=worldVertices[ib],c=worldVertices[ic];Vector2 uv;
                if(!PlaneUv(ray,a,b,c,fine.UV[ia],fine.UV[ib],fine.UV[ic],out uv))return false;
                Vector2 ux,uy;if(!PlaneUv(camera.ScreenPointToRay(new Vector3(screen.x+1,screen.y,0)),a,b,c,fine.UV[ia],fine.UV[ib],fine.UV[ic],out ux)||!PlaneUv(camera.ScreenPointToRay(new Vector3(screen.x,screen.y+1,0)),a,b,c,fine.UV[ia],fine.UV[ib],fine.UV[ic],out uy))return false;
                sample=new PaintHit{Row=map[triangle],UV=uv,Dx=ux-uv,Dy=uy-uv,A=fine.UV[ia],B=fine.UV[ib],C=fine.UV[ic]};distance=hit.distance;return true;
            }
            private static bool PlaneUv(Ray ray,Vector3 a,Vector3 b,Vector3 c,Vector2 ua,Vector2 ub,Vector2 uc,out Vector2 uv)
            {
                uv=Vector2.zero;Vector3 e=b-a,f=c-a,n=Vector3.Cross(e,f);float denominator=Vector3.Dot(n,ray.direction);if(Mathf.Abs(denominator)<1e-10f)return false;
                Vector3 q=ray.origin+ray.direction*(Vector3.Dot(n,a-ray.origin)/denominator)-a;
                float ee=Vector3.Dot(e,e),ef=Vector3.Dot(e,f),ff=Vector3.Dot(f,f),qe=Vector3.Dot(q,e),qf=Vector3.Dot(q,f),d=ee*ff-ef*ef;if(Mathf.Abs(d)<1e-15f)return false;
                float v=(qe*ff-qf*ef)/d,w=(qf*ee-qe*ef)/d;uv=ua*(1-v-w)+ub*v+uc*w;return true;
            }
            public void Dispose(){if(host!=null)UnityEngine.Object.Destroy(host);if(mesh!=null)UnityEngine.Object.Destroy(mesh);}
        }
        private sealed class HoverSurface:IDisposable
        {
            private readonly Renderer renderer;
            private readonly GameObject host;
            private readonly Material material;
            private Mesh mesh;
            private FineMesh fine;
            private int[] indices;
            private Color[] colors;
            private float nextBake;
            private Material alphaSource;
            private string alphaProperty;
            private Texture boundAlpha;
            internal bool Active;
            internal HoverSurface(Renderer renderer)
            {
                this.renderer=renderer;host=new GameObject("Viewer hover overlay",typeof(MeshFilter),typeof(MeshRenderer));host.layer=renderer.gameObject.layer;
                var shader=Shader.Find("UI/Default")??Shader.Find("Hidden/Internal-Colored");material=new Material(shader);material.renderQueue=3100;
                material.SetInt("_ZTest",4);material.SetInt("_ZWrite",0);material.SetInt("_Cull",0);material.SetInt("_SrcBlend",5);material.SetInt("_DstBlend",10);
                host.GetComponent<MeshRenderer>().sharedMaterial=material;host.GetComponent<MeshRenderer>().shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;host.GetComponent<MeshRenderer>().receiveShadows=false;
            }
            internal void Set(int[] indices,FineMesh fine,Material alphaSource)
            {
                this.indices=indices;this.fine=fine;Active=true;
                this.alphaSource=alphaSource;alphaProperty=alphaSource==null?null:new[]{"_BaseMap","_MainTex","_BaseColorMap"}.FirstOrDefault(p=>alphaSource.HasProperty(p)&&alphaSource.GetTexture(p)!=null);
                if(material.HasProperty("_MainTex"))
                {
                    string property=alphaProperty;
                    material.SetTexture("_MainTex",property==null?Texture2D.whiteTexture:alphaSource.GetTexture(property));
                    boundAlpha=property==null?Texture2D.whiteTexture:alphaSource.GetTexture(property);
                    material.SetTextureScale("_MainTex",property==null?Vector2.one:alphaSource.GetTextureScale(property));
                    material.SetTextureOffset("_MainTex",property==null?Vector2.zero:alphaSource.GetTextureOffset(property));
                }
                Bake();
            }
            private void Bake()
            {
                if(mesh==null){mesh=fine.Snapshot();host.GetComponent<MeshFilter>().sharedMesh=mesh;}
                else{var skinned=renderer as SkinnedMeshRenderer;if(skinned!=null)skinned.BakeMesh(mesh,true);}
                mesh.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;mesh.subMeshCount=1;mesh.triangles=indices;
                if(colors==null||colors.Length!=mesh.vertexCount){colors=new Color[mesh.vertexCount];for(int i=0;i<colors.Length;i++)colors[i]=Color.white;}
                mesh.colors=colors;nextBake=Time.realtimeSinceStartup+.12f;
            }
            internal void Tick(bool active,bool frozen)
            {
                host.SetActive(active&&Active);if(!active||!Active)return;
                if(alphaSource!=null&&alphaProperty!=null&&material.HasProperty("_MainTex"))
                {var texture=alphaSource.GetTexture(alphaProperty);if(texture!=boundAlpha){boundAlpha=texture;material.SetTexture("_MainTex",texture);}}
                if(!frozen&&Time.realtimeSinceStartup>=nextBake)Bake();
                host.transform.position=renderer.transform.position;host.transform.rotation=renderer.transform.rotation;host.transform.localScale=renderer.transform.lossyScale;
                material.SetColor("_Color",new Color(.05f,1,1,.2f+.65f*(.5f+.5f*Mathf.Sin(Time.realtimeSinceStartup*9))));
            }
            public void Dispose(){UnityEngine.Object.Destroy(host);UnityEngine.Object.Destroy(material);if(mesh!=null)UnityEngine.Object.Destroy(mesh);}
        }
    }
}
