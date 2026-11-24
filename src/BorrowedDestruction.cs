using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed partial class NativeCharacter
    {
        private bool borrowedFullSequence;
        private AnimatorOverrideController borrowedIdleController;
        private readonly List<object> borrowedDamageVoices=new List<object>();
        private static readonly BindingFlags instanceFields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static FieldInfo SequenceField(object value,string name)
        {
            for(var type=value.GetType();type!=null;type=type.BaseType)
            {var field=type.GetField(name,instanceFields|BindingFlags.DeclaredOnly);if(field!=null)return field;}
            throw new MissingFieldException(value.GetType().Name,name);
        }
        private static object CopySequenceObject(object value)
        {return typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(value,null);}
        internal bool HasNativeDestruction {get{return !borrowedFullSequence&&(bool)Call(native,"IsSupportArmorBreakAnim");}}
        internal async UniTask BorrowDestruction(NativeCharacter seira, bool force = false)
        {
            if(!force&&HasNativeDestruction)return;
            var states=Call(costumeTemplate,"ഈഅഉഇഉഅഈഅഅഌഉഉഉഅഅ");
            var damage=Call(states,"ഉഋഅആആഇഋഈആഇഉഈഉഈഈ","USN_DAMAGE_A");
            if(damage==null&&!force)throw new InvalidOperationException("현재 캐릭터의 피격 상태가 없습니다.");
            // Voices use VoiceSeries, independently from the sequence's SoundEvents.
            var voices=damage==null?null:SequenceField(damage,"ആഊഌഉഉഉആഅഇആഅഉആഇആ").GetValue(damage) as IEnumerable;
            borrowedDamageVoices.Clear();if(voices!=null)foreach(var voice in voices)if(voice!=null)borrowedDamageVoices.Add(voice);
            if(borrowedDamageVoices.Count==0&&!force)throw new InvalidOperationException("현재 캐릭터의 피격 음성 시리즈가 없습니다.");
            var ownIdle=Call(states,"ഉഋഅആആഇഋഈആഇഉഈഉഈഈ","USN_ASTAND");
            var ownAnimator=(Animator)Call(animation,"GetAnimator");
            var ownController=ownAnimator.runtimeAnimatorController as AnimatorOverrideController;
            string ownIdleSlot=(string)SequenceField(ownIdle,"ഉഊഇഉഋഌഅഉഊഇഊആഌഌആ").GetValue(ownIdle);
            var ownIdleClip=ownController==null?null:ownController[ownIdleSlot];
            // The native preset changes animation, cinematic camera and effect rig,
            // retaining the current character's model, avatar and materials.
            var method=modelType.GetMethods().Single(m=>m.Name=="ChangeVFXAnimationPreset"&&m.GetParameters().Length==3);
            object preset=Enum.ToObject(method.GetParameters()[0].ParameterType,1);
            await (UniTask)method.Invoke(native,new object[]{preset,seira.costumeBody,true});
            // Preserve the target's original idle during the three-second lead-in;
            // all destruction/camera/effect clips still come from Seira.
            animation=Call(native,"GetNKCAnim");
            if(ownIdleClip!=null)
            {
                var donorStates=Call(seira.costumeTemplate,"ഈഅഉഇഉഅഈഅഅഌഉഉഉഅഅ");
                var donorIdle=Call(donorStates,"ഉഋഅആആഇഋഈആഇഉഈഉഈഈ","USN_ASTAND");
                string donorIdleSlot=(string)SequenceField(donorIdle,"ഉഊഇഉഋഌഅഉഊഇഊആഌഌആ").GetValue(donorIdle);
                var animator=(Animator)Call(animation,"GetAnimator");var controller=animator.runtimeAnimatorController as AnimatorOverrideController;
                if(controller!=null)
                {
                    var mappings=new List<KeyValuePair<AnimationClip,AnimationClip>>();controller.GetOverrides(mappings);
                    borrowedIdleController=new AnimatorOverrideController(controller.runtimeAnimatorController);
                    borrowedIdleController.ApplyOverrides(mappings);borrowedIdleController[donorIdleSlot]=ownIdleClip;
                    Call(native,"SetAnimatorController",borrowedIdleController);
                }
            }
            Call(player,"ഈഅഋഉഈഇഅഇഋഈഈആഈഈആ",seira.costumeTemplate,native);
            animation=Call(native,"GetNKCAnim");borrowedFullSequence=true;
            Debug.Log("[Viewer] Borrowed Seira 1014 full sequence: camera/VFX/animation; target damage voice series="+borrowedDamageVoices.Count);
        }
        private void ApplyBorrowedVoices()
        {
            if(!borrowedFullSequence)return;
            var current=Viewer.Field(player,35432);if(current==null)throw new InvalidOperationException("의상파괴 연출 상태가 없습니다.");
            // Clone state and voice list; never mutate the shared Seira or target template.
            var copy=CopySequenceObject(current);
            var voiceField=SequenceField(copy,"ആഊഌഉഉഉആഅഇആഅഉആഇആ");
            var merged=(IList)Activator.CreateInstance(voiceField.FieldType);
            foreach(var voice in borrowedDamageVoices)merged.Add(CopySequenceObject(voice));
            voiceField.SetValue(copy,merged);
            // Keep Seira's effects/audio, stripping any voice routed via SoundEvents.
            var soundField=SequenceField(copy,"ഇഉഇഇഊഉഇഇഉഅഌആഌഈഌ");
            var sounds=(IList)Activator.CreateInstance(soundField.FieldType);
            var source=soundField.GetValue(current) as IEnumerable;
            if(source!=null)foreach(var sound in source)if(sound!=null&&!(bool)SequenceField(sound,"ഊഋഈഈഅഌഊഇഉആഇഅഈഌആ").GetValue(sound))sounds.Add(sound);
            soundField.SetValue(copy,sounds);
            SequenceField(player,"ആഉഇഈഋഈആഅഈഇഇഅഈഊഅ").SetValue(player,copy);
            SequenceField(player,"ഈഇഅഈഊഈഉഊആഅഋഊഇഋആ").SetValue(player,null);
            Debug.Log("[Viewer] Sequence voice replaced with current character damage VoiceSeries; Seira SFX retained="+sounds.Count);
        }
        private void DisposeBorrowedDestruction(){if(borrowedIdleController!=null)UnityEngine.Object.Destroy(borrowedIdleController);borrowedIdleController=null;borrowedFullSequence=false;borrowedDamageVoices.Clear();}
    }
}
