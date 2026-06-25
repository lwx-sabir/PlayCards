// Additive glint OVERLAY for chips. Add this as a SECOND material on a chip renderer (alongside its normal
// URP/Lit material) and it draws the chip mesh again, outputting ONLY a bright band that sweeps the face every
// few seconds — a glint layered on top of the real chip, so the base material (metallic, texture, etc.) is left
// completely untouched. Each chip is phase-offset by its world position so a row/stack shimmers independently.
// Applied at runtime to only the spawned chips you choose (see ChipSheen.Apply) — never on the shared material.
Shader "Khela/ChipSheen"
{
    Properties
    {
        [HDR] _SheenColor  ("Sheen Color", Color) = (1,1,1,1)
        _SheenIntensity    ("Intensity", Range(0,4)) = 1.2
        _SheenWidth        ("Band Width", Range(0.005,0.4)) = 0.07
        _SheenAngle        ("Angle (deg)", Range(0,180)) = 25
        _SheenInterval     ("Glint Interval (s)", Range(0.5,12)) = 3.5
        _SheenDuration     ("Sweep Time (s)", Range(0.1,3)) = 0.6
        _SheenStagger      ("Per-Chip Stagger", Range(0,1)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "SheenOverlay"
            Tags { "LightMode"="UniversalForward" }
            Blend One One       // additive — adds the band, never darkens
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _SheenColor;
                half  _SheenIntensity;
                half  _SheenWidth;
                half  _SheenAngle;
                half  _SheenInterval;
                half  _SheenDuration;
                half  _SheenStagger;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float  phaseOffset : TEXCOORD1;   // per-chip, hashed from world position
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                float3 objPos = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                OUT.phaseOffset = frac(sin(dot(objPos, float3(12.9898, 78.233, 37.719))) * 43758.5453);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float ang = radians(_SheenAngle);
                float2 dir = float2(cos(ang), sin(ang));
                float coord = dot(IN.uv - 0.5, dir);
                float interval = max(_SheenInterval, 0.0001);
                float dur = max(_SheenDuration, 0.0001);
                float t = _Time.y + IN.phaseOffset * interval * _SheenStagger;
                float phase = frac(t / interval) * interval;
                float center = lerp(-0.7, 0.7, saturate(phase / dur));
                float window = step(phase, dur);
                float band = smoothstep(_SheenWidth, 0.0, abs(coord - center)) * window;
                return half4(_SheenColor.rgb * (band * _SheenIntensity), 1.0);   // additive: rgb added, alpha ignored
            }
            ENDHLSL
        }
    }
    FallBack Off
}
