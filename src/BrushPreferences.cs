using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed partial class PartVisibility
    {
        private string brushPreferencesFile;
        private bool editSharedUv=true;
        private void LoadBrushPreferences(string viewerRoot)
        {
            brushPreferencesFile=Path.Combine(viewerRoot,"viewer-brush-settings.json");
            try
            {
                if(!File.Exists(brushPreferencesFile))return;
                var json=JObject.Parse(File.ReadAllText(brushPreferencesFile));
                int radius=Mathf.Clamp((int?)json["BrushRadius"]??20,4,80);
                int depth=Mathf.Clamp((int?)json["TearDepth"]??8,0,40);
                int spacing=Mathf.Clamp((int?)json["ToothSpacing"]??8,2,40);
                int strength=Mathf.Clamp((int?)json["EdgeTextureStrength"]??60,0,100);
                bool linked=(bool?)json["EditSharedUV"]??true;
                brushRadius=radius;brushRoughness=depth;brushToothSpacing=spacing;cutEdgeStrength=strength;editSharedUv=linked;
            }
            catch(Exception error){Debug.LogWarning("[Viewer] Brush settings load: "+error.Message);}
        }
        private void SaveBrushPreferences()
        {
            try
            {
                var json=new JObject{["Version"]=new JValue(1),["BrushRadius"]=new JValue(brushRadius),["TearDepth"]=new JValue(brushRoughness),["ToothSpacing"]=new JValue(brushToothSpacing),["EdgeTextureStrength"]=new JValue(cutEdgeStrength),["EditSharedUV"]=new JValue(editSharedUv)};
                ProfileFiles.WriteAtomic(brushPreferencesFile,Encoding.UTF8.GetBytes(json.ToString()));
            }
            catch(Exception failure){error="붓 설정 저장 실패: "+failure.Message;Debug.LogWarning("[Viewer] Brush settings save: "+failure.Message);}
        }
        private bool CanPaintPixel(int index)
        {byte mask=brush.Coverage[index];return (mask&1)!=0&&(brush.LinkedUv||mask==1);}
    }
}
