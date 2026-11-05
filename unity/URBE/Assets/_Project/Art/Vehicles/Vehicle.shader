// URBE M2-06/M2-07: shader de vehiculos. Un atlas de 64x64; el alfa del atlas marca la pintura que se tine por instancia (_Tint).
// Sirve para los dos caminos de dibujo: RenderMeshInstanced (instancing clasico, _Tint por MaterialPropertyBlock) y
// BatchRendererGroup (DOTS instancing: _Tint, unity_ObjectToWorld y unity_WorldToObject salen del buffer del grupo).
Shader "Urbe/Vehicle"
{
    Properties
    {
        _BaseMap ("Atlas", 2D) = "white" {}
        _Tint ("Tinte (por instancia)", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
                CBUFFER_START(UnityPerMaterial)
                    float4 _Tint;
                CBUFFER_END
                UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                    UNITY_DOTS_INSTANCED_PROP(float4, _Tint)
                UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
                #define GET_TINT() UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _Tint)
            #else
                // Con el buffer llamado UnityPerMaterial el shader es compatible con el SRP Batcher (requisito de BatchRendererGroup)
                // y a la vez admite instancing clasico con MaterialPropertyBlock.
                UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
                    UNITY_DEFINE_INSTANCED_PROP(float4, _Tint)
                UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)
                #define GET_TINT() UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Tint)
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
                float3 normalWS : TEXCOORD1;
                float4 tint : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes i)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i, o);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.uv = i.uv;
                o.tint = GET_TINT();
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                half4 t = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                half3 albedo = lerp(t.rgb, t.rgb * i.tint.rgb, t.a);
                half3 n = normalize(i.normalWS);
                Light l = GetMainLight();
                half ndl = saturate(dot(n, l.direction));
                half3 ambient = lerp(half3(0.34, 0.33, 0.36), half3(0.62, 0.64, 0.68), n.y * 0.5 + 0.5);
                half3 lit = ambient + l.color * ndl * 0.62;
                return half4(albedo * lit, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
