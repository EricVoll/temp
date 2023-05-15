Shader "Custom/VertexColorLitShader"
{
    Properties
    {
    }
    SubShader
    {
        Pass
        {
            ColorMaterial AmbientAndDiffuse
            Lighting On
        }
    }
    Fallback "VertexLit", 1
}
