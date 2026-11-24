using System;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed partial class NativeCharacter
    {
        internal void RestoreIdlePose()
        {
            if(native==null||Model==null||!Model.activeInHierarchy)return;
            // Reactivating a hidden Animator can clear its evaluated pose while
            // the native animation wrapper still caches ASTAND as already playing.
            // Reinitialize that wrapper before freezing the editor's selection mode.
            playing=false;
            Call(native,"ResetVFXEvents");
            if(originalAnimatorController!=null)Call(native,"SetAnimatorController",originalAnimatorController);
            animation=Call(native,"GetNKCAnim");
            player=Activator.CreateInstance(stateType);
            Call(player,"ഈഅഋഉഈഇഅഇഋഈഈആഈഈആ",costumeTemplate,native);
            Change("USN_ASTAND");
            var animator=Call(animation,"GetAnimator") as Animator;
            if(animator!=null&&animator.speed<=0)animator.speed=1;
            for(int frame=0;frame<12;frame++)
            {
                Tick(1f/60);if(animator!=null&&animator.isActiveAndEnabled)animator.Update(1f/60);
                LateTick(1f/60);
            }
            Debug.Log("[Viewer] Original character idle pose restored before editor selection freeze");
        }
    }
}
