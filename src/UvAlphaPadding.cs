using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed partial class PartVisibility
    {
        private readonly Dictionary<string,byte[]> alphaUvCoverage=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        // Bilinear samples straddle UV borders. Painting pixel centers alone leaves
        // the opaque atlas gutter visible as a dotted silhouette. Extend zero alpha
        // into that gutter only; never erode a texel belonging to another UV surface.
        internal int PadAlphaEdges(string file,Color32[] pixels,int width,int height)
        {
            string key=file+"|"+width+"x"+height;byte[] coverage;
            if(!alphaUvCoverage.TryGetValue(key,out coverage))
            {
                coverage=new byte[pixels.Length];
                foreach(var part in Parts.Where(p=>HasGeometry(p)))
                {
                    var seen=new HashSet<string>();
                    foreach(var binding in Textures(part).Where(b=>string.Equals(b.File,file,StringComparison.OrdinalIgnoreCase)))
                    {
                        string transform=binding.Material.GetTextureScale(binding.Property).ToString("R",System.Globalization.CultureInfo.InvariantCulture)+binding.Material.GetTextureOffset(binding.Property).ToString("R",System.Globalization.CultureInfo.InvariantCulture)+binding.Original.wrapMode;
                        if(!seen.Add(transform))continue;
                        var mesh=EnsureMesh(part);UvRaster.Paint(new UvRasterLayer(mesh,mesh.SourceIndices[part.GeometrySlot],binding),width,height,p=>coverage[p]=1);
                    }
                }
                alphaUvCoverage[key]=coverage;
            }
            int changed=0;
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int index=y*width+x;if(coverage[index]!=0||pixels[index].a==0)continue;
                bool clear=false;
                for(int dy=-1;dy<=1&&!clear;dy++)for(int dx=-1;dx<=1;dx++)
                {
                    int nx=x+dx,ny=y+dy;if(nx<0||nx>=width||ny<0||ny>=height)continue;
                    int neighbor=ny*width+nx;
                    if(coverage[neighbor]!=0&&pixels[neighbor].a==0){clear=true;break;}
                }
                if(clear){pixels[index].a=0;changed++;}
            }
            return changed;
        }
        internal void ExportEditorTexture(string source,string destination)
        {
            if(!RequiresAlpha(source)){File.Copy(source,destination,false);return;}
            var copy=new Texture2D(2,2,TextureFormat.RGBA32,false,true);
            try
            {
                if(!ImageConversion.LoadImage(copy,File.ReadAllBytes(source),false))throw new IOException("PNG를 읽을 수 없습니다.");
                var pixels=copy.GetPixels32();PadAlphaEdges(source,pixels,copy.width,copy.height);
                copy.SetPixels32(pixels);copy.Apply(false,false);File.WriteAllBytes(destination,AssetExporter.EncodePng(copy));
            }
            finally{UnityEngine.Object.Destroy(copy);}
        }
    }
}
