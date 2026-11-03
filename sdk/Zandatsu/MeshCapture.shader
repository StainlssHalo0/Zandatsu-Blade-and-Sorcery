// Reads a mesh the game keeps on the graphics card only, compressed (half-float normals and UVs): drawn once with this
// shader, each triangle's corners, decoded by the graphics card, are written into a buffer the mod can read back
// (MeshReader). Nothing is drawn on screen.
Shader "Hidden/Zandatsu/MeshCapture"
{
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 5.0
            #pragma require geometry randomwrite

            struct Corner { float3 p; float3 n; float4 t; float2 uv; float2 uv2; float4 c; };   // 18 floats
            RWStructuredBuffer<Corner> _Corners : register(u1);

            struct Attributes
            {
                float4 p : POSITION;
                float3 n : NORMAL;
                float4 t : TANGENT;
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1;
                float4 c : COLOR;
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
                float3 p : TEXCOORD0;
                float3 n : TEXCOORD1;
                float4 t : TEXCOORD2;
                float4 uv : TEXCOORD3;
                float4 c : TEXCOORD4;
            };

            Varyings vert(Attributes a)
            {
                Varyings o;
                o.pos = float4(0, 0, 0.5, 1);
                o.p = a.p.xyz;
                o.n = a.n;
                o.t = a.t;
                o.uv = float4(a.uv, a.uv2);
                o.c = a.c;
                return o;
            }

            [maxvertexcount(1)]
            void geom(triangle Varyings v[3], uint id : SV_PrimitiveID, inout PointStream<Varyings> stream)
            {
                for (uint k = 0; k < 3; k++)
                {
                    Corner c;
                    c.p = v[k].p;
                    c.n = v[k].n;
                    c.t = v[k].t;
                    c.uv = v[k].uv.xy;
                    c.uv2 = v[k].uv.zw;
                    c.c = v[k].c;
                    _Corners[id * 3 + k] = c;
                }
            }

            half4 frag(Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
