using System;
using Newtonsoft.Json.Linq;
using StarSaviorMods;
internal static class BodySettingsCheck
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static int Main()
    {
        try
        {
            var saved = new ChestSettings { Height = 1.2f, Fullness = 1.5f, Size = 1.4f, Physics = true };
            var restored = ChestSettings.Read(saved.ToJson());
            Check(restored.Height == saved.Height && restored.Fullness == saved.Fullness && restored.Size == saved.Size && restored.Physics, "Shape round trip");
            foreach (int version in new[] { 1, 2 })
            {
                var old = saved.ToJson(); old["Version"] = new JValue(version); old.Remove("Height"); old.Remove("Fullness");
                if (version == 1) old["Yaw"] = new JValue(0);
                var compatible = ChestSettings.Read(old);
                Check(compatible.Height == 1f && compatible.Fullness == 0f, "Legacy profile migration");
            }
            var invalid = saved.ToJson(); invalid["Height"] = new JValue(50);
            bool rejected = false; try { ChestSettings.Read(invalid); } catch (System.IO.InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid height accepted");
            foreach(float ratio in new[]{.85f,1f,1.2f})
            {
                float scale=BodyShapeMath.HeightScale(.12f,1.5f,1.8f,ratio);
                Check(scale>0,"Height reverses skeleton");
                Check(Math.Abs((1.5f-.12f)*(scale-1)-(ratio-1)*1.8f)<.00001f,"Height delta differs");
                for(int i=0;i<=200;i++)
                {
                    float p=BodyShapeMath.FullnessProfile(i/100f);
                    Check(p>=0 && p<=1 && !float.IsNaN(p),"Unbounded fullness");
                }
            }
            var excessive=saved.ToJson(); excessive["Fullness"]=new JValue(1.51);
            bool fullnessRejected=false;try{ChestSettings.Read(excessive);}catch(System.IO.InvalidDataException){fullnessRejected=true;}
            Check(fullnessRejected,"Invalid fullness accepted");
            Console.WriteLine("PASS: Shape persistence, legacy profiles, value limits, 150% fullness limits and coherent axial height mapping.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
