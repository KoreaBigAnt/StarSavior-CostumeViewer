using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace StarSaviorCostumeViewer
{
    // GPU vertex buffers belong to Mesh, not SkinnedMeshRenderer: these are the
    // original, unskinned vertices, unaffected by the current animation/BlendShapes.
    internal static class EditableMeshExport
    {
        private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
        private static string PathOf(Transform value,Transform root)
        {if(value==null)return null;var names=new List<string>();while(value!=null&&value!=root){names.Add(value.name);value=value.parent;}if(value!=root)throw new InvalidDataException("Bone outside model hierarchy");names.Reverse();return string.Join("/",names.ToArray());}
        private static JArray Matrix(Matrix4x4 value){var a=new JArray();for(int i=0;i<16;i++)a.Add(new JValue(value[i]));return a;}
        private static byte[] Ints(int[] values){var b=new byte[values.Length*4];Buffer.BlockCopy(values,0,b,0,b.Length);return b;}
        private static void WriteObj(string folder,Mesh mesh,FineMesh fine,PartVisibility.Part[] parts,AssetExporter exporter,JObject channels)
        {
            byte[] pos=File.ReadAllBytes(System.IO.Path.Combine(folder,(string)channels["Position"]["File"]));int pd=(int)channels["Position"]["Components"];
            byte[] uv=channels["TexCoord0"]==null?null:File.ReadAllBytes(System.IO.Path.Combine(folder,(string)channels["TexCoord0"]["File"]));int ud=uv==null?0:(int)channels["TexCoord0"]["Components"];
            var vertices=new StringBuilder("# Original bind-pose mesh; vertex n maps to original vertex n-1. OBJ: mirrored X, Y-up.\nmtllib rest.mtl\n");
            for(int v=0;v<mesh.vertexCount;v++)vertices.Append(string.Format(CultureInfo.InvariantCulture,"v {0:R} {1:R} {2:R}\n",-BitConverter.ToSingle(pos,v*pd*4),BitConverter.ToSingle(pos,(v*pd+1)*4),BitConverter.ToSingle(pos,(v*pd+2)*4)));
            for(int v=0;v<mesh.vertexCount;v++)vertices.Append(string.Format(CultureInfo.InvariantCulture,"vt {0:R} {1:R}\n",uv==null?0:BitConverter.ToSingle(uv,v*ud*4),ud<2?0:BitConverter.ToSingle(uv,(v*ud+1)*4)));
            var materials=new StringBuilder();foreach(var part in parts.Where(p=>p.Slot<fine.SourceIndices.Length))
            {
                materials.AppendLine("newmtl slot_"+part.Slot+"\nKd 1 1 1");var binding=exporter.Bindings.FirstOrDefault(b=>b.Renderer==part.Renderer&&b.Material==part.Original&&(b.Property=="_BaseMap"||b.Property=="_MainTex"));
                if(binding!=null)materials.AppendLine("map_Kd ../../Textures/"+System.IO.Path.GetFileName(binding.File));
            }
            File.WriteAllText(System.IO.Path.Combine(folder,"rest.mtl"),materials.ToString());
            foreach(bool visible in new[]{false,true})
            {
                var obj=new StringBuilder(vertices.ToString());for(int sub=0;sub<fine.SourceIndices.Length;sub++)
                {
                    obj.AppendLine("usemtl slot_"+sub);var part=parts.FirstOrDefault(p=>p.Slot==sub);
                    int[] indices=visible?(part!=null&&!part.Visible?new int[0]:fine.VisibleIndices(sub)):fine.SourceIndices[sub];
                    for(int i=0;i<indices.Length;i+=3){int a=indices[i]+1,b=indices[i+1]+1,c=indices[i+2]+1;obj.Append("f "+c+"/"+c+" "+b+"/"+b+" "+a+"/"+a+"\n");}
                }
                File.WriteAllText(System.IO.Path.Combine(folder,visible?"rest-visible.obj":"rest-original.obj"),obj.ToString());
            }
        }
        private static float Half(ushort value)
        {int sign=(value&32768)==0?1:-1,exponent=(value>>10)&31,mantissa=value&1023;if(exponent==31)throw new InvalidDataException("Non-finite half vertex attribute");return exponent==0?sign*(mantissa/1024f)*(float)Math.Pow(2,-14):sign*(1+mantissa/1024f)*(float)Math.Pow(2,exponent-15);}
        private static int Size(VertexAttributeFormat format)
        {switch(format){case VertexAttributeFormat.Float32:case VertexAttributeFormat.UInt32:case VertexAttributeFormat.SInt32:return 4;case VertexAttributeFormat.Float16:case VertexAttributeFormat.UNorm16:case VertexAttributeFormat.SNorm16:case VertexAttributeFormat.UInt16:case VertexAttributeFormat.SInt16:return 2;default:return 1;}}
        private static float Number(byte[] bytes,int offset,VertexAttributeFormat format)
        {
            float value;
            switch(format)
            {
                case VertexAttributeFormat.Float32:value=BitConverter.ToSingle(bytes,offset);break;
                case VertexAttributeFormat.Float16:value=Half(BitConverter.ToUInt16(bytes,offset));break;
                case VertexAttributeFormat.UNorm8:value=bytes[offset]/255f;break;
                case VertexAttributeFormat.SNorm8:value=Mathf.Max(-1,(sbyte)bytes[offset]/127f);break;
                case VertexAttributeFormat.UNorm16:value=BitConverter.ToUInt16(bytes,offset)/65535f;break;
                case VertexAttributeFormat.SNorm16:value=Mathf.Max(-1,BitConverter.ToInt16(bytes,offset)/32767f);break;
                case VertexAttributeFormat.UInt8:value=bytes[offset];break;
                case VertexAttributeFormat.SInt8:value=(sbyte)bytes[offset];break;
                case VertexAttributeFormat.UInt16:value=BitConverter.ToUInt16(bytes,offset);break;
                case VertexAttributeFormat.SInt16:value=BitConverter.ToInt16(bytes,offset);break;
                case VertexAttributeFormat.UInt32:value=BitConverter.ToUInt32(bytes,offset);break;
                case VertexAttributeFormat.SInt32:value=BitConverter.ToInt32(bytes,offset);break;
                default:throw new InvalidDataException("Unsupported vertex format: "+format);
            }
            if(float.IsNaN(value)||float.IsInfinity(value))throw new InvalidDataException("Non-finite vertex attribute");return value;
        }
        private static byte[] Decode(Mesh mesh,VertexAttribute attribute,byte[][] streams,int dimensions)
        {
            int stream=mesh.GetVertexAttributeStream(attribute),offset=mesh.GetVertexAttributeOffset(attribute),stride=mesh.GetVertexBufferStride(stream),count=mesh.GetVertexAttributeDimension(attribute);
            var format=mesh.GetVertexAttributeFormat(attribute);int size=Size(format);
            using(var memory=new MemoryStream())using(var writer=new BinaryWriter(memory))
            {for(int v=0;v<mesh.vertexCount;v++)for(int c=0;c<dimensions;c++)writer.Write(c<count?Number(streams[stream],v*stride+offset+c*size,format):0f);return memory.ToArray();}
        }
        private static async UniTask<byte[]> ReadStream(Mesh mesh,int stream)
        {
            int size=checked(mesh.vertexCount*mesh.GetVertexBufferStride(stream));if(size<=0||size>536870912)throw new InvalidDataException("Invalid vertex buffer size");
            using(var buffer=mesh.GetVertexBuffer(stream))
            {
                var request=AsyncGPUReadback.Request(buffer,size,0,null);float started=Time.realtimeSinceStartup;
                while(!request.done){if(Time.realtimeSinceStartup-started>20)throw new IOException("Vertex buffer readback timed out");await UniTask.Yield();}
                if(request.hasError)throw new IOException("GPU vertex readback failed; original rest mesh was not exported");
                var data=request.GetData<byte>(0);if(data.Length<size)throw new IOException("Truncated vertex buffer");
                var bytes=new byte[size];for(int i=0;i<size;i++)bytes[i]=data[i];return bytes;
            }
        }
        internal static async UniTask<JObject> Export(GameObject model,PartVisibility parts,AssetExporter exporter,string profile)
        {
            string root=System.IO.Path.Combine(profile,"MeshEdit");Directory.CreateDirectory(root);var entries=new JArray();var errors=new JArray();
            foreach(var renderer in AssetExporter.Renderers(model))
            {
                string rendererPath=parts.RendererPath(renderer);
                try
                {
                    var matching=parts.Parts.Where(p=>p.Renderer==renderer).ToArray();if(matching.Length==0)continue;
                    var fine=parts.MeshForExport(renderer);var mesh=fine.OriginalMesh;
                    string id=matching[0].Id,folder=System.IO.Path.Combine(root,id);Directory.CreateDirectory(folder);
                    var streams=new byte[mesh.vertexBufferCount][];var raw=new JArray();
                    for(int stream=0;stream<streams.Length;stream++)
                    {streams[stream]=await ReadStream(mesh,stream);string file="stream-"+stream+".bin";File.WriteAllBytes(System.IO.Path.Combine(folder,file),streams[stream]);raw.Add(new JObject{["File"]=new JValue(file),["Stride"]=new JValue(mesh.GetVertexBufferStride(stream)),["SHA256"]=new JValue(Hash(streams[stream]))});}
                    if(!mesh.HasVertexAttribute(VertexAttribute.Position))throw new InvalidDataException("No position stream");
                    var channels=new JObject();
                    var desired=new[]{VertexAttribute.Position,VertexAttribute.Normal,VertexAttribute.Tangent,VertexAttribute.Color,VertexAttribute.TexCoord0,VertexAttribute.TexCoord1,VertexAttribute.TexCoord2,VertexAttribute.TexCoord3,VertexAttribute.TexCoord4,VertexAttribute.TexCoord5,VertexAttribute.TexCoord6,VertexAttribute.TexCoord7};
                    foreach(var attr in desired.Where(mesh.HasVertexAttribute))
                    {
                        int dimensions=mesh.GetVertexAttributeDimension(attr);string file=attr.ToString()+".f32le";var bytes=Decode(mesh,attr,streams,dimensions);File.WriteAllBytes(System.IO.Path.Combine(folder,file),bytes);
                        channels[attr.ToString()]=new JObject{["File"]=new JValue(file),["Components"]=new JValue(dimensions),["SHA256"]=new JValue(Hash(bytes))};
                    }
                    var attributes=new JArray();foreach(var attr in mesh.GetVertexAttributes())attributes.Add(new JObject{["Attribute"]=new JValue(attr.attribute.ToString()),["Format"]=new JValue(attr.format.ToString()),["Dimension"]=new JValue(attr.dimension),["Stream"]=new JValue(attr.stream),["Offset"]=new JValue(mesh.GetVertexAttributeOffset(attr.attribute))});
                    var submeshes=new JArray();
                    for(int sub=0;sub<fine.SourceIndices.Length;sub++)
                    {
                        byte[] original=Ints(fine.SourceIndices[sub]);string source="indices-"+sub+".i32le",visible="visible-"+sub+".i32le";File.WriteAllBytes(System.IO.Path.Combine(folder,source),original);
                        var part=matching.FirstOrDefault(p=>p.Slot==sub);var indices=part!=null&&!part.Visible?new int[0]:fine.VisibleIndices(sub);File.WriteAllBytes(System.IO.Path.Combine(folder,visible),Ints(indices));
                        submeshes.Add(new JObject{["SubMesh"]=new JValue(sub),["OriginalIndices"]=new JValue(source),["VisibleIndices"]=new JValue(visible),["OriginalIndexSHA256"]=new JValue(Hash(original)),["MaterialSlot"]=new JValue(sub)});
                    }
                    var skin=renderer as SkinnedMeshRenderer;var bones=new JArray();var bindposes=mesh.bindposes;
                    if(skin!=null)
                    {
                        var transforms=skin.bones;if(bindposes.Length!=transforms.Length)throw new InvalidDataException("Bone/bindpose count mismatch");
                        for(int b=0;b<transforms.Length;b++)
                        {
                            int parent=-1;for(var ancestor=transforms[b]==null?null:transforms[b].parent;ancestor!=null&&ancestor!=model.transform;ancestor=ancestor.parent){parent=Array.IndexOf(transforms,ancestor);if(parent>=0)break;}
                            bones.Add(new JObject{["Index"]=new JValue(b),["Name"]=new JValue(transforms[b]==null?"bone_"+b:transforms[b].name),["Path"]=new JValue(PathOf(transforms[b],model.transform)),["Parent"]=new JValue(parent),["BindPose"]=Matrix(bindposes[b]),["RestMatrix"]=Matrix(bindposes[b].inverse)});
                        }
                        var counts=mesh.GetBonesPerVertex();var weights=mesh.GetAllBoneWeights();if(counts.Length!=mesh.vertexCount)throw new InvalidDataException("Skin weights unavailable");
                        var countBytes=new byte[counts.Length];int total=0;for(int v=0;v<counts.Length;v++){countBytes[v]=counts[v];total+=counts[v];}if(total!=weights.Length)throw new InvalidDataException("Invalid influence count");
                        File.WriteAllBytes(System.IO.Path.Combine(folder,"bones-per-vertex.u8"),countBytes);
                        using(var file=new FileStream(System.IO.Path.Combine(folder,"bone-weights.bin"),FileMode.Create,FileAccess.Write))using(var writer=new BinaryWriter(file))
                        for(int i=0;i<weights.Length;i++){if(weights[i].boneIndex<0||weights[i].boneIndex>=bones.Count)throw new InvalidDataException("Invalid bone index");writer.Write(weights[i].boneIndex);writer.Write(weights[i].weight);}
                    }
                    var mats=new JArray();foreach(var part in matching)
                    {
                        var textures=new JArray();foreach(var binding in exporter.Bindings.Where(b=>b.Renderer==renderer&&b.Material==part.Original))textures.Add(new JObject{["Property"]=new JValue(binding.Property),["File"]=new JValue("Textures/"+System.IO.Path.GetFileName(binding.File)),["Scale"]=new JArray(new float[]{binding.Material.GetTextureScale(binding.Property).x,binding.Material.GetTextureScale(binding.Property).y}),["Offset"]=new JArray(new float[]{binding.Material.GetTextureOffset(binding.Property).x,binding.Material.GetTextureOffset(binding.Property).y})});
                        mats.Add(new JObject{["Slot"]=new JValue(part.Slot),["Name"]=new JValue(part.Original.name),["Visible"]=new JValue(part.Visible),["Textures"]=textures});
                    }
                    var doc=new JObject{["Format"]=new JValue("StarSavior.EditableMesh"),["Version"]=new JValue(1),["RendererPath"]=new JValue(rendererPath),["MeshName"]=new JValue(mesh.name),["VertexCount"]=new JValue(mesh.vertexCount),["Pose"]=new JValue("OriginalBindPose"),["CoordinateSpace"]=new JValue("UnityRendererLocalLeftHanded"),["MatrixOrder"]=new JValue("ColumnMajor"),["GeometrySource"]=new JValue("OriginalMeshVertexBuffer"),["Channels"]=channels,["RawStreams"]=raw,["VertexLayout"]=attributes,["SubMeshes"]=submeshes,["Skinned"]=new JValue(skin!=null),["Bones"]=bones,["RootBonePath"]=new JValue(skin==null?null:PathOf(skin.rootBone,model.transform)),["BoneCountsFile"]=new JValue(skin==null?null:"bones-per-vertex.u8"),["BoneWeightsFile"]=new JValue(skin==null?null:"bone-weights.bin"),["BlendShapePolicy"]=new JValue("ReuseOriginalRuntimeBlendShapes"),["BlendShapeNames"]=new JArray(Enumerable.Range(0,mesh.blendShapeCount).Select(mesh.GetBlendShapeName)),["Materials"]=mats};
                    doc["RendererToModel"]=Matrix(model.transform.worldToLocalMatrix*renderer.transform.localToWorldMatrix);
                    doc["RendererActive"]=new JValue(renderer.gameObject.activeInHierarchy);
                    doc["RendererEnabled"]=new JValue(renderer.enabled);
                    WriteObj(folder,mesh,fine,matching,exporter,channels);
                    File.WriteAllText(System.IO.Path.Combine(folder,"mesh-edit.json"),doc.ToString());entries.Add(new JObject{["Id"]=new JValue(id),["RendererPath"]=new JValue(rendererPath),["File"]=new JValue(id+"/mesh-edit.json")});
                }
                catch(Exception failure){errors.Add(new JObject{["RendererPath"]=new JValue(rendererPath),["Error"]=new JValue(failure.Message)});Debug.LogWarning("[Viewer] Rest mesh export: "+rendererPath+" "+failure.Message);}
            }
            var index=new JObject{["Format"]=new JValue("StarSavior.MeshEditIndex"),["Version"]=new JValue(1),["Complete"]=new JValue(errors.Count==0&&entries.Count>0),["Meshes"]=entries,["Errors"]=errors};File.WriteAllText(System.IO.Path.Combine(root,"mesh-index.json"),index.ToString());return index;
        }
    }
}
