using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mossbound
{
    /// <summary>Accumulates flat-shaded triangles (low-poly look) with optional sub-meshes.</summary>
    public class MeshBuilder
    {
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<List<int>> subs = new List<List<int>> { new List<int>() };

        /// <summary>Transform applied to every triangle added.</summary>
        public Matrix4x4 M = Matrix4x4.identity;
        int sub;
        public int Sub
        {
            get => sub;
            set { sub = value; while (subs.Count <= sub) subs.Add(new List<int>()); }
        }

        public void Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            int i = verts.Count;
            verts.Add(M.MultiplyPoint3x4(a));
            verts.Add(M.MultiplyPoint3x4(b));
            verts.Add(M.MultiplyPoint3x4(c));
            var t = subs[sub];
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
        }

        /// <summary>Adds a triangle oriented so its face normal points along <paramref name="outward"/> (local space).</summary>
        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) { var t = b; b = c; c = t; }
            Tri(a, b, c);
        }

        public Mesh Build(string name = "mesh")
        {
            var m = new Mesh { name = name };
            if (verts.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(verts);
            m.subMeshCount = subs.Count;
            for (int i = 0; i < subs.Count; i++) m.SetTriangles(subs[i], i);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>Procedural low-poly primitives. All write into a MeshBuilder using its current matrix.</summary>
    public static class Prim
    {
        public static float Hash(int i, int seed = 0)
        {
            float v = Mathf.Sin(i * 12.9898f + seed * 78.233f) * 43758.5453f;
            return v - Mathf.Floor(v);
        }

        /// <summary>Surface of revolution around +Y. profile = (radius, height) from bottom to top.</summary>
        public static void Lathe(MeshBuilder mb, IList<Vector2> prof, int seg, float jitter = 0f, int seed = 0,
                                 bool capBottom = false, bool capTop = false)
        {
            int n = prof.Count;
            var ring = new Vector3[n, seg];
            for (int i = 0; i < n; i++)
                for (int s = 0; s < seg; s++)
                {
                    float a = (float)s / seg * Mathf.PI * 2f;
                    float j = jitter > 0 ? 1f + jitter * (Hash(i * 131 + s, seed) - 0.5f) : 1f;
                    float r = prof[i].x * j;
                    float y = prof[i].y + jitter * 0.3f * prof[i].x * (Hash(i * 71 + s * 3, seed + 5) - 0.5f);
                    ring[i, s] = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
                }

            for (int i = 0; i < n - 1; i++)
            {
                float dr = prof[i + 1].x - prof[i].x, dy = prof[i + 1].y - prof[i].y;
                bool z0 = prof[i].x < 1e-4f, z1 = prof[i + 1].x < 1e-4f;
                if (z0 && z1) continue;
                for (int s = 0; s < seg; s++)
                {
                    int s2 = (s + 1) % seg;
                    Vector3 a = ring[i, s], b = ring[i, s2], c = ring[i + 1, s2], d = ring[i + 1, s];
                    Vector3 mid = (a + b + c + d) * 0.25f;
                    Vector3 radial = new Vector3(mid.x, 0f, mid.z);
                    radial = radial.sqrMagnitude > 1e-8f ? radial.normalized : Vector3.right;
                    Vector3 hint = radial * dy + Vector3.up * -dr;   // profile-plane outward normal
                    if (hint.sqrMagnitude < 1e-8f) hint = radial;
                    if (z0) mb.Tri(a, c, d, hint);
                    else if (z1) mb.Tri(a, b, c, hint);
                    else { mb.Tri(a, b, c, hint); mb.Tri(a, c, d, hint); }
                }
            }
            if (capBottom && prof[0].x > 1e-4f)
            {
                var ctr = new Vector3(0, prof[0].y, 0);
                for (int s = 0; s < seg; s++) mb.Tri(ctr, ring[0, s], ring[0, (s + 1) % seg], Vector3.down);
            }
            if (capTop && prof[n - 1].x > 1e-4f)
            {
                var ctr = new Vector3(0, prof[n - 1].y, 0);
                for (int s = 0; s < seg; s++) mb.Tri(ctr, ring[n - 1, s], ring[n - 1, (s + 1) % seg], Vector3.up);
            }
        }

        public static void Cylinder(MeshBuilder mb, float rBottom, float rTop, float h, int seg, bool caps = true)
            => Lathe(mb, new[] { new Vector2(rBottom, 0), new Vector2(rTop, h) }, seg, 0, 0, caps, caps);

        /// <summary>Hexagonal crystal shard, base at origin, pointing +Y.</summary>
        public static void Crystal(MeshBuilder mb, float r, float h, int sides = 6)
            => Lathe(mb, new[] { new Vector2(r * 0.55f, 0), new Vector2(r, h * 0.16f), new Vector2(r * 0.9f, h * 0.72f), new Vector2(0, h) },
                     sides, 0, 0, true, false);

        // ---- Icosphere (unit diameter) ----
        static readonly Dictionary<int, (List<Vector3>, List<int>)> icoCache = new Dictionary<int, (List<Vector3>, List<int>)>();

        static (List<Vector3>, List<int>) Ico(int subdiv)
        {
            if (icoCache.TryGetValue(subdiv, out var cached)) return cached;
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            var v = new List<Vector3> {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1) };
            for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized;
            var f = new List<int> { 0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                                    3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1 };
            for (int d = 0; d < subdiv; d++)
            {
                var mid = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (mid.TryGetValue(key, out int idx)) return idx;
                    v.Add(((v[a] + v[b]) * 0.5f).normalized);
                    mid[key] = v.Count - 1; return v.Count - 1;
                }
                var nf = new List<int>();
                for (int i = 0; i < f.Count; i += 3)
                {
                    int a = f[i], b = f[i + 1], c = f[i + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    nf.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                f = nf;
            }
            icoCache[subdiv] = (v, f);
            return (v, f);
        }

        public static void Sphere(MeshBuilder mb, int subdiv = 1, float jitter = 0f, int seed = 0)
        {
            var (v, f) = Ico(subdiv);
            var p = new Vector3[v.Count];
            for (int i = 0; i < v.Count; i++)
                p[i] = v[i] * 0.5f * (jitter > 0 ? 1f + jitter * (Hash(i, seed) - 0.5f) : 1f);
            for (int i = 0; i < f.Count; i += 3)
            {
                Vector3 a = p[f[i]], b = p[f[i + 1]], c = p[f[i + 2]];
                mb.Tri(a, b, c, (a + b + c) / 3f);
            }
        }

        /// <summary>Torus in the XY plane (ring faces +Z/-Z).</summary>
        public static void Torus(MeshBuilder mb, float R, float r, int segU, int segV)
        {
            Vector3 P(int u, int vv)
            {
                float a = (float)u / segU * Mathf.PI * 2f, b = (float)vv / segV * Mathf.PI * 2f;
                return new Vector3((R + r * Mathf.Cos(b)) * Mathf.Cos(a), (R + r * Mathf.Cos(b)) * Mathf.Sin(a), r * Mathf.Sin(b));
            }
            for (int u = 0; u < segU; u++)
                for (int vv = 0; vv < segV; vv++)
                {
                    Vector3 a = P(u, vv), b = P(u + 1, vv), c = P(u + 1, vv + 1), d = P(u, vv + 1);
                    float ac = (u + 0.5f) / segU * Mathf.PI * 2f;
                    Vector3 hint = (a + b + c + d) * 0.25f - new Vector3(Mathf.Cos(ac) * R, Mathf.Sin(ac) * R, 0);
                    mb.Tri(a, b, c, hint); mb.Tri(a, c, d, hint);
                }
        }

        public static void Disc(MeshBuilder mb, float radius, int seg, Vector3 normal)
        {
            for (int s = 0; s < seg; s++)
            {
                float a0 = (float)s / seg * Mathf.PI * 2f, a1 = (float)(s + 1) / seg * Mathf.PI * 2f;
                mb.Tri(Vector3.zero, new Vector3(Mathf.Cos(a0) * radius, 0, Mathf.Sin(a0) * radius),
                       new Vector3(Mathf.Cos(a1) * radius, 0, Mathf.Sin(a1) * radius), normal);
            }
        }

        public static Mesh Make(System.Action<MeshBuilder> f, string name = "mesh")
        {
            var mb = new MeshBuilder(); f(mb); return mb.Build(name);
        }
    }
}
