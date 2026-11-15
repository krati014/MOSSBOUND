using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Mossbound
{
    public enum StepState { Pending, Active, Done, Failed }

    /// <summary>One object the VLM detected in MOSS's front camera (for the on-screen overlay).</summary>
    public struct SeenThing
    {
        public string label;
        public bool isTarget;
        public int[] bbox;           // pixels of the front view
        public Vector3? world;       // where it is in the garden (ray through the box), if found
    }

    /// <summary>
    /// Player command pipeline:
    ///   1. PLAN   - camera views + free-form words -> VLM planner -> validated skill plan (GO_TO / MOVE / TURN / ...)
    ///               If the planner is unsure what the player means, MOSS asks first (yes / other options) before moving.
    ///   2. ACT    - each skill is mapped to bounded rover motion. GO_TO runs the closed perception loop:
    ///               4 views -> VLM -> bbox -> 3D goal -> A* path -> drive -> re-observe (until physically arrived).
    ///               While it drives, the VLM labels what it sees in the front camera (live overlay).
    ///   3. SHARE  - the player can grab the keyboard at any time: MOSS pauses, and carries on when they let go.
    ///   4. HELP   - if MOSS is stuck or cannot find the goal it stops and asks the player for help.
    /// The same coroutines run in Play mode and in the batch-mode tests (EditorPump), so tests exercise this exact code.
    /// </summary>
    public class CommandExecutor : MonoBehaviour
    {
        public RoverController rover;
        public CameraCapture capture;
        public VLMClient client;
        public UIController ui;
        public QuestManager quest;
        public int maxSteps = 8;

        public bool Running { get; private set; }
        public bool NeedsHelp { get; private set; }
        public bool Asking { get; private set; }
        public bool PlayerHasWheel => takeover;
        public Plan CurrentPlan { get; private set; }
        public int CurrentStep { get; private set; } = -1;
        public string LastOutcome { get; private set; } = "";
        public string HelpReason { get; private set; } = "";

        Coroutine loop;
        GoalMarker marker;
        string instruction = "", helpContext = "", askContext = "";
        Plan pending;
        bool takeover, tookOver, detecting, detectBusy;
        Texture2D snapshot;
        readonly List<string> recent = new List<string>();

        public static float Dt => Application.isPlaying ? Time.deltaTime : 1f / 30f;
        static object Wait(float s) => Application.isPlaying ? new WaitForSeconds(s) : null;
        GoalMarker Marker { get { if (!marker) marker = GoalMarker.Create(transform); return marker; } }
        string TimeOfDay => DayNight.Instance ? DayNight.Instance.TimeOfDay : "night";
        static string Nice(string s) => CommandParser.PrettyTarget(s);

        void Say(string text, float seconds = 3.5f, bool dots = false)
        {
            rover.Say(text, seconds, dots);
            if (ui) ui.MossSays(text, dots);
        }

        // ------------------------------------------------------------------ public control
        static readonly Regex YesWords = new Regex(@"^\s*(yes|yeah|yep|yup|sure|ok|okay|do it|go|yes please|correct|right|that one)\s*[!.]*\s*$", RegexOptions.IgnoreCase);

        public void Execute(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            // Answering MOSS's yes/no question by typing "yes" works like pressing the YES button.
            if (Asking && YesWords.IsMatch(text)) { if (ui) ui.Log("<b>You:</b> " + text, LogKind.User); ConfirmYes(); return; }
            StopLoop();
            loop = StartCoroutine(RunCommand(text));
        }

        /// <summary>The player confirmed MOSS's guess: run the plan it already made (no extra AI call).</summary>
        public void ConfirmYes()
        {
            if (!Asking || pending == null) return;
            StopLoop();
            loop = StartCoroutine(ConfirmYesRoutine());
        }

        public IEnumerator ConfirmYesRoutine()
        {
            Asking = false; if (ui) ui.HideQuestion();
            var p = pending; pending = null;
            if (p == null) yield break;
            if (quest) quest.Commands++;
            Say("Yay, let's go!", 2f);
            rover.Hop(10);
            yield return RunSteps(p, 0);
        }

        public void ConfirmNo()
        {
            if (!Asking) return;
            Asking = false; pending = null; if (ui) ui.HideQuestion();
            rover.SetMode(RoverMode.Idle);
            Say("Oops, okay! Tell me what you'd like.", 3.5f);
            if (ui) ui.FocusInput();
        }

        /// <summary>Retry the step MOSS asked for help with (after the player helped), then continue the plan.</summary>
        public void Retry()
        {
            if (!NeedsHelp || CurrentPlan == null) return;
            int from = Mathf.Max(0, CurrentStep);
            StopLoop();
            loop = StartCoroutine(RunSteps(CurrentPlan, from));
        }

        public void GiveUp()
        {
            if (!NeedsHelp) return;
            NeedsHelp = false; if (ui) ui.HideHelp();
            rover.SetMode(RoverMode.Idle);
            Say("Okay! What should we do instead?", 3f);
        }

        /// <summary>
        /// The player grabbed the keyboard while MOSS was working: MOSS hands over the wheel and pauses its plan.
        /// When the player lets go, it looks again from where it is now and carries on.
        /// </summary>
        public void PlayerTookWheel()
        {
            if (!Running || takeover) return;
            takeover = true;
            rover.Halt();
            rover.SetMode(RoverMode.Manual);
            if (ui) { ui.Log("You took the wheel - MOSS will carry on when you let go.", LogKind.Warn); ui.SetTakeover(true); }
            Say("Ooh, you're driving! I'll carry on when you let go.", 3f);
        }

        public void Cancel(bool log = true)
        {
            bool was = Running || Asking;
            StopLoop();
            NeedsHelp = false; if (ui) ui.HideHelp();
            Asking = false; pending = null; if (ui) ui.HideQuestion();
            if (was && log) { if (ui) ui.Log("Stopped by the player.", LogKind.Warn); Say("Okay, stopping!", 2f); }
            rover.SetMode(RoverMode.Idle);
        }

        void StopLoop()
        {
            if (loop != null) StopCoroutine(loop);
            loop = null;
            Running = false;
            takeover = false; tookOver = false; detecting = false;
            Application.targetFrameRate = MossboundApp.NormalFps;
            rover.Halt();
            Marker.Hide();
            if (ui) { ui.SetBusy(false); ui.SetTakeover(false); ui.ClearDetections(); }
        }

        // ------------------------------------------------------------------ player takeover
        IEnumerator WaitForPlayer()
        {
            float idle = 0f;
            while (idle < 1.2f)
            {
                idle = rover.PlayerDriving ? 0f : idle + Dt;
                yield return null;
                if (!Application.isPlaying) break;
            }
            takeover = false; tookOver = true;
            if (ui) ui.SetTakeover(false);
            Say("My turn! Carrying on from here.", 2.5f);
        }

        /// <summary>True once after the player drove MOSS during the last wait/drive.</summary>
        bool TookOver() { bool t = tookOver; tookOver = false; return t; }

        // ------------------------------------------------------------------ 1. understand
        public IEnumerator RunCommand(string text)
        {
            Running = true;
            instruction = text;
            if (ui) { ui.SetBusy(true); ui.Log("<b>You:</b> " + text, LogKind.User); }
            if (quest) quest.Commands++;
            string context = NeedsHelp ? helpContext : Asking ? askContext : "";
            NeedsHelp = false; if (ui) ui.HideHelp();
            Asking = false; pending = null; if (ui) ui.HideQuestion();

            rover.SetMode(RoverMode.Thinking);
            Say("Hmm, let me think", -1f, true);
            if (ui) ui.ShowPlanning();
            string[] views = null;
            yield return capture.Capture(v => views = v);
            var req = new PlanRequest
            {
                instruction = text, images_b64 = views, image_width = CameraCapture.Width, image_height = CameraCapture.Height,
                time_of_day = TimeOfDay, quest = quest ? quest.ObjectiveForPlanner() : "",
                context = (context + (quest && quest.Inventory.Count > 0 ? $" MOSS is carrying: {string.Join(", ", quest.Inventory)}." : "")).Trim(),
                nearby = rover.Nearby(), recent = recent.ToArray()
            };
            Plan plan = null; string err = null;
            Application.targetFrameRate = MossboundApp.ThinkingFps;
            yield return client.PlanFor(req, p => plan = p, e => err = e);
            Application.targetFrameRate = MossboundApp.NormalFps;
            recent.Add(text); if (recent.Count > 5) recent.RemoveAt(0);
            if (err != null) { Fail(err); yield break; }
            if (takeover) yield return WaitForPlayer();
            TookOver();

            plan = CommandParser.ValidatePlan(plan);
            CurrentPlan = plan;
            if (ui) ui.ShowPlan(plan);
            if (ui && !string.IsNullOrEmpty(plan.understanding)) ui.Log("<i>MOSS understood:</i> " + plan.understanding, LogKind.Ai);
            if (plan.unsure) { AskPlayer(plan); yield break; }
            if (!string.IsNullOrEmpty(plan.reply)) { Say(plan.reply, 4.5f); if (ui) ui.Log("<b>MOSS:</b> " + plan.reply, LogKind.Ai); }
            yield return Wait(0.25f);
            yield return RunSteps(plan, 0);
        }

        /// <summary>MOSS isn't sure what the player meant: it asks before moving (YES runs its guess; options re-ask).</summary>
        void AskPlayer(Plan plan)
        {
            Running = false; loop = null;
            Asking = true; pending = plan;
            bool acts = false;
            foreach (var s in plan.steps) acts |= s.skill != "SAY" && s.skill != "STOP";
            string guess = acts ? string.Join(", then ", Array.ConvertAll(plan.steps, s => CommandParser.DescribeStep(s).ToLowerInvariant())) : "";
            askContext = $"MOSS was not sure what the player meant by \"{instruction}\" and asked: \"{plan.reply}\"" +
                         (acts ? $" (its guess was: {guess})" : "") + ". The player's words are their answer to that question.";
            if (!acts) pending = null;
            rover.Halt(); rover.SetMode(RoverMode.Idle); rover.Hop(0);
            Say(plan.reply, 10f);
            LastOutcome = "asked: " + plan.reply;
            if (ui) { ui.SetBusy(false); ui.ShowQuestion(plan.reply, acts ? guess : "", plan.options); ui.Log("<b>MOSS asks:</b> " + plan.reply, LogKind.Ai); }
        }

        void Fail(string err)
        {
            rover.SetMode(RoverMode.Error);
            Say("Oops... I can't reach my brain!", 4f);
            if (ui) { ui.ShowError(err); ui.Log(err, LogKind.Error); }
            Running = false; if (ui) ui.SetBusy(false);
            LastOutcome = "error: " + err;
        }

        // ------------------------------------------------------------------ 2. act
        public IEnumerator RunSteps(Plan plan, int from)
        {
            Running = true; NeedsHelp = false; LastOutcome = "";
            CurrentPlan = plan;
            if (ui) { ui.SetBusy(true); ui.HideHelp(); if (from == 0) ui.ShowPlan(plan); }
            for (int i = from; i < plan.steps.Length; i++)
            {
                CurrentStep = i;
                var s = plan.steps[i];
                if (ui) { ui.SetPlanStep(i, StepState.Active); ui.Log($"▸ {CommandParser.DescribeStep(s)}", LogKind.Ai); }
                bool ok = true; string why = "";
                switch (s.skill)
                {
                    case "GO_TO": yield return GoTo(s, (o, w) => { ok = o; why = w; }); break;
                    case "COME_BACK": yield return GoHome((o, w) => { ok = o; why = w; }); break;
                    case "MOVE": yield return Move(s.amount); break;
                    case "TURN": yield return Turn(s.amount); break;
                    case "LOOK_AROUND":
                        Say("Let me look all around!", 2.5f);
                        yield return Turn(360f); break;
                    case "DANCE":
                        Say("♪ Dance time! ♪", 3f); rover.SetMode(RoverMode.Arrived); rover.Dance();
                        if (quest) quest.Tick();          // dance objectives count while MOSS is dancing
                        yield return Wait(3.2f); break;
                    case "SAY":
                        rover.SetMode(RoverMode.Idle);
                        yield return Wait(Mathf.Clamp(plan.reply.Length * 0.05f, 1.5f, 4f)); break;
                    default:
                        rover.Halt(); rover.SetMode(RoverMode.Idle); break;
                }
                TookOver();
                if (quest) quest.Tick();
                if (!ok)
                {
                    if (LastOutcome.StartsWith("error")) yield break;
                    if (ui) ui.SetPlanStep(i, StepState.Failed);
                    AskForHelp(s, why);
                    yield break;
                }
                if (ui) ui.SetPlanStep(i, StepState.Done);
            }
            Running = false; loop = null;
            if (ui) { ui.SetBusy(false); ui.ClearDetections(); }
            LastOutcome = "done";
            if (rover.Mode == RoverMode.Moving || rover.Mode == RoverMode.Thinking || rover.Mode == RoverMode.Manual) rover.SetMode(RoverMode.Idle);
            if (rover.Mode == RoverMode.Arrived && Application.isPlaying)
            {
                yield return new WaitForSeconds(4f);
                if (!Running && rover.Mode == RoverMode.Arrived) { rover.SetMode(RoverMode.Idle); Marker.Hide(); }
            }
        }

        // ------------------------------------------------------------------ 3. help
        void AskForHelp(PlanStep s, string why)
        {
            Running = false; NeedsHelp = true; loop = null; detecting = false;
            HelpReason = why;
            LastOutcome = "help: " + why;
            helpContext = $"MOSS was trying to {CommandParser.DescribeStep(s).ToLowerInvariant()} but {why}, so it asked the player for help. " +
                          "The player's words are their reply to that help request.";
            if (quest) quest.HelpCount++;
            rover.Halt();
            rover.SetMode(RoverMode.NeedsHelp);
            Marker.Hide();
            Say("Help! " + Capital(why) + ". Can you help me?", 6f);
            if (ui) { ui.SetBusy(false); ui.ClearDetections(); ui.ShowHelp(why, s); ui.Log("MOSS asked for help: " + why, LogKind.Warn); }
            Application.targetFrameRate = MossboundApp.NormalFps;
        }

        static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ------------------------------------------------------------------ skills
        /// <summary>Drive the current rover command to completion. If the player grabs the wheel, wait for them instead.</summary>
        IEnumerator StepRover()
        {
            while (true)
            {
                if (takeover) { rover.Halt(); yield return WaitForPlayer(); yield break; }
                bool finished = rover.Step(Dt);
                if (!Application.isPlaying) Physics.SyncTransforms();
                if (finished) yield break;
                yield return null;
            }
        }

        IEnumerator Drive(Command c, Vector3? goal)
        {
            if (goal.HasValue) rover.BeginNavigate(c, goal.Value); else rover.Begin(c);
            yield return StepRover();
            if (ui) ui.SetMovementResult(rover.LastResult);
        }

        IEnumerator Turn(float degrees)
        {
            rover.SetMode(RoverMode.Moving);
            const float rate = 90f;
            var c = new Command { action = degrees < 0 ? "TURN_LEFT" : "TURN_RIGHT", duration = Mathf.Abs(degrees) / rate, turn_rate = rate, target = "" };
            if (Mathf.Abs(degrees) >= 300f) Say("Wheee, spinning!", 2.5f);
            yield return Drive(c, null);
        }

        IEnumerator Move(float metres)
        {
            rover.SetMode(RoverMode.Moving);
            if (metres >= 0)
            {
                // Forward moves are path-planned to the point ahead, so "go forward 8 m" never drives into the pond.
                var goal = rover.transform.position + rover.transform.forward * metres;
                var c = new Command { action = "MOVE_FORWARD", target = "spot ahead", duration = 4f + metres / 2f, speed = CommandParser.MaxSpeed };
                Say(metres > 4 ? "Zoom zoom!" : "Scooting forward!", 2f);
                rover.BeginNavigate(c, goal, 0.35f);
                yield return StepRover();
                if (ui) ui.SetMovementResult(rover.LastResult);
            }
            else
            {
                var c = new Command { action = "MOVE_BACKWARD", duration = Mathf.Abs(metres) / 1.2f, speed = 1.2f };
                Say("Beep beep, backing up!", 2f);
                yield return Drive(c, null);
            }
        }

        IEnumerator GoHome(Action<bool, string> done)
        {
            var home = EnvironmentController.RoverStart;
            Vector3 goal = EnvironmentController.OnGround(home.x, home.z);
            rover.SetMode(RoverMode.Moving);
            Say("Heading home!", 2.5f);
            var c = new Command { action = "MOVE_TO_TARGET", target = "home", duration = CommandParser.MaxNavLeg, speed = CommandParser.MaxSpeed };
            for (int leg = 0; leg < 4; leg++)
            {
                if (Array.IndexOf(rover.Nearby(), "home") >= 0) { Arrive("home"); done(true, ""); yield break; }
                rover.SetMode(RoverMode.Moving);
                rover.BeginNavigate(c, goal);
                Marker.Show(goal, rover.CurrentPath, "HOME");
                yield return StepRover();
                if (TookOver()) continue;
                if (ui) ui.SetMovementResult(rover.LastResult);
                if (rover.LastResult.StartsWith("reached")) { Arrive("home"); done(true, ""); yield break; }
                if (rover.LastResult.StartsWith("stuck")) break;
            }
            done(false, "I got stuck on the way home");
        }

        /// <summary>GO_TO: the closed vision loop. Every movement toward the goal comes from the VLM's grounding.</summary>
        IEnumerator GoTo(PlanStep s, Action<bool, string> done)
        {
            string target = s.target;
            if (target == "home") { yield return GoHome(done); yield break; }
            if (Array.IndexOf(rover.Nearby(), target) >= 0) { Arrive(target); done(true, ""); yield break; }
            detecting = true;
            if (Application.isPlaying) StartCoroutine(DetectLoop(target));
            try
            {
                string lastAction = "", lastResult = "";
                var history = new List<string>();
                int explores = 0, stucks = 0, legs = 0, wrong = 0;
                var rejected = new List<Vector3>();   // spots the proximity sensor ruled out (look-alikes)
                Vector3? lastReach = null;
                Command locked = null; Vector3 lockedGoal = Vector3.zero;

                for (int step = 0; step < maxSteps; step++)
                {
                    if (locked != null)
                    {
                        // A leg timed out: keep driving to the same VLM-grounded goal without re-asking.
                        rover.SetMode(RoverMode.Moving);
                        Say("Almost there!", 2f);
                        rover.BeginNavigate(locked, lockedGoal);
                        Marker.Show(lockedGoal, rover.CurrentPath, Nice(target));
                        yield return StepRover();
                        if (TookOver()) { locked = null; history.Add($"step {step + 1}: the player drove MOSS somewhere else"); if (CheckArrived(target, "")) { Arrive(target); done(true, ""); yield break; } continue; }
                        lastResult = rover.LastResult;
                        if (ui) ui.SetMovementResult(lastResult);
                        history.Add($"step {step + 1}: continued MOVE_TO_TARGET {target} -> {lastResult}");
                        if (!lastResult.StartsWith("leg time limit")) locked = null;
                        if (CheckArrived(target, lastResult)) { Arrive(target); done(true, ""); yield break; }
                        continue;
                    }

                    // ---- observe
                    rover.SetMode(RoverMode.Thinking);
                    Say(step == 0 ? $"Looking for the {Nice(target)}" : "Checking again", -1f, true);
                    if (ui) ui.ShowThinking(step, maxSteps);
                    string[] views = null;
                    yield return capture.Capture(v => views = v);
                    bool obstacle = rover.ObstacleAhead(out float obstacleDist);
                    var req = new ActRequest
                    {
                        instruction = instruction, images_b64 = views, image_width = CameraCapture.Width, image_height = CameraCapture.Height,
                        hfov_deg = CameraCapture.HorizontalFov, history = history.ToArray(), mission_target = target,
                        target_description = s.description, view_hint = step == 0 ? (s.view ?? "") : "", time_of_day = TimeOfDay,
                        state = new RoverStateDto
                        {
                            step = step, max_steps = maxSteps, heading_deg = rover.Heading, obstacle_ahead = obstacle,
                            obstacle_distance = Mathf.Min(obstacleDist, 99f), nearby = rover.Nearby(), last_action = lastAction, last_result = lastResult
                        }
                    };

                    // ---- ask the VLM
                    Command cmd = null; string err = null;
                    Application.targetFrameRate = MossboundApp.ThinkingFps;
                    yield return client.Act(req, c => cmd = c, e => err = e);
                    Application.targetFrameRate = MossboundApp.NormalFps;
                    if (err != null) { Fail(err); done(false, "I can't reach my brain"); yield break; }
                    if (takeover || TookOver())
                    {
                        // The player moved MOSS while it was looking: that view is out of date - look again from here.
                        if (takeover) yield return WaitForPlayer();
                        TookOver();
                        if (CheckArrived(target, "")) { Arrive(target); done(true, ""); yield break; }
                        history.Add($"step {step + 1}: the player drove MOSS somewhere else");
                        continue;
                    }

                    cmd = CommandParser.Validate(cmd);
                    cmd.target = target;
                    TargetLocator.VerifyArrival(cmd, capture, rover.transform, req.state.nearby, out _);
                    if (ui) { ui.ShowCommand(cmd, step, maxSteps); ui.LogCommand(cmd, step); }
                    if (cmd.target_visible) rover.LookToward(cmd.bearing_deg);

                    // ---- act
                    if (cmd.action == "STOP")
                    {
                        if (cmd.status == "arrived")
                        {
                            if (!IsLandmark(target) || Array.IndexOf(rover.Nearby(), target) >= 0) { Arrive(target); done(true, ""); yield break; }
                            // The VLM thinks it's here, but the proximity sensor disagrees: it saw a look-alike.
                            if (ui) ui.Log($"   ↳ sensor check: this is NOT the {Nice(target)} - keep searching", LogKind.Warn);
                            history.Add($"step {step + 1}: what looked like the {target} here was NOT it (proximity sensor disagrees) - it is somewhere else");
                            lastAction = "STOP (rejected arrival)"; lastResult = "not the target";
                            rejected.Add(rover.transform.position);
                            if (++wrong >= 3) { done(false, $"I keep finding things that look like the {Nice(target)}, but they aren't it"); yield break; }
                            yield return Explore(); TookOver(); continue;
                        }
                        done(false, $"I don't think there is a {Nice(target)} here"); yield break;
                    }
                    string result;
                    rover.SetMode(RoverMode.Moving);
                    if (cmd.action == "MOVE_TO_TARGET")
                    {
                        var goal = TargetLocator.Locate(capture, cmd, rover.transform);
                        if (rejected.Exists(r => Flat(r, goal.point) < 3.5f))
                        {
                            // Grounding points at a spot the proximity sensor already ruled out: ignore the look-alike.
                            if (ui) ui.Log($"   ↳ that's the look-alike from before - searching elsewhere", LogKind.Warn);
                            history.Add($"step {step + 1}: ignored a look-alike of the {target} that was already ruled out");
                            if (++explores > 3) { done(false, $"I can't find the real {Nice(target)}"); yield break; }
                            yield return Explore(); TookOver(); continue;
                        }
                        Say(legs++ == 0 ? $"I see the {Nice(target)}! On my way!" : "Getting closer!", 3f);
                        rover.BeginNavigate(cmd, goal.point);
                        Marker.Show(goal.point, rover.CurrentPath, Nice(target));
                        if (ui) { ui.SetGoal(goal.distance, goal.how); ui.Log($"   ↳ 3D goal {goal.distance:0.0} m away ({goal.how})", LogKind.Move); ui.PinTarget(Nice(target), goal.point); }
                        yield return StepRover();
                        if (TookOver())
                        {
                            if (CheckArrived(target, "")) { Arrive(target); done(true, ""); yield break; }
                            history.Add($"step {step + 1}: the player drove MOSS somewhere else");
                            lastAction = ""; lastResult = "";
                            continue;
                        }
                        result = rover.LastResult;
                        if (ui) ui.SetMovementResult(result);
                        if (result.StartsWith("leg time limit")) { locked = cmd; lockedGoal = goal.point; }
                        if (result.StartsWith("reached") && IsLandmark(target) && Array.IndexOf(rover.Nearby(), target) < 0)
                        {
                            // Reached the projected point but the sensor doesn't confirm: usually something in front of the
                            // target caught the rays - look again from here. Only if it happens twice at the same place is
                            // that spot a look-alike.
                            if (lastReach.HasValue && Flat(lastReach.Value, rover.transform.position) < 2.5f)
                            {
                                rejected.Add(goal.point);
                                if (ui) ui.Log($"   ↳ sensor check: this is NOT the {Nice(target)} - it was a look-alike", LogKind.Warn);
                                if (++wrong >= 3) { done(false, $"I keep finding look-alikes of the {Nice(target)}"); yield break; }
                            }
                            else if (ui) ui.Log($"   ↳ sensor: not there yet - looking again", LogKind.Move);
                            lastReach = rover.transform.position;
                            result += $", but the proximity sensor does not detect the {target} yet";
                        }
                        if (result.StartsWith("stuck"))
                        {
                            if (++stucks >= 2) { done(false, "I'm stuck and can't get past something"); yield break; }
                            yield return Move(-1.5f);   // wriggle free, then look again
                            TookOver();
                        }
                    }
                    else
                    {
                        Say(cmd.action == "MOVE_FORWARD" ? "Hmm, not here. Exploring!" : "Looking around", 2.5f);
                        if (++explores > 3) { done(false, $"I can't find the {Nice(target)} anywhere"); yield break; }
                        if (cmd.action == "MOVE_FORWARD") yield return Explore(); else yield return Drive(cmd, null);
                        TookOver();
                        result = rover.LastResult;
                    }
                    if (CheckArrived(target, result)) { Arrive(target); done(true, ""); yield break; }

                    lastAction = cmd.action + (cmd.action == "MOVE_TO_TARGET" ? $" toward {target} (bearing {cmd.bearing_deg:+0;-0}°)" : "");
                    lastResult = result;
                    history.Add($"step {step + 1}: {cmd.action} {target}" + (cmd.action == "MOVE_TO_TARGET" ? $" bearing {cmd.bearing_deg:+0;-0;0}" : "") + $" -> {result}");
                    yield return Wait(0.1f);
                }
                done(false, $"I tried {maxSteps} times but couldn't reach the {Nice(target)}");
            }
            finally
            {
                detecting = false;
                if (ui) ui.ClearDetections();
            }
        }

        // ------------------------------------------------------------------ live "what MOSS sees" overlay
        /// <summary>
        /// While MOSS drives (the GPU is otherwise idle then), the VLM labels the objects in its front camera.
        /// Each box is projected into the garden with a ray so the label can float over the real object on screen.
        /// </summary>
        IEnumerator DetectLoop(string target)
        {
            while (detecting)
            {
                bool driving = rover.IsExecuting && rover.CurrentPath != null && rover.CurrentPath.Count > 0 &&
                               Flat(rover.transform.position, rover.CurrentPath[rover.CurrentPath.Count - 1]) > 9f;   // a detection takes ~3-5 s: only while there is driving left
                if (driving && !detectBusy && client.Connected)
                {
                    detectBusy = true;
                    yield return new WaitForEndOfFrame();
                    string img = capture.CaptureFront(out var pose);
                    DetectResult res = null;
                    yield return client.Detect(new DetectRequest { image_b64 = img, target = target, time_of_day = TimeOfDay }, r => res = r);
                    detectBusy = false;
                    if (res != null && detecting && ui)
                    {
                        if (!snapshot) snapshot = new Texture2D(CameraCapture.Width, CameraCapture.Height, TextureFormat.RGB24, false);
                        snapshot.LoadImage(Convert.FromBase64String(img));
                        var seen = new List<SeenThing>();
                        int mask = ~((1 << RoverController.RoverLayer) | (1 << 4));
                        foreach (var d in res.objects)
                        {
                            if (d.bbox == null || d.bbox.Length != 4) continue;
                            var ray = capture.PixelRayFrom(pose, (d.bbox[0] + d.bbox[2]) * 0.5f, Mathf.Lerp(d.bbox[1], d.bbox[3], 0.6f));
                            Vector3? w = Physics.Raycast(ray, out var hit, 90f, mask, QueryTriggerInteraction.Ignore) ? hit.point : (Vector3?)null;
                            seen.Add(new SeenThing { label = d.label, isTarget = d.landmark == target && !string.IsNullOrEmpty(target), bbox = d.bbox, world = w });
                        }
                        ui.ShowDetections(seen, snapshot, res.latency_ms);
                    }
                }
                yield return new WaitForSeconds(0.3f);
            }
            detectBusy = false;
        }

        /// <summary>
        /// Search move when the goal is not in view: a path-planned 8 m hop. Near the edge of the garden it heads back
        /// toward the open middle instead of into the boundary (a search pattern - it uses no knowledge of where things are).
        /// </summary>
        IEnumerator Explore()
        {
            rover.SetMode(RoverMode.Moving);
            var p = rover.transform.position; var flat = new Vector2(p.x, p.z);
            Vector3 dir = rover.transform.forward;
            if (flat.magnitude > 16f) dir = Vector3.Slerp(dir, new Vector3(-flat.x, 0, -flat.y).normalized, 0.75f).normalized;
            var c = new Command { action = "MOVE_FORWARD", target = "search spot", duration = 6f, speed = CommandParser.MaxSpeed };
            yield return Drive(c, p + dir * 8f);
        }

        /// <summary>Physical arrival check after a navigation leg - no extra VLM call needed when the sensors agree.</summary>
        bool CheckArrived(string target, string result)
        {
            if (Array.IndexOf(rover.Nearby(), target) >= 0) return true;              // proximity sensor: known landmark is right here
            if (IsLandmark(target)) return false;                                     // a landmark must be confirmed by the sensor
            return result != null && result.StartsWith("reached");                    // open-vocabulary object: reached its 3D point
        }

        static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        static bool IsLandmark(string label)
        {
            var env = EnvironmentController.Instance;
            if (!env) return false;
            foreach (var l in env.landmarks) if (l.label == label) return true;
            return false;
        }

        static readonly Dictionary<string, string> Cheers = new Dictionary<string, string> {
            { "largest_tree", "Ta-da! The Big Tree - smell those golden apples!" }, { "pond_water", "Here's the Pond! *splish splash*" },
            { "glowing_mushrooms", "Boing! Hello, mushroom friends!" }, { "blue_crystal", "The Crystal is humming for me! ♪" },
            { "red_flower", "Mmm, the Red Flower smells so sweet!" }, { "portal", "The Magic Gate! It's tingly here!" },
            { "home", "Home sweet home!" } };

        void Arrive(string target)
        {
            rover.SetMode(RoverMode.Arrived);
            Say(Cheers.TryGetValue(target, out var line) ? line : $"Ta-da! Here's the {Nice(target)}! ♪", 4.5f);
            if (ui) { ui.Toast("ARRIVED AT THE " + Nice(target).ToUpperInvariant() + "!", new Color(0.45f, 1f, 0.6f)); ui.ClearDetections(); }
            Marker.Celebrate();
        }
    }
}
