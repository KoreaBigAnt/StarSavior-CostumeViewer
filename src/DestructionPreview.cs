using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    public sealed partial class Viewer
    {
        private NativeCharacter destructionPreview;
        private NativeCharacter destructionDonor;
        private bool destructionPreparing,destructionStarted,destructionRestoreSelection,destructionOriginalActive,destructionDedicated;
        private int destructionRequest;
        private float destructionReadyAt;
        private bool DestructionBusy {get{return destructionPreparing||destructionPreview!=null;}}
        private async UniTask StartDestruction()
        {
            if(loading||exportingProfile||MeshPickerBusy||DestructionBusy||character==null||parts==null||parts.PngEditorOpen)return;
            chestPreview?.Suspend();
            int request=++destructionRequest;var owner=character;var costume=selected;
            destructionRestoreSelection=parts.SelectionMode;destructionOriginalActive=model.activeSelf;
            destructionPreparing=true;status="의상파괴: 원본 모델 준비 중…";
            NativeCharacter pending=null,donor=null;
            try
            {
                parts.SetSelectionMode(false);parts.ClearHover();character.Stop();
                pending=new NativeCharacter();await pending.Load(costume,false);
                if(request!=destructionRequest||owner!=character||quitting){pending.Dispose();return;}
                if(!pending.HasNativeDestruction)
                {
                    var seira=costumes.FirstOrDefault(row=>row.Unit==1014&&row.Skin==0);
                    if(seira==null)throw new InvalidOperationException("세이라(1014) 기본 의상을 찾을 수 없습니다.");
                    status="의상파괴: 세이라(1014) 전체 연출 준비 중…";
                    donor=new NativeCharacter();await donor.Load(seira,false);
                    if(request!=destructionRequest||owner!=character||quitting){pending.Dispose();donor.Dispose();return;}
                    await pending.BorrowDestruction(donor);
                    if(request!=destructionRequest||owner!=character||quitting){pending.Dispose();donor.Dispose();return;}
                }
                // Separate assets/materials: no edit snapshots, textures or undo stacks
                // are modified to temporarily show the unedited costume.
                destructionPreview=pending;pending=null;destructionDonor=donor;donor=null;destructionPreparing=false;
                destructionPreview.Damage(false);model.SetActive(false);destructionPreview.Model.SetActive(true);
                destructionStarted=false;destructionReadyAt=Time.realtimeSinceStartup;
                dragButton=-1;status="원본 의상 대기 모션 · 약 3초 후 의상파괴";
            }
            catch(Exception failure)
            {
                if(pending!=null)pending.Dispose();
                if(donor!=null)donor.Dispose();
                if(request==destructionRequest){StopDestruction(true);status="의상파괴 재생 실패: "+failure.Message;}
                Debug.LogWarning("[Viewer] Destruction preview: "+failure);
            }
        }
        private void TickDestruction()
        {
            if(destructionPreview==null)return;
            try
            {
                if(!destructionStarted&&Time.realtimeSinceStartup-destructionReadyAt>=3f)
                {
                    destructionDedicated=destructionPreview.PlayDestruction();destructionStarted=true;
                    status=destructionDedicated?"의상파괴 연출 중 · 종료 후 편집 상태로 복귀":"세이라 전체 연출 · 현재 캐릭터 모델 / 피격 음성";
                }
                destructionPreview.Tick(Time.unscaledDeltaTime);
                if(destructionStarted&&(!destructionPreview.Playing||Time.realtimeSinceStartup-destructionReadyAt>18))
                {
                    bool timedOut=destructionPreview.Playing;StopDestruction(true);
                    status=timedOut?"연출 종료 대기시간 초과 · 편집 상태를 복원했습니다.":"의상파괴 완료 · 현재 편집 상태로 돌아왔습니다.";
                }
            }
            catch(Exception failure){StopDestruction(true);status="의상파괴 중지: "+failure.Message;Debug.LogWarning("[Viewer] Destruction tick: "+failure);}
        }
        private void StopDestruction(bool restoreSelection)
        {
            bool busy=DestructionBusy;++destructionRequest;
            var preview=destructionPreview;var donor=destructionDonor;destructionPreview=null;destructionDonor=null;destructionPreparing=false;destructionStarted=false;
            try{if(preview!=null)preview.Dispose();}
            catch(Exception failure){Debug.LogWarning("[Viewer] Destruction cleanup: "+failure.Message);}
            finally
            {
                try{if(donor!=null)donor.Dispose();}catch(Exception failure){Debug.LogWarning("[Viewer] Destruction donor cleanup: "+failure.Message);}
                if(busy&&model!=null)model.SetActive(destructionOriginalActive);
                bool poseReady=true;
                if(busy&&restoreSelection&&character!=null&&!quitting)
                {
                    try{character.RestoreIdlePose();}
                    catch(Exception failure){poseReady=false;Debug.LogWarning("[Viewer] Idle restore: "+failure);}
                }
                if(busy&&restoreSelection&&poseReady&&destructionRestoreSelection&&parts!=null&&!quitting)parts.SetSelectionMode(true);
                destructionRestoreSelection=false;
            }
        }
    }
}
