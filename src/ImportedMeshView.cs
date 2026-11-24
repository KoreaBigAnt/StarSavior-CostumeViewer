using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    public sealed partial class Viewer
    {
        private ImportedMeshSession importedMesh;
        private string meshPickerId;
        private bool MeshPickerBusy{get{return meshPickerId!=null;}}
        private void PickImportedMesh()
        {
            if(loading||exportingProfile||DestructionBusy||MeshPickerBusy||parts==null||parts.PngEditorOpen)return;
            meshPickerId=Guid.NewGuid().ToString("N");
            var request=new JObject{["Id"]=new JValue(meshPickerId),["InitialDirectory"]=new JValue(lastProfile??Path.Combine(root,"Profiles"))};
            try{ProfileFiles.WriteAtomic(Path.Combine(root,"mesh-import-request.json"),System.Text.Encoding.UTF8.GetBytes(request.ToString()));status="Blender 수정 폴더의 mesh-overrides.json을 선택하세요.";}
            catch(Exception failure){meshPickerId=null;status="파일 선택 실패: "+failure.Message;}
        }
        private void PollMeshPicker()
        {
            if(meshPickerId==null)return;string file=Path.Combine(root,"mesh-import-response.json");if(!File.Exists(file))return;
            JObject reply;try{reply=JObject.Parse(File.ReadAllText(file));}catch{return;}
            if((string)reply["Id"]!=meshPickerId)return;File.Delete(file);meshPickerId=null;
            if((bool?)reply["Cancelled"]==true){status="수정 메시 불러오기를 취소했습니다.";return;}
            if(reply["Error"]!=null){status="파일 선택 실패: "+(string)reply["Error"];return;}
            ImportMeshFolder((string)reply["Path"]);
        }
        private bool ImportMeshFolder(string folder)
        {
            bool wasSelecting=parts.SelectionMode;var previous=importedMesh;ImportedMeshSession next=null;
            try
            {
                next=ImportedMeshSession.Snapshot(folder,exporter.Folder,selected);
                parts.SetSelectionMode(false);parts.ClearHover();
                if(previous!=null)previous.Clear(parts);
                next.Apply(model,parts);importedMesh=next;parts.ExternalGeometryChanged();
                status="수정 메시 "+next.Count+"개 불러옴 · 기존 숨김 / PNG와 함께 미리보기 및 최종 ZIP에 포함됩니다.";
                return true;
            }
            catch(Exception failure)
            {
                importedMesh=previous;
                try{parts.ResetExternalGeometry();if(previous!=null)previous.Apply(model,parts);parts.ExternalGeometryChanged();}
                catch(Exception rollback){Debug.LogWarning("[Viewer] Mesh import rollback: "+rollback);}
                status="수정 메시 불러오기 실패: "+failure.Message;Debug.LogWarning("[Viewer] Mesh import: "+failure);
                return false;
            }
            finally{if(wasSelecting&&!parts.SelectionMode)parts.SetSelectionMode(true);}
        }
        private void ClearImportedMesh()
        {
            if(importedMesh==null)return;bool selectedMode=parts.SelectionMode;
            try{parts.SetSelectionMode(false);importedMesh.Clear(parts);importedMesh=null;parts.ExternalGeometryChanged();status="수정 메시 해제 · 원본 형태로 복원했습니다. 숨김 / PNG 편집은 유지됩니다.";}
            catch(Exception failure){status="수정 메시 해제 실패: "+failure.Message;}
            finally{if(selectedMode)parts.SetSelectionMode(true);}
        }
        private void DrawMeshImport()
        {
            if(GUILayout.Button("Blender 수정 메시 불러오기"))PickImportedMesh();
            if(importedMesh!=null)
            {
                GUILayout.Label("수정 메시 "+importedMesh.Count+"개 적용 중 · 최종 ZIP에 포함",text);
                if(GUILayout.Button("수정 메시 해제 (숨김·PNG 유지)"))ClearImportedMesh();
            }
        }
    }
}
