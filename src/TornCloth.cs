using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed partial class PartVisibility
    {
        private void LockTearAxis(Vector2 direction)
        {
            if(brush.AxisLocked||direction.sqrMagnitude<1e-8f)return;
            brush.TearAxis=direction.normalized;brush.AxisLocked=true;
            // The initial click may already have been previewed before dragging.
            foreach(var index in brush.Margins.Keys)UpdatePaintPreviewPixel(index);
        }
        private static float ClothTooth(float coordinate,int seed)
        {
            int cell=Mathf.FloorToInt(coordinate);float t=coordinate-cell;
            float tip=.12f+.76f*(RandomValue(cell,7,seed)+1)*.5f;
            float height=.35f+.65f*(RandomValue(cell,31,seed)+1)*.5f;
            return (t<tip?t/tip:(1-t)/(1-tip))*height;
        }
        private float TornEdge(int index,float margin)
        {
            float x=index%brush.Width,y=index/brush.Width;
            var axis=brush.TearAxis;float pitch=brush.Density*Mathf.Max(2,brushToothSpacing);
            float along=(x*axis.x+y*axis.y)/pitch,across=(-x*axis.y+y*axis.x)/pitch;
            // Coarse asymmetric flaps, medium split fibers and fine points share
            // the drag direction. The cross-axis bends them rather than adding dots.
            float bend=.35f*ClothTooth(across*.27f,brush.Seed+53);
            float broad=ClothTooth(along*.29f+bend,brush.Seed);
            float split=ClothTooth(along*.91f+bend*.6f,brush.Seed+17);
            float fine=ClothTooth(along*2.7f+across*.08f,brush.Seed+37);
            float profile=.64f*broad+.27f*split+.09f*fine;
            // Occasional thin incisions break up the repetitive sawtooth shape.
            float riftCoord=along*.43f+bend*.2f;int cell=Mathf.FloorToInt(riftCoord);
            float tip=.2f+.6f*(RandomValue(cell,13,brush.Seed)+1)*.5f;
            float slit=Mathf.Max(0,1-Mathf.Abs(riftCoord-cell-tip)/.055f);
            if(RandomValue(cell,83,brush.Seed)>.25f)profile-=slit*.55f;
            float core=brushRadius*.8f,depth=Mathf.Min(brushRoughness,core);
            float band=Mathf.Clamp01((core-margin)/Mathf.Max(1,core));
            return margin-depth*(profile*1.25f-.14f)*band;
        }
        private Color32 PaintColor(int index,float margin)
        {
            var color=brush.Original[index];float edge=TornEdge(index,margin);
            int alpha=Mathf.RoundToInt(255*(1-Mathf.Clamp01((edge+.2f)/.4f)));
            color.a=(byte)Mathf.Min(color.a,Mathf.Clamp(alpha,0,255));
            // Texture detail exists only on the retained side of this new cut.
            // No alpha is added, and existing transparent edges are never outlined.
            if(!brush.ColorTexture||cutEdgeStrength==0||brushRoughness==0||color.a<128||brush.Original[index].a<240)return color;
            float distance=-edge*brush.Density;if(distance<0||distance>3.5f)return color;
            float x=index%brush.Width,y=index/brush.Width;var axis=brush.TearAxis;
            int fiber=Mathf.FloorToInt((-x*axis.y+y*axis.x)/1.4f);
            int segment=Mathf.FloorToInt((x*axis.x+y*axis.y)/5f);
            float grain=(RandomValue(fiber,segment,brush.Seed+107)+1)*.5f;
            float strength=cutEdgeStrength/100f;
            float rim=Mathf.Clamp01(1-distance/1.1f)*Mathf.Clamp01((grain-.2f)/.8f)*strength*.28f;
            float shadow=Mathf.Max(0,1-Mathf.Abs(distance-1.7f)/1.8f)*(.4f+.6f*grain)*strength*.16f;
            color.r=ClothChannel(color.r,rim,shadow);color.g=ClothChannel(color.g,rim,shadow);color.b=ClothChannel(color.b,rim,shadow);
            return color;
        }
        private static byte ClothChannel(byte value,float rim,float shadow)
        {return (byte)Mathf.Clamp(Mathf.RoundToInt(value*(1-shadow)+(255-value)*rim),0,255);}
    }
}
