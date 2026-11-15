using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Mossbound.EditorTools
{
    /// <summary>Project setup + batch-mode verification tools (render previews, real VLM pipeline test).</summary>
    [InitializeOnLoad]
    public static class MossboundEditor
    {
        static MossboundEditor()
        {
            // Unity blocks plain-HTTP requests by default; the backend runs on http://127.0.0.1.
            if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
                PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            PlayerSettings.runInBackground = true;
        }

        static string OutDir
        {
            get
            {
                var arg = Environment.GetEnvironmentVariable("MOSS_OUT");
                var d = string.IsNullOrEmpty(arg) ? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/screenshots")) : arg;
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static MossboundApp BuildEditScene(bool interactive = false)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var app = new GameObject("MOSSBOUND").AddComponent<MossboundApp>();
            app.BuildAll(interactive);
            foreach (var ps in UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None)) ps.Simulate(6f, true, true);
            Physics.SyncTransforms();
            return app;
        }

        static void SaveCamera(Camera cam, int w, int h, string path)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[MOSS] saved " + path);
        }

        static void SetLayerRecursive(GameObject g, int layer) { g.layer = layer; foreach (Transform c in g.transform) SetLayerRecursive(c.gameObject, layer); }

        /// <summary>World camera + UI camera composited on the CPU (URP base cameras always clear, so they can't be stacked by "depth only").</summary>
        static void SaveCameras(Camera[] cams, int w, int h, string path)
        {
            Texture2D Grab(Camera cam, bool transparent)
            {
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                var prev = cam.targetTexture; var flags = cam.clearFlags; var bg = cam.backgroundColor;
                if (transparent) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0, 0, 0, 0); }
                cam.targetTexture = rt; cam.Render(); cam.targetTexture = prev;
                cam.clearFlags = flags; cam.backgroundColor = bg;
                RenderTexture.active = rt;
                var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
                t.ReadPixels(new Rect(0, 0, w, h), 0, 0); t.Apply();
                RenderTexture.active = null;
                return t;
            }
            var basePx = Grab(cams[0], false).GetPixels();
            for (int i = 1; i < cams.Length; i++)
            {
                var data = cams[i].GetUniversalAdditionalCameraData();
                if (data != null) data.renderPostProcessing = false;
                var over = Grab(cams[i], true).GetPixels();
                for (int k = 0; k < basePx.Length; k++)
                {
                    float a = over[k].a;
                    if (a <= 0f) continue;
                    // UI renders premultiplied-ish into a cleared target: treat rgb as already weighted by alpha
                    basePx[k] = new Color(over[k].r + basePx[k].r * (1 - a), over[k].g + basePx[k].g * (1 - a), over[k].b + basePx[k].b * (1 - a), 1);
                }
            }
            var outTex = new Texture2D(w, h, TextureFormat.RGB24, false);
            outTex.SetPixels(basePx); outTex.Apply();
            File.WriteAllBytes(path, outTex.EncodeToPNG());
            Debug.Log("[MOSS] saved " + path);
        }

        [MenuItem("MOSSBOUND/Render Preview Screenshots")]
        public static void RenderPreview()
        {
            var app = BuildEditScene();
            var dir = OutDir;
            SaveCamera(app.mainCam, 1600, 900, Path.Combine(dir, "preview_follow.png"));
            File.WriteAllBytes(Path.Combine(dir, "preview_rover_vision.jpg"), Convert.FromBase64String(app.capture.RenderAll()[0]));

            var cam = app.mainCam;
            cam.GetComponent<FollowCamera>().enabled = false;
            cam.transform.SetPositionAndRotation(new Vector3(26, 22, -34), Quaternion.LookRotation(new Vector3(-2, 0, 4) - new Vector3(26, 22, -34)));
            SaveCamera(cam, 1600, 900, Path.Combine(dir, "preview_aerial.png"));
            cam.transform.SetPositionAndRotation(new Vector3(6, 3.2f, -10), Quaternion.LookRotation(new Vector3(-4, 2.5f, 14) - new Vector3(6, 3.2f, -10)));
            SaveCamera(cam, 1600, 900, Path.Combine(dir, "preview_portal.png"));
            cam.transform.SetPositionAndRotation(new Vector3(-6, 2.2f, -16), Quaternion.LookRotation(new Vector3(-13, 0.8f, -8) - new Vector3(-6, 2.2f, -16)));
            SaveCamera(cam, 1600, 900, Path.Combine(dir, "preview_mushrooms.png"));
            var rv = app.rover.transform;
            var eye = rv.position + rv.forward * 3.2f + rv.right * 1.2f + Vector3.up * 1.7f;
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(rv.position + Vector3.up * 1.0f - eye));
            cam.fieldOfView = 40f;
            SaveCamera(cam, 1200, 900, Path.Combine(dir, "preview_moss_portrait.png"));

            // Game HUD (render the overlay canvas through the main camera for a screenshot).
            var game = BuildEditScene(true);
            game.dayNight.SetImmediate(Environment.GetEnvironmentVariable("MOSS_DAY") == "1");
            var canvas = game.ui.GetComponentInChildren<Canvas>();
            // UI drawn by a second camera without post-processing on top of the world (like the overlay in Play mode).
            var uiCam = new GameObject("UICam").AddComponent<Camera>();
            uiCam.clearFlags = CameraClearFlags.Depth; uiCam.cullingMask = 1 << 5; uiCam.orthographic = false;
            uiCam.transform.SetPositionAndRotation(game.mainCam.transform.position, game.mainCam.transform.rotation);
            SetLayerRecursive(canvas.gameObject, 5);
            game.mainCam.cullingMask &= ~(1 << 5);
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = uiCam; canvas.planeDistance = 1f;
            foreach (var pc in UnityEngine.Object.FindObjectsByType<PortraitCam>(FindObjectsSortMode.None)) pc.GetComponent<Camera>().Render();
            game.capture.RenderAll();
            string tod = game.dayNight.TimeOfDay;
            Canvas.ForceUpdateCanvases();
            SaveCameras(new[] { game.mainCam, uiCam }, 1920, 1080, Path.Combine(dir, $"preview_hud_start_{tod}.png"));
            canvas.transform.Find("Start").gameObject.SetActive(false);
            game.quest.Begin();
            game.ui.MossSays("I see the Crystal! On my way!", false);
            game.ui.Log("<b>You:</b> go to the crystal", LogKind.User);
            game.ui.Log("<i>MOSS understood:</i> You want to visit the Crystal, so I'll go there.", LogKind.Ai);
            game.ui.Log("<b>MOSS:</b> Off to the sparkly Crystal!", LogKind.Ai);
            game.ui.Log("The Crystal starts to hum a tune!", LogKind.Info);
            game.ui.ShowPlan(new Plan { understanding = "You want to visit the Crystal.", steps = new[] { new PlanStep { skill = "GO_TO", target = "blue_crystal", request = "go to the crystal" } } });
            game.ui.SetPlanStep(0, StepState.Active);
            var seen = new System.Collections.Generic.List<SeenThing> {
                new SeenThing { label = "blue crystal", isTarget = true, bbox = new[] { 300, 120, 380, 260 } },
                new SeenThing { label = "small tree", bbox = new[] { 40, 60, 160, 250 } },
                new SeenThing { label = "rock", bbox = new[] { 450, 230, 540, 290 } } };
            var snap = new Texture2D(CameraCapture.Width, CameraCapture.Height, TextureFormat.RGB24, false);
            snap.LoadImage(Convert.FromBase64String(game.capture.CaptureFront(out _)));
            game.ui.ShowDetections(seen, snap, 1400);
            game.ui.Toast("✓  The Crystal starts to hum a tune!", new Color(0.45f, 1f, 0.6f));
            game.ui.Update_ForPreview();
            Canvas.ForceUpdateCanvases();
            SaveCameras(new[] { game.mainCam, uiCam }, 1920, 1080, Path.Combine(dir, $"preview_hud_{tod}.png"));
            game.ui.ShowQuestion("Do you mean the Red Flower?", "go to the red flower", new[] { "Go to the Pond", "Go to the Magic Gate" });
            game.ui.Update_ForPreview();
            Canvas.ForceUpdateCanvases();
            SaveCameras(new[] { game.mainCam, uiCam }, 1920, 1080, Path.Combine(dir, $"preview_hud_ask_{tod}.png"));
            game.ui.HideQuestion();
            game.ui.ShowHelp("I'm stuck and can't get past something", new PlanStep { skill = "GO_TO", target = "blue_crystal" });
            Canvas.ForceUpdateCanvases();
            SaveCameras(new[] { game.mainCam, uiCam }, 1920, 1080, Path.Combine(dir, $"preview_hud_help_{tod}.png"));
            Debug.Log("[MOSS] PREVIEW_OK");
        }

        /// <summary>Runs a coroutine tree synchronously (edit mode): nested IEnumerators are entered, async web requests are
        /// waited for, every other yield is one 1/30 s tick. This lets tests run the real CommandExecutor code.</summary>
        static void Pump(System.Collections.IEnumerator root, int maxTicks = 60000)
        {
            var stack = new System.Collections.Generic.Stack<System.Collections.IEnumerator>();
            stack.Push(root);
            int ticks = 0;
            while (stack.Count > 0 && ticks++ < maxTicks)
            {
                var top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                var cur = top.Current;
                if (cur is System.Collections.IEnumerator e) stack.Push(e);
                else if (cur is AsyncOperation op) { var t0 = DateTime.Now; while (!op.isDone && (DateTime.Now - t0).TotalSeconds < 180) System.Threading.Thread.Sleep(10); }
            }
            if (ticks >= maxTicks) Debug.LogError("[MOSS] pump tick limit reached");
        }

        static string QuestStatus(QuestManager q)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var o in q.Current.objectives) parts.Add((o.done ? "[x] " : "[ ] ") + o.text);
            return string.Join(" | ", parts) + (q.Complete ? "  => QUEST COMPLETE" : "");
        }

        /// <summary>
        /// Game-level test WITHOUT play mode: builds the full game (HUD, quest, executor), then sends one or more player
        /// commands (MOSS_INSTRUCTION, separated by "||") through the real planner -> skills -> vision loop.
        /// Optional: MOSS_QUEST (index), MOSS_DAY=1, MOSS_START "x,z,yaw".
        /// </summary>
        [MenuItem("MOSSBOUND/Run Mission Test")]
        public static void MissionTest()
        {
            string all = Environment.GetEnvironmentVariable("MOSS_INSTRUCTION");
            if (string.IsNullOrEmpty(all)) all = "I'm hungry";
            string tag = Environment.GetEnvironmentVariable("MOSS_TAG"); if (string.IsNullOrEmpty(tag)) tag = "mission";
            int qi = int.TryParse(Environment.GetEnvironmentVariable("MOSS_QUEST"), out var qv) ? qv : 0;
            var app = BuildEditScene(true);
            var dir = Path.Combine(OutDir, "mission_" + tag); Directory.CreateDirectory(dir);
            PlaceRover(app);
            app.client.baseUrl = "http://127.0.0.1:8765";
            app.quest.NewQuest(qi); app.quest.Begin();
            app.dayNight.SetImmediate(app.quest.Current.day || Environment.GetEnvironmentVariable("MOSS_DAY") == "1");
            Debug.Log($"[MOSS] MISSION '{app.quest.Current.title}' time={app.dayNight.TimeOfDay} start={app.rover.transform.position}");
            int k = 0;
            foreach (var raw in all.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string instr = raw.Trim(); k++;
                var t0 = DateTime.Now;
                Debug.Log($"[MOSS] ===== command {k}: \"{instr}\"");
                if (instr.StartsWith("DRIVE "))
                {
                    // simulate the player driving MOSS somewhere with the keyboard
                    var f = instr.Substring(6).Split(',');
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    app.rover.cc.enabled = false;
                    app.rover.transform.SetPositionAndRotation(EnvironmentController.OnGround(float.Parse(f[0], ci), float.Parse(f[1], ci), 0.05f),
                                                               Quaternion.Euler(0, f.Length > 2 ? float.Parse(f[2], ci) : 0f, 0));
                    app.rover.cc.enabled = true; Physics.SyncTransforms();
                    Debug.Log($"[MOSS] player drove MOSS to {app.rover.transform.position}");
                    continue;
                }
                if (instr == "RETRY") Pump(app.executor.RunSteps(app.executor.CurrentPlan, app.executor.CurrentStep));
                else if (instr == "YES") Pump(app.executor.ConfirmYesRoutine());   // the player presses YES on MOSS's question
                else Pump(app.executor.RunCommand(instr));
                var p = app.executor.CurrentPlan;
                if (p != null)
                {
                    Debug.Log($"[MOSS] understood: {p.understanding}  ||  reply: {p.reply}  ||  unsure: {p.unsure} options: [{string.Join(" | ", p.options)}]  ||  warnings: {string.Join(" | ", p.warnings)}");
                    foreach (var st in p.steps) Debug.Log($"[MOSS]    plan step: {st.skill} target={st.target} view={st.view} amount={st.amount} request='{st.request}'");
                }
                app.quest.Tick();
                Debug.Log($"[MOSS] RESULT command {k}: outcome={app.executor.LastOutcome} needsHelp={app.executor.NeedsHelp} " +
                          $"asking={app.executor.Asking} pos={app.rover.transform.position} nearby=[{string.Join(",", app.rover.Nearby())}] took={(DateTime.Now - t0).TotalSeconds:0}s");
                Debug.Log("[MOSS] QUEST " + QuestStatus(app.quest));
                var cam = app.mainCam; cam.GetComponent<FollowCamera>().Snap();
                SaveCamera(cam, 1280, 720, Path.Combine(dir, $"after_{k:00}.png"));
            }
            Debug.Log("[MOSS] MISSION_END");
        }

        static void PlaceRover(MossboundApp app)
        {
            string startPos = Environment.GetEnvironmentVariable("MOSS_START");
            if (string.IsNullOrEmpty(startPos)) return;
            var f = startPos.Split(',');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            float sx = float.Parse(f[0], ci), sz = float.Parse(f[1], ci), yaw = f.Length > 2 ? float.Parse(f[2], ci) : 0f;
            app.rover.cc.enabled = false;
            app.rover.transform.SetPositionAndRotation(EnvironmentController.OnGround(sx, sz, 0.05f), Quaternion.Euler(0, yaw, 0));
            app.rover.cc.enabled = true; Physics.SyncTransforms();
        }

        /// <summary>
        /// Full closed-loop test WITHOUT play mode: Unity renders the rover camera, sends it to the real backend/VLM,
        /// validates the reply and executes it with the same motion code used at runtime. Repeats until STOP.
        /// Instruction from env var MOSS_INSTRUCTION.
        /// </summary>
        [MenuItem("MOSSBOUND/Run Pipeline Test")]
        public static void PipelineTest()
        {
            string instr = Environment.GetEnvironmentVariable("MOSS_INSTRUCTION");
            if (string.IsNullOrEmpty(instr)) instr = "Find the blue crystal and move towards it.";
            string tag = Environment.GetEnvironmentVariable("MOSS_TAG"); if (string.IsNullOrEmpty(tag)) tag = "test";
            int maxSteps = 8;
            var app = BuildEditScene();
            var dir = Path.Combine(OutDir, "pipeline_" + tag); Directory.CreateDirectory(dir);
            string startPos = Environment.GetEnvironmentVariable("MOSS_START");   // optional "x,z,yaw"
            if (!string.IsNullOrEmpty(startPos))
            {
                var f = startPos.Split(',');
                float sx = float.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture), sz = float.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture);
                float yaw = f.Length > 2 ? float.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                app.rover.cc.enabled = false;
                app.rover.transform.SetPositionAndRotation(EnvironmentController.OnGround(sx, sz, 0.05f), Quaternion.Euler(0, yaw, 0));
                app.rover.cc.enabled = true; Physics.SyncTransforms();
            }
            Command lockedCmd = null; Vector3 lockedGoal = Vector3.zero;
            string missionTarget = "";
            string lastAction = "", lastResult = "";
            var history = new System.Collections.Generic.List<string>();
            Debug.Log($"[MOSS] PIPELINE '{instr}' start pos={app.rover.transform.position}");
            for (int step = 0; step < maxSteps; step++)
            {
                if (lockedCmd != null)
                {
                    var r2 = app.rover.ExecuteImmediate(lockedCmd, lockedGoal);
                    Debug.Log($"[MOSS] step {step + 1}: continue locked goal -> {r2}  pos={app.rover.transform.position}");
                    history.Add($"step {step + 1}: continued MOVE_TO_TARGET {lockedCmd.target} -> {r2}"); lastResult = r2;
                    if (!r2.StartsWith("leg time limit")) lockedCmd = null;
                    continue;
                }
                string[] views = app.capture.RenderAll();
                for (int v = 0; v < 4; v++) File.WriteAllBytes(Path.Combine(dir, $"step{step + 1:00}_{CameraCapture.ViewNames[v]}.jpg"), Convert.FromBase64String(views[v]));
                bool obs = app.rover.ObstacleAhead(out float od);
                var req = new ActRequest
                {
                    instruction = instr, images_b64 = views, image_width = CameraCapture.Width, image_height = CameraCapture.Height,
                    hfov_deg = CameraCapture.HorizontalFov, history = history.ToArray(), mission_target = missionTarget,
                    state = new RoverStateDto { step = step, max_steps = maxSteps, heading_deg = app.rover.Heading, obstacle_ahead = obs,
                                                obstacle_distance = Mathf.Min(od, 99f), nearby = app.rover.Nearby(), last_action = lastAction, last_result = lastResult }
                };
                var cmd = VLMClient.ActBlocking("http://127.0.0.1:8765", req, out string err);
                if (cmd == null) { Debug.LogError("[MOSS] PIPELINE_ERROR " + err); return; }
                cmd = CommandParser.Validate(cmd);
                if (missionTarget == "" && !string.IsNullOrEmpty(cmd.target) && cmd.target != "unknown" && cmd.status != "impossible") missionTarget = cmd.target;
                TargetLocator.VerifyArrival(cmd, app.capture, app.rover.transform, req.state.nearby, out _);
                Debug.Log($"[MOSS] step {step + 1}: {cmd.action} target={cmd.target} intent='{cmd.intent}' view={cmd.view} vis={cmd.target_visible} bearing={cmd.bearing_deg} dur={cmd.duration} status={cmd.status} " +
                          $"lat={cmd.latency_ms}ms nearby=[{string.Join(",", req.state.nearby)}] obs={obs}\n   sees: {cmd.observation}\n   why: {cmd.reason}\n   warn: {string.Join(" | ", cmd.warnings)}");
                if (cmd.action == "STOP") { Debug.Log($"[MOSS] PIPELINE_DONE status={cmd.status} steps={step + 1} pos={app.rover.transform.position}"); break; }
                string res;
                if (cmd.action == "MOVE_TO_TARGET")
                {
                    var goal = TargetLocator.Locate(app.capture, cmd, app.rover.transform);
                    Debug.Log($"[MOSS]    goal {goal.point} dist={goal.distance:0.0} via {goal.how} {goal.debug} box=[{string.Join(",", cmd.target_bbox)}]");
                    res = app.rover.ExecuteImmediate(cmd, goal.point);
                    if (res.StartsWith("leg time limit")) { lockedCmd = cmd; lockedGoal = goal.point; }
                }
                else res = app.rover.ExecuteImmediate(cmd);
                lastAction = cmd.action + (cmd.action == "MOVE_TO_TARGET" ? $" toward {cmd.target} (bearing {cmd.bearing_deg:+0;-0}°)" : ""); lastResult = res;
                history.Add($"step {step + 1}: {cmd.action} {cmd.target}" + (cmd.action == "MOVE_TO_TARGET" ? $" bearing {cmd.bearing_deg:+0;-0;0}" : "") + $" -> {res}");
                Debug.Log($"[MOSS]    -> {res}  pos={app.rover.transform.position} heading={app.rover.Heading:0}");
            }
            var cam = app.mainCam; cam.GetComponent<FollowCamera>().Snap();
            SaveCamera(cam, 1280, 720, Path.Combine(dir, "final_follow.png"));
            Debug.Log("[MOSS] PIPELINE_END");
        }
    }
}
