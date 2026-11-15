using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Mossbound
{
    public enum RoverMode { Idle, Thinking, Moving, Arrived, Error, Manual, NeedsHelp }

    /// <summary>
    /// MOSS rover: procedural cute model with an expressive face, speech bubble, bounded motion primitives,
    /// path-following navigation, and obstacle + proximity sensors.
    /// </summary>
    public class RoverController : MonoBehaviour
    {
        public const int SignLayer = 3;  // player-only name signs: never rendered by MOSS's AI cameras
        public const int RoverLayer = 2; // "Ignore Raycast": keeps sensors & vision cameras from seeing the rover itself

        public CharacterController cc;
        public Transform visual;
        public Camera visionCam;
        public Transform Head => head;
        public RoverMode Mode { get; private set; } = RoverMode.Idle;
        public bool IsExecuting { get; private set; }
        public float maxSpeed = CommandParser.MaxSpeed;
        /// <summary>Set by the HUD: true when keyboard driving is allowed (not typing, no overlay open).</summary>
        public static bool DriveInputAllowed = true;
        public float ManualSeconds { get; private set; }
        public bool PlayerDriving { get; private set; }
        public bool Dancing => danceT >= 0f;
        float danceT = -1f, manualIdle;
        readonly List<Transform> carried = new List<Transform>();
        readonly List<(Transform t, Vector3 to, float s)> flying = new List<(Transform, Vector3, float)>();

        // look
        Material eyeMat, ringMat, tipMat, cheekMat, bodyMat;
        Light underGlow, headlight;
        readonly List<Transform> wheels = new List<Transform>();
        Transform ring, head, eyeL, eyeR, antenna, sprout;
        Vector3 eyeScale = new Vector3(0.15f, 0.2f, 0.08f);
        float vy, wheelSpin, speedNow, blinkAt, blinkT = -1f, hopT = -1f, spinT = -1f, wobble, lookYaw, lookTarget;
        ParticleSystem sparkles;
        // speech bubble
        Canvas bubble; Text bubbleText; Image bubbleBg; float bubbleUntil; string bubbleBase; bool bubbleDots;

        // motion state
        Command cmd;
        float t, yawRemaining;
        string result;
        bool avoided;
        List<Vector3> path; int pathIndex; Vector3 navGoal; float navArrive, navStartDist, progressT; Vector3 progressPos; bool facing;

        // ------------------------------------------------------------------ construction
        public static RoverController Create(Transform parent, Vector3 pos, float yaw)
        {
            var go = new GameObject("MOSS_Rover");
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            var r = go.AddComponent<RoverController>();
            r.BuildVisual();
            r.cc = go.AddComponent<CharacterController>();
            r.cc.radius = 0.62f; r.cc.height = 1.3f; r.cc.center = new Vector3(0, 0.7f, 0);
            r.cc.slopeLimit = 40f; r.cc.stepOffset = 0.3f; r.cc.skinWidth = 0.04f;
            SetLayer(go, RoverLayer);
            return r;
        }

        static void SetLayer(GameObject g, int layer) { g.layer = layer; foreach (Transform c in g.transform) SetLayer(c.gameObject, layer); }

        GameObject Part(string name, Mesh mesh, Material mat, Transform parent, bool shadows = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
            mr.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return go;
        }

        static Mesh Ball(int sub = 2, float jitter = 0, int seed = 0) => Prim.Make(mb => Prim.Sphere(mb, sub, jitter, seed));

        void BuildVisual()
        {
            visual = new GameObject("Visual").transform;
            visual.SetParent(transform, false);

            bodyMat = Mats.Glow(new Color(0.62f, 0.78f, 0.74f), new Color(0.55f, 0.9f, 0.8f), 0.03f, 0.6f);
            var dark = Mats.Surface(Palette.Graphite, 0.4f);
            var visor = Mats.Surface(new Color(0.015f, 0.025f, 0.04f), 0.95f);
            var amberTrim = Mats.Glow(Palette.Amber * 0.8f, Palette.Amber, 0.9f, 0.5f);
            var leaf = Mats.Glow(new Color(0.25f, 0.7f, 0.3f), new Color(0.3f, 1f, 0.5f), 0.35f, 0.4f);
            eyeMat = Mats.Glow(Palette.Turquoise, Palette.Turquoise, 5f);
            ringMat = Mats.Glow(Palette.Turquoise, Palette.Turquoise, 4f);
            tipMat = Mats.Glow(Palette.Amber, Palette.Amber, 6f);
            cheekMat = Mats.Glow(new Color(1f, 0.5f, 0.65f), new Color(1f, 0.35f, 0.55f), 1.4f);

            // Round little body
            var body = Part("Body", Ball(), bodyMat, visual); body.transform.localPosition = new Vector3(0, 0.6f, 0); body.transform.localScale = new Vector3(1.05f, 0.72f, 1.1f);
            var belt = Part("Belt", Prim.Make(mb => { mb.M = Matrix4x4.Rotate(Quaternion.Euler(90, 0, 0)); Prim.Torus(mb, 0.53f, 0.035f, 32, 5); }), amberTrim, visual, false);
            belt.transform.localPosition = new Vector3(0, 0.55f, 0); belt.transform.localScale = new Vector3(1f, 1.05f, 1f);

            // Big head (cuteness = big head + big eyes)
            head = new GameObject("Head").transform; head.SetParent(visual, false); head.localPosition = new Vector3(0, 1.12f, 0.02f);
            var skull = Part("Skull", Ball(), bodyMat, head); skull.transform.localScale = new Vector3(0.95f, 0.78f, 0.88f);
            var face = Part("Face", Ball(), visor, head); face.transform.localPosition = new Vector3(0, -0.02f, 0.2f); face.transform.localScale = new Vector3(0.74f, 0.52f, 0.5f);
            eyeL = Part("EyeL", Ball(1), eyeMat, head, false).transform; eyeL.localPosition = new Vector3(-0.15f, 0.0f, 0.44f); eyeL.localScale = eyeScale;
            eyeR = Part("EyeR", Ball(1), eyeMat, head, false).transform; eyeR.localPosition = new Vector3(0.15f, 0.0f, 0.44f); eyeR.localScale = eyeScale;
            for (int s = -1; s <= 1; s += 2)
            {
                var ch = Part("Cheek", Ball(1), cheekMat, head, false); ch.transform.localPosition = new Vector3(s * 0.27f, -0.13f, 0.38f); ch.transform.localScale = new Vector3(0.09f, 0.05f, 0.03f);
            }
            // Antenna on a pivot so it can wobble
            antenna = new GameObject("Antenna").transform; antenna.SetParent(head, false); antenna.localPosition = new Vector3(0.22f, 0.3f, -0.05f);
            Part("Stalk", Prim.Make(mb => Prim.Cylinder(mb, 0.022f, 0.014f, 0.42f, 5)), dark, antenna);
            var tip = Part("Tip", Ball(1), tipMat, antenna, false); tip.transform.localPosition = new Vector3(0, 0.47f, 0); tip.transform.localScale = Vector3.one * 0.12f;
            // Little leaf sprout (MOSS!)
            sprout = new GameObject("Sprout").transform; sprout.SetParent(head, false); sprout.localPosition = new Vector3(-0.12f, 0.33f, 0f);
            Part("Stem", Prim.Make(mb => Prim.Cylinder(mb, 0.018f, 0.012f, 0.16f, 4)), leaf, sprout);
            for (int s = -1; s <= 1; s += 2)
            {
                var lf = Part("Leaf", Ball(1), leaf, sprout); lf.transform.localPosition = new Vector3(s * 0.08f, 0.17f, 0); lf.transform.localRotation = Quaternion.Euler(0, 0, -s * 35f); lf.transform.localScale = new Vector3(0.16f, 0.05f, 0.09f);
            }

            // Chunky toy wheels
            var wheelMesh = Prim.Make(mb => { mb.M = Matrix4x4.Scale(new Vector3(0.24f, 0.5f, 0.5f)); Prim.Sphere(mb, 1); }, "Wheel");
            var hubMesh = Prim.Make(mb => { mb.M = Matrix4x4.Scale(new Vector3(0.26f, 0.2f, 0.2f)); Prim.Sphere(mb, 1); }, "Hub");
            var hubMat = Mats.Glow(Palette.Turquoise, Palette.Turquoise, 2f);
            foreach (var p in new[] { new Vector3(-0.52f, 0.25f, 0.38f), new Vector3(0.52f, 0.25f, 0.38f), new Vector3(-0.52f, 0.25f, -0.38f), new Vector3(0.52f, 0.25f, -0.38f) })
            {
                var pivot = new GameObject("Wheel").transform; pivot.SetParent(visual, false); pivot.localPosition = p;
                Part("Tyre", wheelMesh, dark, pivot);
                Part("Hub", hubMesh, hubMat, pivot, false).transform.localPosition = new Vector3(Mathf.Sign(p.x) * 0.03f, 0, 0);
                wheels.Add(pivot);
            }

            ring = Part("StatusRing", Prim.Make(mb => { mb.M = Matrix4x4.Rotate(Quaternion.Euler(90, 0, 0)); Prim.Torus(mb, 0.9f, 0.035f, 40, 4); }), ringMat, visual, false).transform;
            ring.localPosition = new Vector3(0, 0.06f, 0);

            var hl = new GameObject("Headlight"); hl.transform.SetParent(head, false);
            hl.transform.localPosition = new Vector3(0, 0f, 0.45f); hl.transform.localRotation = Quaternion.Euler(4, 0, 0);
            headlight = hl.AddComponent<Light>(); headlight.type = LightType.Spot; headlight.range = 14f; headlight.spotAngle = 70f;
            headlight.intensity = 0.5f; headlight.color = new Color(0.85f, 0.95f, 1f); headlight.shadows = LightShadows.None;

            var ug = new GameObject("UnderGlow"); ug.transform.SetParent(visual, false); ug.transform.localPosition = new Vector3(0, 0.4f, 0);
            underGlow = ug.AddComponent<Light>(); underGlow.type = LightType.Point; underGlow.range = 3.5f; underGlow.intensity = 2.5f; underGlow.color = Palette.Turquoise;

            sparkles = EnvironmentController.Particles(visual, transform.position + Vector3.up * 1.2f, Palette.Amber, ParticleSystemShapeType.Sphere, 0.6f, 0, 0.09f, 0.6f, 1.4f);
            var sm = sparkles.main; sm.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 3f); sm.simulationSpace = ParticleSystemSimulationSpace.World;
            sm.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.4f), new Color(0.5f, 1f, 0.9f));
            sparkles.transform.localPosition = new Vector3(0, 1.3f, 0);

            // Vision camera (rendered on demand at 4 yaws by CameraCapture)
            var camGo = new GameObject("VisionCamera"); camGo.transform.SetParent(transform, false);
            camGo.transform.localPosition = new Vector3(0, 1.35f, 0.1f); camGo.transform.localRotation = Quaternion.Euler(6f, 0, 0);
            visionCam = camGo.AddComponent<Camera>();
            visionCam.cullingMask = ~((1 << RoverLayer) | (1 << 5) | (1 << SignLayer));
            visionCam.nearClipPlane = 0.15f; visionCam.farClipPlane = 140f;
            visionCam.clearFlags = CameraClearFlags.Skybox; visionCam.backgroundColor = Palette.Fog;
            visionCam.depth = -5;

            BuildBubble();
            blinkAt = 2f;
        }

        void BuildBubble()
        {
            var go = new GameObject("SpeechBubble", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0, 2.45f, 0);
            bubble = go.AddComponent<Canvas>(); bubble.renderMode = RenderMode.WorldSpace; bubble.sortingOrder = 5;
            var rt = (RectTransform)go.transform; rt.sizeDelta = new Vector2(360, 70); rt.localScale = Vector3.one * 0.0085f;
            bubbleBg = UIKit.Panel(go.transform, "Bg", new Color(0.03f, 0.07f, 0.09f, 0.88f), 1.4f);
            UIKit.Stretch(bubbleBg.rectTransform);
            bubbleBg.transform.Find("Outline").GetComponent<Image>().color = new Color(0.12f, 0.95f, 0.82f, 0.7f);
            var tail = UIKit.Rect("Tail", go.transform); UIKit.Place(tail, new Vector2(0.5f, 0), new Vector2(0, -9), new Vector2(16, 16));
            var ti = tail.gameObject.AddComponent<Image>(); ti.color = new Color(0.03f, 0.07f, 0.09f, 0.88f); tail.localRotation = Quaternion.Euler(0, 0, 45);
            bubbleText = UIKit.Label(go.transform, "", 24, Palette.UiText, TextAnchor.MiddleCenter, FontStyle.Bold);
            UIKit.Stretch(bubbleText.rectTransform, 14, 14, 4, 4);
            bubbleText.resizeTextForBestFit = true; bubbleText.resizeTextMinSize = 14; bubbleText.resizeTextMaxSize = 24;
            go.SetActive(false);
        }

        // ------------------------------------------------------------------ personality API
        public void Say(string text, float seconds = 3.5f, bool dots = false)
        {
            if (!bubble) return;
            bubbleBase = text; bubbleDots = dots;
            bubbleText.text = text;
            bubbleUntil = seconds < 0 ? float.MaxValue : Time.time + seconds;
            bubble.gameObject.SetActive(true);
        }

        public void Dance() { danceT = 0f; if (sparkles) sparkles.Emit(15); }

        /// <summary>Give MOSS an item to carry (a little glowing orb circling its head).</summary>
        public Transform Carry(Color c, float size = 0.16f, Vector3? fromWorld = null)
        {
            var go = Part("Carried", Ball(1), Mats.Glow(c, c, 3.5f), transform, false);
            go.layer = RoverLayer;
            go.transform.localScale = Vector3.one * size;
            go.transform.localPosition = new Vector3(0, 2f, 0);
            if (fromWorld.HasValue) go.transform.position = fromWorld.Value;
            carried.Add(go.transform);
            if (sparkles) sparkles.Emit(12);
            return go.transform;
        }

        public int CarriedCount => carried.Count;

        /// <summary>Hand all carried items over: they fly to the given point and fade away.</summary>
        public void DropCarried(Vector3 toWorld)
        {
            foreach (var c in carried) { c.SetParent(null, true); flying.Add((c, toWorld, 0f)); }
            carried.Clear();
        }

        void AnimateItems(float dt)
        {
            for (int i = 0; i < carried.Count; i++)
            {
                float a = Time.time * 2.2f + i * Mathf.PI * 2f / carried.Count;
                var want = new Vector3(Mathf.Cos(a) * 0.75f, 2.0f + 0.08f * Mathf.Sin(Time.time * 3f + i), Mathf.Sin(a) * 0.75f);
                carried[i].localPosition = Vector3.Lerp(carried[i].localPosition, want, 1f - Mathf.Exp(-6f * dt));
            }
            for (int i = flying.Count - 1; i >= 0; i--)
            {
                var (t, to, s) = flying[i];
                if (!t) { flying.RemoveAt(i); continue; }
                s += dt;
                t.position = Vector3.Lerp(t.position, to + Vector3.up * (1f + Mathf.Sin(s * 3f)), 1f - Mathf.Exp(-3f * dt));
                t.localScale *= Mathf.Exp(-0.9f * dt);
                if (s > 1.8f) { Destroy(t.gameObject); flying.RemoveAt(i); } else flying[i] = (t, to, s);
            }
        }

        public void Hop(int sparkleCount = 25) { hopT = 0f; wobble = 1f; if (sparkles && sparkleCount > 0) sparkles.Emit(sparkleCount); }
        public void Spin() { spinT = 0f; wobble = 1f; }
        public void LookToward(float bearing) => lookTarget = Mathf.Clamp(bearing, -75f, 75f);

        static readonly string[] Pokes = { "Hehe, that tickles!", "Hi there! Where to next?", "Beep boop! ♪", "I love this garden!", "Ready for adventure!", "Did you see the fireflies?" };

        public void Poke()
        {
            if (Mode == RoverMode.NeedsHelp) { Say("Please help me! Drive me with WASD, or tell me what to do.", 3f); wobble = 1f; return; }
            if (Mode == RoverMode.Thinking || IsExecuting) { Say("Shh... I'm concentrating!", 2f); wobble = 1f; return; }
            Say(Pokes[UnityEngine.Random.Range(0, Pokes.Length)], 2.5f);
            if (UnityEngine.Random.value < 0.5f) Hop(15); else Spin();
        }

        // ------------------------------------------------------------------ state + visuals
        public void SetMode(RoverMode m)
        {
            if (m == Mode) return;
            if (m == RoverMode.Arrived) Hop(40);
            if (m == RoverMode.Error) wobble = 1f;
            Mode = m;
        }

        Color ModeColor()
        {
            switch (Mode)
            {
                case RoverMode.Thinking: return Palette.Violet;
                case RoverMode.Moving: return Palette.Amber;
                case RoverMode.Arrived: return new Color(0.4f, 1f, 0.55f);
                case RoverMode.Error: return Palette.Ember;
                case RoverMode.Manual: return new Color(0.45f, 0.85f, 1f);
                case RoverMode.NeedsHelp: return Mathf.Repeat(Time.time, 0.8f) < 0.4f ? Palette.Ember : Palette.Amber;
                default: return Palette.Turquoise;
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (!IsExecuting) ManualDrive(dt);
            AnimateVisual(dt);
            AnimateItems(dt);
        }

        void LateUpdate()
        {
            if (!bubble || !bubble.gameObject.activeSelf) return;
            var cam = Camera.main;
            if (cam) bubble.transform.rotation = Quaternion.LookRotation(bubble.transform.position - cam.transform.position);
            if (bubbleDots) bubbleText.text = bubbleBase + new string('.', 1 + (int)(Time.time * 3f) % 3);
            if (Time.time > bubbleUntil) bubble.gameObject.SetActive(false);
        }

        void AnimateVisual(float dt)
        {
            Color c = ModeColor();
            float pulse = Mode == RoverMode.Thinking ? 0.55f + 0.45f * Mathf.Sin(Time.time * 7f) : 1f;
            Mats.SetEmission(eyeMat, Mode == RoverMode.Error ? Palette.Ember : Mode == RoverMode.Thinking ? Color.Lerp(Palette.Turquoise, Palette.Violet, 0.5f) : Palette.Turquoise, 5f);
            Mats.SetEmission(ringMat, c, (Mode == RoverMode.Idle ? 2.5f : 5f) * pulse);
            Mats.SetEmission(tipMat, Mode == RoverMode.Thinking ? Palette.Violet : Palette.Amber, 3f + 3f * Mathf.Abs(Mathf.Sin(Time.time * (Mode == RoverMode.Thinking ? 6f : 1.5f))));
            underGlow.color = c; underGlow.intensity = 1.3f * pulse;
            if (ring) ring.Rotate(0, 0, (Mode == RoverMode.Thinking ? 220f : 25f) * dt, Space.Self);

            wheelSpin += speedNow * dt / 0.25f * Mathf.Rad2Deg;
            foreach (var w in wheels) w.localRotation = Quaternion.Euler(wheelSpin, 0, 0);

            // ---- face: blink, mood, gaze
            blinkAt -= dt;
            if (blinkAt <= 0f) { blinkT = 0f; blinkAt = UnityEngine.Random.Range(2.2f, 5f); }
            float blink = 1f;
            if (blinkT >= 0f) { blinkT += dt; blink = blinkT < 0.07f ? 0.12f : 1f; if (blinkT > 0.14f) blinkT = -1f; }
            float squash = Mode == RoverMode.Arrived || Dancing ? 0.38f : Mode == RoverMode.Error || Mode == RoverMode.NeedsHelp ? 0.55f : 1f;   // ^ ^ happy squint / worried
            Vector3 gaze = Vector3.zero;
            if (Mode == RoverMode.Thinking) gaze = new Vector3(Mathf.Sin(Time.time * 2.3f) * 0.05f, 0.03f + Mathf.Sin(Time.time * 1.7f) * 0.015f, 0);
            else if (Mode == RoverMode.NeedsHelp) gaze = new Vector3(Mathf.Sin(Time.time * 5f) * 0.06f, -0.02f, 0);
            else if (Mode == RoverMode.Arrived) gaze = new Vector3(0, 0.035f, 0);
            foreach (var (e, sx) in new[] { (eyeL, -0.15f), (eyeR, 0.15f) })
            {
                e.localScale = Vector3.Lerp(e.localScale, new Vector3(eyeScale.x, eyeScale.y * squash * blink, eyeScale.z), 1f - Mathf.Exp(-25f * dt));
                e.localPosition = Vector3.Lerp(e.localPosition, new Vector3(sx, 0f, 0.44f) + gaze, 1f - Mathf.Exp(-10f * dt));
            }

            // ---- head: look toward the target, tilt while thinking, spin when poked
            if (Mode != RoverMode.Thinking && !IsExecuting && Mode != RoverMode.Arrived) lookTarget = Mathf.MoveTowards(lookTarget, 0f, 20f * dt);
            lookYaw = Mathf.LerpAngle(lookYaw, lookTarget, 1f - Mathf.Exp(-5f * dt));
            float tilt = Mode == RoverMode.Thinking ? 12f * Mathf.Sin(Time.time * 1.3f) : Mode == RoverMode.NeedsHelp ? 14f * Mathf.Sin(Time.time * 3f) : 0f;
            float spin = 0f, danceRoll = 0f, danceHop = 0f;
            if (spinT >= 0f) { spinT += dt; spin = Mathf.SmoothStep(0, 360, spinT / 0.7f); if (spinT > 0.7f) spinT = -1f; }
            if (danceT >= 0f)
            {
                // little dance: head bop, body wiggle, bouncing, and a spin at the end
                danceT += dt;
                tilt += 18f * Mathf.Sin(danceT * 9f);
                danceRoll = 10f * Mathf.Sin(danceT * 9f + 1f);
                danceHop = Mathf.Abs(Mathf.Sin(danceT * Mathf.PI * 2f)) * 0.18f;
                if (danceT > 2.2f) spin += Mathf.SmoothStep(0, 360, (danceT - 2.2f) / 0.8f);
                wobble = 1f;
                if (danceT > 3f) { danceT = -1f; Hop(20); }
            }
            head.localRotation = Quaternion.Euler(0, lookYaw + spin, tilt);

            // ---- antenna & sprout wobble (spring-ish)
            wobble = Mathf.MoveTowards(wobble, 0f, dt * 0.9f);
            float accelKick = Mathf.Clamp(speedNow, -3f, 3f) * 4f;
            antenna.localRotation = Quaternion.Euler(-accelKick + Mathf.Sin(Time.time * 14f) * 25f * wobble, 0, Mathf.Sin(Time.time * 2f) * 4f);
            sprout.localRotation = Quaternion.Euler(Mathf.Sin(Time.time * 3f) * 6f + Mathf.Sin(Time.time * 15f) * 15f * wobble, 0, 0);

            // ---- body: align to terrain, idle bob, hops
            if (Physics.Raycast(transform.position + Vector3.up * 1.5f, Vector3.down, out var hit, 4f, ~(1 << RoverLayer), QueryTriggerInteraction.Ignore))
            {
                var target = Quaternion.FromToRotation(Vector3.up, hit.normal) * transform.rotation;
                visual.rotation = Quaternion.Slerp(visual.rotation, target, 1f - Mathf.Exp(-8f * dt));
            }
            float hop = 0f;
            if (hopT >= 0f) { hopT += dt; hop = Mathf.Abs(Mathf.Sin(hopT * Mathf.PI * 2.6f)) * 0.32f * Mathf.Clamp01(1.2f - hopT); if (hopT > 1.2f) hopT = -1f; }
            float bob = Mode == RoverMode.Idle || Mode == RoverMode.Arrived ? 0.03f * Mathf.Sin(Time.time * 2.4f) : 0.012f * Mathf.Sin(Time.time * 9f);
            visual.localPosition = new Vector3(0, bob + hop + danceHop, 0);
            if (danceRoll != 0f) visual.rotation = visual.rotation * Quaternion.Euler(0, 0, danceRoll);
        }

        // ------------------------------------------------------------------ sensors
        public bool ObstacleAhead(out float distance) => ObstacleAhead(out distance, out _);

        public bool ObstacleAhead(out float distance, out Collider what) { distance = Probe(0f, 3f, out what); return distance < 1.6f; }

        /// <summary>Sphere-cast probe at a yaw offset from forward. Returns free distance (max if clear).</summary>
        float Probe(float yawOffset, float max, out Collider what)
        {
            what = null;
            float distance = max;
            var dir = Quaternion.Euler(0, yawOffset, 0) * transform.forward;
            var origin = transform.position + Vector3.up * 0.7f;
            var hits = Physics.SphereCastAll(origin, 0.5f, dir, max, ~(1 << RoverLayer), QueryTriggerInteraction.Ignore);
            var ground = EnvironmentController.Instance ? EnvironmentController.Instance.groundCollider : null;
            foreach (var h in hits)
            {
                if (h.collider == ground) continue;
                if (h.point.y > transform.position.y + 1.8f) continue; // canopies overhead are not obstacles
                float d = h.distance;
                if (d <= 0f)
                {
                    var cp = h.collider.ClosestPoint(origin) - transform.position;
                    if (Vector3.Dot(cp, dir) <= 0f) continue;
                }
                if (d < distance) { distance = d; what = h.collider; }
            }
            return distance;
        }

        static string[] Tokens(string s)
        {
            var generic = new HashSet<string> { "the", "a", "glowing", "largest", "biggest", "big", "huge", "ancient" };
            var list = new List<string>();
            foreach (var w in (s ?? "").ToLowerInvariant().Split('_', ' ', '-'))
            {
                if (w.Length == 0 || generic.Contains(w)) continue;
                list.Add(w.Length > 3 && w.EndsWith("s") ? w.Substring(0, w.Length - 1) : w);
            }
            return list.ToArray();
        }

        static bool IsTarget(Collider c, string target)
        {
            var lm = c ? c.GetComponentInParent<Landmark>() : null;
            if (!lm) return false;
            var have = new HashSet<string>(Tokens(lm.label));
            var want = Tokens(target);
            if (want.Length == 0) return false;
            foreach (var w in want) if (!have.Contains(w)) return false;
            return true;
        }

        /// <summary>Landmarks physically close to the rover (proximity sensor).</summary>
        public string[] Nearby(float range = 2.6f)
        {
            var list = new List<string>();
            var env = EnvironmentController.Instance;
            if (!env) return list.ToArray();
            var p = new Vector2(transform.position.x, transform.position.z);
            foreach (var lm in env.landmarks)
            {
                float d = Vector2.Distance(p, new Vector2(lm.transform.position.x, lm.transform.position.z)) - lm.radius;
                if (d < range) list.Add(lm.label);
            }
            return list.ToArray();
        }

        public float Heading => transform.eulerAngles.y;

        // ------------------------------------------------------------------ motion primitives
        public void Begin(Command c)
        {
            cmd = c; t = 0f; result = "completed"; IsExecuting = true; avoided = false; path = null;
            yawRemaining = 0f;
        }

        /// <summary>Start a bounded navigation leg toward a world point that came from the VLM's bounding box.</summary>
        public void BeginNavigate(Command c, Vector3 goal, float arriveRadius = 1.9f)
        {
            Begin(c);
            navGoal = goal;
            navArrive = arriveRadius;
            navStartDist = FlatDist(transform.position, goal);
            path = NavGrid.Instance != null ? NavGrid.Instance.FindPath(transform.position, goal) : null;
            if (path == null) path = new List<Vector3> { goal };
            pathIndex = 0; progressT = 0f; progressPos = transform.position; facing = false;
        }

        public List<Vector3> CurrentPath => path;
        public string LastResult => result;
        public void Halt() { IsExecuting = false; speedNow = 0; cmd = null; path = null; }

        static float FlatDist(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        float YawTo(Vector3 p)
        {
            var d = p - transform.position; d.y = 0;
            if (d.sqrMagnitude < 1e-4f) return 0f;
            return Mathf.DeltaAngle(transform.eulerAngles.y, Quaternion.LookRotation(d).eulerAngles.y);
        }

        /// <summary>Advance the current command by dt. Returns true when finished. Same code path for runtime and tests.</summary>
        public bool Step(float dt)
        {
            if (cmd == null) { IsExecuting = false; return true; }
            float rate = Mathf.Clamp(cmd.turn_rate, 20f, 90f);
            if (path != null) return NavStep(dt);
            switch (cmd.action)
            {
                case "TURN_LEFT":
                case "TURN_RIGHT":
                    transform.Rotate(0, (cmd.action == "TURN_LEFT" ? -1 : 1) * rate * dt, 0);
                    speedNow = Mathf.Lerp(speedNow, 0.6f, 0.2f);
                    ApplyGravityOnly(dt);
                    t += dt;
                    if (t >= cmd.duration) return Finish("turned " + Mathf.RoundToInt(rate * cmd.duration) + "°");
                    return false;
                case "MOVE_FORWARD": return DriveStep(dt, 1f);
                case "MOVE_BACKWARD": return DriveStep(dt, -1f);
                default: return Finish("stopped");
            }
        }

        bool NavStep(float dt)
        {
            t += dt;
            float toGoal = FlatDist(transform.position, navGoal);
            if (facing || toGoal < navArrive || pathIndex >= path.Count)
            {
                // Final flourish: turn to face the target so the camera (and the operator) can see it.
                facing = true;
                float d = YawTo(navGoal);
                transform.Rotate(0, Mathf.Sign(d) * Mathf.Min(Mathf.Abs(d), 160f * dt), 0);
                speedNow = 0.4f; ApplyGravityOnly(dt);
                if (Mathf.Abs(d) < 4f || t > cmd.duration + 2f)
                    return Finish(toGoal < navArrive + 1.2f ? $"reached the {Pretty(cmd.target)} (travelled {navStartDist - toGoal + 0.01f:0.0} m)"
                                                            : $"got as close as possible ({toGoal:0.0} m away)");
                return false;
            }
            if (t > cmd.duration) return Finish($"leg time limit reached, {toGoal:0.0} m still to go");

            var wp = path[pathIndex];
            if (FlatDist(transform.position, wp) < 0.8f) { pathIndex++; return false; }
            float diff = YawTo(wp);
            transform.Rotate(0, Mathf.Sign(diff) * Mathf.Min(Mathf.Abs(diff), 150f * dt), 0);
            float ramp = Mathf.Clamp01(t / 0.4f);
            float slow = Mathf.Clamp01(toGoal / 3f) * 0.6f + 0.4f;
            float v = maxSpeed * ramp * slow * (Mathf.Abs(diff) > 55f ? 0.1f : 1f - Mathf.Abs(diff) / 120f);
            float ahead = Probe(0f, 1.2f, out var hit);
            if (hit && ahead < 0.25f) v = 0f; // never ram anything
            Drive(v * dt, dt);
            speedNow = v;

            progressT += dt;
            if (progressT > 1.6f)
            {
                if (FlatDist(transform.position, progressPos) < 0.2f && Mathf.Abs(diff) < 30f) return Finish($"stuck, {toGoal:0.0} m from the goal");
                progressT = 0f; progressPos = transform.position;
            }
            return false;
        }

        static string Pretty(string s) => string.IsNullOrEmpty(s) ? "target" : s.Replace('_', ' ');

        bool DriveStep(float dt, float dir)
        {
            float speed = Mathf.Min(cmd.speed, maxSpeed);
            float ramp = Mathf.Clamp01(t / 0.3f) * Mathf.Clamp01((cmd.duration - t) / 0.3f + 0.15f);
            float v = speed * ramp * dir;
            if (dir < 0)
            {
                float back = Probe(180f, 1.2f, out var bh);
                if (bh && back < 0.3f) return Finish("stopped: something is right behind me");
            }
            if (dir > 0)
            {
                float d = Probe(0f, 3f, out var hit);
                if (hit && IsTarget(hit, cmd.target) && d < 0.6f) return Finish("reached " + Pretty(cmd.target));
                if (hit && d < 1.5f)
                {
                    // Local reactive avoidance while exploring.
                    float left = Probe(-50f, 3f, out _), right = Probe(50f, 3f, out _);
                    if (Mathf.Max(left, right) < 0.7f && d < 0.35f) return Finish("blocked by obstacle");
                    transform.Rotate(0, (left > right ? -1f : 1f) * 110f * dt, 0);
                    v *= d < 0.5f ? 0.15f : 0.5f;
                    avoided = true;
                }
            }
            Drive(v * dt, dt);
            speedNow = v;
            var flat = new Vector2(transform.position.x, transform.position.z);
            if (flat.magnitude > EnvironmentController.PlayRadius)
            {
                var back = flat.normalized * (EnvironmentController.PlayRadius - 0.05f);
                cc.enabled = false; transform.position = new Vector3(back.x, transform.position.y, back.y); cc.enabled = true;
                return Finish("reached garden boundary");
            }
            t += dt;
            if (t >= cmd.duration) return Finish("drove " + (speed * cmd.duration * 0.85f).ToString("0.0") + " m" + (avoided ? " (steered around an obstacle)" : ""));
            return false;
        }

        bool Finish(string r) { result = r; IsExecuting = false; speedNow = 0f; cmd = null; path = null; return true; }

        void Drive(float dist, float dt)
        {
            vy = cc.isGrounded ? -2f : vy - 20f * dt;
            cc.Move(transform.forward * dist + Vector3.up * vy * dt);
        }

        void ApplyGravityOnly(float dt)
        {
            if (!cc || !cc.enabled) return;
            vy = cc.isGrounded ? -2f : vy - 20f * dt;
            cc.Move(Vector3.up * vy * dt);
        }

        public IEnumerator Execute(Command c, Action<string> done)
        {
            Begin(c);
            while (!Step(Time.deltaTime)) yield return null;
            done?.Invoke(result);
        }

        public IEnumerator Navigate(Command c, Vector3 goal, Action<string> done)
        {
            BeginNavigate(c, goal);
            while (!Step(Time.deltaTime)) yield return null;
            done?.Invoke(result);
        }

        /// <summary>Synchronous execution for editor/batch tests (fixed 30 Hz steps).</summary>
        public string ExecuteImmediate(Command c, Vector3? goal = null)
        {
            if (goal.HasValue) BeginNavigate(c, goal.Value); else Begin(c);
            int guard = 0;
            while (!Step(1f / 30f) && guard++ < 1200) Physics.SyncTransforms();
            Physics.SyncTransforms();
            return result;
        }

        // ------------------------------------------------------------------ player driving (WASD / arrows, any time)
        void ManualDrive(float dt)
        {
            float v = 0f, h = 0f;
            if (DriveInputAllowed && Application.isPlaying)
            {
                v = InputCompat.Vertical(); h = InputCompat.Horizontal();
                if (InputCompat.SpaceDown() && Mode != RoverMode.Thinking) Hop(10);
            }
            bool active = Mathf.Abs(v) > 0.01f || Mathf.Abs(h) > 0.01f;
            if (active)
            {
                PlayerDriving = true; manualIdle = 0f;
                if (Mode == RoverMode.Idle || Mode == RoverMode.Arrived || Mode == RoverMode.Error) SetMode(RoverMode.Manual);
                ManualSeconds += dt;
            }
            else if (PlayerDriving && (manualIdle += dt) > 0.6f)
            {
                PlayerDriving = false;
                if (Mode == RoverMode.Manual) SetMode(RoverMode.Idle);
            }
            if (!cc || !cc.enabled) return;
            float speed = v < 0 ? maxSpeed * 0.6f : maxSpeed * (InputCompat.ShiftHeld() ? 1.4f : 1f);
            transform.Rotate(0, h * 95f * dt, 0);
            vy = cc.isGrounded ? -2f : vy - 20f * dt;
            cc.Move(transform.forward * v * speed * dt + Vector3.up * vy * dt);
            speedNow = Mathf.Lerp(speedNow, v * speed, 1f - Mathf.Exp(-10f * dt));
            var flat = new Vector2(transform.position.x, transform.position.z);
            if (flat.magnitude > EnvironmentController.PlayRadius)
            {
                var back = flat.normalized * (EnvironmentController.PlayRadius - 0.05f);
                cc.enabled = false; transform.position = new Vector3(back.x, transform.position.y, back.y); cc.enabled = true;
            }
        }
    }
}
