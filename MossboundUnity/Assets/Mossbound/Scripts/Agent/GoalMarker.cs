using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mossbound
{
    /// <summary>Glowing beacon + dotted trail showing the 3D goal the VLM's bounding box was projected to.</summary>
    public class GoalMarker : MonoBehaviour
    {
        Transform beacon, ring;
        LineRenderer trail;
        Material beamMat, ringMat;
        TextMesh label;
        float celebrateT = -1f;

        public static GoalMarker Create(Transform parent)
        {
            var go = new GameObject("GoalMarker");
            go.transform.SetParent(parent, false);
            var m = go.AddComponent<GoalMarker>();
            m.Build();
            // The beacon is for the player only: put it on the rover layer, which MOSS's vision cameras never render,
            // so the VLM can't "see" its own goal label and ground on it.
            SetLayer(go, RoverController.RoverLayer);
            return m;
        }

        static void SetLayer(GameObject g, int layer) { g.layer = layer; foreach (Transform c in g.transform) SetLayer(c.gameObject, layer); }

        void Build()
        {
            beacon = new GameObject("Beacon").transform; beacon.SetParent(transform, false);
            beamMat = Mats.Transparent(new Color(1f, 0.75f, 0.3f, 0.18f), Palette.Amber, 1.6f);
            ringMat = Mats.Glow(Palette.Amber, Palette.Amber, 4f);
            var beam = new GameObject("Beam"); beam.transform.SetParent(beacon, false);
            beam.AddComponent<MeshFilter>().sharedMesh = Prim.Make(mb => Prim.Cylinder(mb, 0.35f, 0.08f, 9f, 12, false));
            var br = beam.AddComponent<MeshRenderer>(); br.sharedMaterial = beamMat; br.shadowCastingMode = ShadowCastingMode.Off;
            var r = new GameObject("Ring"); r.transform.SetParent(beacon, false); ring = r.transform;
            r.AddComponent<MeshFilter>().sharedMesh = Prim.Make(mb => { mb.M = Matrix4x4.Rotate(Quaternion.Euler(90, 0, 0)); Prim.Torus(mb, 1.1f, 0.05f, 40, 4); });
            var rr = r.AddComponent<MeshRenderer>(); rr.sharedMaterial = ringMat; rr.shadowCastingMode = ShadowCastingMode.Off;
            ring.localPosition = Vector3.up * 0.1f;
            var lg = new GameObject("Label"); lg.transform.SetParent(beacon, false); lg.transform.localPosition = Vector3.up * 3.4f;
            label = lg.AddComponent<TextMesh>(); label.font = UIKit.Font; label.GetComponent<MeshRenderer>().sharedMaterial = UIKit.Font.material;
            label.fontSize = 64; label.characterSize = 0.06f; label.anchor = TextAnchor.MiddleCenter; label.color = new Color(1f, 0.85f, 0.5f); label.fontStyle = FontStyle.Bold;

            trail = gameObject.AddComponent<LineRenderer>();
            trail.material = Mats.Particle(Palette.Amber, 2.5f);
            trail.textureMode = LineTextureMode.Tile; trail.widthMultiplier = 0.22f; trail.numCornerVertices = 2;
            trail.shadowCastingMode = ShadowCastingMode.Off;
            Hide();
        }

        public void Show(Vector3 goal, List<Vector3> path, string name)
        {
            beacon.gameObject.SetActive(true);
            beacon.position = new Vector3(goal.x, EnvironmentController.Height(goal.x, goal.z), goal.z);
            label.text = name.ToUpperInvariant();
            celebrateT = -1f;
            var pts = new List<Vector3>();
            var rover = FindAnyObjectByType<RoverController>();
            if (rover) pts.Add(rover.transform.position + Vector3.up * 0.15f);
            if (path != null) foreach (var p in path) pts.Add(new Vector3(p.x, EnvironmentController.Height(p.x, p.z) + 0.15f, p.z));
            // densify so the dotted trail hugs the terrain
            var dense = new List<Vector3>();
            for (int i = 0; i < pts.Count - 1; i++)
            {
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(pts[i], pts[i + 1]) / 0.7f));
                for (int k = 0; k < n; k++) { var q = Vector3.Lerp(pts[i], pts[i + 1], k / (float)n); q.y = EnvironmentController.Height(q.x, q.z) + 0.15f; dense.Add(q); }
            }
            if (pts.Count > 0) dense.Add(pts[pts.Count - 1]);
            trail.positionCount = dense.Count;
            trail.SetPositions(dense.ToArray());
            trail.enabled = dense.Count > 1;
        }

        public void Celebrate() { if (beacon.gameObject.activeSelf) celebrateT = 0f; trail.enabled = false; }
        public void Hide() { if (beacon) beacon.gameObject.SetActive(false); if (trail) trail.enabled = false; }

        void Update()
        {
            if (!beacon.gameObject.activeSelf) return;
            float s = 1f + 0.12f * Mathf.Sin(Time.time * 4f);
            if (celebrateT >= 0f) { celebrateT += Time.deltaTime; s = 1f + celebrateT * 2.5f; if (celebrateT > 1.2f) { Hide(); return; } }
            ring.localScale = new Vector3(s, 1, s);
            ring.Rotate(0, 60f * Time.deltaTime, 0, Space.World);
            var cam = Camera.main;
            if (cam) label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position);
        }
    }
}
