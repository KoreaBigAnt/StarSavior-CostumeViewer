using System;

namespace StarSaviorMods
{
    internal static class BodyShapeMath
    {
        internal static float HeightScale(float floor,float neck,float fullHeight,float ratio)
            => 1f+(ratio-1f)*fullHeight/Math.Max(.1f,neck-floor);

        internal static float FullnessProfile(float normalizedHeight)
        {
            float y = normalizedHeight;
            float core = .52f * Bell(y, .59f, .10f) + .64f * Bell(y, .48f, .12f) + .36f * Bell(y, .30f, .16f);
            return Math.Min(1f, core) * Smooth((y - .08f) / .12f) * (1f - Smooth((y - .77f) / .10f));
        }
        internal static float Smooth(float value) { float t = Clamp(value); return t * t * (3f - 2f * t); }
        private static float Clamp(float value) => Math.Max(0f, Math.Min(1f, value));
        private static float Bell(float y, float center, float width) { float x = (y - center) / width; return (float)Math.Exp(-x * x * .5f); }
    }
}
