using System.Collections.Generic;
using UnityEngine;

namespace Mossbound
{
    /// <summary>
    /// Occupancy grid + A* planner. Built once from the physics colliders (trees, rocks, landmarks, the water barrier).
    /// It only answers "how do I get to this point without hitting things" - WHERE to go always comes from the VLM.
    /// </summary>
    public class NavGrid
    {
        public static NavGrid Instance { get; private set; }
        public const float Cell = 0.8f;
        const float Half = 34f;
        int n;
        bool[,] blocked;

        public static NavGrid Build(Collider ground, int ignoreLayer)
        {
            var g = new NavGrid();
            g.n = Mathf.CeilToInt(2 * Half / Cell);
            g.blocked = new bool[g.n, g.n];
            var buf = new Collider[16];
            int mask = ~(1 << ignoreLayer);
            for (int i = 0; i < g.n; i++)
                for (int j = 0; j < g.n; j++)
                {
                    var c = g.Center(i, j);
                    if (new Vector2(c.x, c.z).magnitude > EnvironmentController.PlayRadius - 0.5f) { g.blocked[i, j] = true; continue; }
                    float y = EnvironmentController.Height(c.x, c.z);
                    int k = Physics.OverlapCapsuleNonAlloc(new Vector3(c.x, y + 0.45f, c.z), new Vector3(c.x, y + 1.3f, c.z), 0.72f, buf, mask, QueryTriggerInteraction.Ignore);
                    for (int q = 0; q < k; q++) if (buf[q] != ground) { g.blocked[i, j] = true; break; }
                }
            Instance = g;
            return g;
        }

        Vector3 Center(int i, int j) => new Vector3(-Half + (i + 0.5f) * Cell, 0, -Half + (j + 0.5f) * Cell);
        bool InGrid(int i, int j) => i >= 0 && j >= 0 && i < n && j < n;
        void ToCell(Vector3 p, out int i, out int j) { i = Mathf.FloorToInt((p.x + Half) / Cell); j = Mathf.FloorToInt((p.z + Half) / Cell); }
        public bool Free(Vector3 p) { ToCell(p, out int i, out int j); return InGrid(i, j) && !blocked[i, j]; }

        bool NearestFree(int i, int j, int maxR, Vector3 prefer, out int fi, out int fj)
        {
            fi = i; fj = j;
            if (InGrid(i, j) && !blocked[i, j]) return true;
            for (int r = 1; r <= maxR; r++)
            {
                float best = float.MaxValue; bool found = false;
                for (int di = -r; di <= r; di++)
                    for (int dj = -r; dj <= r; dj++)
                    {
                        if (Mathf.Max(Mathf.Abs(di), Mathf.Abs(dj)) != r) continue;
                        int a = i + di, b = j + dj;
                        if (!InGrid(a, b) || blocked[a, b]) continue;
                        float d = (Center(a, b) - prefer).sqrMagnitude;
                        if (d < best) { best = d; fi = a; fj = b; found = true; }
                    }
                if (found) return true;
            }
            return false;
        }

        bool LineFree(Vector3 a, Vector3 b)
        {
            float len = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
            int steps = Mathf.CeilToInt(len / (Cell * 0.5f));
            for (int s = 0; s <= steps; s++) if (!Free(Vector3.Lerp(a, b, s / (float)Mathf.Max(1, steps)))) return false;
            return true;
        }

        /// <summary>Path (world points, y = ground) from 'from' to the free cell closest to 'to'. Null if unreachable.</summary>
        public List<Vector3> FindPath(Vector3 from, Vector3 to)
        {
            ToCell(from, out int si, out int sj);
            ToCell(to, out int gi, out int gj);
            if (!NearestFree(si, sj, 4, from, out si, out sj)) return null;
            if (!NearestFree(gi, gj, 10, from, out gi, out gj)) return null;

            var gScore = new float[n, n];
            var parent = new int[n, n];
            var closed = new bool[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) { gScore[i, j] = float.MaxValue; parent[i, j] = -1; }
            var open = new MinHeap();
            gScore[si, sj] = 0;
            open.Push(si * n + sj, H(si, sj, gi, gj));
            int[] di = { 1, -1, 0, 0, 1, 1, -1, -1 }, dj = { 0, 0, 1, -1, 1, -1, 1, -1 };
            bool ok = false;
            while (open.Count > 0)
            {
                int cur = open.Pop(); int ci = cur / n, cj = cur % n;
                if (closed[ci, cj]) continue;
                closed[ci, cj] = true;
                if (ci == gi && cj == gj) { ok = true; break; }
                for (int k = 0; k < 8; k++)
                {
                    int a = ci + di[k], b = cj + dj[k];
                    if (!InGrid(a, b) || blocked[a, b] || closed[a, b]) continue;
                    if (k >= 4 && (blocked[ci + di[k], cj] || blocked[ci, cj + dj[k]])) continue; // no corner cutting
                    float ng = gScore[ci, cj] + (k >= 4 ? 1.4142f : 1f);
                    if (ng < gScore[a, b]) { gScore[a, b] = ng; parent[a, b] = cur; open.Push(a * n + b, ng + H(a, b, gi, gj)); }
                }
            }
            if (!ok) return null;

            var cells = new List<Vector3>();
            for (int c = gi * n + gj; c != -1; c = parent[c / n, c % n]) { var p = Center(c / n, c % n); p.y = EnvironmentController.Height(p.x, p.z); cells.Add(p); }
            cells.Reverse();
            // String-pulling: keep only the waypoints needed for straight, obstacle-free segments.
            var path = new List<Vector3>();
            Vector3 anchor = from;
            for (int k = 1; k < cells.Count; k++)
                if (!LineFree(anchor, cells[k])) { path.Add(cells[k - 1]); anchor = cells[k - 1]; }
            path.Add(cells[cells.Count - 1]);
            return path;
        }

        static float H(int a, int b, int c, int d) { int dx = Mathf.Abs(a - c), dy = Mathf.Abs(b - d); return Mathf.Max(dx, dy) + 0.4142f * Mathf.Min(dx, dy); }

        class MinHeap
        {
            readonly List<(int id, float k)> h = new List<(int, float)>();
            public int Count => h.Count;
            public void Push(int id, float k)
            {
                h.Add((id, k)); int i = h.Count - 1;
                while (i > 0) { int p = (i - 1) / 2; if (h[p].k <= h[i].k) break; (h[p], h[i]) = (h[i], h[p]); i = p; }
            }
            public int Pop()
            {
                var top = h[0].id; var last = h[h.Count - 1]; h.RemoveAt(h.Count - 1);
                if (h.Count > 0)
                {
                    h[0] = last; int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1, r = l + 1, m = i;
                        if (l < h.Count && h[l].k < h[m].k) m = l;
                        if (r < h.Count && h[r].k < h[m].k) m = r;
                        if (m == i) break;
                        (h[m], h[i]) = (h[i], h[m]); i = m;
                    }
                }
                return top;
            }
        }
    }
}
