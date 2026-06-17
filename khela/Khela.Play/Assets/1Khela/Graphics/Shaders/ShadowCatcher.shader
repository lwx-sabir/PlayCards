Shader "Khela/ShadowCatcher"
{
    // An invisible ground that renders ONLY the shadows that fall on it, composited over whatever is behind
    // (your gradient BG canvas). Put this on a flat plane under the tables: the BG shows through, the soft
    // shadows from tables / chairs / animated players land on it, and nothing recolours your background.
    Properties
    {
        _ShadowColor ("Shadow Color", Color) = (0, 0, 0, 1)
        _Strength    ("Shadow Strength", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ShadowCatcher"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // URP main-light shadow keywords (so GetMainLight returns real shadow attenuation + soft PCF).
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            half4 _ShadowColor;
            half  _Strength;

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = p.positionCS;
                OUT.positionWS  = p.positionWS;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half atten = mainLight.shadowAttenuation;     // 1 = lit, 0 = fully shadowed
                half a = saturate((1.0h - atten) * _Strength);
                return half4(_ShadowColor.rgb, a);            // transparent everywhere except under shadows
            }
            ENDHLSL
        }
    }

    Fallback Off
}
