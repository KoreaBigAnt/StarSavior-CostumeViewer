using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StarSaviorMods;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace StarSaviorCostumeViewer
{
    public static class Entry
    {
        public static void Start(Func<UniTaskVoid> unused)
        {

            var host = new GameObject("StarSavior Costume Viewer");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<Viewer>();
        }
    }

    internal sealed class Costume
    {
        public int Unit, Skin;
        public string Name, CharacterName, Title, Json, Body, Head;
        public object Template;
    }

    public sealed partial class Viewer : MonoBehaviour
    {
        private readonly List<Costume> costumes = new List<Costume>();
        private Camera camera;
        private GameObject model;
        private Costume selected;
        private Vector2 scroll;
        private string query = "", status = "클라이언트의 의상을 읽는 중…";
        private float yaw = 0, pitch = 0, distance = 3.5f, height = 1;
        private int dragButton=-1;
        private Vector3 lastMouse;
        private bool loading, damaged;
        private GUIStyle text, button, heading;
        private string root;
        private NativeCharacter character;
        private AssetExporter exporter;
        private Font font;
        private bool searchFocus;
        private float nextCommand;
        private PartVisibility parts;
        private Vector2 toolsScroll;
        private int toolTab;
        internal static float RightPanelWidth => Mathf.Min(380f, Mathf.Max(320f, Screen.width * .30f));
        internal static float ViewportRight => Screen.width - RightPanelWidth;
        private static float PanelsWidth => 280f + RightPanelWidth;
        private Vector3 frameTarget;
        private bool quitting;
        private bool exportingProfile;
        private string lastProfile;

        private static IEnumerable<FieldInfo> Fields(Type type)
        {
            for (; type != null; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return field;
        }
        internal static object Field(object value, int row)
        {
            row=GameSymbols.FieldRow(value.GetType(),row);
            return Fields(value.GetType()).Single(field => (field.MetadataToken & 0xffffff) == row).GetValue(value);
        }
        internal static string Localize(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            try {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("ഊഉഅഉഈഉആഋഉഌഈഇഋഅഈ")).First(value => value != null);
                return (string)type.GetMethod("ഋഌആഇഈഅആആഅഌഅഈഊഊഅ").Invoke(null, new object[] { key, new object[0] });
            } catch { return key; }
        }
        private void Start()
        {
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering+=RetainBodyBeforeRender;
            root = Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_ROOT") ?? Application.dataPath;
            Directory.CreateDirectory(Path.Combine(root, "Exports"));
            RefreshProfileHistory();
            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>()) if (canvas.GetComponent<StartupAppearance>() == null) canvas.gameObject.SetActive(false);
            foreach (var old in Camera.allCameras) old.enabled = false;
            foreach(var listener in Resources.FindObjectsOfTypeAll<AudioListener>()) listener.enabled=false;
            camera = new GameObject("Viewer camera").AddComponent<Camera>();
            camera.gameObject.tag = "MainCamera";
            camera.gameObject.AddComponent<AudioListener>();
            var background=new GameObject("Viewer background").AddComponent<Camera>();
            background.depth=-100; background.cullingMask=0; background.clearFlags=CameraClearFlags.SolidColor; background.backgroundColor=new Color(.065f,.08f,.11f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.065f, .08f, .11f);
            camera.fieldOfView = 32;
            camera.nearClipPlane = .01f; camera.farClipPlane = 100;
            var light = new GameObject("Viewer key light").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.1f;
            light.transform.rotation = Quaternion.Euler(35, -35, 0);
            RenderSettings.ambientLight = new Color(.7f, .72f, .76f);
            StartupAppearance.LoadingModel();
            Index();
            if(Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_SMOKE")=="1")
            {
                var keys=Addressables.ResourceLocators.SelectMany(locator=>locator.Keys).OfType<string>().Where(key=>key.IndexOf("STRANIS",StringComparison.OrdinalIgnoreCase)>=0 || key.IndexOf("BREAK",StringComparison.OrdinalIgnoreCase)>=0 || key.IndexOf("DAMAGED",StringComparison.OrdinalIgnoreCase)>=0);
                File.WriteAllText(Path.Combine(root,"asset-keys.json"),new JArray(keys).ToString());
            }
            if (costumes.Count > 0) Load(costumes.FirstOrDefault(row => row.Unit == 1001 && row.Skin == 0) ?? costumes[0]).Forget();
        }
        private void Index()
        {
            var module = Type.GetType("ഉആഇഇഇഈഊഅഅആആഈഊഈആ, Star.Templets", true).Module;
            var metadata = new JArray();
            foreach (int method in new[] { 6060, 6061 })
            foreach (var skin in (IEnumerable)module.ResolveMethod(0x06000000 + method).Invoke(null, null))
            {
                int id = (int)Field(skin, 2831);
                if (id < 1000 || id >= 10000) continue;
                int skinId = (int)Field(skin, 2832);
                if (costumes.Any(row => row.Unit == id && row.Skin == skinId)) continue;
                object unit = module.ResolveMethod(0x06000000 + 6367).Invoke(null, new object[] { id });
                var row = new Costume { Unit = id, Skin = skinId, Name = Localize((string)Field(skin, 2835)), Template = skin,
                    CharacterName = unit == null ? id.ToString() : Localize((string)Field(unit, 2969)), Title = unit == null ? "" : Localize((string)Field(unit, 2968)),
                    Json = (string)Field(skin, 2837), Body = (string)Field(skin, 2842), Head = (string)Field(skin, 2843) };
                costumes.Add(row);
                metadata.Add(new JObject { ["Unit"] = new JValue(id), ["Skin"] = new JValue(skinId),
                    ["Name"] = new JValue(row.Name), ["CharacterName"] = new JValue(row.CharacterName), ["Title"] = new JValue(row.Title),
                    ["Json"] = new JValue(row.Json), ["Body"] = new JValue(row.Body), ["Head"] = new JValue(row.Head) });
            }
            costumes.Sort((a,b) => a.Unit == b.Unit ? a.Skin.CompareTo(b.Skin) : a.Unit.CompareTo(b.Unit));
            File.WriteAllText(Path.Combine(root, "costumes.json"), metadata.ToString());
            Debug.Log("[Viewer] Indexed costumes=" + costumes.Count);
        }
        private async UniTask Load(Costume row)
        {
            if (loading||exportingProfile||MeshPickerBusy) return;
            StopDestruction(false);
            loading = true; status = "모델 불러오는 중…";
            try
            {
                chestPreview?.Suspend();
                chestPreview = null;
                chestDraft = savedChest = null;
                if (parts != null) { parts.Dispose(); parts = null; }
                importedMesh=null;
                if (exporter != null) { exporter.Dispose(); exporter = null; }
                if (character != null) { character.Dispose(); character = null; model = null; }
                if (model != null) Destroy(model);
                selected = row;
                Debug.Log("[Viewer] Loading " + row.Unit + ":" + row.Skin + " body=" + row.Body + " json=" + row.Json + " head=" + row.Head);
                string folder = Path.Combine(root, "Exports", row.Unit + "_" + row.Skin);
                Directory.CreateDirectory(folder);
                if (!string.IsNullOrEmpty(row.Json))
                {
                    var document = Addressables.LoadAssetAsync<TextAsset>(row.Json);
                    await document.Task;
                    if (document.Status == AsyncOperationStatus.Succeeded)
                        File.WriteAllText(Path.Combine(folder, "unit-source.txt"), document.Result.text);
                    Addressables.Release(document);
                }
                character = new NativeCharacter();
                await character.Load(row);
                model = character.Model;
                model.name = "Viewed character";
                var renderers = model.GetComponentsInChildren<Renderer>(true);
                var details = new JArray();
                foreach (var renderer in renderers) details.Add(new JObject { ["Object"] = new JValue(renderer.name),
                    ["Type"] = new JValue(renderer.GetType().Name), ["Active"] = new JValue(renderer.gameObject.activeSelf) });
                File.WriteAllText(Path.Combine(folder, "renderers.json"), details.ToString());
                Debug.Log("[Viewer] Loaded renderers=" + renderers.Length);
                for(int step=0;step<12;step++) {character.Tick(1f/60); character.LateTick(1f/60); await UniTask.Yield();}
                Frame();
                exporter = new AssetExporter();
                exporter.Export(model, folder, "normal");
                damaged = false;
                if (character.HasDamage) { character.Damage(true); exporter.ExportMesh(model, folder, "damaged"); character.Damage(false); }
                status = "의상 편집 준비 완료 · " + exporter.Bindings.Select(binding => binding.File).Distinct().Count() + "개 텍스처";
                parts = new PartVisibility(model, folder,exporter.Bindings,root);
                parts.TextureEdited=(file,enabled)=>exporter.EnablePaintAlpha(file,enabled);
                parts.PaintPreview=(file,texture)=>exporter.ShowPaintPreview(file,texture);
                parts.OutlineModeChanged=force=>exporter.SetOutlineSuppression(force);
                exporter.PartFilter=parts;
                chestPreview = new ChestRig(model);
                LoadChestSettings(row);
                parts.BeforeSelection = () => { chestPreview.Suspend(); if(ChestPreviewAvailable)chestPreview.Apply(chestDraft, 0f, false); };
                if(Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_SMOKE")=="1")
                {
                    var survey=new JArray();
                    foreach(var renderer in AssetExporter.Renderers(model).OfType<SkinnedMeshRenderer>())
                    {
                        var mesh=renderer.sharedMesh; var clone=Instantiate(mesh); var desc=clone.GetSubMesh(0); int count=desc.indexCount;
                        object boxed=desc; typeof(UnityEngine.Rendering.SubMeshDescriptor).GetField("<indexCount>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(boxed,0);desc=(UnityEngine.Rendering.SubMeshDescriptor)boxed;
                        clone.SetSubMesh(0,desc,UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds|UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);
                        survey.Add(new JObject{["Name"]=new JValue(renderer.name),["Readable"]=new JValue(mesh.isReadable),["IndexCount"]=new JValue(count),["UpdatedIndexCount"]=new JValue(clone.GetSubMesh(0).indexCount)});Destroy(clone);
                    }
                    File.WriteAllText(Path.Combine(folder,"mesh-survey.json"),survey.ToString());
                }
                foreach(var source in Resources.FindObjectsOfTypeAll<AudioSource>()) if(source.clip!=null && source.clip.name.StartsWith("MUSIC_")) source.Stop();
                File.WriteAllText(Path.Combine(folder,"controllers.json"),character.Diagnostics().ToString());
                File.WriteAllText(Path.Combine(root, "viewer-ready.json"), new JObject { ["Unit"] = new JValue(row.Unit), ["Skin"] = new JValue(row.Skin),
                    ["Renderers"] = new JValue(renderers.Length), ["Textures"] = new JValue(exporter.Bindings.Count), ["Skills"] = new JValue(character.Skills.Count),
                    ["LoadedIcons"] = new JValue(character.Skills.Count(skill=>skill.Icon!=null)), ["Parts"]=new JValue(parts.Parts.Count),
                    ["Damage"] = new JValue(character.HasDamage), ["Vertices"] = new JValue(exporter.VertexCount) }.ToString());
            }
            catch (Exception error) { status = error.Message; Debug.LogException(error); }
            finally { loading = false; StartupAppearance.Finish(); }
        }
        private void Frame()
        {
            if (model == null) return;
            var visible = importedMesh!=null && importedMesh.StaticReplacement!=null
                ? model.GetComponentsInChildren<StarSaviorMods.StaticModelReplacement>().Select(node=>node.GetComponent<Renderer>()).Where(row=>row!=null).ToArray()
                : AssetExporter.Renderers(model).Where(row => row.enabled && !row.forceRenderingOff && row.gameObject.activeInHierarchy).ToArray();
            if (visible.Length == 0) return;
            var bounds = visible[0].bounds;
            foreach (var renderer in visible) bounds.Encapsulate(renderer.bounds);
            height = bounds.center.y;
            frameTarget=bounds.center;
            yaw=0; pitch=0; dragButton=-1;
            float aspect=Mathf.Max(.3f,(Screen.width-PanelsWidth)/Screen.height);
            distance = Mathf.Max(1,Mathf.Max(bounds.size.y,bounds.size.x/aspect)/(2*Mathf.Tan(16*.0174532925f))*1.12f);
        }
        private void Update()
        {
            if (camera == null) return;
            chestPreview?.Restore();
            PollMeshPicker();
            UpdateProfileHistory();
            if(parts!=null&&parts.SelectionMode&&character!=null&&character.Playing)parts.SetSelectionMode(false);
            bool selecting=parts!=null&&parts.SelectionMode;
            if(DestructionBusy)TickDestruction();
            else if (character != null && !loading&&!selecting) character.Tick(Time.unscaledDeltaTime);
            if (exporter != null) exporter.Tick();
            if(Input.GetKeyDown(KeyCode.Escape)&&DestructionBusy){StopDestruction(true);status="의상파괴 중지 · 편집 상태 복원";}
            else if (Input.GetKeyDown(KeyCode.Escape) && character != null&&!selecting) character.Stop();
            Commands();
            camera.rect = new Rect(280f / Screen.width, 0, Mathf.Max(.2f, 1 - PanelsWidth / Screen.width), 1);
            bool viewport=!MeshPickerBusy&&!exportingProfile&&!DestructionBusy&&(parts==null||!parts.PngEditorOpen)&&Input.mousePosition.x > 280 && Input.mousePosition.x < ViewportRight;
            if(parts!=null&&parts.PngEditorOpen)dragButton=-1;
            if(DestructionBusy||character!=null && character.Playing) dragButton=-1;
            else
            {
                if(viewport && (Input.GetMouseButtonDown(selecting?1:0)||Input.GetMouseButtonDown(2)))
                {dragButton=Input.GetMouseButtonDown(2)?2:selecting?1:0;lastMouse=Input.mousePosition;}
                if(dragButton>=0)
                {
                    if(!Input.GetMouseButton(dragButton)) dragButton=-1;
                    else {var delta=Input.mousePosition-lastMouse;CameraInput(dragButton==1?0:dragButton,delta.x,delta.y,0);lastMouse=Input.mousePosition;}
                }
                if(viewport) CameraInput(-1,0,0,Input.mouseScrollDelta.y);
                if(!searchFocus && Input.GetKeyDown(KeyCode.F)) Frame();
            }
            var target = frameTarget;
            camera.transform.position = target + Quaternion.Euler(pitch, yaw, 0) * new Vector3(0, 0, distance);
            camera.transform.LookAt(target);
        }
        private void CameraInput(int button,float dx,float dy,float wheel)
        {
            var rotation=Quaternion.Euler(pitch,yaw,0);
            if(button==2)
            {
                float perPixel=2*distance*Mathf.Tan(16*.0174532925f)/Mathf.Max(1,Screen.height);
                var screenRight=-(rotation*Vector3.right);
                var screenUp=rotation*Vector3.up;
                frameTarget-=screenRight*(dx*perPixel)+screenUp*(dy*perPixel);
            }
            else if(button==0) {yaw+=dx*.3f;pitch=Mathf.Clamp(pitch-dy*.3f,-89,89);}
            distance=Mathf.Clamp(distance*Mathf.Exp(-wheel*.15f),.03f,100);
        }
        private void OnApplicationFocus(bool focused) {if(!focused) dragButton=-1;}
        private void LateUpdate()
        {
            if (character == null || loading) return;
            RestoreReviewPose();
            if(destructionPreview!=null)
            {
                destructionPreview.LateTick(Time.unscaledDeltaTime);
                if(destructionPreview.ApplyCamera(camera))
                {float width=Mathf.Max(.2f,1-PanelsWidth/Screen.width),viewportHeight=width*Screen.width/(Screen.height*(16f/9));camera.rect=new Rect(280f/Screen.width,(1-viewportHeight)/2,width,viewportHeight);}
                else camera.fieldOfView=32;
                return;
            }
            if(destructionPreparing)return;
            if(parts==null||!parts.SelectionMode)character.LateTick(Time.unscaledDeltaTime);
            if (ChestPreviewAvailable && thumbnailChest != null)
                chestPreview.Apply(thumbnailChest, 0f, false);
            else if (ChestPreviewAvailable && !exportingProfile)
                chestPreview.Apply(chestDraft, Time.unscaledDeltaTime, parts == null || !parts.SelectionMode);
            else chestPreview?.Suspend();
            if(character.ApplyCamera(camera))
            {
                float width=Mathf.Max(.2f,1-PanelsWidth/Screen.width), viewportHeight=width*Screen.width/(Screen.height*(16f/9));
                camera.rect=new Rect(280f/Screen.width,(1-viewportHeight)/2,width,viewportHeight);
            }
            else camera.fieldOfView=32;
            if(parts!=null&&!MeshPickerBusy&&!exportingProfile){parts.UpdateModelSelection(camera);parts.TickHover();}
        }
        private void SetDamage(bool value)
        {
            if (character == null || !character.HasDamage) { status = "이 의상에는 손상 상태가 없습니다."; return; }
            chestPreview?.Suspend();
            if(parts!=null)parts.SetSelectionMode(false);
            damaged = value; character.Damage(value);
        }
        private async UniTask ExportProfile(bool modZip=false)
        {
            if(loading||exportingProfile||MeshPickerBusy||DestructionBusy||model==null||parts==null||exporter==null)return;
            exportingProfile=true;status="원본 메시 / 리깅 / 텍스처 내보내는 중…";
            chestPreview?.Suspend();
            try
            {
                var exportedChest = ChestPreviewAvailable ? chestDraft?.Copy() : null;
                lastProfile=await AppearanceProfile.Export(root,selected,model,parts,exporter,new JObject{["Damaged"]=new JValue(damaged),["Camera"]=new JObject{["Yaw"]=new JValue(yaw),["Pitch"]=new JValue(pitch),["Distance"]=new JValue(distance),["Target"]=new JArray(new float[]{frameTarget.x,frameTarget.y,frameTarget.z})}},importedMesh,chestPreview,exportedChest);
                thumbnailChest = exportedChest;
                if (thumbnailChest != null) await UniTask.NextFrame();
                await ProfileThumbnail(lastProfile);RefreshProfileHistory();
                string output=modZip?ModPackage.Zip(lastProfile):lastProfile;
                var result=JObject.Parse(File.ReadAllText(Path.Combine(lastProfile,"MeshEdit/mesh-index.json")));
                status="모드용 프로필 저장: "+output+((bool)result["Complete"]?"":"\n일부 원본 메시 추출 실패: MeshEdit/mesh-index.json 확인");
                if(Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_SMOKE")=="1")File.WriteAllText(Path.Combine(root,"viewer-profile-result.json"),new JObject{["Path"]=new JValue(lastProfile),["Output"]=new JValue(output),["MeshEditingComplete"]=result["Complete"].DeepClone()}.ToString());
            }
            catch(Exception error){status=error.Message;Debug.LogException(error);}
            finally{thumbnailChest=null;chestPreview?.Suspend();exportingProfile=false;}
        }
        private async UniTask ProfileThumbnail(string folder)
        {
            await UniTask.WaitForEndOfFrame(this);
            if(!Directory.Exists(folder))return;
            var rect=camera.pixelRect;
            var image=new Texture2D((int)rect.width,(int)rect.height,TextureFormat.RGBA32,false);
            try{image.ReadPixels(rect,0,0,false);image.Apply(false,false);File.WriteAllBytes(Path.Combine(folder,"thumbnail.png"),AssetExporter.EncodePng(image));}
            catch(Exception error){Debug.LogWarning("[Viewer] Thumbnail: "+error.Message);}
            finally{Destroy(image);}
        }
        private void OnApplicationQuit() { quitting=true; }
        private void RetainBodyBeforeRender(UnityEngine.Rendering.ScriptableRenderContext context,Camera renderingCamera)
        {
            if(renderingCamera==camera&&!loading)chestPreview?.RetainAppliedPose();
        }
        private void OnDestroy() { UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering-=RetainBodyBeforeRender;chestPreview?.Suspend();StopDestruction(false);if(parts!=null) parts.Dispose(); if (exporter != null) exporter.Dispose(); if (!quitting && character != null) character.Dispose(); }
        // Developer smoke control: only enabled by --smoke, no game or account operations.
        private void Commands()
        {
            if (Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_SMOKE") != "1" || loading || Time.realtimeSinceStartup < nextCommand) return;
            nextCommand = Time.realtimeSinceStartup + .3f;
            string path = Path.Combine(root, "viewer-command.json"); if (!File.Exists(path)) return;
            try
            {
                var command = JObject.Parse(File.ReadAllText(path)); File.Delete(path);
                string action = (string)command["action"];
                if(action=="restore-profile"){RestoreHistory((string)command["folder"]).Forget();return;}
                if (action == "load") Load(costumes.Single(row => row.Unit == (int)command["unit"] && row.Skin == ((int?)command["skin"] ?? 0))).Forget();
                else if (action == "damage") SetDamage((bool)command["value"]);
                else if (action == "skill") {StopDestruction(true); character.Play(character.Skills[(int)command["index"]]); if(command["capture"]!=null) { Capture((string)command["capture"]+"-early",800).Forget(); Capture((string)command["capture"]+"-mid",1800).Forget(); Capture((string)command["capture"]+"-late",3500).Forget(); } }
                else if(action=="destruction")StartDestruction().Forget();
                else if (action == "stop") {StopDestruction(true);character.Stop();}
                else if (action == "part") parts.Set((int)command["index"], (bool)command["visible"]);
                else if(action=="pieces") parts.Analyze((int)command["index"]);
                else if(action=="piece") parts.SetPiece((int)command["parent"],(int)command["index"],(bool)command["visible"]);
                else if(action=="showAll") parts.ShowAll();
                else if(action=="selection") {if(!(bool)command["value"])reviewPose=null;parts.SetSelectionMode((bool)command["value"]);}
                else if(action=="body-review") ReviewBody((string)command["name"],(bool?)command["frame"]??true).Forget();
                else if(action=="profile") ExportProfile((bool?)command["zip"]??false).Forget();
                else if(action=="history-load") RestoreHistory((string)command["folder"]).Forget();
                else if(action=="import-mesh") ImportMeshFolder((string)command["folder"]);
                else if (action == "capture") Capture((string)command["name"]).Forget();
                else if (action == "tab") toolTab=(int)command["value"];
                else if (action == "chest") EditChestFromSmoke(command);
                else if (action == "chest-status") CaptureChestStatus().Forget();
                else if(action=="camera") CameraInput((int?)command["button"]??-1,(float?)command["dx"]??0,(float?)command["dy"]??0,(float?)command["wheel"]??0);
                else if(action=="frame") Frame();
                else if (action == "status") File.WriteAllText(Path.Combine(root, "viewer-status.json"), new JObject {
                    ["Unit"] = new JValue(selected.Unit), ["Skin"] = new JValue(selected.Skin), ["Damage"] = new JValue(damaged),
                    ["Status"]=new JValue(status), ["ImportedMeshCount"]=new JValue(importedMesh==null?0:importedMesh.Count),
                    ["MeshHashes"]=MeshHashes(),
                    ["Playing"] = new JValue(character.Playing), ["State"] = new JValue(character.State), ["Completed"] = new JValue(character.Completed),
                    ["Reloads"] = new JValue(exporter == null ? 0 : exporter.ReloadCount), ["LastReload"] = new JValue(exporter == null ? "" : exporter.LastReload),
                    ["AudioPeak"] = new JValue(character.AudioPeak), ["AudioClips"] = new JArray(character.PlayedAudio),
                    ["Camera"] = new JObject { ["Yaw"]=new JValue(yaw),["Pitch"]=new JValue(pitch),["Distance"]=new JValue(distance),["Target"]=new JArray(new float[]{frameTarget.x,frameTarget.y,frameTarget.z}),["Position"]=new JArray(new float[]{camera.transform.position.x,camera.transform.position.y,camera.transform.position.z}) },
                    ["Parts"] = new JArray(parts.Parts.Select(part=>new JObject{["Id"]=new JValue(part.Id),["Visible"]=new JValue(part.Visible),["Pieces"]=new JValue(part.Detail==null?0:part.Detail.Slots[part.GeometrySlot].Count),["HiddenPieces"]=new JValue(part.Detail==null?0:part.Detail.Slots[part.GeometrySlot].Count(piece=>!piece.Visible))})),
                    ["Skills"] = new JArray(character.Skills.Select(skill => new JObject { ["Name"] = new JValue(skill.Name), ["State"] = new JValue(skill.State), ["Icon"] = new JValue(skill.IconKey) })) }.ToString());
                else if (action == "quit") Application.Quit();
            }
            catch(Exception error) { Debug.LogException(error); }
        }
        private JObject MeshHashes()
        {
            var result=new JObject();
            foreach(var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh=renderer.sharedMesh;if(mesh==null||!mesh.isReadable)continue;
                using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream))
                {foreach(var vertex in mesh.vertices){writer.Write(vertex.x);writer.Write(vertex.y);writer.Write(vertex.z);}writer.Flush();using(var hash=System.Security.Cryptography.SHA256.Create())result[renderer.name]=new JValue(BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-",""));}
            }
            return result;
        }
        private async UniTask Capture(string name, int delay=300)
        {
            await UniTask.Delay(delay, true);
            // Capture the actual player framebuffer after rendering, including the native shader output.
            await UniTask.WaitForEndOfFrame(this);
            var image = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0,false); image.Apply(false,false);
            File.WriteAllBytes(Path.Combine(root, AssetExporter.Safe(name) + ".png"), AssetExporter.EncodePng(image)); Destroy(image);
        }
        private void OnGUI()
        {
            if (text == null)
            {
                font = Font.CreateDynamicFontFromOSFont("Malgun Gothic", 16);
                GUI.skin.font = font;
                text = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
                button = new GUIStyle(GUI.skin.button) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
                heading = new GUIStyle(text) { fontSize = 22, fontStyle = FontStyle.Bold };
            }
            GUI.skin.font = font;
            GUI.skin.button.fontSize = 15;
            GUI.skin.button.wordWrap = true;
            bool wasEnabled=GUI.enabled;
            GUI.enabled=wasEnabled&&!MeshPickerBusy&&!exportingProfile&&!DestructionBusy&&(parts==null||!parts.PngEditorOpen);
            if(parts!=null)parts.DrawSelectionBox();
            if(parts!=null)parts.DrawPaintCursor();
            GUI.Box(new Rect(0,0,280,Screen.height), "");
            GUILayout.BeginArea(new Rect(16,16,248,Screen.height-32));
            GUILayout.Label("의상 편집 뷰어", heading);
            DrawProfileHistory();
            GUILayout.Label(costumes.Select(row => row.Unit).Distinct().Count() + " 캐릭터 · " + costumes.Count + " 의상", text);
            if (GUILayout.Button(searchFocus ? "검색: " + query + " ▌" : "검색: " + (query.Length == 0 ? "이름 / 번호" : query), GUILayout.Height(34))) searchFocus = !searchFocus;
            if (searchFocus && Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Backspace) { if (query.Length > 0) query = query.Substring(0, query.Length-1); }
                else if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.Escape) searchFocus = false;
                else if (!char.IsControl(Event.current.character)) query += Event.current.character;
                Event.current.Use();
            }
            if (query.Length > 0 && GUILayout.Button("검색 지우기")) query = "";
            scroll = GUILayout.BeginScrollView(scroll);
            foreach (var group in costumes.Where(row => string.IsNullOrEmpty(query) || row.Unit.ToString().Contains(query) || (row.Name ?? "").Contains(query) || (row.CharacterName ?? "").Contains(query)).GroupBy(row => row.Unit))
            {
                GUILayout.Label(group.First().CharacterName + " · " + group.Key, text);
                foreach (var row in group)
                    if (GUILayout.Button((selected == row ? "● " : "") + (row.Skin == 0 ? "기본 의상" : string.IsNullOrEmpty(row.Name) ? "의상 " + row.Skin : row.Name), GUILayout.Height(34))) Load(row).Forget();
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
            GUI.Box(new Rect(ViewportRight,0,RightPanelWidth,Screen.height), "");
            GUILayout.BeginArea(new Rect(ViewportRight+16,20,RightPanelWidth-32,Screen.height-40));
            toolsScroll = GUILayout.BeginScrollView(toolsScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView, GUILayout.Width(RightPanelWidth-32));
            GUILayout.BeginVertical(GUIStyle.none, GUILayout.Width(RightPanelWidth-52));
            GUILayout.Label(selected == null ? "모델 선택" : selected.CharacterName, heading);
            if (GUILayout.Button("화면 맞춤")) Frame();
            if (character != null && !loading && character.HasDamage && GUILayout.Button(damaged ? "기본 상태" : "손상 의상"))
            {
                SetDamage(!damaged);
            }
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("스킬")) {toolTab=0;toolsScroll=Vector2.zero;parts?.SetSelectionMode(false);}
            if(GUILayout.Button("체형")) {toolTab=1;toolsScroll=Vector2.zero;parts?.SetSelectionMode(false);}
            if(GUILayout.Button("부위")) {toolTab=2;toolsScroll=Vector2.zero;}
            GUILayout.EndHorizontal();
            if (character != null && !loading && toolTab==0)
            {
                GUILayout.Space(16); GUILayout.Label("스킬 연출", heading);
                GUILayout.Label("아이콘 클릭 · 음성 / 모션 / 이펙트\nEsc: 연출 중지", text);
                foreach (var skill in character.Skills.Where(skill=>skill.HasMotion))
                {
                    GUILayout.BeginHorizontal();
                    if (skill.Icon != null) { var rect = GUILayoutUtility.GetRect(56,56,GUI.skin.button,new GUILayoutOption[] {GUILayout.Width(56), GUILayout.Height(56)}); if (GUI.Button(rect,new GUIContent("",skill.Icon,skill.Name),GUI.skin.button)) character.Play(skill); }
                    if (GUILayout.Button(string.IsNullOrEmpty(skill.Name)?skill.State:skill.Name, GUILayout.Height(56))) character.Play(skill);
                    GUILayout.EndHorizontal();
                }
                if(GUILayout.Button("의상파괴",GUILayout.Height(42)))StartDestruction().Forget();
                if(DestructionBusy)
                {
                    GUI.enabled=wasEnabled;
                    if(GUILayout.Button("의상파괴 중지 / 편집 상태로 복귀")){StopDestruction(true);status="편집 상태 복원 완료";}
                    GUI.enabled=false;
                }
                else if (character.Playing && GUILayout.Button("연출 중지 / 모델로 돌아가기")) character.Stop();
            }
            if (parts != null && !loading && toolTab==2) {DrawMeshImport();if(GUILayout.Button("외부 편집용 / 통합 프로필 내보내기"))ExportProfile().Forget();if(GUILayout.Button("BepInEx 최종 ZIP 내보내기"))ExportProfile(true).Forget();parts.Draw(text, heading);}
            if (model != null && !loading && toolTab == 1) DrawChestPreview();
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.enabled=wasEnabled;
            if(parts!=null)parts.DrawPngEditor(text,heading);
        }
    }
}
