using System;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StarSaviorMods;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    public sealed partial class Viewer
    {
        private ChestRig chestPreview;
        private ChestSettings chestDraft, savedChest, thumbnailChest;
        private bool ChestPreviewAvailable => chestPreview != null && chestPreview.Supported &&
            (importedMesh == null || importedMesh.StaticReplacement == null);

        private string ChestSettingsPath(Costume costume) => Path.Combine(root, "ChestSettings", costume.Unit + "_" + costume.Skin + ".json");

        private void LoadChestSettings(Costume costume)
        {
            var path = ChestSettingsPath(costume);
            try { SetSavedChest(File.Exists(path) ? ChestSettings.Read(JObject.Parse(File.ReadAllText(path))) : null); }
            catch (Exception error)
            {
                SetSavedChest(null);
                status = "체형 설정 불러오기 실패 · 기본값으로 열었습니다.";
                Debug.LogWarning("[Viewer] Chest settings: " + error.Message);
            }
        }

        private void SetSavedChest(ChestSettings value)
        {
            float oldHeight = chestDraft?.Height ?? 1f;
            chestPreview?.Suspend();
            savedChest = value;
            chestDraft = value?.Copy() ?? new ChestSettings();
            FitBodyHeight(oldHeight, chestDraft.Height);
        }

        private void SaveChestSettings()
        {
            if (!ChestPreviewAvailable) return;
            try
            {
                var path = ChestSettingsPath(selected);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ProfileFiles.WriteAtomic(path, Encoding.UTF8.GetBytes(chestDraft.ToJson().ToString()));
                savedChest = chestDraft.Copy();
                status = "이 의상의 체형 설정을 저장했습니다.";
            }
            catch (Exception error) { status = "체형 저장 실패 · " + error.Message; }
        }

        private void ResetChestDraft()
        {
            chestPreview?.Suspend();
            chestDraft = new ChestSettings();
        }

        private void FitBodyHeight(float before, float after)
        {
            if(before == after || chestPreview == null || model == null) return;
            distance *= after / before;
            frameTarget += model.transform.up * ((after-before) * chestPreview.BodyHeight * model.transform.lossyScale.y * .5f);
        }

        private void DrawChestPreview()
        {
            GUILayout.Space(12);
            GUILayout.Label("가슴 조절", heading);
            if (!ChestPreviewAvailable)
            {
                GUILayout.Label("현재 모델에서 골격 체형 조절을 사용할 수 없습니다.", text);
                return;
            }
            bool originalEnabled = GUI.enabled;
            GUI.enabled = originalEnabled && chestPreview.ChestSupported;
            chestDraft.Size = DrawChestSlider("크기", chestDraft.Size, ChestSettings.MinSize, ChestSettings.MaxMultiplier);
            chestDraft.UnderbustSpace = DrawChestSlider("밑가슴 공간", chestDraft.UnderbustSpace, 0f, ChestSettings.MaxUnderbustSpace);
            chestDraft.Pitch = DrawChestSlider("상하 방향", chestDraft.Pitch, -ChestSettings.MaxAngle, ChestSettings.MaxAngle, true);
            chestDraft.Spread = DrawChestSlider("모이기 ↔ 벌어지기", chestDraft.Spread, -ChestSettings.MaxSpread, ChestSettings.MaxSpread);
            chestDraft.Physics = GUILayout.Toggle(chestDraft.Physics, "스프링 물리 켜기");
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && chestDraft.Physics;
            chestDraft.Mass = DrawChestSlider("무게", chestDraft.Mass, ChestSettings.MinPhysics, ChestSettings.MaxMultiplier);
            chestDraft.Elasticity = DrawChestSlider("탄력", chestDraft.Elasticity, ChestSettings.MinPhysics, ChestSettings.MaxMultiplier);
            GUI.enabled = originalEnabled;
            GUILayout.Space(16);
            GUILayout.Label("키", heading);
            GUI.enabled = originalEnabled && chestPreview.BodySupported;
            chestDraft.Height = DrawChestSlider("기준 키 대비", chestDraft.Height, ChestSettings.MinHeight, ChestSettings.MaxHeight);
            GUILayout.Space(12);
            GUILayout.Label("살찜", heading);
            chestDraft.Fullness = DrawChestSlider("살찜 정도", chestDraft.Fullness, 0f, ChestSettings.MaxFullness);
            GUI.enabled = originalEnabled;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("저장")) SaveChestSettings();
            if (GUILayout.Button("기본값")) ResetChestDraft();
            GUILayout.EndHorizontal();
        }

        private float DrawChestSlider(string label, float value, float min, float max, bool degrees = false)
        {
            float units = degrees ? 1f : 100f;
            GUILayout.Label(label + "  " + Mathf.RoundToInt(value * units) + (degrees ? "°" : "%"), text);
            return Mathf.Round(GUILayout.HorizontalSlider(value, min, max) * units) / units;
        }

        private void EditChestFromSmoke(JObject command)
        {
            if (!ChestPreviewAvailable) return;
            if ((bool?)command["reset"] == true) ResetChestDraft();
            else
            {
                chestDraft.Size = ReadSlider("size", chestDraft.Size, ChestSettings.MinSize, ChestSettings.MaxMultiplier);
                chestDraft.UnderbustSpace = ReadSlider("underbustSpace", chestDraft.UnderbustSpace, 0f, ChestSettings.MaxUnderbustSpace);
                chestDraft.Pitch = ReadSlider("pitch", chestDraft.Pitch, -ChestSettings.MaxAngle, ChestSettings.MaxAngle);
                chestDraft.Spread = ReadSlider("spread", chestDraft.Spread, -ChestSettings.MaxSpread, ChestSettings.MaxSpread);
                chestDraft.Mass = ReadSlider("mass", chestDraft.Mass, ChestSettings.MinPhysics, ChestSettings.MaxMultiplier);
                chestDraft.Elasticity = ReadSlider("elasticity", chestDraft.Elasticity, ChestSettings.MinPhysics, ChestSettings.MaxMultiplier);
                chestDraft.Height = ReadSlider("height", chestDraft.Height, ChestSettings.MinHeight, ChestSettings.MaxHeight);
                chestDraft.Fullness = ReadSlider("fullness", chestDraft.Fullness, 0f, ChestSettings.MaxFullness);
                chestDraft.Physics = (bool?)command["physics"] ?? chestDraft.Physics;
            }
            if ((bool?)command["save"] == true) SaveChestSettings();
            float ReadSlider(string key, float value, float min, float max) => Mathf.Clamp((float?)command[key] ?? value, min, max);
        }

        private async UniTask CaptureChestStatus()
        {
            await UniTask.WaitForEndOfFrame(this);
            var result = chestDraft?.ToJson() ?? new JObject();
            result["Bones"] = chestPreview?.Diagnostics() ?? new JArray();
            result["Body"] = chestPreview?.BodyDiagnostics() ?? new JObject();
            if (savedChest != null) result["Saved"] = savedChest.ToJson();
            File.WriteAllText(Path.Combine(root, "viewer-chest-status.json"), result.ToString());
        }
    }
}
