using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed partial class PartVisibility
    {
        internal Func<string,bool,bool> TextureEdited;
        internal Action<string,Texture2D> PaintPreview;
        internal Action<bool> OutlineModeChanged;
        private bool forceOutline;
        private readonly Dictionary<string,byte[]> paintTextures=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,byte[]> paintOriginals=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> restoredAlphaFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private BrushStroke brush;
        private int brushRadius=20,brushRoughness=8,brushToothSpacing=8;
        private int cutEdgeStrength=60;
        private bool lastHoverSingle;
        private sealed class PaintHit
        {
            internal PieceRef Row;
            internal Vector2 UV,Dx,Dy,A,B,C;
        }
        private sealed class BrushStroke:IDisposable
        {
            internal PieceRef Target;
            internal TextureBinding Binding;
            internal byte[] Before,Coverage;
            internal Texture2D Image,Preview;
            internal Color32[] Original,PreviewPixels;
            internal int Width,Height,PWidth,PHeight,Seed;
            internal Vector2 LastPoint,TearAxis,ScreenDx,ScreenDy;
            internal bool AxisLocked;
            internal float Density=1,NextPreview;
            internal bool Supported;
            internal bool Png;
            internal bool ColorTexture;
            internal bool LinkedUv;
            internal readonly Dictionary<int,float> Margins=new Dictionary<int,float>();
            internal readonly Queue<Vector2> Pending=new Queue<Vector2>();
            public void Dispose(){if(Image!=null)UnityEngine.Object.Destroy(Image);if(Preview!=null)UnityEngine.Object.Destroy(Preview);}
        }
        private PaintHit PaintRay(Camera camera,Vector2 screen)
        {
            PaintHit best=null;float distance=10000;
            foreach(var proxy in picking){PaintHit hit;float d;if(proxy.Sample(camera,screen,out hit,out d)&&d<distance){distance=d;best=hit;}}
            return best;
        }
        private bool UpdatePaint(Camera camera)
        {
            bool control=Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl);
            var screen=new Vector2(Input.mousePosition.x,Input.mousePosition.y);bool over=screen.x>280&&screen.x<Viewer.ViewportRight&&screen.y>=0&&screen.y<=Screen.height;
            if(brush==null)
            {
                if(!control||!over||!Input.GetMouseButtonDown(0))return false;
                pointerDown=false;boxDragging=false;pressedPiece=null;var target=PaintRay(camera,screen);if(target==null)return true;
                try{BeginPaint(target,screen);}
                catch(Exception e){CancelPaint();error="붓질 준비 실패: "+e.Message;Debug.LogWarning("[Viewer] Paint: "+e);}
                return true;
            }
            try
            {
                if(Input.GetMouseButton(1)||Input.GetMouseButton(2)){CancelPaint();error="카메라 조작으로 진행 중인 붓질을 취소했습니다.";return true;}
                if(over&&Input.GetMouseButton(0))
                {
                    Vector2 delta=screen-brush.LastPoint;float distance=delta.magnitude;
                    if(distance>=2){LockTearAxis(brush.ScreenDx*delta.x+brush.ScreenDy*delta.y);int steps=Mathf.Max(1,Mathf.CeilToInt(distance/Mathf.Max(2,brushRadius*.4f)));for(int i=1;i<=steps;i++)brush.Pending.Enqueue(brush.LastPoint+delta*((float)i/steps));brush.LastPoint=screen;}
                }
                bool finish=Input.GetMouseButtonUp(0)||!Input.GetMouseButton(0)||!control;
                int budget=finish?2147483647:Mathf.Max(1,3-brushRoughness/10);while(budget-->0&&brush.Pending.Count>0)PaintStamp(camera,brush.Pending.Dequeue());
                if(finish)FinishPaint();
                else if(Time.realtimeSinceStartup>=brush.NextPreview)
                {brush.Preview.SetPixels32(brush.PreviewPixels);brush.Preview.Apply(false,false);if(PaintPreview!=null)PaintPreview(brush.Binding.File,brush.Preview);brush.NextPreview=Time.realtimeSinceStartup+.08f;}
            }
            catch(Exception e){CancelPaint();error="붓질 취소: "+e.Message;Debug.LogWarning("[Viewer] Paint: "+e);}
            return true;
        }
        private void BeginPaint(PaintHit first,Vector2 screen,TextureBinding preferred=null)
        {
            var binding=preferred??Textures(first.Row.Part).FirstOrDefault(b=>new[]{"_BaseMap","_MainTex","_BaseColorMap","_BaseColorTexture","_AlbedoMap","_DiffuseMap"}.Contains(b.Property));
            if(binding==null)throw new InvalidOperationException("이 부위의 색상 PNG를 찾지 못했습니다.");
            string file=Path.GetFullPath(binding.File);
            if(!file.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("작업 폴더의 PNG만 칠할 수 있습니다.");
            var state=new BrushStroke{Target=first.Row,Binding=binding,Before=File.ReadAllBytes(file),Seed=Environment.TickCount&65535,LastPoint=screen,Image=new Texture2D(2,2,TextureFormat.RGBA32,false,true),LinkedUv=editSharedUv};
            state.ColorTexture=new[]{"_BaseMap","_MainTex","_BaseColorMap","_BaseColorTexture","_AlbedoMap","_DiffuseMap"}.Contains(binding.Property);
            float angle=(RandomValue(0,0,state.Seed)+1)*3.14159265f;state.TearAxis=new Vector2(Mathf.Cos(angle),Mathf.Sin(angle));
            brush=state;if(!ImageConversion.LoadImage(state.Image,state.Before,false))throw new IOException("PNG를 읽을 수 없습니다.");
            state.Width=state.Image.width;state.Height=state.Image.height;if((long)state.Width*state.Height>33554432)throw new InvalidDataException("PNG가 너무 큽니다.");
            state.Original=state.Image.GetPixels32();state.Coverage=new byte[state.Width*state.Height];
            UvRaster.Paint(new UvRasterLayer(first.Row.Part.Detail,first.Row.Piece.Indices,binding),state.Width,state.Height,p=>state.Coverage[p]|=1);
            foreach(var part in Parts.Where(p=>p.Visible&&p.Renderer.enabled&&p.Renderer.gameObject.activeInHierarchy&&HasGeometry(p)))
            foreach(var other in Textures(part).Where(b=>string.Equals(b.File,file,StringComparison.OrdinalIgnoreCase)))
            {
                var mesh=EnsureMesh(part);var indices=mesh.Slots[part.GeometrySlot].Where(p=>p.Visible&&(part.Renderer!=first.Row.Part.Renderer||part.GeometrySlot!=first.Row.Part.GeometrySlot||p!=first.Row.Piece)).SelectMany(p=>p.Indices).ToArray();
                UvRaster.Paint(new UvRasterLayer(mesh,indices,other),state.Width,state.Height,p=>state.Coverage[p]|=2);
            }
            state.PWidth=Mathf.Max(1,Mathf.RoundToInt(1024f*state.Width/Mathf.Max(state.Width,state.Height)));state.PHeight=Mathf.Max(1,Mathf.RoundToInt(1024f*state.Height/Mathf.Max(state.Width,state.Height)));
            state.PreviewPixels=new Color32[state.PWidth*state.PHeight];for(int y=0;y<state.PHeight;y++)for(int x=0;x<state.PWidth;x++)state.PreviewPixels[y*state.PWidth+x]=state.Original[(int)((y+.5f)*state.Height/state.PHeight)*state.Width+(int)((x+.5f)*state.Width/state.PWidth)];
            state.Preview=new Texture2D(state.PWidth,state.PHeight,TextureFormat.RGBA32,false,binding.Linear);state.Preview.wrapMode=binding.Original.wrapMode;
            var scale=binding.Material.GetTextureScale(binding.Property);state.Density=Mathf.Max(1,(new Vector2(first.Dx.x*scale.x*state.Width,first.Dx.y*scale.y*state.Height).magnitude+new Vector2(first.Dy.x*scale.x*state.Width,first.Dy.y*scale.y*state.Height).magnitude)*.5f);
            state.ScreenDx=new Vector2(first.Dx.x*scale.x*state.Width,first.Dx.y*scale.y*state.Height);state.ScreenDy=new Vector2(first.Dy.x*scale.x*state.Width,first.Dy.y*scale.y*state.Height);
            state.Supported=TextureEdited!=null&&TextureEdited(file,true);state.Pending.Enqueue(screen);hoverPart=null;
            error="Ctrl+드래그: 이 UV 섬 안에서만 지웁니다.\n마우스를 놓으면 저장 / Esc: 현재 붓질 취소";
            if(state.Coverage.Any(mask=>mask==3))error+=state.LinkedUv?"\n공유 UV: 같은 PNG 좌표를 쓰는 부위도 함께 변경됩니다.":"\n공유 UV 보호 중: 겹친 곳을 지우려면 공유 UV 연동 편집을 켜세요.";
        }
        private void PaintStamp(Camera camera,Vector2 center)
        {
            float reach=brushRadius+brushRoughness*2;int step=Mathf.Max(4,Mathf.CeilToInt(reach/8f));
            for(int y=-(int)reach;y<=reach;y+=step)for(int x=-(int)reach;x<=reach;x+=step)
            {
                if(x*x+y*y>(reach+step)*(reach+step))continue;var hit=PaintRay(camera,center+new Vector2(x,y));
                if(hit==null||hit.Row.Part!=brush.Target.Part||hit.Row.Piece!=brush.Target.Piece)continue;
                PaintCell(hit,new Vector2(x,y),step*.65f);
            }
        }
        private static float Cross2(Vector2 a,Vector2 b){return a.x*b.y-a.y*b.x;}
        private void PaintCell(PaintHit hit,Vector2 screenOffset,float halfCell)
        {
            var state=brush;Vector2 scale=state.Binding.Material.GetTextureScale(state.Binding.Property),offset=state.Binding.Material.GetTextureOffset(state.Binding.Property);
            Vector2 size=new Vector2(state.Width,state.Height),uv=Vector2.Scale(Vector2.Scale(hit.UV,scale)+offset,size),dx=Vector2.Scale(Vector2.Scale(hit.Dx,scale),size),dy=Vector2.Scale(Vector2.Scale(hit.Dy,scale),size);
            Vector2 a=Vector2.Scale(Vector2.Scale(hit.A,scale)+offset,size),b=Vector2.Scale(Vector2.Scale(hit.B,scale)+offset,size),c=Vector2.Scale(Vector2.Scale(hit.C,scale)+offset,size);
            float determinant=Cross2(dx,dy),area=Cross2(b-a,c-a);if(Mathf.Abs(determinant)<1e-8f||Mathf.Abs(area)<1e-8f)return;
            float rx=(Mathf.Abs(dx.x)+Mathf.Abs(dy.x))*halfCell,ry=(Mathf.Abs(dx.y)+Mathf.Abs(dy.y))*halfCell;
            int x0=Mathf.FloorToInt(Mathf.Max(uv.x-rx,Mathf.Min(a.x,Mathf.Min(b.x,c.x)))),x1=Mathf.CeilToInt(Mathf.Min(uv.x+rx,Mathf.Max(a.x,Mathf.Max(b.x,c.x))));
            int y0=Mathf.FloorToInt(Mathf.Max(uv.y-ry,Mathf.Min(a.y,Mathf.Min(b.y,c.y)))),y1=Mathf.CeilToInt(Mathf.Min(uv.y+ry,Mathf.Max(a.y,Mathf.Max(b.y,c.y))));
            if((long)(x1-x0+1)*(y1-y0+1)>524288)return;
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
            {
                var point=new Vector2(x+.5f,y+.5f);float u=Cross2(b-point,c-point)/area,v=Cross2(c-point,a-point)/area;if(u<0||v<0||u+v>1.000001f)continue;
                var delta=point-uv;var screenDelta=new Vector2(Cross2(delta,dy)/determinant,Cross2(dx,delta)/determinant);
                if(Mathf.Abs(screenDelta.x)>halfCell||Mathf.Abs(screenDelta.y)>halfCell)continue;
                int px=WrapPixel(x,state.Width,state.Binding.Original.wrapMode),py=WrapPixel(y,state.Height,state.Binding.Original.wrapMode),index=py*state.Width+px;if(!CanPaintPixel(index))continue;
                float margin=brushRadius-(screenOffset+screenDelta).magnitude,previous;if(state.Margins.TryGetValue(index,out previous)&&previous>=margin)continue;
                RememberPixel(index,margin);
            }
        }
        private void RememberPixel(int index,float margin)
        {
            var state=brush;float previous;if(state.Margins.TryGetValue(index,out previous)&&previous>=margin)return;state.Margins[index]=margin;
            UpdatePaintPreviewPixel(index);
        }
        private void UpdatePaintPreviewPixel(int index)
        {
            var state=brush;
            int px=index%state.Width,py=index/state.Width,previewX=px*state.PWidth/state.Width,previewY=py*state.PHeight/state.Height;
            int sample=(int)((previewY+.5f)*state.Height/state.PHeight)*state.Width+(int)((previewX+.5f)*state.Width/state.PWidth);
            var color=state.Original[sample];float sampleMargin;if(state.Margins.TryGetValue(sample,out sampleMargin))color=PaintColor(sample,sampleMargin);state.PreviewPixels[previewY*state.PWidth+previewX]=color;
        }
        private static int WrapPixel(int value,int size,TextureWrapMode mode)
        {
            if(mode==TextureWrapMode.Clamp)return Mathf.Clamp(value,0,size-1);int tile=Mathf.FloorToInt((float)value/size),pixel=value-tile*size;
            return mode==TextureWrapMode.Mirror&&(tile&1)!=0?size-1-pixel:pixel;
        }
        private static float RandomValue(int x,int y,int seed)
        {unchecked{uint value=(uint)(x*374761393+y*668265263+seed*1274126177);value=(value^(value>>13))*1274126177u;return ((value^(value>>16))&65535)/32767.5f-1;}}
        private void FinishPaint()
        {
            var state=brush;var pixels=(Color32[])state.Original.Clone();int changed=0,shared=0;
            foreach(var pair in state.Margins){var color=PaintColor(pair.Key,pair.Value);var before=pixels[pair.Key];if(color.a!=before.a||color.r!=before.r||color.g!=before.g||color.b!=before.b){pixels[pair.Key]=color;changed++;if(state.Coverage[pair.Key]==3)shared++;}}
            if(changed==0){CancelPaint();error="지워진 픽셀이 없습니다. 겹친 UV 또는 표면 각도를 확인하세요.";return;}
            string file=state.Binding.File;
            PadAlphaEdges(file,pixels,state.Width,state.Height);
            state.Image.SetPixels32(pixels);state.Image.Apply(false,false);
            byte[] bytes=AssetExporter.EncodePng(state.Image);
            var linked = LinkedPaintFiles(state, pixels, bytes);
            bool supported = SaveLinkedPaint(linked);
            brush=null;state.Dispose();hoverPart=null;
            error="부분 지우기 완료: "+changed+"픽셀 · Ctrl+Z로 붓질 전체 복원"+(supported?"":"\n이 재질은 알파클립을 지원하지 않아 PNG에서 결과를 확인하세요.");
            if(shared>0)error+="\n공유 UV "+shared+"픽셀: 같은 좌표를 사용하는 부위에도 반영됐습니다.";
        }
        private void CancelPaint()
        {
            if(brush==null)return;var old=brush;brush=null;
            if(TextureEdited!=null)TextureEdited(old.Binding.File,RequiresAlpha(old.Binding.File));old.Dispose();hoverPart=null;
        }
        private void DrawPaintControls(GUIStyle text)
        {
            int oldRadius=brushRadius,oldDepth=brushRoughness,oldSpacing=brushToothSpacing,oldStrength=cutEdgeStrength;bool oldShared=editSharedUv;
            GUILayout.Label("Ctrl+왼쪽 드래그: 부분 지우기\n처음 칠한 UV 섬에만 적용됩니다.",text);
            GUILayout.Label("붓 반경 "+brushRadius+" px · 찢김 깊이 "+brushRoughness+" / 40",text);GUILayout.BeginHorizontal();
            if(GUILayout.Button("붓 -"))brushRadius=Mathf.Max(4,brushRadius-4);if(GUILayout.Button("붓 +"))brushRadius=Mathf.Min(80,brushRadius+4);
            GUILayout.EndHorizontal();GUILayout.BeginHorizontal();
            if(GUILayout.Button("깊이 -5"))brushRoughness=Mathf.Max(0,brushRoughness-5);if(GUILayout.Button("-1"))brushRoughness=Mathf.Max(0,brushRoughness-1);
            if(GUILayout.Button("+1"))brushRoughness=Mathf.Min(40,brushRoughness+1);if(GUILayout.Button("+5"))brushRoughness=Mathf.Min(40,brushRoughness+5);GUILayout.EndHorizontal();
            GUILayout.Label("가시 간격: "+brushToothSpacing+" px · 깊이가 클수록 길고 날카롭게 찢깁니다.",text);
            GUILayout.BeginHorizontal();if(GUILayout.Button("간격 -"))brushToothSpacing=Mathf.Max(2,brushToothSpacing-2);if(GUILayout.Button("간격 +"))brushToothSpacing=Mathf.Min(40,brushToothSpacing+2);GUILayout.EndHorizontal();
            if(GUILayout.Button("절단면 질감: "+cutEdgeStrength+"%"))cutEdgeStrength=(cutEdgeStrength+20)%120;
            if(GUILayout.Button(editSharedUv?"공유 UV: 연동 편집 켜짐":"공유 UV: 다른 부위 보호"))editSharedUv=!editSharedUv;
            GUILayout.Label(editSharedUv?"같은 PNG 좌표를 쓰는 부위도 함께 지워집니다.":"다른 표시 부위와 겹친 픽셀은 지우지 않습니다.",text);
            if(oldRadius!=brushRadius||oldDepth!=brushRoughness||oldSpacing!=brushToothSpacing||oldStrength!=cutEdgeStrength||oldShared!=editSharedUv)SaveBrushPreferences();
            if(GUILayout.Button(forceOutline?"테두리 보정: 강제 제거":"테두리 보정: 자동")){forceOutline=!forceOutline;if(OutlineModeChanged!=null)OutlineModeChanged(forceOutline);}
        }
        internal void DrawPaintCursor()
        {
            if(PngEditorOpen)return;
            if(!SelectionMode||!(Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl)))return;
            float x=Input.mousePosition.x,y=Screen.height-Input.mousePosition.y;if(x<=280||x>=Viewer.ViewportRight)return;
            var old=GUI.color;GUI.color=brush==null?new Color(1,.8f,.2f):new Color(1,.35f,.15f);
            for(int i=0;i<64;i++){float angle=i*6.2831853f/64;GUI.DrawTexture(new Rect(x+Mathf.Cos(angle)*brushRadius-1,y+Mathf.Sin(angle)*brushRadius-1,2,2),Texture2D.whiteTexture);}GUI.color=old;
        }
        private bool NotifyTexture(string file,bool enabled)
        {
            foreach(var part in Parts)if(part.UvView!=null&&string.Equals(part.UvView.Binding.File,file,StringComparison.OrdinalIgnoreCase))part.UvView.ReloadImage();
            return TextureEdited!=null&&TextureEdited(file,enabled||restoredAlphaFiles.Contains(file));
        }
        internal bool RequiresAlpha(string file){return paintTextures.ContainsKey(file)||restoredAlphaFiles.Contains(file);}
        private void RestorePaintTextures(Dictionary<string,byte[]> state)
        {
            foreach(var original in paintOriginals)
            {
                byte[] desired,current;bool active=state.TryGetValue(original.Key,out desired),had=paintTextures.TryGetValue(original.Key,out current);
                if(active==had&&(!active||ReferenceEquals(desired,current)))continue;ProfileFiles.WriteAtomic(original.Key,active?desired:original.Value);NotifyTexture(original.Key,active);
            }
            paintTextures.Clear();foreach(var pair in state)paintTextures[pair.Key]=pair.Value;
        }
    }
}
