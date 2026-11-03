// The burnt rim of a fire cut (Zandatsu), drawn on the cut face itself: a distance field (_BurnField, made by the
// slice) says how far in from the outline and where along it each spot lies, and the burn line (_BaseMap) is read
// there, blended by its alpha so the flesh shows between its ragged teeth. Lit like the game's own materials (its light probe volumes
// on assembled levels, Unity probes and lights elsewhere), with embers in the cracks that glow and die down (_Glow 1..0,
// driven from code). Single pass instanced stereo.
Shader "Zandatsu/BurnEdge"
{
    Properties
    {
        _BaseMap("Burn Edge", 2D) = "white" {}
        [NoScaleOffset] _BurnField("Distance Field", 2D) = "black" {}
        _BaseColor("Tint", Color) = (1,1,1,1)
        _Glow("Glow", Range(0, 1)) = 0
        [HDR] _EmberColor("Ember", Color) = (4, 1.5, 0.35, 1)
        [HDR] _HeatColor("Heat", Color) = (0.9, 0.5, 0.3, 1)
        _Smoothness("Smoothness", Range(0, 1)) = 0.15

        [HideInInspector] _UseProbeVolume("Use Probe Volume", Float) = 0
        [HideInInspector][NoScaleOffset] _ProbeVolumeShR("Probe Volume SH Red", 3D) = "black" {}
        [HideInInspector][NoScaleOffset] _ProbeVolumeShG("Probe Volume SH Green", 3D) = "black" {}
        [HideInInspector][NoScaleOffset] _ProbeVolumeShB("Probe Volume SH Blue", 3D) = "black" {}
        [HideInInspector][NoScaleOffset] _ProbeVolumeOcc("Probe Volume Occlusion", 3D) = "black" {}
        [HideInInspector] _ProbeVolumeMin("Probe Volume Min", Vector) = (0,0,0,0)
        [HideInInspector] _ProbeVolumeSizeInv("Probe Volume Size Inverse", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Universal Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile_instancing
            // The SDK project has no XR loader set up, so Unity would not add the single pass instanced variant by
            // itself (the game renders both eyes in one instanced pass: without it the rim shows in one eye only).
            #pragma multi_compile _ STEREO_INSTANCING_ON
            #pragma multi_compile_fog
            #pragma skip_variants FOG_EXP FOG_EXP2
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _CLUSTERED_RENDERING
            #pragma multi_compile_local _ _PROBEVOLUME_ON

            // Transparent: the main light's shadow is read from the shadow map, not the screen-space one (no depth here).
            #define _SURFACE_TYPE_TRANSPARENT 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Glow;
                half4 _EmberColor;
                half4 _HeatColor;
                half _Smoothness;
                float4x4 _ProbeWorldToTexture;
                float3 _ProbeVolumeMin;
                float3 _ProbeVolumeSizeInv;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BurnField);
            SAMPLER(sampler_BurnField);

            #if defined(_PROBEVOLUME_ON)
                TEXTURE3D(_ProbeVolumeShR);
                TEXTURE3D(_ProbeVolumeShG);
                TEXTURE3D(_ProbeVolumeShB);
                TEXTURE3D(_ProbeVolumeOcc);
                SAMPLER(sampler_ProbeVolumeShR);
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                #if defined(_PROBEVOLUME_ON)
                    float3 probeUVW : TEXCOORD4;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;   // place on the cut face, in the distance field
                output.fogFactor = ComputeFogFactor(pos.positionCS.z);
                #if defined(_PROBEVOLUME_ON)
                    float3 p = mul(_ProbeWorldToTexture, float4(pos.positionWS, 1.0)).xyz;
                    output.probeUVW = (p - _ProbeVolumeMin.xyz) * _ProbeVolumeSizeInv;
                #endif
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Where this spot lies in the burn line: v from the distance to the outline (1 on it), u from the place
                // along it (stored as cos/sin so it wraps round; its derivatives are unwrapped for the mip level).
                half4 field = SAMPLE_TEXTURE2D(_BurnField, sampler_BurnField, input.uv);
                float2 burnUV = float2(atan2(field.b * 2.0 - 1.0, field.g * 2.0 - 1.0) * (0.5 / PI), field.r);
                float2 dx = ddx(burnUV), dy = ddy(burnUV);
                dx.x -= round(dx.x);
                dy.x -= round(dy.x);
                half4 tex = SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, burnUV, dx, dy);
                half3 albedo = tex.rgb * _BaseColor.rgb;
                half alpha = tex.a * _BaseColor.a;

                // Embers: the orange of the cracks and the ragged edge. They glow while hot and go out to grey ash.
                half ember = saturate((tex.r - tex.b) * 2.5h) * saturate(tex.r * 2.0h - 0.3h);
                half ash = dot(albedo, half3(0.3h, 0.59h, 0.11h));
                albedo = lerp(albedo, ash * half3(0.55h, 0.5h, 0.47h), ember * (1.0h - _Glow));

                // Embers breathe a little: slow waves along the rim, never in step.
                float t = _Time.y;
                half breathe = 0.75h + 0.25h * sin(t * 3.7 + burnUV.x * 23.0 + sin(burnUV.y * 9.0 + t * 1.3) * 3.0);
                half3 emission = tex.rgb * (_EmberColor.rgb * ember * breathe * _Glow
                                           + _HeatColor.rgb * _Glow * _Glow * _Glow);

                float3 normalWS = normalize(input.normalWS);
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                #if defined(_PROBEVOLUME_ON)
                    // The game's light probe volume (LightProbeVolume feeds these to every registered material).
                    float3 uvw = input.probeUVW;
                    inputData.bakedGI = SHEvalLinearL0L1(normalWS,
                        SAMPLE_TEXTURE3D(_ProbeVolumeShR, sampler_ProbeVolumeShR, uvw),
                        SAMPLE_TEXTURE3D(_ProbeVolumeShG, sampler_ProbeVolumeShR, uvw),
                        SAMPLE_TEXTURE3D(_ProbeVolumeShB, sampler_ProbeVolumeShR, uvw));
                    inputData.shadowMask = SAMPLE_TEXTURE3D(_ProbeVolumeOcc, sampler_ProbeVolumeShR, uvw);
                #else
                    inputData.bakedGI = SampleSH(normalWS);
                    inputData.shadowMask = unity_ProbesOcclusion;
                #endif

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.metallic = 0;
                surface.specular = 0;
                surface.smoothness = _Smoothness;
                surface.occlusion = 1;
                surface.emission = emission;
                surface.alpha = alpha;

                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = alpha;
                return color;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
