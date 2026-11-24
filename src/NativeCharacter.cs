using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Newtonsoft.Json.Linq;

namespace StarSaviorCostumeViewer
{
    internal sealed class Skill : IDisposable
    {
        internal string Name, State, IconKey;
        internal Texture2D Icon;
        internal AsyncOperationHandle<Sprite> Handle;
        internal bool HasHandle;
        internal bool HasMotion {get{return !string.IsNullOrWhiteSpace(State)&&State!="USN_ASTAND";}}
        public void Dispose() { if (HasHandle) Addressables.Release(Handle); if (Icon != null) UnityEngine.Object.Destroy(Icon); }
    }

    // Use the client's own animation/VFX and timed voice-event player.
    internal sealed partial class NativeCharacter : IDisposable
    {
        private static readonly Type modelType = Type.GetType("NKC.NKCComNiloModel, NKC", true);
        private static readonly Type stateType = Type.GetType("ഋഋഈഉഊഊഌആഅഋഌഌഋഅഌ, NKC", true);
        private object bundle, native, player, animation;
        private object costumeTemplate;
        private string costumeBody;
        private AnimatorOverrideController originalAnimatorController;
        private bool playing;
        private float started;
        internal GameObject Model { get; private set; }
        internal readonly List<Skill> Skills = new List<Skill>();
        internal bool Playing { get { return playing; } }
        internal int Completed { get; private set; }
        internal string State { get; private set; }
        internal readonly HashSet<string> PlayedAudio = new HashSet<string>();
        internal float AudioPeak { get; private set; }
        private readonly float[] samples = new float[256];
        private float cameraLog;
        private static async UniTask<object> Box<T>(object task) { return (object)(await (UniTask<T>)task); }
        private static UniTask<object> Await(object task)
        {
            return (UniTask<object>)typeof(NativeCharacter).GetMethod("Box", BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(task.GetType().GetGenericArguments()[0]).Invoke(null, new[] { task });
        }
        private static object Call(object instance, string name, params object[] args)
        {
            for(var type=instance.GetType();type!=null;type=type.BaseType)
            {
                string actualName=GameSymbols.MethodName(type,name);
                var method=type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(value=>value.Name==actualName && value.GetParameters().Length==args.Length);
                if(method!=null) return method.Invoke(instance,args);
            }
            throw new MissingMethodException(instance.GetType().Name,name);
        }
        internal async UniTask Load(Costume costume,bool loadSkills=true)
        {
            costumeTemplate=costume.Template;
            costumeBody=costume.Body;
            var factory = modelType.GetMethods().Single(method => method.Name == "InstantiateByBundleInstanceAndModel" && method.GetParameters().Length == 4);
            var preset = factory.GetParameters()[1].ParameterType.GetGenericArguments()[0];
            // Battle preset includes the complete model, animation controller and attached effects.
            object result = await Await(factory.Invoke(null, new object[] { false, Enum.ToObject(preset, 1), costume.Body, true }));
            bundle = result.GetType().GetField("Item1").GetValue(result);
            native = result.GetType().GetField("Item2").GetValue(result);
            if (native == null) throw new InvalidOperationException("클라이언트의 모델 생성에 실패했습니다.");
            Model = ((Component)native).gameObject;
            if(!loadSkills)Model.SetActive(false);
            Model.transform.position = Vector3.zero;
            Call(native, "SetActive", true);
            Call(native, "SetUseUpdateApp", true);
            Call(native, "SetUseAutoAnim", false);
            player = Activator.CreateInstance(stateType);
            Call(player, "ഈഅഋഉഈഇഅഇഋഈഈആഈഈആ", costume.Template, native);
            animation = Call(native, "GetNKCAnim");
            var originalAnimator=Call(animation,"GetAnimator") as Animator;
            originalAnimatorController=originalAnimator==null?null:originalAnimator.runtimeAnimatorController as AnimatorOverrideController;
            Change("USN_ASTAND");
            if(!loadSkills){Model.SetActive(false);return;}
            var module = costume.Template.GetType().Module;
            object unit = module.ResolveMethod(0x06000000 + 6367).Invoke(null, new object[] { costume.Unit });
            var skillMethod = unit.GetType().GetMethod("അഅഅഌഊഉഌഈആഉഇഋഋഅഋ");
            Type slotType = skillMethod.GetParameters()[0].ParameterType;
            foreach (object slot in Enum.GetValues(slotType))
            {
                object templet = skillMethod.Invoke(unit, new[] { slot });
                if (templet == null) continue;
                string state = (string)Viewer.Field(templet, 2659);
                string icon = (string)Viewer.Field(templet, 2654);
                if (string.IsNullOrWhiteSpace(icon) || Skills.Any(row => row.IconKey == icon)) continue;
                var skill = new Skill { State = state, IconKey = icon, Name = Viewer.Localize((string)Viewer.Field(templet, 2655)) };
                Skills.Add(skill);
                try
                {
                    skill.Handle = Addressables.LoadAssetAsync<Sprite>(icon); skill.HasHandle = true;
                    await skill.Handle.Task;
                    if (skill.Handle.Status == AsyncOperationStatus.Succeeded && skill.Handle.Result != null)
                    {
                        var sprite = skill.Handle.Result;
                        // Sprite atlases may pack several icons into one texture: crop the actual sprite rectangle.
                        skill.Icon = AssetExporter.Crop(sprite.texture, sprite.textureRect);
                    }
                }
                catch (Exception error) { Debug.LogWarning("[Viewer] Skill icon: " + error.Message); }
            }
        }
        private void Change(string state)
        {
            Call(native, "ResetVFXEvents");
            Call(player, "ആഇഇഊഊആഈഇഊഉഉഋഈആഅ", state);
            if(borrowedFullSequence&&state=="USN_ARMOR_BREAK")ApplyBorrowedVoices();
            State = state;
        }
        internal void Play(Skill skill)
        {
            if (!skill.HasMotion) return;
            PlayedAudio.Clear(); AudioPeak=0;
            Change(skill.State); started = Time.realtimeSinceStartup; playing = true;
            cameraLog=started;
            Debug.Log("[Viewer] Skill started " + skill.State);
        }
        internal bool PlayDestruction()
        {
            bool dedicated=HasNativeDestruction;
            if(!dedicated&&!borrowedFullSequence)throw new InvalidOperationException("세이라의 파괴 연출이 준비되지 않았습니다.");
            Play(new Skill{State="USN_ARMOR_BREAK"});
            return dedicated;
        }
        internal void Stop()
        {
            if (native == null || Model == null) return;
            Change("USN_ASTAND"); playing = false;
            Debug.Log("[Viewer] Returned to model view");
        }
        internal void Tick(float delta)
        {
            if (native == null || Model == null) return;
            Call(player, "ആആആഅഅഌഊഅഉഅഈഈഇആഊ", delta);
            if (!(bool)Call(player, "ആഇഅഊഋഌഅഋഉഊഊഅഋഉഌ")) Call(native, "UpdateApp", delta);
            if(playing)
            {
                foreach(var source in Resources.FindObjectsOfTypeAll<AudioSource>())
                    if(source.isPlaying && source.clip!=null) PlayedAudio.Add(source.clip.name);
                AudioListener.GetOutputData(samples,0); foreach(float sample in samples) AudioPeak=Mathf.Max(AudioPeak,Mathf.Abs(sample));
            }
            if (playing && Time.realtimeSinceStartup - started > .3f &&
                ((bool)Call(animation, "ആഉഋഅഌഊഉഈഌഇഌഅഅഊഈ") || Time.realtimeSinceStartup - started > 45))
            { Completed++; Stop(); }
        }
        internal void LateTick(float delta) { if (native != null && Model != null) Call(native, "LateUpdateApp", delta); }
        internal bool ApplyCamera(Camera camera)
        {
            if(!playing || native==null) return false;
            var state=Viewer.Field(player,35432);
            if(state==null) return false;
            var flag=state.GetType().GetField("ഇഈഌഉഈഉഉഊഈഇഇഋഉആഋ",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
            if(flag==null || !(bool)flag.GetValue(state)) return false;
            var eye=(Vector3)Call(native,"ഇഌഉഅഌഉഋഅഋആഈആഊഋഇ");
            var target=(Vector3)Call(native,"ഇഋഅആഊഇആഊഈഇഉആആഅഉ");
            var up=(Vector3)Call(native,"ആഈഉഉഉഌഊഇഌആഌഇഅഌഊ");
            float fov=(float)Call(native,"ഉഇഈഌആഊഊഋആഋആഅഋഊഌ")+(float)Call(native,"get_OffsetFov");
            if((eye-target).sqrMagnitude<.00001f || fov<1 || fov>170) return false;
            var forward=(target-eye).normalized; var right=Vector3.Cross(up,forward);
            var eyeOffset=(Vector3)Call(native,"get_OffsetEyePos"); var lookOffset=(Vector3)Call(native,"get_OffsetLookAt");
            camera.transform.position=eye+right*eyeOffset.x+up*eyeOffset.y+forward*eyeOffset.z;
            camera.transform.LookAt(target+right*lookOffset.x+up*lookOffset.y,up.sqrMagnitude<.1f?Vector3.up:up);
            camera.fieldOfView=fov;
            if(Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_SMOKE")=="1" && Time.realtimeSinceStartup-cameraLog>1)
            {
                cameraLog=Time.realtimeSinceStartup;
                Debug.Log("[Viewer] Skill camera elapsed="+(cameraLog-started)+" eye="+eye+" target="+target+" fov="+fov);
            }
            return true;
        }
        internal JArray Diagnostics()
        {
            var result=new JArray();
            foreach(var controller in Model.GetComponentsInChildren(niloType,true))
            {
                var row=new JObject { ["Name"]=new JValue(controller.name), ["HasDamage"]=new JValue((bool)Call(controller,"HasBreakableGroup")) };
                foreach(var field in niloType.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Where(value=>value.Name.StartsWith("break")))
                {
                    var values=field.GetValue(controller) as System.Collections.ICollection;
                    if(values!=null) row[field.Name]=new JValue(values.Count);
                }
                result.Add(row);
            }
            return result;
        }
        private static readonly Type niloType = Type.GetType("NiloToon.NiloToonURP.NiloToonPerCharacterRenderController, NiloToon.NiloToonURP.Runtime", true);
        internal bool HasDamage { get { return Model != null && Model.GetComponentsInChildren(niloType,true).Any(controller => (bool)Call(controller,"HasBreakableGroup")); } }
        internal void Damage(bool value) { if (Model != null) foreach(var controller in Model.GetComponentsInChildren(niloType,true)) Call(controller,"SetBreakGroupActive",value); }
        public void Dispose()
        {
            if (native != null && Model != null) { Stop(); Call(native, "Unload"); }
            DisposeBorrowedDestruction();
            native=null;
            var disposable = bundle as IDisposable; if (disposable != null) disposable.Dispose();
            bundle = null; Model = null;
            foreach (var skill in Skills) skill.Dispose(); Skills.Clear();
        }
    }
}
