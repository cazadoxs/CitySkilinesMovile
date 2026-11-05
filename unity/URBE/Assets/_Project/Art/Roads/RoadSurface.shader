// Superficie de carreteras (M2-04b, ADR 003). URP, sin sombras: color del atlas con luz direccional simple (ambiente + difusa).
// Canales del vértice: normal.w = máscara de calzada (1 donde la capa de tráfico puede teñir), uv0 = atlas, uv1 = índice de segmento
// (-1 en cruces). M2-09 activa _TrafficLayer y lee _TrafficData (256x256, un texel por segmento).
Shader "Urbe/RoadSurface"
{
    Properties
    {
        [MainTexture] _BaseMap("Atlas de carreteras", 2D) = "white" {}
        _Ambient("Ambiente", Range(0, 1)) = 0.60
        _Diffuse("Difusa", Range(0, 1)) = 0.52
        _Tint("Tinte (rgb, a = fuerza)", Color) = (1, 1, 1, 0)
        _TrafficData("Datos de tráfico", 2D) = "black" {}
        _TrafficLayer("Capa de tráfico (0/1)", Float) = 0
        _JunctionTint("Color de cruces en la capa", Color) = (0.42, 0.47, 0.56, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "RoadForward"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_TrafficData);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Ambient;
                float _Diffuse;
                float4 _Tint;
                float _TrafficLayer;
                float4 _JunctionTint;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float segment : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                nointerpolation float segment : TEXCOORD2;
                float mask : TEXCOORD3;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                VertexPositionInputs vp = GetVertexPositionInputs(input.positionOS);
                o.positionCS = vp.positionCS;
                o.uv = input.uv;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS.xyz);
                o.segment = input.segment;
                o.mask = input.normalOS.w;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb;

                Light l = GetMainLight();
                float ndl = saturate(dot(normalize(i.normalWS), l.direction));
                half3 lit = _Ambient + _Diffuse * ndl * l.color;

                if (_TrafficLayer > 0.5)
                {
                    // Con la capa activa todo se apaga hacia gris azulado y la calzada de cada segmento toma su color de flujo.
                    half luma = dot(col, half3(0.299, 0.587, 0.114));
                    half3 dim = lerp(col, luma.xxx * half3(0.9, 0.95, 1.05), 0.7) * 0.8;
                    half3 tint = dim;
                    if (i.mask > 0.5)
                    {
                        if (i.segment < -0.5)
                        {
                            tint = lerp(dim, _JunctionTint.rgb, 0.55);
                        }
                        else
                        {
                            uint idx = (uint)(i.segment + 0.5);
                            uint2 px = uint2(idx & 255u, (idx >> 8) & 255u);
                            half4 data = LOAD_TEXTURE2D(_TrafficData, px);
                            tint = lerp(dim, data.rgb, 0.85 * data.a);
                        }
                    }
                    col = tint;
                }

                col = lerp(col, _Tint.rgb, _Tint.a);
                return half4(col * lit, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
