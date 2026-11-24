using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StarSaviorMods
{
    // Height is one coherent model-space deformation, never a stack of limb-bone scales.
    // Small extremities follow their attachment point while retaining their original dimensions.
    internal sealed class BodyRig
    {
        private sealed class Node
        {
            internal Transform Target, Anchor, End;
            internal bool FootGroup, Applied;
            internal Vector3 BasePosition, BaseScale, AppliedPosition, AppliedScale, WorldPosition, AnchorPosition;
            internal Vector3 BaseLengths, DesiredLengths, DesiredPosition, LengthAxis, EndLocal;
            internal Quaternion BaseRotation, WorldRotation, AppliedRotation;
            internal float Fullness;
        }
        private readonly Transform root;
        private readonly Node[] nodes;
        private readonly float floor, neck, fullHeight, knee, hip;
        private Vector3 baseRootPosition, baseRootScale, appliedRootPosition, appliedRootScale;
        private bool applied;
        internal bool Supported => nodes.Length > 8 && fullHeight > .1f;
        internal float FullHeight => fullHeight;

        internal BodyRig(GameObject model)
        {
            root=model.transform;
            var bound=new HashSet<Transform>(model.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .SelectMany(renderer=>renderer.bones).Where(bone=>bone!=null));
            var all=new HashSet<Transform>();
            foreach(var bone in bound)
                for(var at=bone;at!=null&&at!=root&&at.IsChildOf(root);at=at.parent)all.Add(at);
            nodes=all.OrderBy(Depth).Select(bone=>new Node{Target=bone}).ToArray();
            if(nodes.Length==0)return;
            var feet=bound.Where(bone=>Main(bone)&&Named(bone,"foot")).ToArray();
            var head=bound.Where(bone=>Main(bone)&&Named(bone,"head")).OrderBy(bone=>bone.name.Length).FirstOrDefault();
            floor=feet.Length==0 ? 0f : feet.Min(bone=>root.InverseTransformPoint(bone.position).y);
            float headY=head==null ? floor+1.5f : root.InverseTransformPoint(head.position).y;
            fullHeight=Mathf.Max(.1f,(headY-floor)/.90f);
            neck=JointHeight(bound,"neck",floor+fullHeight*.80f);
            knee=JointHeight(bound,"calf",floor+fullHeight*.30f);
            hip=JointHeight(bound,"thigh",floor+fullHeight*.60f);
            foreach(var node in nodes)
            {
                var bone=node.Target;
                for(var at=bone;at!=null&&at!=root;at=at.parent)
                    if(Main(at)&&(Named(at,"neck")||Named(at,"head")||Named(at,"hand")||Named(at,"foot")))node.Anchor=at;
                node.FootGroup=node.Anchor!=null&&Named(node.Anchor,"foot");
                float y=(root.InverseTransformPoint(bone.position).y-floor)/fullHeight;
                node.Fullness=node.Anchor==null&&Main(bone)&&Named(bone,"spine") ? BodyShapeMath.FullnessProfile(y)*.60f : 0f;
                bool thigh=Named(bone,"thigh"), calf=Named(bone,"calf")||Named(bone,"shin");
                var start=bone;
                if(thigh||calf)
                {
                    node.Fullness=0f;
                    string token=thigh?"thigh":"calf";
                    while(start!=null&&!(Main(start)&&Named(start,token)))start=start.parent;
                    if(start==null)
                        start=all.Where(target=>Main(target)&&Named(target,token)&&Side(target)==Side(bone))
                            .OrderBy(target=>(target.position-bone.position).sqrMagnitude).FirstOrDefault();
                    if(start!=null)
                        node.End=all.Where(target=>target!=start&&target.IsChildOf(start)&&Main(target)&&Named(target,thigh?"calf":"foot"))
                            .OrderBy(Depth).FirstOrDefault();
                }
                var child=node.End??all.Where(target=>target.parent==bone&&Main(target))
                    .OrderByDescending(target=>(target.position-bone.position).sqrMagnitude).FirstOrDefault();
                Vector3 direction=child==null?bone.up:child.position-(start==null?bone.position:start.position);
                node.LengthAxis=bone.InverseTransformDirection(direction).normalized;
                if(node.LengthAxis.sqrMagnitude<.5f)node.LengthAxis=Vector3.up;
            }
        }
        private static bool Named(Transform bone,string token)=>bone.name.IndexOf(token,StringComparison.OrdinalIgnoreCase)>=0;
        private static bool Main(Transform bone)=>!Named(bone,"twist")&&!Named(bone,"point")&&!Named(bone,"end");
        private static int Depth(Transform bone){int depth=0;for(;bone!=null;bone=bone.parent)depth++;return depth;}
        private static int Side(Transform bone)
        {
            string name=bone.name.ToLowerInvariant().Replace(" ","").Replace("_","");
            return name.Contains("lthigh")||name.Contains("lcalf")||name.Contains("lshin")?-1:
                name.Contains("rthigh")||name.Contains("rcalf")||name.Contains("rshin")?1:0;
        }
        private float JointHeight(IEnumerable<Transform> bones,string token,float fallback)
        {
            float sum=0f;int count=0;
            foreach(var bone in bones)if(Main(bone)&&Named(bone,token)){sum+=root.InverseTransformPoint(bone.position).y;count++;}
            return count==0?fallback:sum/count;
        }
        private static Vector3 Lengths(Transform parent,Quaternion rotation)
        {
            if(parent==null)return Vector3.one;
            return new Vector3(parent.TransformVector(rotation*Vector3.right).magnitude,
                parent.TransformVector(rotation*Vector3.up).magnitude,parent.TransformVector(rotation*Vector3.forward).magnitude);
        }
        private static float Ratio(float before,float after)=>after>.00001f?before/after:1f;
        internal void Apply(ChestSettings settings,float dt=0f) { if(settings!=null)Apply(settings.Height,settings.Fullness); }
        internal void Apply(float height,float fullness)
        {
            if(!Supported||height==1f&&fullness==0f)return;
            baseRootPosition=root.localPosition;baseRootScale=root.localScale;
            var inverse=root.worldToLocalMatrix;
            foreach(var node in nodes)
            {
                var bone=node.Target;
                node.BasePosition=bone.localPosition;node.BaseScale=bone.localScale;node.BaseRotation=bone.localRotation;
                node.WorldPosition=bone.position;node.WorldRotation=bone.rotation;
                node.BaseLengths=Lengths(bone.parent,bone.localRotation);
                if(node.Anchor!=null)node.AnchorPosition=node.Anchor.position;
                if(node.End!=null)node.EndLocal=bone.InverseTransformPoint(node.End.position);
            }
            float axial=BodyShapeMath.HeightScale(floor,neck,fullHeight,height);
            var anchor=root.TransformPoint(new Vector3(0f,floor,0f));
            float bodyWidth=1f+.12f*fullness;
            root.localScale=new Vector3(baseRootScale.x*bodyWidth,baseRootScale.y*axial,baseRootScale.z*bodyWidth);
            root.position+=anchor-root.TransformPoint(new Vector3(0f,floor,0f));
            appliedRootPosition=root.localPosition;appliedRootScale=root.localScale;applied=true;
            // Snapshot the height-deformed baseline before fullness modifies any skeleton parent.
            foreach(var node in nodes)
            {
                var bone=node.Target;
                node.DesiredPosition=bone.position;
                node.DesiredLengths=Lengths(bone.parent,bone.localRotation);
                if(node.Anchor!=null)
                {
                    var offset=root.TransformPoint(inverse.MultiplyPoint3x4(node.AnchorPosition))-node.AnchorPosition;
                    if(node.FootGroup)offset-=root.up*Vector3.Dot(offset,root.up);
                    node.DesiredPosition=node.WorldPosition+offset;
                    node.DesiredLengths=node.BaseLengths;
                }
            }
            foreach(var node in nodes)
            {
                var bone=node.Target;
                bone.rotation=node.WorldRotation;
                var lengths=Lengths(bone.parent,bone.localRotation);
                var axis=node.LengthAxis;
                int longitudinal=Mathf.Abs(axis.x)>=Mathf.Abs(axis.y)&&Mathf.Abs(axis.x)>=Mathf.Abs(axis.z)?0:Mathf.Abs(axis.y)>=Mathf.Abs(axis.z)?1:2;
                float radial=1f+.30f*fullness*node.Fullness;
                bone.localScale=new Vector3(node.BaseScale.x*Ratio(node.DesiredLengths.x,lengths.x)*(longitudinal==0?1f:radial),
                    node.BaseScale.y*Ratio(node.DesiredLengths.y,lengths.y)*(longitudinal==1?1f:radial),
                    node.BaseScale.z*Ratio(node.DesiredLengths.z,lengths.z)*(longitudinal==2?1f:radial));
                bone.position=node.DesiredPosition;
                node.AppliedPosition=bone.localPosition;node.AppliedScale=bone.localScale;node.AppliedRotation=bone.localRotation;node.Applied=true;
            }
        }
        internal void Restore()
        {
            foreach(var node in nodes)
            {
                if(!node.Applied||node.Target==null)continue;
                if(node.Target.localPosition.Equals(node.AppliedPosition))node.Target.localPosition=node.BasePosition;
                if(node.Target.localScale.Equals(node.AppliedScale))node.Target.localScale=node.BaseScale;
                if(node.Target.localRotation.Equals(node.AppliedRotation))node.Target.localRotation=node.BaseRotation;
                node.Applied=false;
            }
            if(applied)
            {
                if(root.localPosition.Equals(appliedRootPosition))root.localPosition=baseRootPosition;
                if(root.localScale.Equals(appliedRootScale))root.localScale=baseRootScale;
                applied=false;
            }
        }
        internal bool RetainAppliedPose()
        {
            bool changed=false;
            if(applied)
            {
                if(!root.localPosition.Equals(appliedRootPosition)){root.localPosition=appliedRootPosition;changed=true;}
                if(!root.localScale.Equals(appliedRootScale)){root.localScale=appliedRootScale;changed=true;}
            }
            foreach(var node in nodes)
            {
                if(!node.Applied||node.Target==null)continue;
                if(!node.Target.localPosition.Equals(node.AppliedPosition)){node.Target.localPosition=node.AppliedPosition;changed=true;}
                if(!node.Target.localScale.Equals(node.AppliedScale)){node.Target.localScale=node.AppliedScale;changed=true;}
                if(!node.Target.localRotation.Equals(node.AppliedRotation)){node.Target.localRotation=node.AppliedRotation;changed=true;}
            }
            return changed;
        }
        internal JObject Diagnostics()=>new JObject{["Supported"]=new JValue(Supported),["Bones"]=new JValue(nodes.Length),
            ["Floor"]=new JValue(floor),["Knee"]=new JValue(knee),["Hip"]=new JValue(hip),["Neck"]=new JValue(neck),["Height"]=new JValue(fullHeight),
            ["RootScale"]=new JArray(new float[]{root.localScale.x,root.localScale.y,root.localScale.z}),
            ["RootMatrix"]=new JArray(Enumerable.Range(0,16).Select(index=>new JValue(root.localToWorldMatrix[index]))),
            ["Nodes"]=new JArray(nodes.Select(node=>new JObject{["Name"]=new JValue(node.Target.name),
                ["Group"]=new JValue(node.Anchor==null?"body":node.FootGroup?"foot":"extremity"),
                ["Position"]=new JArray(new float[]{node.Target.position.x,node.Target.position.y,node.Target.position.z}),
                ["WorldScale"]=new JArray(new float[]{node.Target.lossyScale.x,node.Target.lossyScale.y,node.Target.lossyScale.z}),
                ["Scale"]=new JArray(new float[]{node.Target.localScale.x,node.Target.localScale.y,node.Target.localScale.z})}))};
    }
}
