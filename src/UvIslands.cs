using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal static class UvIslands
    {
        private static int Root(int[] parent,int n){while(parent[n]!=n){parent[n]=parent[parent[n]];n=parent[n];}return n;}
        internal static List<MeshPiece> Split(Vector3[] positions,Vector2[] uv,int[] indices)
        {
            if(uv==null||uv.Length!=positions.Length)throw new InvalidOperationException("UV0 좌표가 없는 메시입니다.");
            int count=indices.Length/3;var parent=Enumerable.Range(0,count).ToArray();
            // Weld duplicated normal/tangent seam vertices only if BOTH their position and UV agree.
            // Distinct surfaces stacked at the same UV coordinates remain independently selectable.
            var vertices=new Dictionary<string,int>();var welded=new Dictionary<int,int>();
            foreach(int index in indices.Distinct())
            {
                var p=positions[index];var t=uv[index];string key=Mathf.RoundToInt(p.x*1000000)+":"+Mathf.RoundToInt(p.y*1000000)+":"+Mathf.RoundToInt(p.z*1000000)+":"+Mathf.RoundToInt(t.x*1000000)+":"+Mathf.RoundToInt(t.y*1000000);
                int id;if(!vertices.TryGetValue(key,out id)){id=vertices.Count;vertices.Add(key,id);}welded[index]=id;
            }
            var edges=new Dictionary<long,int>();
            for(int triangle=0;triangle<count;triangle++)for(int edge=0;edge<3;edge++)
            {
                int a=welded[indices[triangle*3+edge]],b=welded[indices[triangle*3+(edge+1)%3]];if(a==b)continue;
                long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);int other;
                if(edges.TryGetValue(key,out other)){int first=Root(parent,triangle),second=Root(parent,other);if(first!=second)parent[first]=second;}else edges.Add(key,triangle);
            }
            var groups=new Dictionary<int,List<int>>();
            for(int triangle=0;triangle<count;triangle++){int root=Root(parent,triangle);List<int> list;if(!groups.TryGetValue(root,out list)){list=new List<int>();groups.Add(root,list);}list.Add(triangle);}
            var result=new List<MeshPiece>();
            foreach(var triangles in groups.Values.OrderBy(list=>list[0]))
            {
                var source=triangles.SelectMany(t=>new[]{indices[t*3],indices[t*3+1],indices[t*3+2]}).ToArray();
                Vector3 center=Vector3.zero;foreach(int v in source)center+=positions[v];center/=Mathf.Max(1,source.Length);
                float u0=uv[source[0]].x,u1=u0,v0=uv[source[0]].y,v1=v0;
                foreach(int vertex in source){u0=Mathf.Min(u0,uv[vertex].x);u1=Mathf.Max(u1,uv[vertex].x);v0=Mathf.Min(v0,uv[vertex].y);v1=Mathf.Max(v1,uv[vertex].y);}
                int id=result.Count;result.Add(new MeshPiece{Indices=source,TriangleIds=triangles.ToArray(),Center=center,IslandId=id,Hint="UV 섬 U"+(id+1).ToString("D3")+"\nU "+u0.ToString("0.###")+"~"+u1.ToString("0.###")+" / V "+v0.ToString("0.###")+"~"+v1.ToString("0.###")});
            }
            return result;
        }
    }
    internal sealed class UvRasterLayer
    {
        internal Vector2[] UV;
        internal int[] Indices;
        internal Vector2 Scale,Offset;
        internal TextureWrapMode Wrap;
        internal UvRasterLayer(FineMesh mesh,int[] indices,TextureBinding binding){UV=mesh.UV;Indices=indices;Scale=binding.Material.GetTextureScale(binding.Property);Offset=binding.Material.GetTextureOffset(binding.Property);Wrap=binding.Original.wrapMode;}
    }
    internal static class UvRaster
    {
        private static float Cross(Vector2 a,Vector2 b){return a.x*b.y-a.y*b.x;}
        private static bool Inside(Vector2 p,Vector2 a,Vector2 b,Vector2 c)
        {float area=Cross(b-a,c-a);if(Mathf.Abs(area)<1e-12f)return false;float u=Cross(b-p,c-p)/area,v=Cross(c-p,a-p)/area;return u>=-1e-6f&&v>=-1e-6f&&u+v<=1.000001f;}
        internal static bool Contains(UvRasterLayer layer,Vector2 point)
        {
            for(int i=0;i+2<layer.Indices.Length;i+=3)
            {
                Vector2 a=Vector2.Scale(layer.UV[layer.Indices[i]],layer.Scale)+layer.Offset,b=Vector2.Scale(layer.UV[layer.Indices[i+1]],layer.Scale)+layer.Offset,c=Vector2.Scale(layer.UV[layer.Indices[i+2]],layer.Scale)+layer.Offset;
                if(layer.Wrap==TextureWrapMode.Clamp)
                {if(Inside(point,new Vector2(Mathf.Clamp01(a.x),Mathf.Clamp01(a.y)),new Vector2(Mathf.Clamp01(b.x),Mathf.Clamp01(b.y)),new Vector2(Mathf.Clamp01(c.x),Mathf.Clamp01(c.y))))return true;continue;}
                int x0=Mathf.FloorToInt(Mathf.Min(a.x,Mathf.Min(b.x,c.x))),x1=Mathf.CeilToInt(Mathf.Max(a.x,Mathf.Max(b.x,c.x)))-1,y0=Mathf.FloorToInt(Mathf.Min(a.y,Mathf.Min(b.y,c.y))),y1=Mathf.CeilToInt(Mathf.Max(a.y,Mathf.Max(b.y,c.y)))-1;
                if((long)(x1-x0+1)*(y1-y0+1)>256)continue;
                for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
                {var p=point;if(layer.Wrap==TextureWrapMode.Mirror){if((x&1)!=0)p.x=1-p.x;if((y&1)!=0)p.y=1-p.y;}p+=new Vector2(x,y);if(Inside(p,a,b,c))return true;}
            }
            return false;
        }
        // Rasterize actual UV triangles at pixel centers. No centroid grouping, color or alpha classification.
        internal static void Paint(UvRasterLayer layer,int width,int height,Action<int> pixel)
        {
            for(int i=0;i+2<layer.Indices.Length;i+=3)
            {
                Vector2 a=Vector2.Scale(layer.UV[layer.Indices[i]],layer.Scale)+layer.Offset,b=Vector2.Scale(layer.UV[layer.Indices[i+1]],layer.Scale)+layer.Offset,c=Vector2.Scale(layer.UV[layer.Indices[i+2]],layer.Scale)+layer.Offset;
                if(layer.Wrap==TextureWrapMode.Clamp)
                {a=new Vector2(Mathf.Clamp01(a.x),Mathf.Clamp01(a.y));b=new Vector2(Mathf.Clamp01(b.x),Mathf.Clamp01(b.y));c=new Vector2(Mathf.Clamp01(c.x),Mathf.Clamp01(c.y));Triangle(a,b,c,width,height,pixel);continue;}
                int x0=Mathf.FloorToInt(Mathf.Min(a.x,Mathf.Min(b.x,c.x))),x1=Mathf.CeilToInt(Mathf.Max(a.x,Mathf.Max(b.x,c.x)))-1;
                int y0=Mathf.FloorToInt(Mathf.Min(a.y,Mathf.Min(b.y,c.y))),y1=Mathf.CeilToInt(Mathf.Max(a.y,Mathf.Max(b.y,c.y)))-1;
                if((long)(x1-x0+1)*(y1-y0+1)>256)throw new InvalidOperationException("UV 삼각형의 반복 타일 범위가 너무 큽니다.");
                for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
                {
                    var shift=new Vector2(x,y);var p=a-shift;var q=b-shift;var r=c-shift;
                    if(layer.Wrap==TextureWrapMode.Mirror){if((x&1)!=0){p.x=1-p.x;q.x=1-q.x;r.x=1-r.x;}if((y&1)!=0){p.y=1-p.y;q.y=1-q.y;r.y=1-r.y;}}
                    Triangle(p,q,r,width,height,pixel);
                }
            }
        }
        private static void Triangle(Vector2 a,Vector2 b,Vector2 c,int w,int h,Action<int> pixel)
        {
            float area=Cross(b-a,c-a);if(Mathf.Abs(area)<1e-12f)return;
            int x0=Mathf.Max(0,Mathf.FloorToInt(Mathf.Min(a.x,Mathf.Min(b.x,c.x))*w)),x1=Mathf.Min(w-1,Mathf.CeilToInt(Mathf.Max(a.x,Mathf.Max(b.x,c.x))*w));
            int y0=Mathf.Max(0,Mathf.FloorToInt(Mathf.Min(a.y,Mathf.Min(b.y,c.y))*h)),y1=Mathf.Min(h-1,Mathf.CeilToInt(Mathf.Max(a.y,Mathf.Max(b.y,c.y))*h));
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
            {
                var p=new Vector2((x+.5f)/w,(y+.5f)/h);float u=Cross(b-p,c-p)/area,v=Cross(c-p,a-p)/area;
                if(u>=-1e-6f&&v>=-1e-6f&&u+v<=1.000001f)pixel(y*w+x);
            }
        }
    }
}
