using System.Collections.Generic;
using UnityEngine;

namespace Mossbound
{
    /// <summary>
    /// Client-side validation (defence in depth). The backend already validates; Unity re-checks
    /// so a buggy or replaced backend can never drive the rover outside these limits.
    /// </summary>
    public static class CommandParser
    {
        public const float MaxMoveDuration = 3f;
        public const float MaxTurnDuration = 1.5f;
        public const float MaxNavLeg = 20f;      // one MOVE_TO_TARGET navigation leg
        public const float MaxSpeed = 3f;

        static readonly HashSet<string> Allowed = new HashSet<string>
            { "MOVE_FORWARD", "MOVE_BACKWARD", "TURN_LEFT", "TURN_RIGHT", "STOP", "MOVE_TO_TARGET" };

        public static Command Validate(Command c)
        {
            if (c == null) return new Command { action = "STOP", status = "impossible", reason = "Empty response." };
            c.action = (c.action ?? "STOP").Trim().ToUpperInvariant();
            if (!Allowed.Contains(c.action))
            {
                c.reason = $"Rejected unknown action '{c.action}'. " + c.reason;
                c.action = "STOP";
            }
            if (float.IsNaN(c.duration) || float.IsInfinity(c.duration)) c.duration = 0f;
            if (float.IsNaN(c.bearing_deg) || float.IsInfinity(c.bearing_deg)) c.bearing_deg = 0f;
            if (c.target_bbox == null || c.target_bbox.Length != 4) c.target_bbox = new int[4];
            if (c.warnings == null) c.warnings = new string[0];
            c.tile = Mathf.Clamp(c.tile, -1, 3);

            switch (c.action)
            {
                case "MOVE_TO_TARGET":
                    bool hasBox = c.tile >= 0 && c.target_bbox[2] > c.target_bbox[0] && c.target_bbox[3] > c.target_bbox[1];
                    if (!hasBox) { c.action = "STOP"; c.reason = "Rejected MOVE_TO_TARGET without a located target. " + c.reason; c.duration = 0; break; }
                    c.duration = Mathf.Clamp(c.duration <= 0 ? MaxNavLeg : c.duration, 1f, MaxNavLeg);
                    c.speed = Mathf.Clamp(c.speed <= 0 ? MaxSpeed : c.speed, 0.3f, MaxSpeed);
                    break;
                case "MOVE_FORWARD":
                case "MOVE_BACKWARD":
                    c.duration = Mathf.Clamp(c.duration, 0.2f, MaxMoveDuration);
                    c.speed = Mathf.Clamp(c.speed <= 0 ? 1.5f : c.speed, 0.3f, MaxSpeed);
                    break;
                case "TURN_LEFT":
                case "TURN_RIGHT":
                    c.duration = Mathf.Clamp(c.duration, 0.1f, MaxTurnDuration);
                    break;
                default:
                    c.duration = 0f; c.speed = 0f;
                    break;
            }
            c.turn_rate = Mathf.Clamp(c.turn_rate <= 0 ? 70f : c.turn_rate, 20f, 90f);
            c.bearing_deg = Mathf.Clamp(c.bearing_deg, -180f, 180f);
            return c;
        }

        // ---------------------------------------------------------------- planner skills
        public const float MaxPlanMove = 10f, MaxPlanTurn = 360f;
        public const int MaxPlanSteps = 4;
        static readonly HashSet<string> Skills = new HashSet<string>
            { "GO_TO", "MOVE", "TURN", "LOOK_AROUND", "DANCE", "COME_BACK", "SAY", "STOP" };

        /// <summary>Client-side re-check of a plan: unknown skills dropped, amounts clamped, at most 4 steps.</summary>
        public static Plan ValidatePlan(Plan p)
        {
            if (p == null) p = new Plan { reply = "Hmm, I didn't get that.", steps = new PlanStep[0] };
            var ok = new List<PlanStep>();
            var warn = new List<string>(p.warnings ?? new string[0]);
            foreach (var s in p.steps ?? new PlanStep[0])
            {
                if (s == null) continue;
                s.skill = (s.skill ?? "").Trim().ToUpperInvariant();
                if (!Skills.Contains(s.skill)) { warn.Add($"unity: rejected skill '{s.skill}'"); continue; }
                if (float.IsNaN(s.amount) || float.IsInfinity(s.amount)) s.amount = 0f;
                if (s.skill == "MOVE") s.amount = Mathf.Clamp(s.amount, -MaxPlanMove, MaxPlanMove);
                if (s.skill == "TURN") s.amount = Mathf.Clamp(s.amount, -MaxPlanTurn, MaxPlanTurn);
                if (s.skill == "GO_TO" && string.IsNullOrEmpty(s.target)) { warn.Add("unity: GO_TO without target dropped"); continue; }
                ok.Add(s);
                if (ok.Count >= MaxPlanSteps) break;
            }
            if (ok.Count == 0) ok.Add(new PlanStep { skill = "SAY" });
            p.steps = ok.ToArray(); p.warnings = warn.ToArray();
            p.reply = p.reply ?? ""; p.understanding = p.understanding ?? "";
            return p;
        }

        public static string DescribeStep(PlanStep s)
        {
            switch (s.skill)
            {
                case "GO_TO": return "Go to the " + PrettyTarget(s.target);
                case "MOVE": return (s.amount >= 0 ? "Drive forward " : "Back up ") + Mathf.Abs(s.amount).ToString("0.#") + " m";
                case "TURN": return "Turn " + (s.amount >= 0 ? "right " : "left ") + Mathf.Abs(s.amount).ToString("0") + "°";
                case "LOOK_AROUND": return "Look all around";
                case "DANCE": return "Do a happy dance";
                case "COME_BACK": return "Drive back home";
                case "SAY": return "Talk";
                default: return "Stop and wait";
            }
        }

        public static string Pretty(string action) => (action ?? "").Replace('_', ' ');
        /// <summary>Player-facing name: the landmark's sign name ("Big Tree") or the label with spaces.</summary>
        public static string PrettyTarget(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            var env = EnvironmentController.Instance;
            if (env) foreach (var l in env.landmarks) if (l.label == t) return l.displayName;
            return t.Replace('_', ' ');
        }
    }
}
