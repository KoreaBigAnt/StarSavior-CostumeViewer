using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed class UvIslandView:IDisposable
    {
        internal readonly TextureBinding Binding;
        internal Action<int> OpenEditor;
        internal Texture2D ImageTexture{get{return image;}}
        internal Texture2D AtlasTexture{get{return atlas;}}
        internal MeshPiece Piece(int island){return mesh.Slots[slot].FirstOrDefault(p=>p.IslandId==island);}
        internal int PickIsland(Vector2 point,int preferred,bool precise)
        {
            if(!precise){int x=Mathf.Clamp((int)(point.x*previewWidth),0,previewWidth-1),y=Mathf.Clamp((int)(point.y*previewHeight),0,previewHeight-1);return hit[y*previewWidth+x];}
            var wanted=Piece(preferred);if(wanted!=null&&UvRaster.Contains(new UvRasterLayer(mesh,wanted.Indices,Binding),point))return wanted.IslandId;
            foreach(var piece in mesh.Slots[slot])if(UvRaster.Contains(new UvRasterLayer(mesh,piece.Indices,Binding),point))return piece.IslandId;
            return -1;
        }
        internal int Width{get{return image.width;}}
        internal int Height{get{return image.height;}}
        internal UvRasterLayer IslandLayer(int island){var piece=mesh.Slots[slot].FirstOrDefault(p=>p.IslandId==island);if(piece==null)throw new InvalidOperationException("UV 섬을 찾을 수 없습니다.");return new UvRasterLayer(mesh,piece.Indices,Binding);}
        internal void ReloadImage(){if(!ImageConversion.LoadImage(image,File.ReadAllBytes(Binding.File),false))throw new IOException("PNG를 읽을 수 없습니다.");}
        private readonly FineMesh mesh;
        private readonly int slot;
        private Texture2D image,atlas,highlight;
        private int[] hit;
        private int previewWidth,previewHeight,lastHighlight=-2;
        internal UvIslandView(TextureBinding binding,FineMesh mesh,int slot)
        {
            Binding=binding;this.mesh=mesh;this.slot=slot;
            image=new Texture2D(2,2,TextureFormat.RGBA32,false,true);
            try
            {
                if(!ImageConversion.LoadImage(image,File.ReadAllBytes(binding.File),false))throw new IOException("PNG를 읽을 수 없습니다.");
                previewWidth=Mathf.Max(1,Mathf.RoundToInt(256f*image.width/Mathf.Max(image.width,image.height)));
                previewHeight=Mathf.Max(1,Mathf.RoundToInt(256f*image.height/Mathf.Max(image.width,image.height)));
                atlas=new Texture2D(previewWidth,previewHeight,TextureFormat.RGBA32,false,true);highlight=new Texture2D(previewWidth,previewHeight,TextureFormat.RGBA32,false,true);
                hit=Enumerable.Repeat(-1,previewWidth*previewHeight).ToArray();var colors=new Color32[hit.Length];
                foreach(var piece in mesh.Slots[slot])
                {
                    var color=new Color32((byte)(55+(piece.IslandId*97)%180),(byte)(55+(piece.IslandId*53)%180),(byte)(55+(piece.IslandId*131)%180),105);
                    UvRaster.Paint(new UvRasterLayer(mesh,piece.Indices,binding),previewWidth,previewHeight,p=>{if(hit[p]<0){hit[p]=piece.IslandId;colors[p]=color;}});
                }
                atlas.SetPixels32(colors);atlas.Apply(false,false);
            }
            catch{Dispose();throw;}
        }
        private void Highlight(int island)
        {
            if(island==lastHighlight)return;lastHighlight=island;var colors=new Color32[hit.Length];
            var piece=mesh.Slots[slot].FirstOrDefault(p=>p.IslandId==island);
            if(piece!=null)UvRaster.Paint(new UvRasterLayer(mesh,piece.Indices,Binding),previewWidth,previewHeight,p=>colors[p]=new Color32(0,255,255,230));
            highlight.SetPixels32(colors);highlight.Apply(false,false);
        }
        internal int Draw(GUIStyle text,int selected)
        {
            GUILayout.Label(Path.GetFileName(Binding.File)+"\n"+Binding.Property+" · "+mesh.Slots[slot].Count+"개 UV 섬",text);
            GUILayout.Label("Ctrl+클릭: 큰 PNG 편집창\n모델 Ctrl+드래그: 부분 지우기\n겹친 UV 섬은 목록에서 각각 선택하세요.",text);
            var outer=GUILayoutUtility.GetRect(220,220,GUI.skin.box,new GUILayoutOption[]{GUILayout.Height(220),GUILayout.ExpandWidth(true)});
            float factor=Mathf.Min(outer.width/image.width,outer.height/image.height);var rect=new Rect(outer.x+(outer.width-image.width*factor)/2,outer.y+(outer.height-image.height*factor)/2,image.width*factor,image.height*factor);
            GUI.DrawTexture(rect,image);GUI.DrawTexture(rect,atlas);int hovered=-1;
            if(rect.Contains(Event.current.mousePosition))
            {
                int x=Mathf.Clamp((int)((Event.current.mousePosition.x-rect.x)/rect.width*previewWidth),0,previewWidth-1);
                int y=Mathf.Clamp((int)((1-(Event.current.mousePosition.y-rect.y)/rect.height)*previewHeight),0,previewHeight-1);hovered=hit[y*previewWidth+x];
                if(GUI.enabled&&Event.current.type==EventType.MouseDown&&Event.current.button==0&&(Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl)))
                {if(OpenEditor!=null)OpenEditor(hovered>=0?hovered:selected);Event.current.Use();}
            }
            Highlight(hovered>=0?hovered:selected);
            var old=GUI.color;GUI.color=new Color(1,1,1,.45f+.4f*Mathf.Sin(Time.realtimeSinceStartup*9));GUI.DrawTexture(rect,highlight);GUI.color=old;
            return hovered;
        }
        internal int ExportTransparent(string destination,bool hideAll,IEnumerable<UvRasterLayer> protection,out int sharedPixels,Action<Color32[],int,int> pad)
        {
            var copy=new Texture2D(2,2,TextureFormat.RGBA32,false,true);
            try
            {
                if(!ImageConversion.LoadImage(copy,File.ReadAllBytes(Binding.File),false))throw new IOException("PNG를 읽을 수 없습니다.");
                int w=copy.width,h=copy.height;if((long)w*h>33554432)throw new InvalidOperationException("3,355만 픽셀 이하 PNG를 지원합니다.");
                var coverage=new byte[w*h];int[] hidden=hideAll?mesh.SourceIndices[slot]:mesh.Slots[slot].Where(p=>!p.Visible).SelectMany(p=>p.Indices).ToArray();
                UvRaster.Paint(new UvRasterLayer(mesh,hidden,Binding),w,h,p=>coverage[p]|=1);
                foreach(var layer in protection)UvRaster.Paint(layer,w,h,p=>coverage[p]|=2);
                var pixels=copy.GetPixels32();int changed=0;sharedPixels=0;
                for(int p=0;p<pixels.Length;p++)
                {
                    if(coverage[p]==3){sharedPixels++;continue;}
                    if(coverage[p]==1){if(pixels[p].a!=0)changed++;pixels[p].a=0;}
                }
                if(pad!=null)pad(pixels,w,h);
                copy.SetPixels32(pixels);copy.Apply(false,false);var png=AssetExporter.EncodePng(copy);
                using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(png,0,png.Length);
                return changed;
            }
            finally{UnityEngine.Object.Destroy(copy);}
        }
        public void Dispose(){if(image!=null)UnityEngine.Object.Destroy(image);if(atlas!=null)UnityEngine.Object.Destroy(atlas);if(highlight!=null)UnityEngine.Object.Destroy(highlight);}
    }
}
