Shader "AIChatToolkit/DesktopScreen"
{
    Properties
    {
        _MainTex ("Desktop Texture", 2D) = "black" {}
        _SourceAspect ("Source Width / Height", Float) = 1.777778
        _ScreenAspect ("Screen Width / Height", Float) = 1.777778
        [Toggle] _FlipY ("Flip Vertically", Float) = 0
        [Toggle] _FlipX ("Flip Horizontally", Float) = 0
        _Brightness ("Brightness", Range(0, 4)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull Off
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _SourceAspect, _ScreenAspect;
            float _FlipY, _FlipX;
            half _Brightness;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float sourceAspect = max(_SourceAspect, 0.0001);
                float screenAspect = max(_ScreenAspect, 0.0001);
                // Expand the sampled UV range on the axis containing the bars.
                float2 scale = max(float2(screenAspect / sourceAspect,
                                          sourceAspect / screenAspect), 1.0);
                float2 uv = (i.uv - 0.5) * scale + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0))
                    return half4(0, 0, 0, 1);

                uv.x = lerp(uv.x, 1.0 - uv.x, saturate(_FlipX));
                uv.y = lerp(uv.y, 1.0 - uv.y, saturate(_FlipY));
                // The desktop is an ordinary shared texture, not an XR eye array.
                return half4(tex2D(_MainTex, uv).rgb * _Brightness, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
