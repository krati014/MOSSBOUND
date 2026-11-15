using UnityEngine;

namespace Mossbound
{
    /// <summary>
    /// Maps the VLM's 2D answer (bounding box in one camera view) to a 3D goal in the world:
    /// rays are cast through several pixels inside the box from the exact pose the view was captured at.
    /// The first ray (bottom -> top) that hits a solid object gives the goal; otherwise the ground under the box;
    /// otherwise a point along the bearing. No scene knowledge (names/positions of objects) is used.
    /// </summary>
    public static class TargetLocator
    {
        public struct Goal
        {
            public Vector3 point; public float distance; public string how; public string debug;
            public Goal With(string hitName, int groundRays) { debug = $"hit={hitName} groundRays={groundRays}"; return this; }
        }

        public static Goal Locate(CameraCapture cap, Command c, Transform rover)
        {
            var b = c.target_bbox;
            float x1 = b[0], y1 = b[1], x2 = b[2], y2 = b[3], w = x2 - x1, h = y2 - y1, cx = (x1 + x2) / 2, cy = (y1 + y2) / 2;
            // 3x3 grid of rays through the box; the MEDIAN hit distance wins, so a foreground occluder
            // covering part of the box (or a ray slipping past the object) is outvoted.
            var ground = EnvironmentController.Instance ? EnvironmentController.Instance.groundCollider : null;
            int mask = ~((1 << RoverController.RoverLayer) | (1 << 4));   // ignore the rover and the pond's invisible safety wall (Water layer)
            int view = Mathf.Clamp(c.tile, 0, 3);
            var objHits = new System.Collections.Generic.List<RaycastHit>();
            var groundHits = new System.Collections.Generic.List<RaycastHit>();
            // grid concentrated on the box centre (edges of a box are mostly background for thin objects)
            foreach (float fy in new[] { 0.35f, 0.55f, 0.75f })
                foreach (float fx in new[] { 0.32f, 0.5f, 0.68f })
                {
                    var ray = cap.PixelRay(view, x1 + fx * w, y1 + fy * h);
                    if (!Physics.Raycast(ray, out var hit, 120f, mask, QueryTriggerInteraction.Ignore)) continue;
                    (hit.collider == ground ? groundHits : objHits).Add(hit);
                }
            if (objHits.Count > 0)
            {
                objHits.Sort((a, b2) => a.distance.CompareTo(b2.distance));
                var med = objHits[objHits.Count / 2];
                return Make(med.point, rover, $"{objHits.Count}/9 rays hit an object in the VLM's box") .With(med.collider.name, groundHits.Count);
            }
            if (groundHits.Count > 0)
            {
                groundHits.Sort((a, b2) => a.distance.CompareTo(b2.distance));
                return Make(groundHits[groundHits.Count / 2].point, rover, "ground inside the VLM's box");
            }
            var dir = Quaternion.Euler(0, c.bearing_deg, 0) * rover.forward;
            return Make(rover.position + dir * 15f, rover, "bearing only (no ray hit)");
        }

        public const float ArriveDistance = 4.5f;

        /// <summary>
        /// Physical check of a VLM "arrived" claim: if the boxed target is still far away in 3D, the claim is rejected
        /// and the command is converted into a navigation leg toward it. Returns true if the command was changed.
        /// </summary>
        public static bool VerifyArrival(Command c, CameraCapture cap, Transform rover, string[] nearby, out Goal goal)
        {
            goal = default;
            if (c.action != "STOP" || c.status != "arrived" || !c.target_visible || c.tile < 0) return false;
            foreach (var n in nearby) if (n == c.target) return false;   // proximity sensor agrees
            goal = Locate(cap, c, rover);
            if (goal.distance <= ArriveDistance) return false;
            c.action = "MOVE_TO_TARGET"; c.status = "executing"; c.duration = CommandParser.MaxNavLeg; c.speed = CommandParser.MaxSpeed;
            var w = new System.Collections.Generic.List<string>(c.warnings) { $"arrival claim rejected: target still {goal.distance:0.0} m away (3D check) -> navigate" };
            c.warnings = w.ToArray();
            return true;
        }

        static Goal Make(Vector3 p, Transform rover, string how)
        {
            float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(rover.position.x, rover.position.z));
            return new Goal { point = p, distance = d, how = how };
        }
    }
}
