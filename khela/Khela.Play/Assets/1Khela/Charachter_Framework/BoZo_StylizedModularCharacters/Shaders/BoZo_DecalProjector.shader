Shader "Hidden/BoZo_DecalProjector"
{
    Properties
    {
        [MainTexture]_DecalTex ("Decal Texture", 2D) = "transparent" {}
        _SourceTex ("Source Texture", 2D) = "white" {}
        _Color("Color", Color) = (1, 1, 1, 1)
        _EdgeSoftness ("Edge Softness", Range(0, 0.5)) = 0.25
        _BakeOffset ("Bake Z Offset", Range(-1, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType"="transparent" "RenderPipeline"="UniversalPipeline" "Queue" = "Overlay+100" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"



            struct Attributes
            {
                float4 positionOS   : POSITION;
                float2 uv           : TEXCOORD0;
                float3 normalOS     : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS   : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float3 worldPos     : TEXCOORD1;
                float3 worldNormal     : TEXCOORD2;
            };

            sampler2D _DecalTex;
            sampler2D _SourceTex;
            float4x4 _DecalWorldToLocal;
            float _BakeOffset;
            float _EdgeSoftness;
            half4 _Color;

            Varyings vert (Attributes input)
            {
                Varyings output;
                
                output.worldPos = TransformObjectToWorld(input.positionOS.xyz);   
                output.worldNormal = TransformObjectToWorldNormal(input.normalOS);
                float2 uv = input.uv;
                #if UNITY_UV_STARTS_AT_TOP
                    uv.y = 1.0 - uv.y;
                #endif
                output.positionHCS = float4(uv * 2.0 - 1.0, _BakeOffset, 1.0);                
                output.uv = input.uv;
                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                float3 localPos = mul(_DecalWorldToLocal, float4(input.worldPos, 1)).xyz;

                float3 distFromCenter = abs(localPos);
                float3 edgeFade = 1.0 - smoothstep(0.75 - 0.3, 0.75, distFromCenter);
                float totalFade = edgeFade.x * edgeFade.y * edgeFade.z;
               
                float2 decalUV = localPos.xy + 0.5;
                half4 decalCol = tex2D(_DecalTex, decalUV); 
                half4 sourceCol = tex2D(_SourceTex, input.uv); 
                sourceCol = sourceCol.r + sourceCol.g + sourceCol.b;
                //sourceCol += 0.15f;
                sourceCol.a = 1;
                
                float3 projectorForward = -_DecalWorldToLocal[2].xyz; 
                float alignment = dot(normalize(input.worldNormal), projectorForward);
                float alphaFade = saturate(alignment * 0.5); 

                decalCol.a *= alphaFade;
                decalCol.a *= totalFade;
                clip(decalCol.a - 0.001);

                half4 finalCol = decalCol * sourceCol  * _Color;
                return finalCol;
            }
            ENDHLSL
        }
    }
}