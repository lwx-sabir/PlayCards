Shader "Khela/ShadowCatcherAdditional"
{
    // Transparent catcher that renders ONLY the SPOT / additional (punctual) light realtime shadows
    // that fall on a flat ground plane, composited over a transparent RenderTexture-camera background.
    // Use under the AvatarStage so the stage's SPOT light's body-shaped shadow lands on the floor
    // without recolouring the background and WITHOUT competing for URP's single main directional light.
    //
    // URP 17.4 (Unity 6) Forward+ NOTES:
    //  - Forward+ keyword is _CLUSTER_LIGHT_LOOP (the old _FORWARD_PLUS is deprecated).
    //  - LIGHT_LOOP_BEGIN expands to code that references a local named `inputData`, so the
    //    InputData variable MUST be named exactly `inputData`.
    //  - GetAdditionalLight(i, positionWS, shadowMask) is the overload that fills shadowAttenuation.
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
            Name "ShadowCatcherAdditional"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            // Forward+ light loop (URP 17.x). _FORWARD_PLUS is deprecated -> use _CLUSTER_LIGHT_LOOP.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            // Additional (punctual) lights + their realtime shadows + soft PCF.
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Lighting.hlsl transitively pulls in RealtimeLights.hlsl (LIGHT_LOOP_BEGIN/END,
            // GetAdditionalLight, GetAdditionalLightsCount), Shadows.hlsl, and Clustering.hlsl.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
            half4 _ShadowColor;
            half  _Strength;
            CBUFFER_END

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
                // The LIGHT_LOOP_BEGIN macro references this variable BY NAME (`inputData`).
                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionHCS);

                // No baked shadow mask on a runtime catcher -> fully lit (1 = no baked occlusion).
                half4 shadowMask = half4(1, 1, 1, 1);

                // Darkest additional-light contributor wins, so the body silhouette reads as solid shadow.
                half minAtten = 1.0h;

                uint pixelLightCount = GetAdditionalLightsCount(); // 0 under Forward+, loop drives itself
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    // 3-arg overload samples AdditionalLightShadow -> fills light.shadowAttenuation.
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS, shadowMask);

                    // Only let lights that actually reach this pixel (in range / inside the cone) darken it.
                    // distanceAttenuation is a float, so the >0 test is overflow-safe (no half magic constant).
                    half atten = (light.distanceAttenuation > 1e-4) ? light.shadowAttenuation : 1.0h;
                    minAtten = min(minAtten, atten);
                LIGHT_LOOP_END

                half a = saturate((1.0h - minAtten) * _Strength);
                return half4(_ShadowColor.rgb, a); // transparent except under the cast shadow
            }
            ENDHLSL
        }
    }

    Fallback Off
}
