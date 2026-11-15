using UnityEngine;
using UnityEngine.Rendering;

namespace Mossbound
{
    /// <summary>Little life-giving animation components.</summary>
    public class Bobber : MonoBehaviour
    {
        public float amplitude = 0.3f, speed = 1f, phase, drift = 0.4f;
        Vector3 origin;
        void Start() { origin = transform.position; }
        void Update()
        {
            float t = Time.time * speed + phase;
            transform.position = origin + new Vector3(Mathf.Sin(t * 0.37f) * drift, Mathf.Sin(t) * amplitude, Mathf.Cos(t * 0.29f) * drift);
        }
    }

    public class Sway : MonoBehaviour
    {
        public float angle = 4f, speed = 1.2f, phase;
        Quaternion baseRot;
        void Start() { baseRot = transform.localRotation; }
        void Update()
        {
            float t = Time.time * speed + phase;
            transform.localRotation = baseRot * Quaternion.Euler(Mathf.Sin(t) * angle, 0, Mathf.Cos(t * 0.8f) * angle * 0.7f);
        }
    }

    public class Breathe : MonoBehaviour
    {
        public float amount = 0.04f, speed = 1.4f, phase;
        Vector3 baseScale;
        void Start() { baseScale = transform.localScale; }
        void Update()
        {
            float s = 1f + Mathf.Sin(Time.time * speed + phase) * amount;
            transform.localScale = new Vector3(baseScale.x * s, baseScale.y * (2f - s), baseScale.z * s);
        }
    }

    /// <summary>Procedural night sky: gradient, stars, a soft aurora and a big moon (6-sided skybox built in code).</summary>
    public static class NightSky
    {
        static readonly Vector3 MoonDir = new Vector3(0.38f, 0.42f, 1f).normalized;

        public static Material Create(int size = 512)
        {
            var shader = Shader.Find("Skybox/6 Sided");
            if (shader == null) return null;
            var m = new Material(shader);
            // Unity 6-sided convention: Front=+Z, Back=-Z, Left=+X, Right=-X, Up=+Y, Down=-Y.
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

        static float Hash(Vector3 p)
        {
            float h = Mathf.Sin(Vector3.Dot(p, new Vector3(127.1f, 311.7f, 74.7f))) * 43758.5453f;
            return h - Mathf.Floor(h);
        }

        static Texture2D Face(int n, System.Func<float, float, Vector3> dirOf)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var d = dirOf((x + 0.5f) / n, (y + 0.5f) / n).normalized;
                    float el = d.y; // -1..1
                    Color horizon = new Color(0.07f, 0.13f, 0.19f), mid = new Color(0.08f, 0.08f, 0.24f), zenith = new Color(0.05f, 0.035f, 0.15f);
                    Color c = el < 0 ? horizon * 0.8f : el < 0.35f ? Color.Lerp(horizon, mid, el / 0.35f) : Color.Lerp(mid, zenith, (el - 0.35f) / 0.65f);
                    if (el > 0.05f)
                    {
                        // stars (direction-hashed so they line up across faces)
                        var cell = new Vector3(Mathf.Floor(d.x * 420f), Mathf.Floor(d.y * 420f), Mathf.Floor(d.z * 420f));
                        float hs = Hash(cell);
                        if (hs > 0.994f) c += Color.Lerp(new Color(0.7f, 0.8f, 1f), new Color(1f, 0.85f, 0.7f), Hash(cell + Vector3.one)) * (hs - 0.994f) * 320f * Mathf.Clamp01(el * 3f);
                        // aurora ribbon
                        float az = Mathf.Atan2(d.x, d.z);
                        float band = Mathf.Exp(-Mathf.Pow((el - (0.32f + 0.08f * Mathf.Sin(az * 3f + 1.3f))) / 0.07f, 2f));
                        float flick = 0.55f + 0.45f * Mathf.Sin(az * 17f + Mathf.Sin(az * 5f) * 3f);
                        c += Color.Lerp(Palette.Turquoise, Palette.Violet, 0.5f + 0.5f * Mathf.Sin(az * 2f)) * band * flick * 0.6f;
                    }
                    // moon + halo
                    float md = Vector3.Angle(d, MoonDir);
                    if (md < 4.5f) c = Color.Lerp(c, new Color(1f, 0.97f, 0.88f), Mathf.Clamp01((4.5f - md) * 1.5f));
                    else c += new Color(0.5f, 0.55f, 0.75f) * Mathf.Exp(-(md - 4.5f) / 7f) * 0.25f;
                    px[y * n + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }

    /// <summary>Faces the main camera; fades out when far away so signs don't clutter the view.</summary>
    public class Billboard : MonoBehaviour
    {
        TextMesh tm; Color baseColor;
        void Start() { tm = GetComponent<TextMesh>(); if (tm) baseColor = tm.color; }
        void LateUpdate()
        {
            var cam = Camera.main;
            if (!cam) return;
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
            if (tm)
            {
                float d = Vector3.Distance(cam.transform.position, transform.position);
                tm.color = new Color(baseColor.r, baseColor.g, baseColor.b, Mathf.Clamp01(Mathf.InverseLerp(55f, 30f, d)));
            }
        }
    }
}
