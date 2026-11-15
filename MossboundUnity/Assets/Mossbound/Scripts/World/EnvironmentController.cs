using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Mossbound
{
    /// <summary>Procedurally builds the bioluminescent garden: terrain, landmarks, flora, lighting, fog, post-FX.</summary>
    public class EnvironmentController : MonoBehaviour
    {
        public static EnvironmentController Instance { get; private set; }
        public readonly List<Landmark> landmarks = new List<Landmark>();
        public Collider groundCollider;
        // atmosphere handles used by DayNight
        public Light sunLight;
        public Material nightSky;
        public ColorAdjustments colorAdjust;
        public Bloom bloomFx;
        public Vignette vignetteFx;

        public const float PlayRadius = 31f;
        public static readonly Vector2 PondCenter = new Vector2(-5f, 3f);
        public const float PondRadius = 5.5f;
        public const float WaterLevel = -0.85f;
        public static readonly Vector3 RoverStart = new Vector3(0f, 0f, -21f);

        // Path the stones follow (start -> ancient tree), plus a branch to the crystals.
        static readonly Vector2[] MainPath = { new Vector2(0, -24), new Vector2(0.5f, -15), new Vector2(3.5f, -8), new Vector2(5.5f, 0),
                                               new Vector2(4.5f, 8), new Vector2(1.5f, 14), new Vector2(0, 18.5f) };
        static readonly Vector2[] BranchPath = { new Vector2(5.5f, 0), new Vector2(8.5f, -0.6f), new Vector2(10.6f, -1.2f) };
        static readonly Vector2[] PortalPath = { new Vector2(4.5f, 8), new Vector2(1, 11), new Vector2(-3.5f, 13.5f), new Vector2(-8f, 14.5f) };

        System.Random rnd;
        float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);
        Transform root;
        readonly List<Vector3> occupied = new List<Vector3>(); // xz + radius in .y

        // ------------------------------------------------------------------ height field
        public static float Height(float x, float z)
        {
            float d = Mathf.Sqrt(x * x + z * z);
            float pd = Vector2.Distance(new Vector2(x, z), PondCenter);
            float noiseMask = Mathf.Clamp01(Mathf.InverseLerp(PondRadius, PondRadius + 5f, pd));
            // Gentle rolling meadow (kept low so MOSS drives smoothly and the cameras see across the garden).
            float h = (Mathf.PerlinNoise(x * 0.04f + 31.7f, z * 0.04f + 11.3f) - 0.5f) * 0.45f
                    + (Mathf.PerlinNoise(x * 0.17f + 5f, z * 0.17f + 9f) - 0.5f) * 0.06f;
            h *= noiseMask;
            h += Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(30f, 46f, d)) * 10f;                 // rim hills
            h += Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(38f, 46f, d)) * (Mathf.PerlinNoise(x * 0.1f, z * 0.1f) * 5f);
            h -= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(PondRadius + 2.5f, PondRadius - 1.5f, pd)) * 1.7f; // pond bowl
            return h;
        }

        public static Vector3 OnGround(float x, float z, float yOff = 0f) => new Vector3(x, Height(x, z) + yOff, z);

        static float DistToPolyline(Vector2 p, Vector2[] line)
        {
            float best = 1e9f;
            for (int i = 0; i < line.Length - 1; i++)
            {
                Vector2 a = line[i], b = line[i + 1], ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }

        static float DistToPaths(Vector2 p) => Mathf.Min(DistToPolyline(p, MainPath), Mathf.Min(DistToPolyline(p, BranchPath), DistToPolyline(p, PortalPath)));

        bool Free(Vector2 p, float r)
        {
            if (Vector2.Distance(p, PondCenter) < PondRadius + 1.2f + r) return false;
            foreach (var o in occupied) if (Vector2.Distance(p, new Vector2(o.x, o.z)) < o.y + r) return false;
            return true;
        }

        void Occupy(float x, float z, float r) => occupied.Add(new Vector3(x, r, z));

        // ------------------------------------------------------------------ helpers
        GameObject Obj(string name, Mesh mesh, Material[] mats, Transform parent, Vector3 pos, bool shadows = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return go;
        }

        GameObject Obj(string name, Mesh mesh, Material mat, Transform parent, Vector3 pos, bool shadows = true)
            => Obj(name, mesh, new[] { mat }, parent, pos, shadows);

        static Light PointLight(Transform parent, Vector3 pos, Color c, float range, float intensity)
        {
            var go = new GameObject("Light");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.color = c; l.range = range; l.intensity = intensity; l.shadows = LightShadows.None;
            return l;
        }

        Landmark AddLandmark(string label, string display, Vector3 pos, float radius, Color color, float colR, float colH)
        {
            var go = new GameObject("Landmark_" + label);
            go.transform.SetParent(root, false);
            go.transform.position = pos;
            var lm = go.AddComponent<Landmark>();
            lm.label = label; lm.displayName = display; lm.radius = radius; lm.color = color;
            if (colR > 0)
            {
                var cap = go.AddComponent<CapsuleCollider>();
                cap.radius = colR; cap.height = Mathf.Max(colH, colR * 2); cap.center = new Vector3(0, cap.height / 2, 0);
            }
            landmarks.Add(lm);
            Occupy(pos.x, pos.z, radius + 1.5f);
            return lm;
        }

        // ------------------------------------------------------------------ build
        public void Build()
        {
            Instance = this;
            rnd = new System.Random(20261110);
            root = transform;
            // Reserve key areas so decoration never blocks them.
            foreach (var p in MainPath) Occupy(p.x, p.y, 1.6f);
            Occupy(RoverStart.x, RoverStart.z, 3f);

            BuildAtmosphere();
            BuildTerrain();
            BuildPond();
            BuildAncientTree();
            BuildMushrooms(new Vector2(-13f, -8f));
            // One crystal only (three similar crystals were hard to tell apart for players and for the AI).
            BuildCrystals("blue_crystal", "Crystal", new Vector2(12.8f, -1.6f), new Color(0.05f, 0.28f, 1f), 1.2f, 11);
            BuildRedFlower(new Vector2(9f, -12f));
            BuildPortal(new Vector2(-10f, 15.5f));
            BuildHome();
            BuildPathStones();
            BuildLanterns();
            BuildTrees();
            BuildRocks();
            BuildFlowersAndGrass();
            BuildFireflies();
            BuildWisps();
            BuildNameSigns();
        }

        /// <summary>
        /// Floating name signs so the player knows what each thing is called. They live on the rover layer, which MOSS's
        /// vision cameras never render, so the AI cannot read them - it still has to recognise things by sight.
        /// </summary>
        void BuildNameSigns()
        {
            foreach (var lm in landmarks)
            {
                float h = lm.label == "largest_tree" ? 19f : lm.label == "portal" ? 6.6f : lm.label == "pond_water" ? 2.2f
                        : lm.label == "home" ? 3.4f : lm.label == "red_flower" ? 4.4f : lm.label == "blue_crystal" ? 5.4f : 3.6f;
                var go = new GameObject("Sign_" + lm.label);
                go.transform.SetParent(root, false);
                go.transform.position = new Vector3(lm.transform.position.x, lm.transform.position.y + h, lm.transform.position.z);
                go.layer = RoverController.SignLayer;
                var tm = go.AddComponent<TextMesh>();
                tm.font = UIKit.Font; go.GetComponent<MeshRenderer>().sharedMaterial = UIKit.Font.material;
                tm.text = lm.displayName; tm.fontSize = 64; tm.characterSize = lm.label == "largest_tree" ? 0.09f : lm.label == "home" ? 0.035f : 0.055f;
                tm.anchor = TextAnchor.MiddleCenter; tm.fontStyle = FontStyle.Bold;
                var c = lm.color; tm.color = Color.Lerp(c, Color.white, 0.55f);
                go.AddComponent<Billboard>();
                go.AddComponent<Bobber>().amplitude = 0.12f;
            }
        }

        void BuildAtmosphere()
        {
            nightSky = NightSky.Create();
            RenderSettings.skybox = nightSky;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = Palette.Fog;
            RenderSettings.fogDensity = 0.017f;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.22f, 0.27f, 0.50f);
            RenderSettings.ambientEquatorColor = new Color(0.07f, 0.17f, 0.18f);
            RenderSettings.ambientGroundColor = new Color(0.03f, 0.05f, 0.05f);
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(new Color(0.13f, 0.20f, 0.26f));
            sh.AddDirectionalLight(Vector3.up, new Color(0.16f, 0.18f, 0.40f), 1f);
            RenderSettings.ambientProbe = sh;
            RenderSettings.reflectionIntensity = 0.25f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;

            // Moonlight
            var moon = new GameObject("Moonlight");
            moon.transform.SetParent(root, false);
            moon.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
            var l = moon.AddComponent<Light>();
            l.type = LightType.Directional;
            l.color = new Color(0.55f, 0.62f, 1f);
            l.intensity = 1.05f;
            l.shadows = LightShadows.Soft;
            l.shadowStrength = 0.75f;
            sunLight = l;
            RenderSettings.sun = l;

            // Post-processing
            var volGo = new GameObject("PostFX");
            volGo.transform.SetParent(root, false);
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true; vol.priority = 50;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = prof.Add<Bloom>(true);
            bloom.intensity.value = 1.0f; bloom.threshold.value = 0.9f; bloom.scatter.value = 0.72f;
            bloom.tint.value = new Color(0.85f, 1f, 0.97f);
            var tm = prof.Add<Tonemapping>(true); tm.mode.value = TonemappingMode.ACES;
            var ca = prof.Add<ColorAdjustments>(true);
            ca.postExposure.value = 0.5f; ca.contrast.value = 14f; ca.saturation.value = 22f;
            ca.colorFilter.value = new Color(0.96f, 1f, 1f);
            var vig = prof.Add<Vignette>(true);
            vig.intensity.value = 0.34f; vig.smoothness.value = 0.5f; vig.color.value = new Color(0.01f, 0.02f, 0.05f);
            colorAdjust = ca; bloomFx = bloom; vignetteFx = vig;
            var wb = prof.Add<WhiteBalance>(true); wb.temperature.value = -5f; wb.tint.value = 6f;
            vol.sharedProfile = prof;
        }

        void BuildTerrain()
        {
            const int N = 84; const float Size = 100f;
            float step = Size / N;
            var mb = new MeshBuilder();
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    float x0 = -Size / 2 + i * step, z0 = -Size / 2 + j * step, x1 = x0 + step, z1 = z0 + step;
                    Vector3 a = OnGround(x0, z0), b = OnGround(x0, z1), c = OnGround(x1, z1), d = OnGround(x1, z0);
                    for (int t = 0; t < 2; t++)
                    {
                        Vector3 p0 = a, p1 = t == 0 ? b : c, p2 = t == 0 ? c : d;
                        Vector3 cen = (p0 + p1 + p2) / 3f;
                        float n = Mathf.PerlinNoise(cen.x * 0.08f + 3f, cen.z * 0.08f + 7f);
                        float pd = Vector2.Distance(new Vector2(cen.x, cen.z), PondCenter);
                        float path = DistToPaths(new Vector2(cen.x, cen.z));
                        int sub = n > 0.55f ? 1 : 0;
                        if (pd < PondRadius + 1.4f || path < 1.0f || cen.y > 4f) sub = 2;
                        mb.Sub = sub;
                        mb.Tri(p0, p1, p2, Vector3.up);
                    }
                }
            var mesh = mb.Build("Terrain");
            var go = Obj("Terrain", mesh, new[] { Mats.Surface(Palette.Moss, 0.12f), Mats.Surface(Palette.MossTeal, 0.12f), Mats.Surface(Palette.Soil, 0.1f) }, root, Vector3.zero);
            go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            groundCollider = mc;
        }

        void BuildPond()
        {
            var water = Obj("Water", Prim.Make(mb => Prim.Disc(mb, PondRadius + 3f, 40, Vector3.up), "WaterDisc"),
                Mats.Transparent(new Color(0.02f, 0.16f, 0.22f, 0.82f), Palette.Turquoise, 0.08f, 0.95f),
                root, new Vector3(PondCenter.x, WaterLevel, PondCenter.y), false);

            // Invisible barrier: the rover physically cannot drive into the water.
            var barrier = new GameObject("WaterBarrier");
            barrier.transform.SetParent(water.transform, false);
            barrier.layer = 4; // "Water"
            var bm = Prim.Make(mb => Prim.Cylinder(mb, PondRadius + 0.3f, PondRadius + 0.3f, 3f, 20), "Barrier");
            var mc = barrier.AddComponent<MeshCollider>();
            mc.sharedMesh = bm; mc.convex = true;
            barrier.transform.localPosition = new Vector3(0, -0.5f, 0);

            var lm = AddLandmark("pond_water", "Pond", new Vector3(PondCenter.x, 0, PondCenter.y), PondRadius, Palette.Turquoise, 0, 0);

            // Glowing lily pads
            var padMat = Mats.Glow(new Color(0.05f, 0.25f, 0.15f), Palette.Turquoise, 0.35f, 0.4f);
            var bloomMat = Mats.Glow(Palette.Ivory, new Color(1f, 0.55f, 0.85f), 2.2f);
            var pads = new MeshBuilder(); var blooms = new MeshBuilder();
            for (int i = 0; i < 9; i++)
            {
                float a = R(0, Mathf.PI * 2), r = R(1f, PondRadius - 0.8f);
                var p = new Vector3(PondCenter.x + Mathf.Cos(a) * r, WaterLevel + 0.03f, PondCenter.y + Mathf.Sin(a) * r);
                float s = R(0.35f, 0.75f);
                pads.M = Matrix4x4.TRS(p, Quaternion.Euler(0, R(0, 360), 0), new Vector3(s, 1, s));
                Prim.Disc(pads, 1f, 9, Vector3.up);
                if (i % 3 == 0)
                {
                    blooms.M = Matrix4x4.TRS(p + Vector3.up * 0.08f, Quaternion.identity, Vector3.one * 0.22f);
                    Prim.Sphere(blooms, 1, 0.3f, i);
                }
            }
            Obj("LilyPads", pads.Build("Pads"), padMat, lm.transform, Vector3.zero, false).transform.position = Vector3.zero;
            Obj("LilyBlooms", blooms.Build("Blooms"), bloomMat, lm.transform, Vector3.zero, false).transform.position = Vector3.zero;
            PointLight(lm.transform, new Vector3(PondCenter.x, WaterLevel + 1.2f, PondCenter.y), Palette.Turquoise, 10f, 1.2f);
        }

        void BuildAncientTree()
        {
            var pos = OnGround(0f, 23f);
            var lm = AddLandmark("largest_tree", "Big Tree", pos, 2.4f, Palette.Turquoise, 1.9f, 12f);
            var t = lm.transform;
            var bark = Mats.Surface(Palette.Bark, 0.15f);
            var trunk = Prim.Make(mb =>
            {
                Prim.Lathe(mb, new[] { new Vector2(2.4f, -0.3f), new Vector2(1.6f, 0.8f), new Vector2(1.15f, 3f), new Vector2(1.0f, 6f),
                                        new Vector2(1.25f, 8.2f), new Vector2(0.7f, 10.5f) }, 9, 0.18f, 3);
                for (int i = 0; i < 6; i++) // roots
                {
                    float a = i / 6f * 360f + 20f;
                    mb.M = Matrix4x4.TRS(Quaternion.Euler(0, a, 0) * new Vector3(0, 0.1f, 1.4f), Quaternion.Euler(0, a, 0) * Quaternion.Euler(70, 0, 0), Vector3.one);
                    Prim.Cylinder(mb, 0.55f, 0.05f, 2.6f, 5);
                }
                for (int i = 0; i < 4; i++) // branches
                {
                    float a = i * 90f + 40f;
                    mb.M = Matrix4x4.TRS(new Vector3(0, 7.5f + i * 0.4f, 0), Quaternion.Euler(0, a, 0) * Quaternion.Euler(55, 0, 0), Vector3.one);
                    Prim.Cylinder(mb, 0.4f, 0.12f, 3.8f, 5);
                }
            }, "AncientTrunk");
            Obj("Trunk", trunk, bark, t, pos);

            var leafMat = Mats.Glow(new Color(0.05f, 0.30f, 0.27f), Palette.Turquoise, 0.22f, 0.3f);
            var leaves = Prim.Make(mb =>
            {
                var blobs = new[] { (new Vector3(0, 11f, 0), new Vector3(8f, 5f, 8f)), (new Vector3(-3.6f, 9.6f, 1.2f), new Vector3(5f, 3.6f, 5f)),
                                    (new Vector3(3.6f, 9.9f, -1.0f), new Vector3(5.4f, 3.8f, 5.4f)), (new Vector3(0.6f, 13.4f, 0.4f), new Vector3(5f, 3.4f, 5f)),
                                    (new Vector3(-1f, 10f, -3.4f), new Vector3(4.6f, 3.2f, 4.6f)), (new Vector3(1.4f, 10.2f, 3.6f), new Vector3(4.4f, 3f, 4.4f)) };
                int k = 0;
                foreach (var (p, s) in blobs) { mb.M = Matrix4x4.TRS(p, Quaternion.Euler(0, k * 37, 0), s); Prim.Sphere(mb, 1, 0.35f, 100 + k++); }
            }, "AncientCanopy");
            Obj("Canopy", leaves, leafMat, t, pos);

            var orbMat = Mats.Glow(Palette.Amber, Palette.Amber, 4f);
            var orbs = Prim.Make(mb =>
            {
                for (int i = 0; i < 18; i++)
                {
                    float a = i / 18f * Mathf.PI * 2f + R(0, 0.3f), r = R(2.5f, 5.5f);
                    mb.M = Matrix4x4.TRS(new Vector3(Mathf.Cos(a) * r, R(7.2f, 8.6f), Mathf.Sin(a) * r), Quaternion.identity, Vector3.one * R(0.18f, 0.3f));
                    Prim.Sphere(mb, 1);
                }
            }, "Orbs");
            Obj("GlowFruit", orbs, orbMat, t, pos, false);
            // Hanging lantern strings + a fairy ring of tiny glowing mushrooms: make the ancient tree unmistakable.
            var strings = Prim.Make(mb =>
            {
                for (int i = 0; i < 10; i++)
                {
                    float a = i / 10f * Mathf.PI * 2f + 0.2f, r = 4.6f + (i % 3) * 0.5f;
                    var top = new Vector3(Mathf.Cos(a) * r, 9.4f, Mathf.Sin(a) * r);
                    mb.M = Matrix4x4.Translate(top - Vector3.up * 1.6f); Prim.Cylinder(mb, 0.02f, 0.02f, 1.6f, 3, false);
                    mb.M = Matrix4x4.TRS(top - Vector3.up * 1.85f, Quaternion.identity, new Vector3(0.38f, 0.5f, 0.38f)); Prim.Sphere(mb, 1);
                }
            }, "LanternStrings");
            Obj("HangingLanterns", strings, Mats.Glow(Palette.Amber, new Color(1f, 0.7f, 0.3f), 3.2f), t, pos, false);
            // A ring of glowing golden pebbles around the roots (pebbles, not mushrooms: tiny mushrooms here used to be
            // mistaken for the mushroom grove by the vision model).
            var pebbles = new MeshBuilder();
            for (int i = 0; i < 18; i++)
            {
                float a = i / 18f * Mathf.PI * 2f, r = 4.4f;
                float gy = Height(pos.x + Mathf.Cos(a) * r, pos.z + Mathf.Sin(a) * r) - pos.y;
                pebbles.M = Matrix4x4.TRS(new Vector3(Mathf.Cos(a) * r, gy + 0.08f, Mathf.Sin(a) * r), Quaternion.Euler(0, a * Mathf.Rad2Deg, 0), new Vector3(0.32f, 0.16f, 0.26f) * (0.8f + 0.2f * (i % 3)));
                Prim.Sphere(pebbles, 1, 0.2f, i);
            }
            var frc = Obj("GoldenPebbleRing", pebbles.Build("Pebbles"), Mats.Glow(new Color(1f, 0.8f, 0.35f), Palette.Amber, 1.4f), t, pos, false);
            frc.AddComponent<Breathe>().amount = 0.05f;
            var canopyCol = new GameObject("CanopyCollider"); canopyCol.transform.SetParent(t, false);
            canopyCol.AddComponent<SphereCollider>().radius = 4.8f; canopyCol.transform.localPosition = new Vector3(0, 11f, 0);
            PointLight(t, pos + new Vector3(0, 7f, -3f), Palette.Turquoise, 22f, 3.5f);
            PointLight(t, pos + new Vector3(0, 2f, -3.5f), Palette.Amber, 8f, 1.5f);
            t.localScale = Vector3.one * 1.3f;   // by far the biggest tree in the garden
            lm.radius = 4.6f;   // standing under the canopy counts as "at the tree"
            Occupy(pos.x, pos.z, 8f);
        }

        void BuildMushrooms(Vector2 c)
        {
            var pos = OnGround(c.x, c.y);
            var lm = AddLandmark("glowing_mushrooms", "Mushrooms", pos, 2.6f, Palette.Turquoise, 2.0f, 2.5f);
            var stem = new MeshBuilder(); var capC = new MeshBuilder(); var capV = new MeshBuilder();
            var spec = new[] { (0f, 0f, 1.9f), (1.4f, 0.6f, 1.2f), (-1.2f, 0.9f, 1.4f), (0.6f, -1.5f, 0.9f), (-0.9f, -1.2f, 0.75f),
                               (1.9f, -0.8f, 0.6f), (-2.0f, -0.2f, 0.55f), (0.2f, 1.9f, 0.7f), (2.3f, 1.6f, 0.45f), (-1.8f, 1.9f, 0.5f) };
            int k = 0;
            foreach (var (dx, dz, s) in spec)
            {
                float gy = Height(c.x + dx, c.y + dz) - pos.y;
                var M = Matrix4x4.TRS(new Vector3(dx, gy, dz), Quaternion.Euler(R(-8, 8), R(0, 360), R(-8, 8)), Vector3.one * s);
                stem.M = M;
                Prim.Lathe(stem, new[] { new Vector2(0.17f, 0), new Vector2(0.13f, 0.45f), new Vector2(0.11f, 0.95f) }, 7);
                var cap = k % 3 == 1 ? capV : capC;
                cap.M = M;
                Prim.Lathe(cap, new[] { new Vector2(0.1f, 0.9f), new Vector2(0.62f, 0.93f), new Vector2(0.58f, 1.07f), new Vector2(0.38f, 1.22f), new Vector2(0f, 1.3f) }, 10, 0.06f, k, true);
                k++;
            }
            Obj("Stems", stem.Build("Stems"), Mats.Glow(Palette.Ivory, new Color(0.6f, 0.9f, 0.9f), 0.25f), lm.transform, pos);
            Obj("CapsCyan", capC.Build("CapsC"), Mats.Glow(new Color(0.1f, 0.5f, 0.55f), Palette.Turquoise, 1.7f), lm.transform, pos).AddComponent<Breathe>().phase = 0.5f;
            Obj("CapsViolet", capV.Build("CapsV"), Mats.Glow(new Color(0.3f, 0.15f, 0.5f), Palette.Violet, 1.9f), lm.transform, pos).AddComponent<Breathe>().phase = 2f;
            var spots = new MeshBuilder();
            foreach (var (dx, dz, s) in spec)
            {
                float gy = Height(c.x + dx, c.y + dz) - pos.y;
                for (int q = 0; q < 5; q++)
                {
                    float a = q * 1.3f + dx, rr = 0.32f * s;
                    spots.M = Matrix4x4.TRS(new Vector3(dx + Mathf.Cos(a) * rr, gy + 1.16f * s, dz + Mathf.Sin(a) * rr), Quaternion.identity, new Vector3(0.11f, 0.05f, 0.11f) * s);
                    Prim.Sphere(spots, 1);
                }
            }
            Obj("Spots", spots.Build("Spots"), Mats.Glow(Color.white, new Color(0.9f, 1f, 1f), 1.2f), lm.transform, pos, false).AddComponent<Breathe>().phase = 0.5f;
            PointLight(lm.transform, pos + Vector3.up * 1.6f, Palette.Turquoise, 9f, 3f);
            PointLight(lm.transform, pos + new Vector3(-1, 0.8f, 1), Palette.Violet, 5f, 1.8f);
            Particles(lm.transform, pos + Vector3.up * 0.5f, Palette.Turquoise, ParticleSystemShapeType.Circle, 2.4f, 14, 0.07f, 0.45f, 2.5f);
        }

        void BuildCrystals(string label, string display, Vector2 c, Color col, float scale, int seed)
        {
            var pos = OnGround(c.x, c.y);
            var lm = AddLandmark(label, display, pos, 1.6f * scale + 0.3f, col, 1.3f * scale, 3f * scale);
            var cr = new MeshBuilder();
            var shards = new[] { (0f, 0f, 0.55f, 3.6f, 0f, 0f), (0.8f, 0.3f, 0.35f, 2.2f, 18f, 60f), (-0.7f, 0.4f, 0.4f, 2.5f, -16f, 20f),
                                 (0.2f, -0.8f, 0.3f, 1.7f, 22f, 150f), (-0.5f, -0.6f, 0.25f, 1.3f, -25f, 200f), (1.1f, -0.5f, 0.2f, 1.0f, 30f, 110f),
                                 (-1.1f, -0.1f, 0.22f, 1.1f, -30f, 300f) };
            foreach (var (dx, dz, r, h, tilt, yaw) in shards)
            {
                cr.M = Matrix4x4.TRS(new Vector3(dx, -0.15f, dz) * scale, Quaternion.Euler(0, yaw, 0) * Quaternion.Euler(tilt, 0, 0), Vector3.one * scale);
                Prim.Crystal(cr, r, h, 6);
            }
            Obj("Shards", cr.Build("Crystal"), Mats.Glow(col * 0.6f, col, 1.35f, 0.85f), lm.transform, pos);
            var baseRock = Prim.Make(mb => { mb.M = Matrix4x4.Scale(new Vector3(2.6f, 0.7f, 2.4f) * scale); Prim.Sphere(mb, 1, 0.4f, seed); });
            Obj("Base", baseRock, Mats.Surface(Palette.Stone, 0.2f), lm.transform, pos);
            var shardMat = Mats.Glow(col * 0.6f, col, 1.8f, 0.85f);
            var shardMesh = Prim.Make(mb => { mb.M = Matrix4x4.Translate(new Vector3(0, -0.3f, 0)); Prim.Crystal(mb, 0.13f, 0.6f, 6); mb.M = Matrix4x4.TRS(new Vector3(0, -0.3f, 0), Quaternion.Euler(180, 0, 0), Vector3.one); Prim.Crystal(mb, 0.13f, 0.3f, 6); });
            for (int k = 0; k < 3; k++)
            {
                float a = k * 2.1f + seed;
                var f = Obj("FloatingShard", shardMesh, shardMat, lm.transform, pos + new Vector3(Mathf.Cos(a) * 1.5f, 3.6f + k * 0.5f, Mathf.Sin(a) * 1.5f) * scale, false);
                f.AddComponent<Spinner>().speed = new Vector3(0, 40f + k * 15f, 0);
                var b = f.AddComponent<Bobber>(); b.amplitude = 0.25f; b.speed = 1.1f + k * 0.3f; b.phase = k; b.drift = 0.1f;
            }
            PointLight(lm.transform, pos + Vector3.up * 2.2f * scale, col, 10f * scale, 3.2f);
            Particles(lm.transform, pos + Vector3.up * 1.5f * scale, col, ParticleSystemShapeType.Sphere, 1.4f * scale, 8, 0.05f, 0.15f, 3f);
        }

        void BuildRedFlower(Vector2 c)
        {
            var pos = OnGround(c.x, c.y);
            var lm = AddLandmark("red_flower", "Red Flower", pos, 1.0f, Palette.Ember, 0.55f, 1.8f);
            var green = Mats.Surface(new Color(0.06f, 0.24f, 0.12f), 0.3f);
            var stem = Prim.Make(mb =>
            {
                Prim.Lathe(mb, new[] { new Vector2(0.09f, 0), new Vector2(0.07f, 1.0f), new Vector2(0.06f, 1.75f) }, 6);
                for (int i = 0; i < 3; i++)
                {
                    mb.M = Matrix4x4.TRS(new Vector3(0, 0.35f + i * 0.35f, 0), Quaternion.Euler(0, i * 130f, 0) * Quaternion.Euler(0, 0, 60f) * Quaternion.Euler(0, 0, 0), new Vector3(0.18f, 0.7f, 0.05f));
                    mb.M = mb.M * Matrix4x4.Translate(new Vector3(0, 0.5f, 0));
                    Prim.Sphere(mb, 1);
                }
            });
            Obj("Stem", stem, green, lm.transform, pos).AddComponent<Sway>().angle = 3f;
            var petals = Prim.Make(mb =>
            {
                for (int i = 0; i < 7; i++)
                {
                    var rot = Quaternion.Euler(0, i * 360f / 7f, 0) * Quaternion.Euler(-35f, 0, 0);
                    mb.M = Matrix4x4.TRS(new Vector3(0, 1.75f, 0) + rot * new Vector3(0, 0, 0.42f), rot, new Vector3(0.42f, 0.09f, 0.85f));
                    Prim.Sphere(mb, 1, 0.15f, i);
                }
                for (int i = 0; i < 5; i++)
                {
                    var rot = Quaternion.Euler(0, i * 72f + 25f, 0) * Quaternion.Euler(-65f, 0, 0);
                    mb.M = Matrix4x4.TRS(new Vector3(0, 1.8f, 0) + rot * new Vector3(0, 0, 0.25f), rot, new Vector3(0.3f, 0.07f, 0.5f));
                    Prim.Sphere(mb, 1, 0.15f, 20 + i);
                }
            });
            Obj("Petals", petals, Mats.Glow(new Color(0.7f, 0.03f, 0.05f), new Color(1f, 0.04f, 0.08f), 1.5f, 0.4f), lm.transform, pos).AddComponent<Sway>().angle = 3f;
            var core = Prim.Make(mb => { mb.M = Matrix4x4.TRS(new Vector3(0, 1.82f, 0), Quaternion.identity, Vector3.one * 0.3f); Prim.Sphere(mb, 1); });
            Obj("Core", core, Mats.Glow(Palette.Amber, Palette.Amber, 5f), lm.transform, pos, false).AddComponent<Sway>().angle = 3f;
            PointLight(lm.transform, pos + new Vector3(0, 2.2f, 0), Palette.Ember, 7f, 3.2f);
            Particles(lm.transform, pos + Vector3.up * 1.9f, Palette.Amber, ParticleSystemShapeType.Sphere, 0.6f, 6, 0.04f, 0.35f, 2.2f);
            // Bigger, so it reads from across the garden; the bloom gets its own collider so grounding rays aimed at the
            // petals hit the flower instead of slipping past to the trees behind it.
            var bloomCol = lm.gameObject.AddComponent<SphereCollider>(); bloomCol.center = new Vector3(0, 1.9f, 0); bloomCol.radius = 0.75f;
            lm.transform.localScale = Vector3.one * 1.6f;
            lm.radius = 1.6f;
        }

        void BuildPortal(Vector2 c)
        {
            var pos = OnGround(c.x, c.y);
            var lm = AddLandmark("portal", "Magic Gate", pos, 2.4f, Palette.Amber, 0, 0);
            var t = lm.transform;
            t.rotation = Quaternion.Euler(0, 25f, 0);
            var box = lm.gameObject.AddComponent<BoxCollider>();
            box.size = new Vector3(5.6f, 5.5f, 1.2f); box.center = new Vector3(0, 2.75f, 0);

            var ring = Prim.Make(mb => { mb.M = Matrix4x4.Translate(new Vector3(0, 2.7f, 0)); Prim.Torus(mb, 2.2f, 0.22f, 36, 8); });
            var go = Obj("Ring", ring, Mats.Glow(new Color(0.6f, 0.4f, 0.1f), new Color(1f, 0.72f, 0.25f), 2.2f, 0.7f), t, pos);
            go.transform.localRotation = Quaternion.identity;

            var inner = Prim.Make(mb => { mb.M = Matrix4x4.TRS(new Vector3(0, 2.7f, 0), Quaternion.Euler(90, 0, 0), Vector3.one); Prim.Disc(mb, 2.05f, 36, Vector3.up); Prim.Disc(mb, 2.05f, 36, Vector3.down); });
            var innerGo = Obj("Veil", inner, Mats.Transparent(new Color(0.35f, 0.2f, 0.7f, 0.35f), new Color(0.5f, 0.4f, 1f), 1.4f), t, pos, false);
            innerGo.transform.localRotation = Quaternion.identity;
            innerGo.AddComponent<Spinner>().speed = new Vector3(0, 0, 25f);

            var stone = Mats.Surface(Palette.Stone, 0.2f);
            var pillars = Prim.Make(mb =>
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    mb.M = Matrix4x4.TRS(new Vector3(s * 2.6f, 0, 0), Quaternion.Euler(0, 15, 0), Vector3.one);
                    Prim.Lathe(mb, new[] { new Vector2(0.55f, -0.2f), new Vector2(0.45f, 0.4f), new Vector2(0.35f, 3.4f), new Vector2(0.45f, 3.6f), new Vector2(0f, 3.9f) }, 6, 0.1f, s + 5, true);
                }
                mb.M = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(5.8f, 0.4f, 2.2f));
                Prim.Sphere(mb, 1, 0.2f, 9);
            });
            Obj("Pillars", pillars, stone, t, pos).transform.localRotation = Quaternion.identity;
            var l = PointLight(t, pos + Vector3.up * 2.7f, new Color(1f, 0.7f, 0.4f), 14f, 4f);
            l.gameObject.AddComponent<Flicker>().baseIntensity = 5f;
            var ps = Particles(t, pos + Vector3.up * 2.7f, new Color(0.7f, 0.55f, 1f), ParticleSystemShapeType.Circle, 2.1f, 45, 0.06f, 0.05f, 1.8f);
            ps.transform.localRotation = Quaternion.Euler(90, 0, 0);
            var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.Local;
            vel.orbitalZ = new ParticleSystem.MinMaxCurve(1.2f); vel.radial = new ParticleSystem.MinMaxCurve(-0.4f);
            vel.x = new ParticleSystem.MinMaxCurve(0f); vel.y = new ParticleSystem.MinMaxCurve(0f); vel.z = new ParticleSystem.MinMaxCurve(0f);
        }

        void BuildHome()
        {
            // MOSS's home pad at the start of the path (goal of COME_BACK and some missions).
            var pos = OnGround(RoverStart.x, RoverStart.z);
            var lm = AddLandmark("home", "Home", pos, 1.2f, Palette.Turquoise, 0, 0);
            var pad = Obj("HomePad", Prim.Make(mb => Prim.Disc(mb, 1.6f, 28, Vector3.up), "HomeDisc"), Mats.Glow(new Color(0.1f, 0.3f, 0.3f), Palette.Turquoise, 0.6f), lm.transform, pos + Vector3.up * 0.04f, false);
            var ring = Obj("HomeRing", Prim.Make(mb => { mb.M = Matrix4x4.Rotate(Quaternion.Euler(90, 0, 0)); Prim.Torus(mb, 1.65f, 0.06f, 36, 4); }, "HomeRingMesh"), Mats.Glow(Palette.Turquoise, Palette.Turquoise, 2.5f), lm.transform, pos + Vector3.up * 0.06f, false);
            ring.AddComponent<Breathe>().amount = 0.03f;
        }

        void BuildPathStones()
        {
            var mb = new MeshBuilder();
            void Along(Vector2[] line, float spacing)
            {
                for (int i = 0; i < line.Length - 1; i++)
                {
                    Vector2 a = line[i], b = line[i + 1];
                    int n = Mathf.CeilToInt(Vector2.Distance(a, b) / spacing);
                    for (int k = 0; k < n; k++)
                    {
                        Vector2 p = Vector2.Lerp(a, b, (k + R(0, 0.4f)) / n);
                        Vector2 side = new Vector2(-(b - a).y, (b - a).x).normalized * R(-0.55f, 0.55f);
                        p += side;
                        float s = R(0.45f, 0.75f);
                        mb.M = Matrix4x4.TRS(OnGround(p.x, p.y, -0.02f), Quaternion.Euler(0, R(0, 60), 0), new Vector3(s, 1, s * R(0.8f, 1.1f)));
                        Prim.Cylinder(mb, 1f, 0.9f, 0.09f, 6);
                    }
                }
            }
            Along(MainPath, 1.15f); Along(BranchPath, 1.25f); Along(PortalPath, 1.3f);
            Obj("PathStones", mb.Build("PathStones"), Mats.Glow(new Color(0.13f, 0.12f, 0.115f), Palette.Amber, 0.07f, 0.2f), root, Vector3.zero, false);
        }

        void BuildLanterns()
        {
            var pts = new[] { new Vector2(2f, -15f), new Vector2(5.8f, -5f), new Vector2(7f, 5.5f), new Vector2(3f, 15f), new Vector2(-5.8f, 15.5f), new Vector2(-2.2f, -24f) };
            var stone = new MeshBuilder(); var glow = new MeshBuilder();
            int k = 0;
            foreach (var p in pts)
            {
                var g = OnGround(p.x, p.y);
                stone.M = Matrix4x4.TRS(g, Quaternion.Euler(0, k * 40, 0), Vector3.one);
                Prim.Lathe(stone, new[] { new Vector2(0.32f, -0.1f), new Vector2(0.18f, 0.2f), new Vector2(0.14f, 0.9f), new Vector2(0.38f, 0.95f), new Vector2(0.38f, 1.02f) }, 6, 0, 0, false, true);
                stone.M = Matrix4x4.TRS(g + Vector3.up * 1.32f, Quaternion.Euler(0, k * 40, 0), Vector3.one);
                Prim.Lathe(stone, new[] { new Vector2(0.5f, 0), new Vector2(0.15f, 0.28f), new Vector2(0f, 0.36f) }, 6, 0, 0, true);
                glow.M = Matrix4x4.TRS(g + Vector3.up * 1.15f, Quaternion.identity, Vector3.one * 0.3f);
                Prim.Sphere(glow, 1);
                PointLight(root, g + Vector3.up * 1.2f, Palette.Amber, 6.5f, 1.8f);
                Occupy(p.x, p.y, 0.8f);
                k++;
            }
            Obj("Lanterns", stone.Build("Lanterns"), Mats.Surface(Palette.Stone, 0.2f), root, Vector3.zero);
            Obj("LanternGlow", glow.Build("LanternGlow"), Mats.Glow(Palette.Amber, Palette.Amber, 2.6f), root, Vector3.zero, false);
        }

        void BuildTrees()
        {
            var trunks = new MeshBuilder(); var canopyA = new MeshBuilder(); var canopyB = new MeshBuilder(); var canopyC = new MeshBuilder();
            var treeCol = new GameObject("TreeColliders"); treeCol.transform.SetParent(root, false);
            int placed = 0, tries = 0;
            while (placed < 46 && tries++ < 2000)
            {
                float a = R(0, Mathf.PI * 2), r = placed < 34 ? R(21f, 34f) : R(9f, 21f);
                var p = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
                if (!Free(p, 2.6f) || DistToPaths(p) < 2.8f || Vector2.Distance(p, new Vector2(RoverStart.x, RoverStart.z)) < 6f) continue;
                if (r < 22f && p.y < 8f && Mathf.Abs(p.x) < 9f) continue; // keep the view from the start to the ancient tree clear
                Occupy(p.x, p.y, 2.6f);
                float h = R(2.6f, 4.8f), tr = h * 0.08f;
                var g = OnGround(p.x, p.y, -0.2f);
                trunks.M = Matrix4x4.TRS(g, Quaternion.Euler(R(-5, 5), R(0, 360), R(-5, 5)), Vector3.one);
                Prim.Lathe(trunks, new[] { new Vector2(tr * 1.7f, 0), new Vector2(tr, h * 0.3f), new Vector2(tr * 0.7f, h) }, 6, 0.15f, placed);
                var cb = placed % 3 == 0 ? canopyB : placed % 5 == 1 ? canopyC : canopyA;
                int blobs = 2 + placed % 2;
                float canopyR = h * 0.25f;
                for (int b = 0; b < blobs; b++)
                {
                    float s = h * R(0.42f, 0.6f);
                    cb.M = Matrix4x4.TRS(g + new Vector3(R(-0.7f, 0.7f), h * (0.85f + b * 0.18f), R(-0.7f, 0.7f)), Quaternion.Euler(0, R(0, 360), 0), new Vector3(s, s * R(0.7f, 1f), s));
                    Prim.Sphere(cb, 1, 0.35f, placed * 7 + b);
                }
                var cc = new GameObject("T"); cc.transform.SetParent(treeCol.transform, false); cc.transform.position = g;
                var cap = cc.AddComponent<CapsuleCollider>(); cap.radius = tr * 1.6f + 0.15f; cap.height = h; cap.center = new Vector3(0, h / 2, 0);
                var cs = cc.AddComponent<SphereCollider>(); cs.radius = canopyR; cs.center = new Vector3(0, h * 0.95f, 0);
                placed++;
            }
            Obj("TreeTrunks", trunks.Build("Trunks"), Mats.Surface(Palette.Bark, 0.15f), root, Vector3.zero);
            Obj("CanopyGreen", canopyA.Build("CanopyA"), Mats.Surface(new Color(0.07f, 0.24f, 0.15f), 0.15f), root, Vector3.zero);
            // pink blossom trees (turquoise is reserved for the Ancient Tree so it stays unmistakable)
            Obj("CanopyBlossom", canopyB.Build("CanopyB"), Mats.Glow(new Color(0.42f, 0.16f, 0.30f), new Color(1f, 0.45f, 0.75f), 0.10f, 0.15f), root, Vector3.zero);
            Obj("CanopyViolet", canopyC.Build("CanopyC"), Mats.Glow(new Color(0.17f, 0.10f, 0.28f), Palette.Violet, 0.08f, 0.15f), root, Vector3.zero);
        }

        void BuildRocks()
        {
            var mb = new MeshBuilder();
            var colRoot = new GameObject("RockColliders"); colRoot.transform.SetParent(root, false);
            int placed = 0, tries = 0;
            while (placed < 30 && tries++ < 1500)
            {
                var p = new Vector2(R(-32, 32), R(-32, 32));
                float s = R(0.4f, 1.6f);
                if (p.magnitude > 33f || !Free(p, s) || DistToPaths(p) < 1.6f) continue;
                Occupy(p.x, p.y, s);
                var g = OnGround(p.x, p.y, -s * 0.15f);
                mb.M = Matrix4x4.TRS(g, Quaternion.Euler(R(-10, 10), R(0, 360), 0), new Vector3(s * R(1f, 1.5f), s * R(0.5f, 0.8f), s));
                Prim.Sphere(mb, 1, 0.45f, placed + 40);
                if (s > 0.7f)
                {
                    var c = new GameObject("R"); c.transform.SetParent(colRoot.transform, false); c.transform.position = g;
                    c.AddComponent<SphereCollider>().radius = s * 0.5f;
                }
                placed++;
            }
            Obj("Rocks", mb.Build("Rocks"), Mats.Surface(Palette.Stone, 0.25f), root, Vector3.zero);
        }

        void BuildFlowersAndGrass()
        {
            var grassA = new MeshBuilder(); var grassB = new MeshBuilder();
            var stalks = new MeshBuilder(); var bulbC = new MeshBuilder(); var bulbV = new MeshBuilder(); var bulbY = new MeshBuilder();
            for (int i = 0; i < 4200; i++)
            {
                var p = new Vector2(R(-36, 36), R(-36, 36));
                if (p.magnitude > 37f) continue;
                if (Vector2.Distance(p, PondCenter) < PondRadius + 0.6f || DistToPaths(p) < 0.9f) continue;
                var g = OnGround(p.x, p.y, -0.05f);
                var mb = i % 2 == 0 ? grassA : grassB;
                float h = R(0.15f, 0.45f);
                mb.M = Matrix4x4.TRS(g, Quaternion.Euler(R(-15, 15), R(0, 360), R(-15, 15)), new Vector3(1, h, 1));
                Prim.Cylinder(mb, 0.045f, 0f, 1f, 3, false);
            }
            for (int i = 0; i < 260; i++)
            {
                var p = new Vector2(R(-33, 33), R(-33, 33));
                if (p.magnitude > 34f || Vector2.Distance(p, PondCenter) < PondRadius + 0.8f || DistToPaths(p) < 1.1f) continue;
                bool nearKey = false;
                foreach (var lm in landmarks) if (lm.label == "red_flower" && Vector2.Distance(p, new Vector2(lm.transform.position.x, lm.transform.position.z)) < 5f) nearKey = true;
                if (nearKey) continue;
                var g = OnGround(p.x, p.y);
                float h = R(0.3f, 0.8f);
                stalks.M = Matrix4x4.TRS(g, Quaternion.Euler(R(-12, 12), 0, R(-12, 12)), Vector3.one);
                Prim.Cylinder(stalks, 0.025f, 0.015f, h, 4, false);
                var b = i % 3 == 0 ? bulbV : i % 3 == 1 ? bulbC : bulbY;
                b.M = Matrix4x4.TRS(stalks.M.MultiplyPoint3x4(new Vector3(0, h, 0)), Quaternion.identity, Vector3.one * R(0.07f, 0.14f));
                Prim.Sphere(b, 1);
            }
            Obj("GrassA", grassA.Build("GrassA"), Mats.Surface(new Color(0.07f, 0.26f, 0.15f), 0.2f), root, Vector3.zero, false);
            Obj("GrassB", grassB.Build("GrassB"), Mats.Glow(new Color(0.05f, 0.22f, 0.2f), Palette.Turquoise, 0.08f, 0.2f), root, Vector3.zero, false);
            Obj("GlowStalks", stalks.Build("Stalks"), Mats.Surface(new Color(0.08f, 0.25f, 0.18f), 0.2f), root, Vector3.zero, false);
            Obj("GlowBulbsCyan", bulbC.Build("BulbC"), Mats.Glow(Palette.Turquoise, Palette.Turquoise, 3f), root, Vector3.zero, false);
            Obj("GlowBulbsViolet", bulbV.Build("BulbV"), Mats.Glow(Palette.Violet, Palette.Violet, 3f), root, Vector3.zero, false);
            Obj("GlowBulbsYellow", bulbY.Build("BulbY"), Mats.Glow(new Color(1f, 0.85f, 0.4f), new Color(1f, 0.8f, 0.35f), 2.5f), root, Vector3.zero, false);
        }

        void BuildFireflies()
        {
            var ps = Particles(root, new Vector3(0, 1.8f, 0), Palette.Amber, ParticleSystemShapeType.Box, 1f, 26, 0.07f, 0.1f, 7f);
            var sh = ps.shape; sh.scale = new Vector3(64f, 3f, 64f);
            var main = ps.main; main.maxParticles = 260;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.75f, 0.35f), new Color(0.4f, 1f, 0.85f));
            var noise = ps.noise; noise.strength = 0.9f; noise.frequency = 0.25f;
        }

        void BuildWisps()
        {
            var cols = new[] { Palette.Turquoise, Palette.Violet, Palette.Amber, new Color(0.6f, 1f, 0.7f) };
            var mesh = Prim.Make(mb => Prim.Sphere(mb, 1));
            var mats = new Material[cols.Length];
            for (int i = 0; i < cols.Length; i++) mats[i] = Mats.Glow(cols[i], cols[i], 3.5f);
            int placed = 0, tries = 0;
            while (placed < 30 && tries++ < 500)
            {
                var p = new Vector2(R(-28, 28), R(-28, 28));
                if (p.magnitude > 29f) continue;
                var w = Obj("Wisp", mesh, mats[placed % cols.Length], root, OnGround(p.x, p.y, R(1.6f, 4.2f)), false);
                w.transform.localScale = Vector3.one * R(0.12f, 0.26f);
                var b = w.AddComponent<Bobber>(); b.amplitude = R(0.2f, 0.5f); b.speed = R(0.5f, 1.2f); b.phase = R(0, 6.28f); b.drift = R(0.3f, 1.2f);
                if (placed % 6 == 0) PointLight(w.transform, w.transform.position, cols[placed % cols.Length], 4f, 1.2f);
                placed++;
            }
            // slow drifting "jelly spores" over the pond
            var ps = Particles(root, new Vector3(PondCenter.x, WaterLevel + 0.8f, PondCenter.y), new Color(0.75f, 0.6f, 1f), ParticleSystemShapeType.Circle, PondRadius, 6, 0.22f, 0.5f, 6f);
            ps.transform.rotation = Quaternion.Euler(90, 0, 0);
        }

        /// <summary>Generic soft glowing particle emitter.</summary>
        public static ParticleSystem Particles(Transform parent, Vector3 pos, Color col, ParticleSystemShapeType shape, float radius,
                                               float rate, float size, float rise, float life)
        {
            var go = new GameObject("Particles");
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true; main.prewarm = true; main.duration = 5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.6f, life);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.15f);
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.5f, size * 1.4f);
            main.startColor = col;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;
            main.gravityModifier = -rise * 0.05f;
            var em = ps.emission; em.rateOverTime = rate;
            var sh = ps.shape; sh.shapeType = shape; sh.radius = radius;
            var noise = ps.noise; noise.enabled = true; noise.strength = 0.35f; noise.frequency = 0.5f;
            var colLife = ps.colorOverLifetime; colLife.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                         new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.25f), new GradientAlphaKey(0.8f, 0.7f), new GradientAlphaKey(0, 1) });
            colLife.color = grad;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = Mats.Particle(Color.white, 4f);
            r.shadowCastingMode = ShadowCastingMode.Off;
            go.SetActive(true);
            return ps;
        }
    }

    public class Spinner : MonoBehaviour
    {
        public Vector3 speed;
        void Update() => transform.Rotate(speed * Time.deltaTime, Space.Self);
    }

    public class Flicker : MonoBehaviour
    {
        public float baseIntensity = 3f;
        Light l;
        void Awake() => l = GetComponent<Light>();
        void Update() { if (l) l.intensity = baseIntensity * (0.85f + 0.15f * Mathf.PerlinNoise(Time.time * 2f, 0.3f)); }
    }
}

