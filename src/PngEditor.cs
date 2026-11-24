using System;
using System.IO;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed partial class PartVisibility
    {
        private Part pngPart;
        private UvIslandView pngView;
        private int pngIsland=-1;
        private bool pngOriginalMode,pngAtlas,pngPanning,pngInside;
        private float pngZoom=1;
        private Vector2 pngPan;
        private Texture2D pngChecker;
        internal bool PngEditorOpen{get{return pngView!=null;}}
        private void ConnectPngEditor(Part part)
        {
            var view=part.UvView;view.OpenEditor=island=>
            {
                CancelPaint();pngOriginalMode=SelectionMode;
                if(!SelectionMode)SetSelectionMode(true);
                pngPart=part;pngView=view;pngIsland=island;pngZoom=1;pngPan=Vector2.zero;pngAtlas=false;pngPanning=false;pngInside=false;
            };
        }
        private void ClosePngEditor()
        {
            CancelPaint();bool restore=pngOriginalMode;pngView=null;pngPart=null;pngPanning=false;
            if(!restore&&SelectionMode)SetSelectionMode(false);
        }
        internal void DrawPngEditor(GUIStyle text,GUIStyle heading)
        {
            if(pngView==null)return;
            var window=new Rect(12,12,Mathf.Max(300,Screen.width-24),Mathf.Max(300,Screen.height-24));
            GUI.Box(window,"");GUILayout.BeginArea(new Rect(window.x+12,window.y+10,window.width-24,65));
            GUILayout.Label("PNG 크게 편집 · "+Path.GetFileName(pngView.Binding.File),heading);GUILayout.BeginHorizontal();
            bool close=GUILayout.Button("닫기 (Esc)");if(GUILayout.Button("화면 맞춤")){pngZoom=1;pngPan=Vector2.zero;}
            if(GUILayout.Button(pngAtlas?"UV 덮개 끄기":"UV 덮개 켜기"))pngAtlas=!pngAtlas;GUILayout.EndHorizontal();GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(window.x+12,window.y+82,232,window.height-94));
            GUILayout.Label("왼쪽 드래그: 부분 지우기\n휠: 확대·축소\n휠 클릭 드래그: 이미지 이동\n붓질마다 시작한 UV 섬으로 제한",text);
            DrawPaintControls(text);
            if(GUILayout.Button("되돌리기 (Ctrl+Z)"))Undo();if(GUILayout.Button("다시 실행 (Ctrl+Y)"))Redo();
            GUILayout.Label(pngIsland>=0?"현재 UV 섬 U"+(pngIsland+1).ToString("D3"):"UV 영역 위에서 칠하세요.",text);
            if(!string.IsNullOrEmpty(error))GUILayout.Label(error,text);GUILayout.EndArea();
            var canvas=new Rect(window.x+256,window.y+82,Mathf.Max(16,window.width-268),Mathf.Max(16,window.height-94));
            GUILayout.BeginArea(canvas);GUI.Box(new Rect(0,0,canvas.width,canvas.height),"");
            float fit=Mathf.Min(canvas.width/pngView.Width,canvas.height/pngView.Height),scale=fit*pngZoom;
            var center=new Vector2(canvas.width*.5f,canvas.height*.5f);var size=new Vector2(pngView.Width*scale,pngView.Height*scale);
            var image=new Rect(center.x+pngPan.x-size.x*.5f,center.y+pngPan.y-size.y*.5f,size.x,size.y);
            if(pngChecker==null)
            {
                pngChecker=new Texture2D(16,16,TextureFormat.RGBA32,false,true);pngChecker.filterMode=FilterMode.Point;var pixels=new Color32[256];
                for(int y=0;y<16;y++)for(int x=0;x<16;x++){byte gray=(byte)((x+y)%2==0?55:90);pixels[y*16+x]=new Color32(gray,gray,gray,255);}pngChecker.SetPixels32(pixels);pngChecker.Apply(false,false);
            }
            GUI.DrawTexture(image,pngChecker);GUI.DrawTexture(image,brush!=null&&brush.Png?brush.Preview:pngView.ImageTexture);
            if(pngAtlas&&brush==null)GUI.DrawTexture(image,pngView.AtlasTexture);
            var ev=Event.current;var point=ev.mousePosition;bool over=new Rect(0,0,canvas.width,canvas.height).Contains(point)&&image.Contains(point);
            if(ev.type==EventType.ScrollWheel&&over&&brush==null)
            {
                var normalized=new Vector2((point.x-image.x)/image.width,(point.y-image.y)/image.height);pngZoom=Mathf.Clamp(pngZoom*Mathf.Exp(-ev.delta.y*.1f),.5f,12);
                var next=new Vector2(pngView.Width*fit*pngZoom,pngView.Height*fit*pngZoom);pngPan=point-center-new Vector2((normalized.x-.5f)*next.x,(normalized.y-.5f)*next.y);ev.Use();
            }
            if(ev.type==EventType.MouseDown&&ev.button==2&&over){pngPanning=true;ev.Use();}
            if(ev.type==EventType.MouseDrag&&ev.button==2&&pngPanning){pngPan+=ev.delta;ev.Use();}
            if(ev.type==EventType.MouseUp&&ev.button==2)pngPanning=false;
            if(over&&brush==null)
            {
                var uv=new Vector2((point.x-image.x)/image.width,1-(point.y-image.y)/image.height);int hit=pngView.PickIsland(uv,pngIsland,false);if(hit>=0)pngIsland=hit;
            }
            try
            {
                if(ev.type==EventType.MouseDown&&ev.button==0&&over)
                {
                    var uv=new Vector2((point.x-image.x)/image.width,1-(point.y-image.y)/image.height);int island=pngView.PickIsland(uv,pngIsland,true);
                    if(island>=0)
                    {
                        pngIsland=island;var first=new PaintHit{Row=new PieceRef{Part=pngPart,Piece=pngView.Piece(island)},Dx=new Vector2(1/image.width,0),Dy=new Vector2(0,1/image.height)};
                        BeginPaint(first,point,pngView.Binding);brush.Png=true;brush.Pending.Clear();brush.Density=1/scale;pngInside=true;PaintPngStamp(point,image,scale);RefreshPngPreview();
                    }
                    ev.Use();
                }
                if(brush!=null&&brush.Png&&ev.type==EventType.MouseDrag&&ev.button==0)
                {
                    if(over)
                    {
                        var delta=point-brush.LastPoint;int steps=pngInside?Mathf.Max(1,Mathf.CeilToInt(delta.magnitude/Mathf.Max(2,brushRadius*.35f))):1;Vector2 from=brush.LastPoint;
                        if(pngInside&&delta.magnitude>=2)LockTearAxis(new Vector2(delta.x,-delta.y));
                        for(int i=1;i<=steps;i++)PaintPngStamp(pngInside?from+delta*((float)i/steps):point,image,scale);
                        brush.LastPoint=point;pngInside=true;if(Time.realtimeSinceStartup>=brush.NextPreview)RefreshPngPreview();
                    }
                    else pngInside=false;ev.Use();
                }
                if(brush!=null&&brush.Png&&ev.type==EventType.MouseUp&&ev.button==0){FinishPaint();pngInside=false;ev.Use();}
            }
            catch(Exception e){CancelPaint();error="PNG 붓질 취소: "+e.Message;Debug.LogWarning("[Viewer] PNG editor: "+e);}
            if(over)
            {
                var old=GUI.color;GUI.color=brush==null?new Color(1,.8f,.2f):new Color(1,.35f,.15f);
                for(int i=0;i<64;i++){float angle=i*6.2831853f/64;GUI.DrawTexture(new Rect(point.x+Mathf.Cos(angle)*brushRadius-1,point.y+Mathf.Sin(angle)*brushRadius-1,2,2),Texture2D.whiteTexture);}GUI.color=old;
            }
            GUILayout.EndArea();if(close)ClosePngEditor();
        }
        private void PaintPngStamp(Vector2 point,Rect image,float scale)
        {
            var state=brush;float cx=(point.x-image.x)/scale,cy=state.Height-(point.y-image.y)/scale;
            float reach=(brushRadius+brushRoughness*2)/scale;
            int x0=Mathf.Max(0,Mathf.FloorToInt(cx-reach)),x1=Mathf.Min(state.Width-1,Mathf.CeilToInt(cx+reach)),y0=Mathf.Max(0,Mathf.FloorToInt(cy-reach)),y1=Mathf.Min(state.Height-1,Mathf.CeilToInt(cy+reach));
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
            {
                int index=y*state.Width+x;if(!CanPaintPixel(index))continue;float dx=(x+.5f-cx)*scale,dy=(y+.5f-cy)*scale;
                float margin=brushRadius-Mathf.Sqrt(dx*dx+dy*dy);if(margin<-(brushRoughness*2))continue;RememberPixel(index,margin);
            }
        }
        private void RefreshPngPreview()
        {
            brush.Preview.SetPixels32(brush.PreviewPixels);brush.Preview.Apply(false,false);if(PaintPreview!=null)PaintPreview(brush.Binding.File,brush.Preview);brush.NextPreview=Time.realtimeSinceStartup+.08f;
        }
        private void DisposePngEditor(){pngView=null;pngPart=null;if(pngChecker!=null)UnityEngine.Object.Destroy(pngChecker);pngChecker=null;}
    }
}
