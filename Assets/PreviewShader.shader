Shader "Goorm/MergeTool/PreviewShader_Transparent_ZWrite"
{
    Properties
    {
        _BaseColor("Main Color", Color) = (1,1,1,0.5)
        _LineColor("Line Color", Color) = (1,1,1,1)
        _LineWidth("Line Width", Range(0, 3)) = 1
        [Toggle]_ShowWireframe("Show Wireframe", Float) = 1
        [HideInInspector]_SrcBlend("SrcBlend", Float) = 5
        [HideInInspector]_DstBlend("DstBlend", Float) = 10
        [HideInInspector]_ZWrite("ZWrite", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100

        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        Cull Back 

        Pass
        {
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2g
            {
                float4 pos : POSITION;
                float3 normal : NORMAL;
            };

            struct g2f
            {
                float4 pos : SV_POSITION;
                float3 bary : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            fixed4 _BaseColor;
            fixed4 _LineColor;
            float _LineWidth;
            float _ShowWireframe;
            static const float3 bary[3] = { float3(1,0,0), float3(0,1,0), float3(0,0,1) };

            v2g vert(appdata v)
            {
                v2g o;
                o.pos = mul(unity_ObjectToWorld, v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            [maxvertexcount(3)]
            void geom(triangle v2g input[3], inout TriangleStream<g2f> stream)
            {
                for (int i = 0; i < 3; i++)
                {
                    g2f o;
                    o.pos = UnityWorldToClipPos(input[i].pos);
                    o.bary = bary[i];
                    o.worldNormal = input[i].normal;
                    stream.Append(o);
                }
            }

            fixed4 frag(g2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                fixed3 normalColor = n * 0.5 + 0.5;

                fixed4 fillColor = fixed4(normalColor, 1.0) * _BaseColor;
                fixed4 lineColor = _LineColor;
                float3 deltas = fwidth(i.bary);
                float3 thickness = deltas * _LineWidth * 0.5;
                float3 edgeCheck = smoothstep(thickness, thickness + deltas, i.bary);
                float minBary = min(min(edgeCheck.x, edgeCheck.y), edgeCheck.z);

                return _ShowWireframe >= 0.5 ? lerp(lineColor, fillColor, minBary) : fillColor;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
