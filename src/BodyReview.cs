using System;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    public sealed partial class Viewer
    {
        private sealed class ReviewPose
        {
            internal Transform Target;internal Vector3 Position,Scale;internal Quaternion Rotation;
        }
        private ReviewPose[] reviewPose;
        private void RestoreReviewPose()
        {
            if(reviewPose==null)return;
            foreach(var pose in reviewPose)if(pose.Target!=null){pose.Target.localPosition=pose.Position;pose.Target.localScale=pose.Scale;pose.Target.localRotation=pose.Rotation;}
        }
        private async UniTask ReviewBody(string name, bool frame)
        {
            if(frame)
            {
                chestPreview?.Suspend();
                reviewPose=model.GetComponentsInChildren<Transform>(true).Select(target=>new ReviewPose{Target=target,Position=target.localPosition,Scale=target.localScale,Rotation=target.localRotation}).ToArray();
            }
            await UniTask.WaitForEndOfFrame(this);
            string folder=Path.Combine(root,"BodyReview",AssetExporter.Safe(name));
            Directory.CreateDirectory(folder);
            var meshes=new JArray();
            var bounds=new Bounds();bool hasBounds=false;
            foreach(var renderer in AssetExporter.Renderers(model).OfType<SkinnedMeshRenderer>().Where(renderer=>renderer.enabled&&renderer.gameObject.activeInHierarchy))
            {
                string lower=renderer.name.ToLowerInvariant();
                if(lower!="body"&&lower!="tights"&&!lower.StartsWith("body_")&&!lower.Contains("face")&&!lower.Contains("hair")&&!lower.Contains("eyes"))continue;
                var baked=new Mesh();
                try
                {
                    renderer.BakeMesh(baked,true);
                    var vertices=baked.vertices;string key=meshes.Count.ToString("D2")+"-"+AssetExporter.Safe(renderer.name);
                    using(var writer=new BinaryWriter(new FileStream(Path.Combine(folder,key+".vertices.bin"),FileMode.Create,FileAccess.Write)))
                    foreach(var vertex in vertices)
                    {
                        var point=renderer.transform.TransformPoint(vertex);
                        writer.Write(point.x);writer.Write(point.y);writer.Write(point.z);
                        if(!hasBounds){bounds=new Bounds(point,Vector3.zero);hasBounds=true;}else bounds.Encapsulate(point);
                    }
                    var indices=Enumerable.Range(0,baked.subMeshCount).SelectMany(baked.GetTriangles).ToArray();
                    using(var writer=new BinaryWriter(new FileStream(Path.Combine(folder,key+".triangles.bin"),FileMode.Create,FileAccess.Write)))foreach(int index in indices)writer.Write(index);
                    var counts=renderer.sharedMesh.GetBonesPerVertex();var weights=renderer.sharedMesh.GetAllBoneWeights();var bones=renderer.bones;
                    using(var writer=new BinaryWriter(new FileStream(Path.Combine(folder,key+".weights.bin"),FileMode.Create,FileAccess.Write)))
                    {
                        int offset=0;
                        for(int vertex=0;vertex<vertices.Length;vertex++)
                        {
                            float leg=0f,calf=0f,thigh=0f,torso=0f;
                            int count=vertex<counts.Length?counts[vertex]:0;
                            for(int i=0;i<count;i++)
                            {
                                var weight=weights[offset++];string boneName=weight.boneIndex<bones.Length&&bones[weight.boneIndex]!=null?bones[weight.boneIndex].name.ToLowerInvariant():"";
                                if(boneName.Contains("thigh")){leg+=weight.weight;thigh+=weight.weight;}
                                else if(boneName.Contains("calf")||boneName.Contains("shin")||boneName.Contains("knee")){leg+=weight.weight;calf+=weight.weight;}
                                if(boneName.Contains("spine")||boneName.Contains("pelvis")||boneName.Contains("breast"))torso+=weight.weight;
                            }
                            writer.Write(leg);writer.Write(calf);writer.Write(thigh);writer.Write(torso);
                        }
                    }
                    meshes.Add(new JObject{["Name"]=new JValue(renderer.name),["Vertices"]=new JValue(vertices.Length),
                        ["Positions"]=new JValue(key+".vertices.bin"),["Triangles"]=new JValue(key+".triangles.bin"),["Weights"]=new JValue(key+".weights.bin")});
                }
                finally{Destroy(baked);}
            }
            if(frame&&hasBounds)
            {
                frameTarget=bounds.center;pitch=0f;yaw=0f;camera.fieldOfView=32;
                float aspect=Mathf.Max(.3f,(Screen.width-PanelsWidth)/Screen.height);
                distance=Mathf.Max(.5f,Mathf.Max(bounds.size.y,bounds.size.x/aspect)/(2*Mathf.Tan(16*.0174532925f))*1.45f);
            }
            var doc=new JObject{["Unit"]=new JValue(selected.Unit),["Skin"]=new JValue(selected.Skin),["Shape"]=chestDraft.ToJson(),
                ["Body"]=chestPreview.BodyDiagnostics(),["Meshes"]=meshes};
            File.WriteAllText(Path.Combine(folder,"survey.json"),doc.ToString());
            await UniTask.WaitForEndOfFrame(this);
            await CaptureReviewViewport(Path.Combine(folder,"front.png"));
            yaw=90f;
            await UniTask.WaitForEndOfFrame(this);
            await UniTask.WaitForEndOfFrame(this);
            await CaptureReviewViewport(Path.Combine(folder,"side.png"));
            yaw=0f;
            File.WriteAllText(Path.Combine(root,"viewer-body-review-result.json"),new JObject{["Path"]=new JValue(folder)}.ToString());
        }
        private async UniTask CaptureReviewViewport(string path)
        {
            await UniTask.WaitForEndOfFrame(this);
            var rect=camera.pixelRect;
            var texture=new Texture2D((int)rect.width,(int)rect.height,TextureFormat.RGBA32,false);
            try{texture.ReadPixels(rect,0,0,false);texture.Apply(false,false);File.WriteAllBytes(path,AssetExporter.EncodePng(texture));}
            finally{Destroy(texture);}
        }
    }
}
