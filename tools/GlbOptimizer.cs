// GLB optimizer for Deep Garage (no Node/Draco needed).
// - drops primitives by material name (duplicate shells, shadow planes, engines...)
// - welds vertices, simplifies triangles (quadric error, endpoint collapse, locked borders)
// - downsizes textures to maxTex (PNG with alpha / JPEG without)
// - strips KHR_materials_transmission
// C# 5 compatible (compiled by Windows PowerShell 5.1 Add-Type).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

public static class GlbOptimizer
{
    static List<string> log = new List<string>();
    static void L(string s) { log.Add(s); Console.WriteLine(s); }

    static double D(object o) { return Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); }
    static int I(object o) { return Convert.ToInt32(o, System.Globalization.CultureInfo.InvariantCulture); }
    static Dictionary<string, object> O(object o) { return o as Dictionary<string, object>; }
    static List<object> A(object o)
    {
        if (o == null) return new List<object>();
        var arr = o as IEnumerable; var l = new List<object>();
        foreach (var x in arr) l.Add(x); return l;
    }
    static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }

    static int CompSize(int ct) { switch (ct) { case 5120: case 5121: return 1; case 5122: case 5123: return 2; default: return 4; } }
    static int NumComp(string t) { switch (t) { case "SCALAR": return 1; case "VEC2": return 2; case "VEC3": return 3; case "VEC4": return 4; case "MAT4": return 16; default: throw new Exception("type " + t); } }

    // ---------- buffer helpers ----------
    static byte[] bin;
    static List<object> accessors, bufferViews;

    static byte[] ReadAccessorElems(int ai, out int count, out int elemSize)
    {
        var acc = O(accessors[ai]);
        if (Get(acc, "sparse") != null) throw new Exception("sparse accessors not supported");
        count = I(acc["count"]);
        elemSize = CompSize(I(acc["componentType"])) * NumComp((string)acc["type"]);
        var outb = new byte[count * elemSize];
        if (Get(acc, "bufferView") == null) return outb;
        var bv = O(bufferViews[I(acc["bufferView"])]);
        int baseOff = (Get(bv, "byteOffset") != null ? I(bv["byteOffset"]) : 0) + (Get(acc, "byteOffset") != null ? I(acc["byteOffset"]) : 0);
        int stride = Get(bv, "byteStride") != null ? I(bv["byteStride"]) : elemSize;
        for (int i = 0; i < count; i++) Buffer.BlockCopy(bin, baseOff + i * stride, outb, i * elemSize, elemSize);
        return outb;
    }

    static uint[] ReadIndices(int ai)
    {
        int count, es; var b = ReadAccessorElems(ai, out count, out es);
        int ct = I(O(accessors[ai])["componentType"]);
        var r = new uint[count];
        for (int i = 0; i < count; i++)
            r[i] = ct == 5125 ? BitConverter.ToUInt32(b, i * 4) : ct == 5123 ? BitConverter.ToUInt16(b, i * 2) : b[i];
        return r;
    }

    // ---------- output buffer ----------
    static MemoryStream outBin;
    static List<object> newViews, newAccessors;

    static int AddView(byte[] data, int? target)
    {
        while (outBin.Length % 4 != 0) outBin.WriteByte(0);
        var v = new Dictionary<string, object>();
        v["buffer"] = 0; v["byteOffset"] = (int)outBin.Length; v["byteLength"] = data.Length;
        if (target.HasValue) v["target"] = target.Value;
        outBin.Write(data, 0, data.Length);
        newViews.Add(v); return newViews.Count - 1;
    }

    // ---------- simplifier ----------
    class Q { public double[] m = new double[10];
        public void AddPlane(double a, double b, double c, double d, double w) {
            m[0]+=w*a*a; m[1]+=w*a*b; m[2]+=w*a*c; m[3]+=w*a*d; m[4]+=w*b*b; m[5]+=w*b*c; m[6]+=w*b*d; m[7]+=w*c*c; m[8]+=w*c*d; m[9]+=w*d*d; }
        public void Add(Q o) { for (int i = 0; i < 10; i++) m[i] += o.m[i]; }
        public double Err(double x, double y, double z) {
            return m[0]*x*x + 2*m[1]*x*y + 2*m[2]*x*z + 2*m[3]*x + m[4]*y*y + 2*m[5]*y*z + 2*m[6]*y + m[7]*z*z + 2*m[8]*z + m[9]; } }

    struct HeapItem { public double cost; public int from, to, vf, vt; }
    class MinHeap {
        List<HeapItem> h = new List<HeapItem>();
        public int Count { get { return h.Count; } }
        public void Push(HeapItem x) { h.Add(x); int i = h.Count - 1; while (i > 0) { int p = (i - 1) / 2; if (h[p].cost <= h[i].cost) break; var t = h[p]; h[p] = h[i]; h[i] = t; i = p; } }
        public HeapItem Pop() { var r = h[0]; var last = h[h.Count - 1]; h.RemoveAt(h.Count - 1); if (h.Count > 0) { h[0] = last; int i = 0; for (;;) { int l = 2*i+1, rr = l+1, m = i; if (l < h.Count && h[l].cost < h[m].cost) m = l; if (rr < h.Count && h[rr].cost < h[m].cost) m = rr; if (m == i) break; var t = h[m]; h[m] = h[i]; h[i] = t; i = m; } } return r; }
    }

    // Simplifies on position-welded topology (so attribute seams / hard edges don't lock the mesh),
    // while every face corner keeps a real vertex (with its own normal/uv) at the surviving position.
    // returns new index list (triangles) referencing original vertex ids
    static uint[] Simplify(float[] Pv, uint[] idx, float[] Nv, int targetTris)
    {
        int nvv = Pv.Length / 3, nf = idx.Length / 3;
        var posId = new int[nvv]; var pmap = new Dictionary<long, int>(); var plist = new List<float>(); var posVerts = new List<List<int>>();
        for (int i = 0; i < nvv; i++) {
            long kx = (long)Math.Round(Pv[i*3] * 1e5), ky = (long)Math.Round(Pv[i*3+1] * 1e5), kz = (long)Math.Round(Pv[i*3+2] * 1e5);
            long key = (kx * 73856093L) ^ (ky * 19349663L) ^ (kz * 83492791L);
            // resolve hash collisions by checking actual coordinates
            int id; bool found = false;
            while (pmap.TryGetValue(key, out id)) {
                if (Math.Abs(plist[id*3]-Pv[i*3]) < 2e-5 && Math.Abs(plist[id*3+1]-Pv[i*3+1]) < 2e-5 && Math.Abs(plist[id*3+2]-Pv[i*3+2]) < 2e-5) { found = true; break; }
                key++;
            }
            if (!found) { id = posVerts.Count; pmap[key] = id; plist.Add(Pv[i*3]); plist.Add(Pv[i*3+1]); plist.Add(Pv[i*3+2]); posVerts.Add(new List<int>()); }
            posId[i] = id; posVerts[id].Add(i);
        }
        var P = plist.ToArray(); int nv = posVerts.Count;
        var Fc = new int[nf * 3]; for (int i = 0; i < Fc.Length; i++) Fc[i] = (int)idx[i];
        var F = new int[nf * 3]; for (int i = 0; i < F.Length; i++) F[i] = posId[Fc[i]];
        Func<int, int, int> pickVertex = (oldV, pv) => {
            var cand = posVerts[pv]; if (cand.Count == 1 || Nv == null) return cand[0];
            int best = cand[0]; double bd = -2;
            foreach (int c in cand) { double d = Nv[c*3]*Nv[oldV*3] + Nv[c*3+1]*Nv[oldV*3+1] + Nv[c*3+2]*Nv[oldV*3+2]; if (d > bd) { bd = d; best = c; } }
            return best;
        };
        var alive = new bool[nf]; int faces = 0;
        for (int i = 0; i < nf; i++) { alive[i] = F[i*3] != F[i*3+1] && F[i*3+1] != F[i*3+2] && F[i*3] != F[i*3+2]; if (alive[i]) faces++; }
        var vf = new List<int>[nv]; for (int i = 0; i < nv; i++) vf[i] = new List<int>(6);
        for (int f = 0; f < nf; f++) if (alive[f]) for (int k = 0; k < 3; k++) vf[F[f*3+k]].Add(f);
        var Qs = new Q[nv]; for (int i = 0; i < nv; i++) Qs[i] = new Q();
        // diagonal for scale-aware border penalty
        double minx=1e30,miny=1e30,minz=1e30,maxx=-1e30,maxy=-1e30,maxz=-1e30;
        for (int i = 0; i < nv; i++) { minx=Math.Min(minx,P[i*3]); maxx=Math.Max(maxx,P[i*3]); miny=Math.Min(miny,P[i*3+1]); maxy=Math.Max(maxy,P[i*3+1]); minz=Math.Min(minz,P[i*3+2]); maxz=Math.Max(maxz,P[i*3+2]); }
        for (int f = 0; f < nf; f++) {
            if (!alive[f]) continue;
            int a = F[f*3], b = F[f*3+1], c = F[f*3+2];
            double ux=P[b*3]-P[a*3], uy=P[b*3+1]-P[a*3+1], uz=P[b*3+2]-P[a*3+2];
            double vx=P[c*3]-P[a*3], vy=P[c*3+1]-P[a*3+1], vz=P[c*3+2]-P[a*3+2];
            double nx=uy*vz-uz*vy, ny=uz*vx-ux*vz, nz=ux*vy-uy*vx; double len=Math.Sqrt(nx*nx+ny*ny+nz*nz);
            if (len < 1e-30) continue; nx/=len; ny/=len; nz/=len; double d=-(nx*P[a*3]+ny*P[a*3+1]+nz*P[a*3+2]);
            double area = len * 0.5;
            for (int k = 0; k < 3; k++) Qs[F[f*3+k]].AddPlane(nx,ny,nz,d,area);
        }
        // border detection: edges used by exactly one face -> lock their vertices
        var edgeCount = new Dictionary<long, int>();
        for (int f = 0; f < nf; f++) if (alive[f]) for (int k = 0; k < 3; k++) {
            int a = F[f*3+k], b = F[f*3+(k+1)%3]; long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int c; edgeCount.TryGetValue(key, out c); edgeCount[key] = c + 1; }
        var locked = new bool[nv];
        foreach (var kv in edgeCount) if (kv.Value != 2) { locked[(int)(kv.Key >> 32)] = true; locked[(int)(kv.Key & 0xffffffff)] = true; }
        var ver = new int[nv];
        var dead = new bool[nv];
        var heap = new MinHeap();
        Action<int,int> pushEdge = null;
        pushEdge = (a, b) => {
            // try a->b and b->a, keep cheapest legal
            double best = double.MaxValue; int fr = -1, to = -1;
            if (!locked[a]) { var q = new Q(); q.Add(Qs[a]); q.Add(Qs[b]); double e = q.Err(P[b*3],P[b*3+1],P[b*3+2]); if (e < best) { best = e; fr = a; to = b; } }
            if (!locked[b]) { var q = new Q(); q.Add(Qs[a]); q.Add(Qs[b]); double e = q.Err(P[a*3],P[a*3+1],P[a*3+2]); if (e < best) { best = e; fr = b; to = a; } }
            if (fr < 0) return;
            var it = new HeapItem(); it.cost = best; it.from = fr; it.to = to; it.vf = ver[fr]; it.vt = ver[to]; heap.Push(it);
        };
        foreach (var kv in edgeCount) { int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff); pushEdge(a, b); }
        var tmpN =new HashSet<int>(); var tmpN2 = new HashSet<int>();
        while (faces > targetTris && heap.Count > 0) {
            var it = heap.Pop();
            int u = it.from, v = it.to;
            if (dead[u] || dead[v] || ver[u] != it.vf || ver[v] != it.vt) continue;
            // link condition: common neighbours must be exactly 2 (interior edge)
            tmpN.Clear(); tmpN2.Clear();
            foreach (int f in vf[u]) if (alive[f]) for (int k = 0; k < 3; k++) tmpN.Add(F[f*3+k]);
            foreach (int f in vf[v]) if (alive[f]) for (int k = 0; k < 3; k++) tmpN2.Add(F[f*3+k]);
            if (!tmpN.Contains(v)) continue;
            int common = 0; foreach (int x in tmpN) if (x != u && x != v && tmpN2.Contains(x)) common++;
            if (common != 2) continue;
            // flip check
            bool bad = false;
            foreach (int f in vf[u]) {
                if (!alive[f]) continue;
                int a = F[f*3], b = F[f*3+1], c = F[f*3+2];
                if (a == v || b == v || c == v) continue;
                double[] n0 = Normal(P, a, b, c);
                int a2 = a == u ? v : a, b2 = b == u ? v : b, c2 = c == u ? v : c;
                double[] n1 = Normal(P, a2, b2, c2);
                if (n1 == null || (n0 != null && n0[0]*n1[0]+n0[1]*n1[1]+n0[2]*n1[2] < 0.3)) { bad = true; break; }
            }
            if (bad) continue;
            // collapse u -> v
            foreach (int f in vf[u]) {
                if (!alive[f]) continue;
                bool hasV = F[f*3] == v || F[f*3+1] == v || F[f*3+2] == v;
                if (hasV) { alive[f] = false; faces--; continue; }
                for (int k = 0; k < 3; k++) if (F[f*3+k] == u) { F[f*3+k] = v; Fc[f*3+k] = pickVertex(Fc[f*3+k], v); }
                vf[v].Add(f);
            }
            dead[u] = true; Qs[v].Add(Qs[u]); ver[v]++;
            vf[v].RemoveAll(f => !alive[f]);
            tmpN.Clear(); foreach (int f in vf[v]) for (int k = 0; k < 3; k++) tmpN.Add(F[f*3+k]);
            foreach (int x in tmpN) if (x != v && !dead[x]) { ver[x]++; }
            foreach (int x in tmpN) if (x != v && !dead[x]) pushEdge(v, x);
        }
        var res = new List<uint>(faces * 3);
        for (int f = 0; f < nf; f++) if (alive[f]) { res.Add((uint)Fc[f*3]); res.Add((uint)Fc[f*3+1]); res.Add((uint)Fc[f*3+2]); }
        return res.ToArray();
    }

    static double[] Normal(float[] P, int a, int b, int c)
    {
        double ux=P[b*3]-P[a*3], uy=P[b*3+1]-P[a*3+1], uz=P[b*3+2]-P[a*3+2];
        double vx=P[c*3]-P[a*3], vy=P[c*3+1]-P[a*3+1], vz=P[c*3+2]-P[a*3+2];
        double nx=uy*vz-uz*vy, ny=uz*vx-ux*vz, nz=ux*vy-uy*vx; double len=Math.Sqrt(nx*nx+ny*ny+nz*nz);
        if (len < 1e-30) return null; return new double[] { nx/len, ny/len, nz/len };
    }

    // ---------- images ----------
    static byte[] ShrinkImage(byte[] data, int maxTex, out string mime)
    {
        using (var ms = new MemoryStream(data))
        using (var src = new Bitmap(ms))
        {
            int w = src.Width, h = src.Height;
            double s = Math.Min(1.0, (double)maxTex / Math.Max(w, h));
            int nw = Math.Max(1, (int)Math.Round(w * s)), nh = Math.Max(1, (int)Math.Round(h * s));
            using (var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(dst)) {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using (var wrap = new ImageAttributes()) { wrap.SetWrapMode(WrapMode.TileFlipXY); g.DrawImage(src, new Rectangle(0, 0, nw, nh), 0, 0, w, h, GraphicsUnit.Pixel, wrap); }
                }
                bool alpha = false;
                var bd = dst.LockBits(new Rectangle(0, 0, nw, nh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var px = new byte[bd.Stride * nh]; System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length); dst.UnlockBits(bd);
                for (int i = 3; i < px.Length; i += 4) if (px[i] < 250) { alpha = true; break; }
                using (var outMs = new MemoryStream()) {
                    if (alpha) { dst.Save(outMs, ImageFormat.Png); mime = "image/png"; }
                    else {
                        var enc = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
                        var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 86L);
                        dst.Save(outMs, enc, ep); mime = "image/jpeg";
                    }
                    L(string.Format("  image {0}x{1} -> {2}x{3} {4} {5:0} KB -> {6:0} KB", w, h, nw, nh, mime, data.Length / 1024.0, outMs.Length / 1024.0));
                    if (s >= 1.0 && outMs.Length >= data.Length) { mime = null; return data; }
                    return outMs.ToArray();
                }
            }
        }
    }

    // ---------- main ----------
    public static string Run(string inPath, string outPath, int targetTris, int maxTex, string dropMaterials, string dropNodes)
    {
        log.Clear();
        var file = File.ReadAllBytes(inPath);
        if (BitConverter.ToUInt32(file, 0) != 0x46546C67) throw new Exception("not a GLB");
        int jsonLen = BitConverter.ToInt32(file, 12);
        string json = Encoding.UTF8.GetString(file, 20, jsonLen);
        int binOff = 20 + jsonLen; int binLen = BitConverter.ToInt32(file, binOff);
        bin = new byte[binLen]; Buffer.BlockCopy(file, binOff + 8, bin, 0, binLen);
        var ser = new JavaScriptSerializer(); ser.MaxJsonLength = int.MaxValue; ser.RecursionLimit = 1000;
        var gl = (Dictionary<string, object>)ser.DeserializeObject(json);
        accessors = A(Get(gl, "accessors")); bufferViews = A(Get(gl, "bufferViews"));
        var materials = A(Get(gl, "materials")); var meshes = A(Get(gl, "meshes")); var nodes = A(Get(gl, "nodes"));
        // the site shows static cars: drop animations and skinning (meshes stay in bind pose)
        if (gl.Remove("animations")) L("drop animations");
        if (gl.Remove("skins")) {
            L("drop skins (static bind pose)");
            foreach (var no in nodes) O(no).Remove("skin");
            foreach (var mo in meshes) foreach (var po in A(O(mo)["primitives"])) {
                var at = O(O(po)["attributes"]);
                foreach (var k in at.Keys.Where(k => k.StartsWith("JOINTS_") || k.StartsWith("WEIGHTS_")).ToList()) at.Remove(k);
            }
        }
        var dropM = new HashSet<string>((dropMaterials ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        var dropN = string.IsNullOrEmpty(dropNodes) ? null : new System.Text.RegularExpressions.Regex(dropNodes, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // nodes to drop
        if (dropN != null) foreach (var no in nodes) { var n = O(no); var nm = (string)Get(n, "name") ?? ""; if (Get(n, "mesh") != null && dropN.IsMatch(nm)) { L("drop node mesh: " + nm); n.Remove("mesh"); } }

        outBin = new MemoryStream(); newViews = new List<object>(); newAccessors = new List<object>();

        // count tris for ratio
        long total = 0, kept = 0;
        foreach (var mo in meshes) foreach (var po in A(O(mo)["primitives"])) {
            var p = O(po); string mn = Get(p, "material") != null ? (string)Get(O(materials[I(p["material"])]), "name") : "";
            int n = Get(p, "indices") != null ? I(O(accessors[I(p["indices"])])["count"]) / 3 : I(O(accessors[I(O(p["attributes"])["POSITION"])])["count"]) / 3;
            total += n; if (!dropM.Contains(mn)) kept += n; }
        double ratio = Math.Min(1.0, (double)targetTris / Math.Max(1, kept));
        L(string.Format("triangles: {0} total, {1} after material drop, ratio {2:0.000}", total, kept, ratio));

        long outTris = 0;
        foreach (var mo in meshes) {
            var mesh = O(mo); var prims = A(mesh["primitives"]); var newPrims = new List<object>();
            foreach (var po in prims) {
                var p = O(po);
                string mn = Get(p, "material") != null ? (string)Get(O(materials[I(p["material"])]), "name") : "";
                if (dropM.Contains(mn)) { L("drop primitive in mesh '" + Get(mesh, "name") + "' (material " + mn + ")"); continue; }
                if (Get(p, "mode") != null && I(p["mode"]) != 4) throw new Exception("only triangle primitives supported");
                if (Get(p, "targets") != null) throw new Exception("morph targets not supported");
                var attrs = O(p["attributes"]);
                var names = attrs.Keys.ToList();
                int vcount = 0; var raw = new Dictionary<string, byte[]>(); var esz = new Dictionary<string, int>();
                foreach (var an in names) { int c, es; raw[an] = ReadAccessorElems(I(attrs[an]), out c, out es); esz[an] = es; vcount = c; }
                uint[] idx;
                if (Get(p, "indices") != null) idx = ReadIndices(I(p["indices"]));
                else { idx = new uint[vcount]; for (int i = 0; i < vcount; i++) idx[i] = (uint)i; }
                // weld identical vertices (quantised floats)
                var remap = new int[vcount]; var keyMap = new Dictionary<string, int>(); var uniq = new List<int>();
                for (int v = 0; v < vcount; v++) {
                    var sb = new StringBuilder();
                    foreach (var an in names) {
                        var acc = O(accessors[I(attrs[an])]); var b = raw[an]; int es = esz[an];
                        if (I(acc["componentType"]) == 5126) { double qf = an == "POSITION" ? 1e5 : 1e3; for (int k = 0; k < es / 4; k++) sb.Append(Math.Round(BitConverter.ToSingle(b, v * es + k * 4) * qf)).Append(','); }
                        else for (int k = 0; k < es; k++) sb.Append(b[v * es + k]).Append(',');
                        sb.Append('|');
                    }
                    var key = sb.ToString(); int id;
                    if (!keyMap.TryGetValue(key, out id)) { id = uniq.Count; keyMap[key] = id; uniq.Add(v); }
                    remap[v] = id;
                }
                int nv = uniq.Count;
                var P = new float[nv * 3];
                for (int i = 0; i < nv; i++) for (int k = 0; k < 3; k++) P[i * 3 + k] = BitConverter.ToSingle(raw["POSITION"], uniq[i] * 12 + k * 4);
                float[] Nn = null;
                if (raw.ContainsKey("NORMAL") && esz["NORMAL"] == 12) {
                    Nn = new float[nv * 3];
                    for (int i = 0; i < nv; i++) for (int k = 0; k < 3; k++) Nn[i * 3 + k] = BitConverter.ToSingle(raw["NORMAL"], uniq[i] * 12 + k * 4);
                }
                var wi = new uint[idx.Length]; for (int i = 0; i < idx.Length; i++) wi[i] = (uint)remap[idx[i]];
                // drop degenerate
                var clean = new List<uint>(wi.Length);
                for (int f = 0; f < wi.Length / 3; f++) { uint a = wi[f*3], b = wi[f*3+1], c = wi[f*3+2]; if (a != b && b != c && a != c) { clean.Add(a); clean.Add(b); clean.Add(c); } }
                wi = clean.ToArray();
                int tris = wi.Length / 3;
                int target = tris < 400 ? tris : Math.Max(64, (int)(tris * ratio));
                uint[] si = target < tris ? Simplify(P, wi, Nn, target) : wi;
                // compact
                var used = new Dictionary<uint, uint>(); var order = new List<int>();
                var fi = new uint[si.Length];
                for (int i = 0; i < si.Length; i++) { uint ni; if (!used.TryGetValue(si[i], out ni)) { ni = (uint)order.Count; used[si[i]] = ni; order.Add((int)si[i]); } fi[i] = ni; }
                var newAttrs = new Dictionary<string, object>();
                foreach (var an in names) {
                    var acc = O(accessors[I(attrs[an])]); int es = esz[an];
                    var data = new byte[order.Count * es];
                    for (int i = 0; i < order.Count; i++) Buffer.BlockCopy(raw[an], uniq[order[i]] * es, data, i * es, es);
                    int view = AddView(data, 34962);
                    var na = new Dictionary<string, object>();
                    na["bufferView"] = view; na["componentType"] = acc["componentType"]; na["count"] = order.Count; na["type"] = acc["type"];
                    if (Get(acc, "normalized") != null) na["normalized"] = acc["normalized"];
                    if (an == "POSITION") {
                        var mn3 = new double[] { 1e30, 1e30, 1e30 }; var mx3 = new double[] { -1e30, -1e30, -1e30 };
                        for (int i = 0; i < order.Count; i++) for (int k = 0; k < 3; k++) { double x = BitConverter.ToSingle(data, i * 12 + k * 4); mn3[k] = Math.Min(mn3[k], x); mx3[k] = Math.Max(mx3[k], x); }
                        na["min"] = mn3.Cast<object>().ToList(); na["max"] = mx3.Cast<object>().ToList();
                    }
                    newAccessors.Add(na); newAttrs[an] = newAccessors.Count - 1;
                }
                bool u16 = order.Count < 65536;
                var ib = new byte[fi.Length * (u16 ? 2 : 4)];
                for (int i = 0; i < fi.Length; i++) { if (u16) BitConverter.GetBytes((ushort)fi[i]).CopyTo(ib, i * 2); else BitConverter.GetBytes(fi[i]).CopyTo(ib, i * 4); }
                int iv = AddView(ib, 34963);
                var ia = new Dictionary<string, object>(); ia["bufferView"] = iv; ia["componentType"] = u16 ? 5123 : 5125; ia["count"] = fi.Length; ia["type"] = "SCALAR";
                newAccessors.Add(ia);
                p["attributes"] = newAttrs; p["indices"] = newAccessors.Count - 1;
                newPrims.Add(p); outTris += fi.Length / 3;
                L(string.Format("  {0} [{1}]: {2} -> {3} tris, {4} -> {5} verts", Get(mesh, "name"), mn, tris, fi.Length / 3, vcount, order.Count));
            }
            mesh["primitives"] = newPrims;
        }
        // detach empty meshes from nodes
        var emptyMesh = new HashSet<int>();
        for (int i = 0; i < meshes.Count; i++) if (A(O(meshes[i])["primitives"]).Count == 0) emptyMesh.Add(i);
        foreach (var no in nodes) { var n = O(no); if (Get(n, "mesh") != null && emptyMesh.Contains(I(n["mesh"]))) n.Remove("mesh"); }
        // keep empty mesh entries valid: give them nothing referencing them; glTF requires >=1 primitive, so remap meshes
        var meshMap = new Dictionary<int, int>(); var newMeshes = new List<object>();
        for (int i = 0; i < meshes.Count; i++) if (!emptyMesh.Contains(i)) { meshMap[i] = newMeshes.Count; newMeshes.Add(meshes[i]); }
        foreach (var no in nodes) { var n = O(no); if (Get(n, "mesh") != null) n["mesh"] = meshMap[I(n["mesh"])]; }
        gl["meshes"] = newMeshes;

        // images
        var images = A(Get(gl, "images"));
        foreach (var imo in images) {
            var im = O(imo); if (Get(im, "bufferView") == null) continue;
            var bv = O(bufferViews[I(im["bufferView"])]);
            int off = Get(bv, "byteOffset") != null ? I(bv["byteOffset"]) : 0; int len = I(bv["byteLength"]);
            var data = new byte[len]; Buffer.BlockCopy(bin, off, data, 0, len);
            string mime; byte[] nd;
            try { nd = ShrinkImage(data, maxTex, out mime); } catch (Exception e) { L("  image kept (" + e.Message + ")"); nd = data; mime = null; }
            if (mime != null) im["mimeType"] = mime;
            im["bufferView"] = AddView(nd, null);
        }
        // materials: strip transmission
        foreach (var mo in materials) { var m = O(mo); var ext = O(Get(m, "extensions")); if (ext != null && ext.ContainsKey("KHR_materials_transmission")) { ext.Remove("KHR_materials_transmission"); if (ext.Count == 0) m.Remove("extensions"); } }
        foreach (var key in new[] { "extensionsUsed", "extensionsRequired" }) if (Get(gl, key) != null) { var l = A(gl[key]).Where(x => (string)x != "KHR_materials_transmission").ToList(); if (l.Count == 0) gl.Remove(key); else gl[key] = l; }

        while (outBin.Length % 4 != 0) outBin.WriteByte(0);
        gl["accessors"] = newAccessors; gl["bufferViews"] = newViews;
        var buf = new Dictionary<string, object>(); buf["byteLength"] = (int)outBin.Length; gl["buffers"] = new List<object> { buf };
        var asset = O(gl["asset"]); asset["generator"] = "DeepGarage GlbOptimizer (from " + Get(asset, "generator") + ")";

        var jb = Encoding.UTF8.GetBytes(ser.Serialize(gl));
        int jpad = (4 - jb.Length % 4) % 4;
        var binBytes = outBin.ToArray();
        using (var fs = File.Create(outPath)) using (var bw = new BinaryWriter(fs)) {
            bw.Write(0x46546C67u); bw.Write(2u); bw.Write((uint)(12 + 8 + jb.Length + jpad + 8 + binBytes.Length));
            bw.Write((uint)(jb.Length + jpad)); bw.Write(0x4E4F534Au); bw.Write(jb); for (int i = 0; i < jpad; i++) bw.Write((byte)0x20);
            bw.Write((uint)binBytes.Length); bw.Write(0x004E4942u); bw.Write(binBytes);
        }
        L(string.Format("RESULT tris={0} size={1:0.00} MB", outTris, new FileInfo(outPath).Length / 1048576.0));
        return string.Join("\n", log);
    }
}
