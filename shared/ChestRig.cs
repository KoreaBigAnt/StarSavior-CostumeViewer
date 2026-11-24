using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StarSaviorMods
{
    internal sealed class ChestRig
    {
        private sealed class Bone
        {
            internal Transform Target, Reference;
            internal Vector3 Right, Up, Forward;
            internal float Side, UpperPivotHeight;
            internal Vector3 BasePosition, BaseScale, AppliedPosition, AppliedScale;
            internal Quaternion BaseRotation, AppliedRotation;
            internal Vector3 Anchor, Offset, Velocity;
            internal bool Applied, Tracking;
            internal bool Translate, Resize, Rotate;
        }

        private readonly Bone[] bones;
        private readonly BodyRig body;
        internal bool ChestSupported => bones.Length != 0;
        internal bool Supported => ChestSupported || body.Supported;
        internal bool BodySupported => body.Supported;
        internal JObject BodyDiagnostics() => body.Diagnostics();
        internal float BodyHeight => body.FullHeight;

        internal ChestRig(GameObject model)
        {
            body = new BodyRig(model);
            var bound = new HashSet<Transform>(model.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .SelectMany(renderer => renderer.bones).Where(bone => bone != null && bone.parent != null));
            var points = FindPairs(bound, "Point_breast_L", "Point_breast_R", true, model.transform);
            var pointReferences = new HashSet<Transform>(points.Select(bone => bone.Reference));
            bones = points.Concat(FindPairs(bound, "Bone_breast_L", "Bone_breast_R", false, model.transform)
                .Where(bone => !pointReferences.Contains(bone.Reference))).ToArray();
        }

        private static Bone[] FindPairs(HashSet<Transform> bound, string left, string right, bool points, Transform root)
        {
            var result = new List<Bone>();
            var pairs = bound.Where(bone => Named(bone, left) || Named(bone, right))
                .GroupBy(bone => points && bone.parent.parent != null &&
                    (Named(bone.parent, "Bone_breast_L") || Named(bone.parent, "Bone_breast_R")) ? bone.parent.parent : bone.parent);
            foreach (var pair in pairs)
            {
                var leftBone = pair.FirstOrDefault(bone => Named(bone, left));
                var rightBone = pair.FirstOrDefault(bone => Named(bone, right));
                if (leftBone == null || rightBone == null) continue;
                var reference = pair.Key;
                var rightAxis = reference.InverseTransformDirection(root.right);
                var upAxis = reference.InverseTransformDirection(root.up);
                var forwardAxis = reference.InverseTransformDirection(root.forward);
                float leftSide = Vector3.Dot(leftBone.position - rightBone.position, root.right) > 0f ? 1f : -1f;
                float pivotHeight = reference.InverseTransformVector(leftBone.position - rightBone.position).magnitude * .5f;
                result.Add(new Bone { Target = leftBone, Reference = reference, Right = rightAxis, Up = upAxis, Forward = forwardAxis, Side = leftSide, UpperPivotHeight = pivotHeight });
                result.Add(new Bone { Target = rightBone, Reference = reference, Right = rightAxis, Up = upAxis, Forward = forwardAxis, Side = -leftSide, UpperPivotHeight = pivotHeight });
            }
            return result.ToArray();
        }

        private static bool Named(Transform target, string name) => string.Equals(target.name, name, StringComparison.OrdinalIgnoreCase);

        internal void Restore()
        {
            foreach (var bone in bones)
            {
                if (!bone.Applied || bone.Target == null) continue;
                // The native animation may already have written a new pose.
                if (bone.Translate && bone.Target.localPosition.Equals(bone.AppliedPosition)) bone.Target.localPosition = bone.BasePosition;
                if (bone.Resize && bone.Target.localScale.Equals(bone.AppliedScale)) bone.Target.localScale = bone.BaseScale;
                if (bone.Rotate && bone.Target.localRotation.Equals(bone.AppliedRotation)) bone.Target.localRotation = bone.BaseRotation;
                bone.Applied = false;
            }
            body.Restore();
        }

        internal void Suspend()
        {
            Restore();
            foreach (var bone in bones) bone.Tracking = false;
        }

        internal void Apply(ChestSettings settings, float dt, bool simulatePhysics = true)
        {
            if (settings == null || settings.IsDefault) { Suspend(); return; }
            body.Apply(settings.Height, settings.Fullness);
            bool physics = settings.Physics && simulatePhysics;
            foreach (var bone in bones)
            {
                var target = bone.Target;
                if (target == null || !target.gameObject.activeInHierarchy)
                {
                    bone.Tracking = false;
                    continue;
                }
                bone.BasePosition = target.localPosition;
                bone.BaseScale = target.localScale;
                bone.BaseRotation = target.localRotation;
                var right = bone.Reference.TransformDirection(bone.Right);
                var up = bone.Reference.TransformDirection(bone.Up);
                var displacement = right * (.035f * settings.Spread * bone.Side);
                float underbust = settings.UnderbustSpace - 1f;
                var shapeRotation = Quaternion.identity;
                var shapeScale = Vector3.one;
                if (underbust != 0f)
                {
                    // Keep the upper attachment near its original position while the lower contour moves in or out.
                    var upperPivot = bone.Reference.TransformVector(bone.Up * bone.UpperPivotHeight);
                    shapeRotation = Quaternion.AngleAxis(-12f * underbust, right);
                    displacement += upperPivot - shapeRotation * upperPivot;
                    var localForward = target.InverseTransformDirection(bone.Reference.TransformDirection(bone.Forward));
                    shapeScale = DepthScale(localForward, 1f + .18f * underbust);
                }
                if (physics) Step(bone, target.position + displacement, dt, settings);
                else bone.Tracking = false;
                bone.Translate = settings.Spread != 0f || underbust != 0f || physics;
                bone.Resize = settings.Size != 1f || underbust != 0f;
                bone.Rotate = settings.Yaw != 0f || settings.Pitch != 0f || underbust != 0f;
                if (bone.Translate)
                    target.localPosition += target.parent.InverseTransformVector(displacement + (physics ? bone.Offset : Vector3.zero));
                if (bone.Resize) target.localScale = Vector3.Scale(target.localScale, shapeScale) * settings.Size;
                if (bone.Rotate)
                    target.rotation = Quaternion.AngleAxis(settings.Yaw, up) * Quaternion.AngleAxis(-settings.Pitch, right) * shapeRotation * target.rotation;
                bone.AppliedPosition = target.localPosition;
                bone.AppliedScale = target.localScale;
                bone.AppliedRotation = target.localRotation;
                bone.Applied = true;
            }
        }

        private static Vector3 DepthScale(Vector3 direction, float scale)
        {
            float amount = scale - 1f;
            return Vector3.one + amount * Vector3.Scale(direction, direction);
        }

        internal bool RetainAppliedPose()
        {
            bool corrected = body.RetainAppliedPose();
            foreach (var bone in bones)
            {
                var target = bone.Target;
                if (!bone.Applied || target == null || !target.gameObject.activeInHierarchy) continue;
                // A native animation can write again after LateUpdate, before the camera renders.
                if (bone.Translate && !target.localPosition.Equals(bone.AppliedPosition))
                { target.localPosition = bone.AppliedPosition; corrected = true; }
                if (bone.Resize && !target.localScale.Equals(bone.AppliedScale))
                { target.localScale = bone.AppliedScale; corrected = true; }
                if (bone.Rotate && !target.localRotation.Equals(bone.AppliedRotation))
                { target.localRotation = bone.AppliedRotation; corrected = true; }
            }
            return corrected;
        }

        private static void Step(Bone bone, Vector3 anchor, float dt, ChestSettings settings)
        {
            const float travel = .045f;
            var movement = anchor - bone.Anchor;
            bone.Anchor = anchor;
            if (!bone.Tracking || dt > .12f || movement.sqrMagnitude > .09f)
            {
                bone.Offset = bone.Velocity = Vector3.zero;
                bone.Tracking = true;
                return;
            }
            if (dt <= 0f) return;

            float stiffness = 105f * settings.Elasticity;
            float damping = .58f * 2f * Mathf.Sqrt(stiffness * settings.Mass);
            int steps = Mathf.CeilToInt(dt * 120f);
            float step = dt / steps;
            var anchorStep = movement / steps;
            for (int i = 0; i < steps; i++)
            {
                bone.Offset -= anchorStep;
                var force = settings.Mass * Vector3.down * .65f - stiffness * bone.Offset - damping * bone.Velocity;
                bone.Velocity += force * (step / settings.Mass);
                bone.Offset += bone.Velocity * step;
                if (bone.Offset.sqrMagnitude <= travel * travel) continue;
                var direction = bone.Offset.normalized;
                bone.Offset = direction * travel;
                bone.Velocity -= direction * Mathf.Max(0f, Vector3.Dot(bone.Velocity, direction));
            }
        }

        internal JArray Diagnostics() => new JArray(bones.Where(bone => bone.Target != null).Select(bone => new JObject
        {
            ["Name"] = new JValue(bone.Target.name), ["Applied"] = new JValue(bone.Applied),
            ["ScaleRatio"] = new JValue(bone.Applied && bone.BaseScale.sqrMagnitude > 0f ? bone.Target.localScale.magnitude / bone.BaseScale.magnitude : 1f),
            ["Rotation"] = new JValue(bone.Applied ? Quaternion.Angle(bone.BaseRotation, bone.Target.localRotation) : 0f),
            ["Translation"] = new JValue(bone.Applied ? bone.Target.parent.TransformVector(bone.Target.localPosition - bone.BasePosition).magnitude : 0f),
            ["Offset"] = new JValue(bone.Tracking ? bone.Offset.magnitude : 0f)
        }));


    }
}
