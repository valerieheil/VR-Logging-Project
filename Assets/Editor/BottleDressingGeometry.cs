// ============================================================
//  BottleDressingGeometry.cs — pure C# (no Unity API), used by BottleDressingTool.
//  Reads the vertices of a bottle mesh, measures its radius profile along
//  its long axis, and builds two meshes that follow that profile:
//    * Water : a closed "lathe" volume slightly inside the bottle wall,
//              from just above the base up to the shoulder.
//    * Label : an open band slightly outside the bottle, placed on the
//              longest smooth (rib-free) section of the body.
//  All output is in the bottle mesh's own local space, so the result can
//  be attached as a child with an identity transform — independent of
//  import scale or axis conversion.
// ============================================================
using System;
using System.Collections.Generic;

public static class BottleDressingGeometry
{
    public struct V3
    {
        public double x, y, z;
        public V3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        public static V3 operator +(V3 a, V3 b) => new V3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static V3 operator *(V3 a, double s) => new V3(a.x * s, a.y * s, a.z * s);
        public double this[int i] => i == 0 ? x : i == 1 ? y : z;
        public static double Dot(V3 a, V3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static V3 Cross(V3 a, V3 b) => new V3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static V3 Axis(int i, double s = 1) => new V3(i == 0 ? s : 0, i == 1 ? s : 0, i == 2 ? s : 0);
    }

    public class Profile
    {
        public V3 up, e1, e2;          // up = towards the neck; e1/e2 radial basis
        public V3 center;              // a point on the axis
        public double tMin, tMax;      // extent along 'up', measured from 'center'
        public List<double> ringT = new List<double>();
        public List<double> ringR = new List<double>();
        public double Height => tMax - tMin;

        public double R(double t)
        {
            if (t <= ringT[0]) return ringR[0];
            for (int i = 1; i < ringT.Count; i++)
                if (t <= ringT[i])
                {
                    double f = (t - ringT[i - 1]) / Math.Max(1e-12, ringT[i] - ringT[i - 1]);
                    return ringR[i - 1] + f * (ringR[i] - ringR[i - 1]);
                }
            return ringR[ringR.Count - 1];
        }

        public V3 Point(double t, double r, double angle)
            => center + up * t + e1 * (r * Math.Cos(angle)) + e2 * (r * Math.Sin(angle));
    }

    public class MeshData
    {
        public List<V3> v = new List<V3>();
        public List<double[]> uv = new List<double[]>();
        public List<int> tri = new List<int>();
        public string info = "";
    }

    // ── 1) Profile ────────────────────────────────────────────
    public static Profile Analyze(IList<V3> verts)
    {
        double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue };
        double[] mx = { double.MinValue, double.MinValue, double.MinValue };
        foreach (var p in verts)
            for (int i = 0; i < 3; i++) { mn[i] = Math.Min(mn[i], p[i]); mx[i] = Math.Max(mx[i], p[i]); }

        int a = 0;
        for (int i = 1; i < 3; i++) if (mx[i] - mn[i] > mx[a] - mn[a]) a = i;   // longest axis = bottle axis
        int b = (a + 1) % 3, c = (a + 2) % 3;

        var pr = new Profile
        {
            center = new V3((mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2),
            up = V3.Axis(a), e1 = V3.Axis(b), e2 = V3.Axis(c)
        };
        BuildRings(verts, pr);

        // The neck is the narrow end: flip so that 'up' points to the neck.
        int n = pr.ringT.Count, k = Math.Max(1, n / 8);
        double low = 0, high = 0;
        for (int i = 0; i < k; i++) { low += pr.ringR[i]; high += pr.ringR[n - 1 - i]; }
        if (high > low) { pr.up = V3.Axis(a, -1); pr.e2 = V3.Axis(c, -1); BuildRings(verts, pr); }
        return pr;
    }

    private static void BuildRings(IList<V3> verts, Profile pr)
    {
        var tr = new List<double[]>();
        double tMin = double.MaxValue, tMax = double.MinValue;
        foreach (var p in verts)
        {
            var d = p - pr.center;
            double t = V3.Dot(d, pr.up);
            double u = V3.Dot(d, pr.e1), w = V3.Dot(d, pr.e2);
            tr.Add(new[] { t, Math.Sqrt(u * u + w * w) });
            tMin = Math.Min(tMin, t); tMax = Math.Max(tMax, t);
        }
        tr.Sort((p, q) => p[0].CompareTo(q[0]));
        double tol = (tMax - tMin) / 500.0;

        pr.ringT.Clear(); pr.ringR.Clear();
        int i = 0;
        while (i < tr.Count)
        {
            double t0 = tr[i][0], sumT = 0, maxR = 0; int cnt = 0;
            while (i < tr.Count && tr[i][0] - t0 <= tol) { sumT += tr[i][0]; maxR = Math.Max(maxR, tr[i][1]); cnt++; i++; }
            pr.ringT.Add(sumT / cnt); pr.ringR.Add(maxR);
        }
        pr.tMin = tMin; pr.tMax = tMax;
    }

    // ── 2) Water ──────────────────────────────────────────────
    /// <param name="shoulderRatio">fill up to where the bottle radius drops below this share of its max radius</param>
    public static MeshData BuildWater(Profile pr, int segments = 48, double wallInset = 0.94,
                                      double baseOffset = 0.03, double shoulderRatio = 0.80)
    {
        double H = pr.Height, rMax = 0;
        foreach (var r in pr.ringR) rMax = Math.Max(rMax, r);

        double tb = pr.tMin + baseOffset * H;
        double tt = pr.tMin + 0.85 * H;                       // fallback
        for (int i = 1; i < pr.ringT.Count; i++)
        {
            if (pr.ringT[i] < pr.tMin + 0.55 * H) continue;
            if (pr.ringR[i] < shoulderRatio * rMax && pr.ringR[i - 1] >= shoulderRatio * rMax)
            {
                double f = (pr.ringR[i - 1] - shoulderRatio * rMax) / Math.Max(1e-12, pr.ringR[i - 1] - pr.ringR[i]);
                tt = pr.ringT[i - 1] + f * (pr.ringT[i] - pr.ringT[i - 1]);
                break;
            }
        }

        var ts = new List<double> { tb };
        for (int i = 0; i < pr.ringT.Count; i++) if (pr.ringT[i] > tb && pr.ringT[i] < tt) ts.Add(pr.ringT[i]);
        ts.Add(tt);

        var m = new MeshData();
        // side
        for (int j = 0; j < ts.Count; j++)
            for (int s = 0; s <= segments; s++)
            {
                m.v.Add(pr.Point(ts[j], pr.R(ts[j]) * wallInset, 2 * Math.PI * s / segments));
                m.uv.Add(new[] { (double)s / segments, (ts[j] - tb) / (tt - tb) });
            }
        for (int j = 0; j < ts.Count - 1; j++)
            for (int s = 0; s < segments; s++)
            {
                int i0 = j * (segments + 1) + s, i1 = i0 + 1, i2 = i0 + segments + 1, i3 = i2 + 1;
                m.tri.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
            }
        OrientOutward(m, 0, m.tri.Count, pr, false, 0);

        // caps (own vertices -> flat normals)
        AddCap(m, pr, tb, pr.R(tb) * wallInset, segments, -1);
        AddCap(m, pr, tt, pr.R(tt) * wallInset, segments, +1);

        m.info = $"water from {(tb - pr.tMin) / H:P0} to {(tt - pr.tMin) / H:P0} of bottle height";
        return m;
    }

    private static void AddCap(MeshData m, Profile pr, double t, double r, int segments, int dir)
    {
        int start = m.tri.Count, c0 = m.v.Count;
        m.v.Add(pr.Point(t, 0, 0)); m.uv.Add(new[] { 0.5, 0.5 });
        for (int s = 0; s <= segments; s++)
        {
            double ang = 2 * Math.PI * s / segments;
            m.v.Add(pr.Point(t, r, ang)); m.uv.Add(new[] { 0.5 + 0.5 * Math.Cos(ang), 0.5 + 0.5 * Math.Sin(ang) });
        }
        for (int s = 0; s < segments; s++) m.tri.AddRange(new[] { c0, c0 + 1 + s, c0 + 2 + s });
        OrientOutward(m, start, m.tri.Count, pr, true, dir);
    }

    // ── 3) Label ──────────────────────────────────────────────
    public static MeshData BuildLabel(Profile pr, int segments = 64, double offset = 1.012,
                                      bool reverseU = false)
    {
        double H = pr.Height;
        double a = 0, b = 0, best = 0;
        for (int i = 1; i < pr.ringT.Count; i++)
        {
            double t0 = pr.ringT[i - 1], t1 = pr.ringT[i];
            if (t0 < pr.tMin + 0.20 * H || t1 > pr.tMin + 0.85 * H) continue;
            if (t1 - t0 > best) { best = t1 - t0; a = t0; b = t1; }
        }
        string where;
        if (best >= 0.12 * H) { double pad = 0.03 * best; a += pad; b -= pad; where = "smooth body section"; }
        else { a = pr.tMin + 0.44 * H; b = pr.tMin + 0.66 * H; where = "fallback 44-66 %"; }

        double r = 0;
        for (double t = a; t <= b; t += (b - a) / 20) r = Math.Max(r, pr.R(t));
        r *= offset;

        var m = new MeshData();
        for (int j = 0; j <= 1; j++)
            for (int s = 0; s <= segments; s++)
            {
                double u = (double)s / segments;
                m.v.Add(pr.Point(j == 0 ? a : b, r, 2 * Math.PI * u));
                m.uv.Add(new[] { reverseU ? 1 - u : u, (double)j });
            }
        for (int s = 0; s < segments; s++)
        {
            int i0 = s, i1 = s + 1, i2 = s + segments + 1, i3 = i2 + 1;
            m.tri.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
        }
        OrientOutward(m, 0, m.tri.Count, pr, false, 0);
        m.info = $"label {(a - pr.tMin) / H:P0}-{(b - pr.tMin) / H:P0} of height ({where}), " +
                 $"height/circumference = {(b - a) / (2 * Math.PI * r):0.000}";
        return m;
    }

    /// <summary>Flip triangle winding in [start,end) so faces point away from the axis
    /// (side) or along ±up (caps). Unity front face normal = Cross(b-a, c-a).</summary>
    private static void OrientOutward(MeshData m, int start, int end, Profile pr, bool cap, int dir)
    {
        var A = m.v[m.tri[start]]; var B = m.v[m.tri[start + 1]]; var C = m.v[m.tri[start + 2]];
        var n = V3.Cross(B - A, C - A);
        V3 want;
        if (cap) want = pr.up * dir;
        else
        {
            var mid = (A + B + C) * (1.0 / 3);
            var d = mid - pr.center;
            want = d - pr.up * V3.Dot(d, pr.up);
        }
        if (V3.Dot(n, want) < 0)
            for (int i = start; i < end; i += 3) { int tmp = m.tri[i + 1]; m.tri[i + 1] = m.tri[i + 2]; m.tri[i + 2] = tmp; }
    }

    /// <summary>World-direction check for readable (non-mirrored) text. Returns true if U must be reversed.
    /// Inputs are already transformed to world space: up, outward radial at angle 0, tangent of increasing angle.</summary>
    public static bool TextNeedsReverse(V3 upW, V3 outwardW, V3 tangentW)
    {
        // Viewer outside looks inward (forward = -outward). In Unity, right = Cross(up, forward).
        var right = V3.Cross(upW, outwardW * -1);
        return V3.Dot(tangentW, right) < 0;
    }
}
