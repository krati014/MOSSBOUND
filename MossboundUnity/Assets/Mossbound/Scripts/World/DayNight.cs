using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mossbound
{
    /// <summary>
    /// Smooth day <-> night transition: sun/moon light, ambient, fog, skybox, post-processing and the garden's point lights.
    /// The time of day is also sent to the VLM (it changes how things look: glow is faint in daylight).
    /// </summary>
    public class DayNight : MonoBehaviour
    {
        public static DayNight Instance { get; private set; }
        public float T { get; private set; }          // 0 = night, 1 = day
        public bool IsDay => target > 0.5f;
        public string TimeOfDay => IsDay ? "day" : "night";
        public float transitionSeconds = 2.5f;

        EnvironmentController env;
        Material daySky;
        float target;
        readonly List<(Light l, float baseI)> lamps = new List<(Light, float)>();
        readonly List<(Material m, Color e)> glows = new List<(Material, Color)>();

        static readonly Color NightFog = Palette.Fog, DayFog = new Color(0.70f, 0.84f, 0.93f);
        static readonly Color NightAmbSky = new Color(0.22f, 0.27f, 0.50f), DayAmbSky = new Color(0.75f, 0.84f, 0.98f);
        static readonly Color NightAmbEq = new Color(0.07f, 0.17f, 0.18f), DayAmbEq = new Color(0.60f, 0.68f, 0.60f);
        static readonly Color NightAmbGr = new Color(0.03f, 0.05f, 0.05f), DayAmbGr = new Color(0.22f, 0.21f, 0.16f);

        public static DayNight Create(EnvironmentController env, bool startDay)
        {
            var dn = env.gameObject.AddComponent<DayNight>();
            dn.env = env;
            Instance = dn;
            dn.daySky = DaySky.Create();
            foreach (var l in env.GetComponentsInChildren<Light>(true))
                if (l.type == LightType.Point) dn.lamps.Add((l, l.intensity));
            // Glowing materials: in sunlight the glow is dimmed so colours read naturally (and the VLM sees "daytime").
            var seen = new HashSet<Material>();
            foreach (var r in env.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m && seen.Add(m) && m.HasProperty("_EmissionColor") && m.IsKeywordEnabled("_EMISSION"))
                        dn.glows.Add((m, m.GetColor("_EmissionColor")));
            dn.SetImmediate(startDay);
            return dn;
        }

        public void Toggle() => target = IsDay ? 0f : 1f;
        public void SetImmediate(bool day) { target = day ? 1f : 0f; T = target; Apply(T); }

        void Update()
        {
            if (Mathf.Approximately(T, target)) return;
            T = Mathf.MoveTowards(T, target, Time.deltaTime / transitionSeconds);
            Apply(Mathf.SmoothStep(0f, 1f, T));
        }

        void Apply(float t)
        {
            if (!env) return;
            var sun = env.sunLight;
            if (sun)
            {
                sun.transform.rotation = Quaternion.Slerp(Quaternion.Euler(42f, -35f, 0f), Quaternion.Euler(52f, 25f, 0f), t);
                sun.color = Color.Lerp(new Color(0.55f, 0.62f, 1f), new Color(1f, 0.95f, 0.85f), t);
                sun.intensity = Mathf.Lerp(1.05f, 2.1f, t);
                sun.shadowStrength = Mathf.Lerp(0.75f, 0.85f, t);
            }
            RenderSettings.fogColor = Color.Lerp(NightFog, DayFog, t);
            RenderSettings.fogDensity = Mathf.Lerp(0.017f, 0.0045f, t);
            RenderSettings.ambientSkyColor = Color.Lerp(NightAmbSky, DayAmbSky, t);
            RenderSettings.ambientEquatorColor = Color.Lerp(NightAmbEq, DayAmbEq, t);
            RenderSettings.ambientGroundColor = Color.Lerp(NightAmbGr, DayAmbGr, t);
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(Color.Lerp(new Color(0.13f, 0.20f, 0.26f), new Color(0.62f, 0.68f, 0.72f), t));
            sh.AddDirectionalLight(Vector3.up, Color.Lerp(new Color(0.16f, 0.18f, 0.40f), new Color(0.45f, 0.55f, 0.75f), t), 1f);
            RenderSettings.ambientProbe = sh;
            RenderSettings.reflectionIntensity = Mathf.Lerp(0.25f, 0.35f, t);

            // Skyboxes can't be blended: fade the night sky out, swap at dusk, fade the day sky in.
            var sky = t < 0.5f ? env.nightSky : daySky;
            if (sky)
            {
                sky.SetFloat("_Exposure", Mathf.Lerp(0.25f, 1f, Mathf.Abs(t - 0.5f) * 2f));
                if (RenderSettings.skybox != sky) RenderSettings.skybox = sky;
            }

            if (env.colorAdjust)
            {
                env.colorAdjust.postExposure.value = Mathf.Lerp(0.5f, 0.3f, t);
                env.colorAdjust.saturation.value = Mathf.Lerp(22f, 14f, t);
                env.colorAdjust.contrast.value = Mathf.Lerp(14f, 10f, t);
            }
            if (env.bloomFx) { env.bloomFx.intensity.value = Mathf.Lerp(1.0f, 0.45f, t); env.bloomFx.threshold.value = Mathf.Lerp(0.9f, 1.1f, t); }
            if (env.vignetteFx) env.vignetteFx.intensity.value = Mathf.Lerp(0.34f, 0.18f, t);
            foreach (var (l, baseI) in lamps) if (l) l.intensity = baseI * Mathf.Lerp(1f, 0.3f, t);
            foreach (var (m, e) in glows) if (m) m.SetColor("_EmissionColor", e * Mathf.Lerp(1f, 0.28f, t));
        }
    }

    /// <summary>Procedural daytime sky: blue gradient, sun with halo, soft drifting-looking clouds (6-sided skybox).</summary>
    public static class DaySky
    {
        static readonly Vector3 SunDir = (Quaternion.Euler(52f, 25f, 0f) * Vector3.back).normalized;

        public static Material Create(int size = 256)
        {
            var shader = Shader.Find("Skybox/6 Sided");
            if (shader == null) return null;
            var m = new Material(shader);
            m.SetTexture("_FrontTex", Face(size, (u, v) => new Vector3(2 * u - 1, 2 * v - 1, 1)));
            m.SetTexture("_BackTex", Face(size, (u, v) => new Vector3(1 - 2 * u, 2 * v - 1, -1)));
            m.SetTexture("_LeftTex", Face(size, (u, v) => new Vector3(1, 2 * v - 1, 1 - 2 * u)));
            m.SetTexture("_RightTex", Face(size, (u, v) => new Vector3(-1, 2 * v - 1, 2 * u - 1)));
            m.SetTexture("_UpTex", Face(size, (u, v) => new Vector3(2 * u - 1, 1, 2 * v - 1)));
            m.SetTexture("_DownTex", Face(size, (u, v) => new Vector3(2 * u - 1, -1, 1 - 2 * v)));
            m.SetColor("_Tint", new Color(0.5f, 0.5f, 0.5f));
            m.SetFloat("_Exposure", 1f);
            return m;
        }

        static Texture2D Face(int n, System.Func<float, float, Vector3> dirOf)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            Color horizon = new Color(0.78f, 0.88f, 0.93f), zenith = new Color(0.28f, 0.55f, 0.92f), below = new Color(0.62f, 0.72f, 0.70f);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var d = dirOf((x + 0.5f) / n, (y + 0.5f) / n).normalized;
                    float el = d.y;
                    Color c = el < 0 ? below : Color.Lerp(horizon, zenith, Mathf.Pow(Mathf.Clamp01(el), 0.55f));
                    if (el > 0.02f)
                    {
                        // fluffy clouds: noise on a virtual cloud plane
                        float k = 1f / (el + 0.12f);
                        float cx = d.x * k * 1.4f, cz = d.z * k * 1.4f;
                        float nse = Mathf.PerlinNoise(cx + 40f, cz + 12f) * 0.65f + Mathf.PerlinNoise(cx * 2.7f + 3f, cz * 2.7f + 9f) * 0.35f;
                        float cloud = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.52f, 0.72f, nse)) * Mathf.Clamp01(el * 4f);
                        c = Color.Lerp(c, new Color(1f, 0.99f, 0.97f), cloud * 0.85f);
                    }
                    float sd = Vector3.Angle(d, SunDir);
                    if (sd < 3f) c = new Color(1f, 0.98f, 0.9f);
                    else c += new Color(1f, 0.9f, 0.7f) * Mathf.Exp(-(sd - 3f) / 9f) * 0.35f;
                    px[y * n + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }
}
