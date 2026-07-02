Shader "BoZo/BakeTexture"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        _BlendTex ("BlendTex", 2D) = "black" {}
        _IDMap ("IDMap", 2D) = "red" {}

        _hasTextureMap("Has Texture Map", Range(0,1)) = 1
        _UsingIDMap("Using ID Map", Range(0,1)) = 1
        _UseCustomColors("UseCustomTexture", Range(0,1)) = 1
        _isNormalMap("isNormalMap", Range(0,1)) = 0

        _BaseColor("BaseColor", Color) = (1, 0, 0, 0)
        [Header(Colors Options)]
        [Space(10)]
        _Color_1("Color_1", Color) = (1, 0, 0, 0)
        _Color_2("Color_2", Color) = (0, 1, 0, 0)
        _Color_3("Color_3", Color) = (0, 0, 1, 0)
        _Color_4("Color_4", Color) = (0.9727613, 1, 0, 0)
        _Color_5("Color_5", Color) = (0, 0.9845986, 1, 0)
        _Color_6("Color_6", Color) = (1, 0, 0.988061, 0)
        _Color_7("Color_7", Color) = (1, 1, 1, 0)
        _Color_8("Color_8", Color) = (0.5031446, 0.5031446, 0.5031446, 0)
        _Color_9("Color_9", Color) = (0, 0, 0, 0)

        [Header(Pattern Options)]
        [Space(10)]
        [NoScaleOffset]_PatternMap("PatternMap", 2D) = "black" {}
        _PatternBlend("PatternBlend", Range(0, 1)) = 0
        _PatternScale("PatternScale", Vector) = (1, 1, 0, 0)
        _PatternColor_1("PatternColor_1", Color) = (0, 0, 0, 0)
        _PatternColor_2("PatternColor_2", Color) = (0, 0, 0, 0)
        _PatternColor_3("PatternColor_3", Color) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType"="transparent" "Queue"="Geometry" }
        Pass
        {
            Name "BakeTexture"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            TEXTURE2D(_BlendTex);
            SAMPLER(sampler_BlendTex);

            TEXTURE2D(_IDMap);
            SAMPLER(sampler_IDMap);

            float4 _BaseColor;
            float _hasTextureMap;
            float _UsingIDMap;
            float _UseCustomColors;
            float _isNormalMap;
            //BaseColor
            float4 _Color_1;
            float4 _Color_2;
            float4 _Color_3;
            float4 _Color_4;
            float4 _Color_5;
            float4 _Color_6;
            float4 _Color_7;
            float4 _Color_8;
            float4 _Color_9;

            //pattern
            sampler2D _PatternMap;
            float4 _PatternMap_ST;
            float  _PatternUVSet;
            float  _PatternBlend;
            float4 _PatternScale;
            float4 _PatternColor_1;
            float4 _PatternColor_2;
            float4 _PatternColor_3;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 vertexColor : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 vertexColor : COLOR;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float2 uv = IN.uv;
                #if UNITY_UV_STARTS_AT_TOP
                    uv.y = 1.0 - uv.y;
                #endif
                OUT.positionHCS = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                OUT.uv = IN.uv;
                OUT.vertexColor = IN.vertexColor;
                return OUT;
            }

            float4 ApplyPattern(float4 tex, float flatTexture, float mask, Varyings i)
            {
                float2 uv = i.uv;
                float2 scaleduv = uv * _PatternScale + ((_PatternScale * -1) / 2) + 0.5;

                float4 pattern = tex2D(_PatternMap, scaleduv);


                float4 color1 = lerp(0, _PatternColor_1, pattern.r);
                float4 color2 = lerp(0, _PatternColor_2, pattern.g);
                float4 color3 = lerp(0, _PatternColor_3, pattern.b);
                
                float4 combine = color1 + color2 + color3;

                float steppedMask = step(0.01, mask);
                steppedMask = steppedMask  * i.vertexColor.x;
                steppedMask = steppedMask * pattern.a;
                steppedMask = lerp(pattern.a, steppedMask, _PatternBlend);
                float4 blend  = lerp(combine, flatTexture * combine, _PatternBlend);

                float4 final = lerp(tex, blend, pattern.w * steppedMask);
                return float4(final.rgb, tex.a + pattern.a);
            }

            float4 CustomColors(float4 tex, float4 vertexColors)
            {
               float4 color1 = lerp(0, _Color_1, tex.x);
               float4 color2 = lerp(0, _Color_2, tex.y);
               float4 color3 = lerp(0, _Color_3, tex.z);
               float4 color4 = lerp(0, _Color_4, tex.x);
               float4 color5 = lerp(0, _Color_5, tex.y);
               float4 color6 = lerp(0, _Color_6, tex.z);
               float4 color7 = lerp(0, _Color_7, tex.x);
               float4 color8 = lerp(0, _Color_8, tex.y);
               float4 color9 = lerp(0, _Color_9, tex.z);

               float4 combine1 = color1 + color2 + color3;
               float4 combine2 = color4 + color5 + color6;
               float4 combine3 = color7 + color8 + color9;

               float4 layer1 = lerp(0, combine1, vertexColors.x);
               float4 layer2 = lerp(layer1, combine2, vertexColors.y);
               float4 layer3 = lerp(layer2, combine3, vertexColors.z);

               return float4(layer3.rgb, tex.a);
            }

            half4 frag(Varyings IN) : SV_Target
            {

                half4 id = SAMPLE_TEXTURE2D(_IDMap, sampler_IDMap, IN.uv);
                half4 map = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half4 blendMap = SAMPLE_TEXTURE2D(_BlendTex, sampler_BlendTex, IN.uv);

                half4 flat = blendMap.x + blendMap.y + blendMap.z;
                half4 tex = CustomColors(blendMap, id); 
                tex = ApplyPattern(tex,flat,blendMap.r,IN);

                tex = lerp(blendMap, tex, _UseCustomColors);

                tex = lerp(map, tex, blendMap.a);


                //Nomral Stuff
                float3 blendN = UnpackNormal(blendMap);
                map = normalize(map);
                float3 enc = blendN * 0.5 + 0.5; 
                float4 col = float4(enc, id.a);

                
                #if defined(SHADER_API_MOBILE)
                    enc += 0.216;
                    col = float4(0, enc.y, 0, enc.x);
                #else
                    col = float4(enc.x, enc.y, enc.z, 1);
                #endif
                
                float3 n = normalize(lerp(map, blendN, id.a)); 
                col.a = lerp(0, id.a * col.a, _hasTextureMap);
                //Normal Stuff ends


                float4 final;
                final.a = lerp(1, id.a , _UsingIDMap);
                final.a = lerp(final.a * _BaseColor.a, tex.a * final.a, _hasTextureMap);
                final.rgb = lerp(_BaseColor.rgb, tex.rgb, _hasTextureMap);

                final = lerp(final, col, _isNormalMap);

                return final;
            }
            ENDHLSL
        }
    }
}
