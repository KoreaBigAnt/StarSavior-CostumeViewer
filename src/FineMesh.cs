using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace StarSaviorCostumeViewer
{
    internal sealed class MeshPiece
    {
        internal int[] Indices;
        internal int[] TriangleIds;
        internal bool Visible=true;
        internal Vector3 Center;
        internal string Hint;
        internal int IslandId=-1;
    }
    // Keep the original skinned vertex/bone/blendshape streams intact; only replace a private clone's index buffer.
    internal sealed class FineMesh : IDisposable
    {
        internal readonly Renderer Renderer;
        internal readonly List<MeshPiece>[] Slots;
        internal readonly int[][] SourceIndices;
        internal Vector2[] UV;
        private readonly Vector2[] originalUV;
        private readonly Bounds originalBounds;
        private HashSet<int>[] externalHidden;
        internal string MeshName {get{return original.name;}}
        internal Mesh OriginalMesh {get{return original;}}
        internal int VertexCount {get{return original.vertexCount;}}
        private readonly Mesh original;
        private Mesh edited;
        private readonly SubMeshDescriptor[] descriptors;
        private readonly SkinnedMeshRenderer skinned;
        private readonly MeshFilter filter;
        private readonly int capacity;
        private readonly IndexFormat format;
        internal FineMesh(Renderer renderer)
        {
            Renderer=renderer; skinned=renderer as SkinnedMeshRenderer; filter=renderer.GetComponent<MeshFilter>();
            original=skinned!=null?skinned.sharedMesh:filter!=null?filter.sharedMesh:null;
            originalBounds=skinned!=null?skinned.localBounds:original!=null?original.bounds:new Bounds();
            if(original==null) throw new InvalidOperationException("메시가 없는 부위입니다.");
            Mesh readable=original;bool baked=false;
            try
            {
                if(skinned!=null){readable=new Mesh();skinned.BakeMesh(readable,true);baked=true;}
                if(!readable.isReadable) throw new InvalidOperationException("이 정적 메시의 조각 분석은 지원하지 않습니다.");
                var vertices=readable.vertices;
                UV=readable.uv;
                originalUV=(Vector2[])UV.Clone();
                Slots=new List<MeshPiece>[readable.subMeshCount]; descriptors=new SubMeshDescriptor[Slots.Length];
                SourceIndices=new int[Slots.Length][];
                for(int slot=0;slot<Slots.Length;slot++)
                {
                    SourceIndices[slot]=readable.GetTriangles(slot);
                    Slots[slot]=UvIslands.Split(vertices,UV,SourceIndices[slot]);
                    descriptors[slot]=original.GetSubMesh(slot);
                    capacity+=Slots[slot].Sum(piece=>piece.Indices.Length);
                }
            }
            finally{if(baked) UnityEngine.Object.Destroy(readable);}
            format=original.indexFormat==IndexFormat.UInt32||SourceIndices.Any(slot=>slot.Any(index=>index>65535))?IndexFormat.UInt32:IndexFormat.UInt16;
            edited=UnityEngine.Object.Instantiate(original);edited.name=original.name+" · Viewer pieces";
            try
            {
                edited.SetIndexBufferParams(capacity,format);edited.subMeshCount=Slots.Length;
                Upload();
                if(skinned!=null)skinned.sharedMesh=edited;else filter.sharedMesh=edited;
            }
            catch{UnityEngine.Object.Destroy(edited);throw;}
        }
        internal int[] PieceIndices(int slot,MeshPiece piece)
        {if(externalHidden==null||externalHidden[slot].Count==0)return piece.Indices;var values=new List<int>();for(int i=0;i<piece.TriangleIds.Length;i++)if(!externalHidden[slot].Contains(piece.TriangleIds[i])){values.Add(piece.Indices[i*3]);values.Add(piece.Indices[i*3+1]);values.Add(piece.Indices[i*3+2]);}return values.ToArray();}
        internal int[] VisibleIndices(int slot){return Slots[slot].Where(piece=>piece.Visible).SelectMany(piece=>PieceIndices(slot,piece)).ToArray();}
        internal void ExternalGeometry(Mesh geometry,HashSet<int>[] hidden)
        {
            var next=UnityEngine.Object.Instantiate(geometry??original);var old=edited;var oldUV=UV;var oldHidden=externalHidden;
            try
            {
                edited=next;externalHidden=hidden;UV=geometry==null?(Vector2[])originalUV.Clone():geometry.uv;
                if(UV.Length!=originalUV.Length)throw new InvalidOperationException("수정 메시 UV 수가 원본과 다릅니다.");
                next.SetIndexBufferParams(capacity,format);next.subMeshCount=Slots.Length;Upload();
                if(skinned!=null){skinned.sharedMesh=next;var bounds=originalBounds;if(geometry!=null)bounds.Encapsulate(geometry.bounds);skinned.localBounds=bounds;}
                else filter.sharedMesh=next;
            }
            catch{edited=old;UV=oldUV;externalHidden=oldHidden;UnityEngine.Object.Destroy(next);throw;}
            UnityEngine.Object.Destroy(old);
        }
        internal Mesh Snapshot()
        {
            if(skinned==null)return UnityEngine.Object.Instantiate(edited);
            var copy=new Mesh();skinned.BakeMesh(copy,true);return copy;
        }
        private static SubMeshDescriptor Descriptor(SubMeshDescriptor original,int start,int count)
        {
            object boxed=original;var flags=BindingFlags.Instance|BindingFlags.NonPublic;var type=typeof(SubMeshDescriptor);
            type.GetField("<indexStart>k__BackingField",flags).SetValue(boxed,start);
            type.GetField("<indexCount>k__BackingField",flags).SetValue(boxed,count);
            type.GetField("<baseVertex>k__BackingField",flags).SetValue(boxed,0);
            return (SubMeshDescriptor)boxed;
        }
        internal void Upload()
        {
            var indices=new int[capacity];int offset=0;
            var updates=new SubMeshDescriptor[Slots.Length];
            for(int slot=0;slot<Slots.Length;slot++)
            {
                var visible=VisibleIndices(slot);Array.Copy(visible,0,indices,offset,visible.Length);
                updates[slot]=Descriptor(descriptors[slot],offset,visible.Length);offset+=visible.Length;
            }
            using(var buffer=edited.GetIndexBuffer())
            {
                if(format==IndexFormat.UInt16)
                {
                    var narrow=indices.Select(index=>(ushort)index).ToArray();
                    var data=new NativeArray<ushort>(narrow,Allocator.Temp);try{if(offset>0)buffer.SetData(data,0,0,offset);}finally{data.Dispose();}
                }
                else {var data=new NativeArray<int>(indices,Allocator.Temp);try{if(offset>0)buffer.SetData(data,0,0,offset);}finally{data.Dispose();}}
            }
            for(int slot=0;slot<Slots.Length;slot++)edited.SetSubMesh(slot,updates[slot],MeshUpdateFlags.DontRecalculateBounds|MeshUpdateFlags.DontValidateIndices);
        }
        public void Dispose()
        {
            if(skinned!=null)skinned.sharedMesh=original;else if(filter!=null)filter.sharedMesh=original;
            if(edited!=null)UnityEngine.Object.Destroy(edited);
        }
    }
}
