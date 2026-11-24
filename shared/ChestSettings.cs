using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace StarSaviorMods
{
    internal sealed class ChestSettings
    {
        internal const string ProfileKey = "ChestShape";
        internal const float MinSize = .80f, MinPhysics = .50f, MaxMultiplier = 3f;
        internal const float MaxAngle = 35f, MaxSpread = 1f, MaxUnderbustSpace = 2f;
        internal const float MinHeight = .85f, MaxHeight = 1.20f, MaxFullness = 1.5f;

        internal float Size = 1f, UnderbustSpace = 1f, Yaw, Pitch, Spread, Mass = 1f, Elasticity = 1f;
        internal bool Physics;
        internal float Height = 1f, Fullness;
        internal bool IsDefault => Size == 1f && UnderbustSpace == 1f && Yaw == 0f && Pitch == 0f && Spread == 0f && !Physics && Height == 1f && Fullness == 0f;

        internal ChestSettings Copy() => (ChestSettings)MemberwiseClone();

        internal JObject ToJson()
        {
            var data = new JObject
            {
                ["Version"] = new JValue(3), ["Size"] = new JValue(Size), ["UnderbustSpace"] = new JValue(UnderbustSpace),
                ["Height"] = new JValue(Height), ["Fullness"] = new JValue(Fullness),
                ["Pitch"] = new JValue(Pitch), ["Spread"] = new JValue(Spread),
                ["Physics"] = new JValue(Physics), ["Mass"] = new JValue(Mass), ["Elasticity"] = new JValue(Elasticity)
            };
            // Preserve the angle of profiles saved before this slider was replaced.
            if (Yaw != 0f) data["Yaw"] = new JValue(Yaw);
            return data;
        }

        internal static ChestSettings Read(JToken token)
        {
            if (token == null) return null;
            var data = token as JObject;
            if (data == null || data["Version"]?.Type != JTokenType.Integer ||
                data["Physics"]?.Type != JTokenType.Boolean)
                throw new InvalidDataException("지원하지 않는 체형 설정 형식입니다.");
            int version = (int)data["Version"];
            if (version < 1 || version > 3) throw new InvalidDataException("지원하지 않는 체형 설정 형식입니다.");
            return new ChestSettings
            {
                Size = ReadNumber(data, "Size", MinSize, MaxMultiplier),
                Height = version < 3 ? 1f : ReadNumber(data, "Height", MinHeight, MaxHeight),
                Fullness = version < 3 ? 0f : ReadNumber(data, "Fullness", 0f, MaxFullness),
                UnderbustSpace = version == 1 ? 1f : ReadNumber(data, "UnderbustSpace", 0f, MaxUnderbustSpace),
                Yaw = version == 1 || data["Yaw"] != null ? ReadNumber(data, "Yaw", -MaxAngle, MaxAngle) : 0f,
                Pitch = ReadNumber(data, "Pitch", -MaxAngle, MaxAngle),
                Spread = ReadNumber(data, "Spread", -MaxSpread, MaxSpread),
                Physics = (bool)data["Physics"],
                Mass = ReadNumber(data, "Mass", MinPhysics, MaxMultiplier),
                Elasticity = ReadNumber(data, "Elasticity", MinPhysics, MaxMultiplier)
            };
        }

        private static float ReadNumber(JObject data, string key, float min, float max)
        {
            var value = data[key];
            if (value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer))
            {
                var number = (float)value;
                if (number >= min && number <= max) return number;
            }
            throw new InvalidDataException("체형 설정 값이 범위를 벗어났습니다. " + key);
        }
    }
}
