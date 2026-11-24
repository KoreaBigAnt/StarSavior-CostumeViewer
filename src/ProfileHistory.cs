using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StarSaviorMods;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    public sealed partial class Viewer
    {
        private sealed class ProfileEntry
        {
            internal string Folder,Name,When;
        }
        private readonly List<ProfileEntry> history=new List<ProfileEntry>();
        private bool historyOpen=true,refreshHistory;
        private int historyPage;
        private string pendingProfileLoad,pendingProfileDelete;
        private static string LocalHistoryTime(string utc)
        {
            DateTime time;
            if (!DateTime.TryParse(utc, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time))
                return utc;
            return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        private void RefreshProfileHistory()
        {
            history.Clear();string folder=Path.Combine(root,"Profiles");if(!Directory.Exists(folder))return;
            foreach(string path in Directory.GetDirectories(folder))
            {
                if(Path.GetFileName(path).StartsWith("."))continue;string file=Path.Combine(path,"appearance-profile.json");if(!File.Exists(file))continue;
                try
                {
                    if(new FileInfo(file).Length>16777216)continue;var data=JObject.Parse(File.ReadAllText(file));if((string)data["Format"]!="StarSavior.AppearanceProfile")continue;
                    history.Add(new ProfileEntry{Folder=path,Name=((string)data["Card"]?["DisplayName"]??"커스텀 의상")+" · "+(int)data["Unit"],When=(string)data["ExportedUtc"]??ProfileFiles.LegacyTime(path)});
                }catch(Exception e){Debug.LogWarning("[Viewer] Profile history: "+e.Message);}
            }
            var sorted=history.OrderByDescending(e=>e.When,StringComparer.Ordinal).ToArray();history.Clear();history.AddRange(sorted);historyPage=Mathf.Clamp(historyPage,0,Mathf.Max(0,(history.Count-1)/3));
        }
        private void DrawProfileHistory()
        {
            if(GUILayout.Button("내보내기 이력 "+history.Count+"개 · "+(historyOpen?"접기":"펼치기")))historyOpen=!historyOpen;
            if(!historyOpen)return;
            if(GUILayout.Button("이력 새로고침"))refreshHistory=true;
            if(history.Count==0){GUILayout.Label("내보낸 프로필이 없습니다.",text);return;}
            bool previous=GUI.enabled;GUI.enabled=previous&&!loading;
            for(int i=historyPage*3;i<Mathf.Min(history.Count,historyPage*3+3);i++)
            {
                var entry=history[i];
                GUILayout.Label(entry.Name+"\n"+LocalHistoryTime(entry.When),text);
                GUILayout.BeginHorizontal();if(GUILayout.Button("불러오기"))pendingProfileLoad=entry.Folder;if(GUILayout.Button("삭제"))pendingProfileDelete=entry.Folder;GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();if(GUILayout.Button("이전"))historyPage=Mathf.Max(0,historyPage-1);GUILayout.Label((historyPage+1)+" / "+Mathf.Max(1,(history.Count+2)/3),text);if(GUILayout.Button("다음"))historyPage=Mathf.Min((history.Count-1)/3,historyPage+1);GUILayout.EndHorizontal();GUI.enabled=previous;
        }
        private string CheckedProfileFolder(string path)
        {
            string profiles=Path.GetFullPath(Path.Combine(root,"Profiles")).TrimEnd(Path.DirectorySeparatorChar),full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if(!string.Equals(Path.GetDirectoryName(full),profiles,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(full).StartsWith("."))throw new InvalidDataException("이력 폴더 경로가 잘못되었습니다.");
            return full;
        }
        private void UpdateProfileHistory()
        {
            if(loading||exportingProfile||MeshPickerBusy)return;
            if(refreshHistory){refreshHistory=false;RefreshProfileHistory();}
            if(pendingProfileDelete!=null)
            {
                string path=pendingProfileDelete;pendingProfileDelete=null;
                try
                {
                    path=CheckedProfileFolder(path);string trash=Path.Combine(root,"Profiles",".Trash");Directory.CreateDirectory(trash);
                    ProfileFiles.MoveFolder(path,Path.Combine(trash,Path.GetFileName(path)+"-"+Guid.NewGuid().ToString("N").Substring(0,6)));
                    if(lastProfile==path)lastProfile=null;RefreshProfileHistory();status="내보내기 이력 삭제 완료 · Profiles/.Trash에 보관";
                }catch(Exception e){status="이력 삭제 실패: "+e.Message;}
            }
            if(pendingProfileLoad!=null){string path=pendingProfileLoad;pendingProfileLoad=null;RestoreHistory(path).Forget();}
        }
        private async UniTask RestoreHistory(string folder)
        {
            try
            {
                folder=CheckedProfileFolder(folder);string file=Path.Combine(folder,"appearance-profile.json");if(new FileInfo(file).Length>16777216)throw new InvalidDataException("프로필 파일이 너무 큽니다.");
                var document=JObject.Parse(File.ReadAllText(file));if((string)document["Format"]!="StarSavior.AppearanceProfile"||(int?)document["Version"]!=1||(int?)document["ClientRevision"]!=277650)throw new InvalidDataException("지원하지 않는 프로필 버전입니다.");
                var profileChest=ChestSettings.Read(document[ChestSettings.ProfileKey]);
                var costume=costumes.SingleOrDefault(c=>c.Unit==(int)document["Unit"]&&c.Skin==(int)document["Skin"]);if(costume==null)throw new InvalidDataException("이 프로필의 캐릭터/의상을 찾을 수 없습니다.");
                await Load(costume);if(parts==null||exporter==null||character==null||selected!=costume)throw new InvalidOperationException("캐릭터를 불러오지 못했습니다.");
                bool savedDamage=(bool?)document["EditorState"]?["Damaged"]??false;if(character.HasDamage)SetDamage(savedDamage);
                var textures=document["EditorTextures"] as JArray;
                if(textures==null)textures=new JArray(exporter.Bindings.Select(b=>Path.GetFileName(b.File)).Distinct().Select(name=>new JObject{["Name"]=new JValue(name),["File"]=new JValue("Textures/"+name)}));
                exporter.RestoreEditorTextures(folder,textures);parts.RestoreProfile(document);
                string meshFile=Path.Combine(folder,"mesh-overrides.json");
                if(File.Exists(meshFile)&&((JArray)JObject.Parse(File.ReadAllText(meshFile))["Meshes"]).Count>0&&!ImportMeshFolder(folder))throw new InvalidDataException(status);
                SetSavedChest(profileChest);
                var view=document["EditorState"]?["Camera"] as JObject;
                if(view!=null)
                {
                    yaw=(float?)view["Yaw"]??yaw;pitch=Mathf.Clamp((float?)view["Pitch"]??pitch,-89,89);distance=Mathf.Clamp((float?)view["Distance"]??distance,.03f,100);
                    var target=view["Target"] as JArray;if(target!=null&&target.Count==3)frameTarget=new Vector3((float)target[0],(float)target[1],(float)target[2]);
                }
                toolTab=profileChest!=null&&ChestPreviewAvailable?1:2;lastProfile=folder;status="내보낸 숨김 / 텍스처 / 묶음 상태 복원 완료\n편집 PNG는 별도 Restored 폴더에 복사했습니다."+(importedMesh==null?"":"\nBlender 수정 메시 "+importedMesh.Count+"개도 복원했습니다.")+(profileChest==null?"":"\n저장된 체형 설정도 복원했습니다.");
            }
            catch(Exception e){status="프로필 불러오기 실패: "+e.Message;Debug.LogWarning("[Viewer] Restore profile: "+e);}
        }
    }
}
