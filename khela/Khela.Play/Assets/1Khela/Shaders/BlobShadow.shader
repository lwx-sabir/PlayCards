Shader "Khela/BlobShadow"
{
    // A soft, self-contained "blob" shadow for the avatar stage: a flat quad that draws a radial dark oval
    // (opaque-ish centre fading to transparent at the edge). No real lights / shadow-catcher needed, so it works
    // regardless of URP shadow settings and composites cleanly over the transparent render-texture background.
    // Put it on a flat Quad under the character's feet, on the Avatar layer. Tune Color (alpha = strength) + Softness.
    Properties
    {
        _Color ("Color (alpha = strength)", Color) = (0, 0, 0, 0.55)
        _Softness ("Edge Softness", Range(0.001, 1)) = 0.6
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings  { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            float4 _Color;
            float  _Softness;

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // Distance from the quad centre: 0 at centre, 1 at the edge.
                float d = length(IN.uv - 0.5) * 2.0;
                // Fade out toward the rim; Softness widens the falloff.
                float a = 1.0 - smoothstep(1.0 - _Softness, 1.0, d);
                return half4(_Color.rgb, _Color.a * saturate(a));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
