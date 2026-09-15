Shader "Hidden/NeEEvA/SceneVisionGrounding"
{
    Properties { _NevaGroundingCull ("Cull", Float) = 2  _NevaGroundingZTest ("Depth test", Float) = 4 }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull [_NevaGroundingCull]
            ZWrite On
            ZTest [_NevaGroundingZTest]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _NevaGroundingId;
            struct v2f { float4 vertex : SV_POSITION; float eyeDepth : TEXCOORD0; };
            v2f vert(appdata_base v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.eyeDepth = -UnityObjectToViewPos(v.vertex).z;
                return o;
            }
            float2 frag(v2f i) : SV_Target { return float2(i.eyeDepth, _NevaGroundingId); }
            ENDCG
        }
    }
    Fallback Off
}
