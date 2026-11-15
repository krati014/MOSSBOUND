using UnityEngine;
using UnityEngine.Rendering;

namespace Mossbound
{
    /// <summary>Runtime material factory for URP (falls back to Built-in Standard if URP is missing).</summary>
    public static class Mats
    {
        static Shader lit, particle;
        static Texture2D dot;

        static Shader Lit
        {
            get
            {
                if (lit == null) lit = Shader.Find("Universal Render Pipeline/Lit");
                if (lit == null) lit = Shader.Find("Standard");
                return lit;
            }
        }

        public static Material Surface(Color c, float smooth = 0.2f, float metal = 0f)
        {
            var m = new Material(Lit) { enableInstancing = true };
            m.SetColor("_BaseColor", c); m.SetColor("_Color", c);
            m.SetFloat("_Smoothness", smooth); m.SetFloat("_Glossiness", smooth);
            m.SetFloat("_Metallic", metal);
            m.SetFloat("_EnvironmentReflections", 0.3f);
            return m;
        }

        public static Material Glow(Color baseCol, Color emission, float intensity, float smooth = 0.5f)
        {
            var m = Surface(baseCol, smooth);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            m.SetColor("_EmissionColor", emission * intensity);
            return m;
        }

        public static void SetEmission(Material m, Color c, float intensity) => m.SetColor("_EmissionColor", c * intensity);

        public static Material Transparent(Color c, Color emission, float intensity, float smooth = 0.9f)
        {
            var m = Glow(c, emission, intensity, smooth);
            m.SetFloat("_Surface", 1); m.SetFloat("_Blend", 0);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0);
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            return m;
        }

        /// <summary>Additive soft-dot particle material. HDR <paramref name="intensity"/> feeds bloom.</summary>
        public static Material Particle(Color tint, float intensity)
        {
            if (particle == null) particle = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (particle == null) particle = Shader.Find("Particles/Standard Unlit");
            var m = new Material(particle);
            m.SetTexture("_BaseMap", Dot); m.SetTexture("_MainTex", Dot);
            m.SetColor("_BaseColor", tint * intensity); m.SetColor("_Color", tint * intensity);
            m.SetFloat("_Surface", 1); m.SetFloat("_Blend", 2);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.One);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent + 10;
            return m;
        }

        public static Texture2D Dot
        {
            get
            {
                if (dot != null) return dot;
                const int S = 64;
                dot = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "SoftDot" };
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(S / 2f, S / 2f)) / (S / 2f);
                        float a = Mathf.Clamp01(1f - d); a = a * a * (3f - 2f * a); a *= a;
                        dot.SetPixel(x, y, new Color(1, 1, 1, a));
                    }
                dot.Apply();
                return dot;
            }
        }
    }
}
