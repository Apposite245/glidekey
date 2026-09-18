using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;

namespace GlideKey
{
    // assets/words.txt: one entry per line, most frequent first (line index = frequency rank).
    // An entry may contain an apostrophe or capital I ("don't", "I'm"); matching uses only the
    // lowercase letters with repeated letters collapsed ("hello" -> "helo"), since a swipe can't
    // express a double letter.
    class WordDict
    {
        public string[] Words = Array.Empty<string>();
        public string[] Keys = Array.Empty<string>();
        // (first key letter, last key letter) -> entry indices, in rank order
        readonly Dictionary<int, int[]> byEnds = new();
        public int Count => Words.Length;

        public static WordDict FromResource()
        {
            var d = new WordDict();
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GlideKey.assets.words.txt");
                if (s != null) using (var r = new StreamReader(s)) d.Load(r.ReadToEnd());
            }
            catch (Exception ex) { Debug.Log("dictionary load failed: " + ex.Message); }
            return d;
        }

        public void Load(string text)
        {
            var words = new List<string>();
            var keys = new List<string>();
            var ends = new Dictionary<int, List<int>>();
            foreach (var line in text.Split('\n'))
            {
                var w = line.Trim();
                string k = KeyString(w);
                if (k.Length < 2) continue;
                int idx = words.Count;
                words.Add(w); keys.Add(k);
                int e = EndKey(k[0], k[^1]);
                if (!ends.TryGetValue(e, out var l)) ends[e] = l = new List<int>();
                l.Add(idx);
            }
            Words = words.ToArray(); Keys = keys.ToArray();
            byEnds.Clear();
            foreach (var kv in ends) byEnds[kv.Key] = kv.Value.ToArray();
        }

        public static string KeyString(string w)
        {
            var sb = new StringBuilder(w.Length);
            foreach (char ch in w)
            {
                char c = char.ToLowerInvariant(ch);
                if (c < 'a' || c > 'z') continue;
                if (sb.Length > 0 && sb[^1] == c) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        static int EndKey(char a, char b) => (a - 'a') * 26 + (b - 'a');

        public int[] WithEnds(char first, char last)
            => byEnds.TryGetValue(EndKey(first, last), out var a) ? a : Array.Empty<int>();
    }

    class SwipeResult
    {
        public List<string> Words = new();  // best first
        public double BestAvg = double.MaxValue; // mean per-point cost of the best word (fit quality)
        public string Raw = "";               // letters passed over, for when nothing fits
    }


    // Elastic alignment of the trace against each candidate word's ideal path (the polyline through
    // its letters' key centres). The trace is resampled at even arc-length steps; each point is
    // assigned, in order, either to a letter (pays squared distance to the key centre) or to the
    // straight run between two consecutive letters (pays squared distance to that segment, plus a
    // penalty for moving against the run's direction). Every letter must claim at least one point,
    // so a word must actually be visited letter by letter, while points in transit cost nothing
    // when they're on the way. The direction term stops a back-and-forth wiggle ("there": e->r->e)
    // being absorbed by a run that happens to lie along it ("the": h->e).
    //
    //   score(w) = Σ cost(point)  [endpoints weighted ×ENDPOINT_W]  +  CHAR_PRIOR·|w|  +  FREQ·ln(rank+1)
    class SwipeEngine
    {
        public const double CHAR_PRIOR = 0.35;
        public const double FREQ = 0.55;
        public const double ENDPOINT_W = 2.0;
        public const double DIR_W = 1.2;          // cost of a point moving straight against its run
        public const double RAW_THRESHOLD = 1.6;  // mean per-point cost above this = no word fits
        const float STEP = 0.22f;                 // resample spacing, in key pitches
        const int MAX_POINTS = 110;
        const float END_RADIUS = 1.25f;           // first/last letter must be this close to trace ends (pitches)
        const int MAX_ENDS = 5;
        const float LETTER_REACH = 1.05f;         // every inner letter must pass this close to the trace (pitches)

        readonly Dictionary<char, PointF> centers;
        readonly float[] kx = new float[26], ky = new float[26];
        readonly bool[] hasKey = new bool[26];
        readonly float pitch, inv2s2;
        readonly float[] idealLen; // per dictionary entry, for this layout
        public readonly WordDict Dict;

        public readonly List<PointF> Trace = new();

        public SwipeEngine(Dictionary<char, PointF> centers, WordDict dict)
        {
            this.centers = centers;
            foreach (var kv in centers) { int i = kv.Key - 'a'; kx[i] = kv.Value.X; ky[i] = kv.Value.Y; hasKey[i] = true; }
            Dict = dict ?? new WordDict();
            pitch = centers.TryGetValue('f', out var f) && centers.TryGetValue('g', out var g) ? Math.Abs(g.X - f.X) : 40f;
            float sigma = 0.5f * pitch;
            inv2s2 = 1f / (2f * sigma * sigma);

            idealLen = new float[Dict.Count];
            for (int w = 0; w < Dict.Count; w++)
            {
                string k = Dict.Keys[w];
                float len = 0;
                for (int j = 1; j < k.Length; j++)
                {
                    int a = k[j - 1] - 'a', b = k[j] - 'a';
                    float dx = kx[b] - kx[a], dy = ky[b] - ky[a];
                    len += MathF.Sqrt(dx * dx + dy * dy);
                }
                idealLen[w] = len;
            }
        }

        public float Pitch => pitch;

        public void Start(PointF p) { Trace.Clear(); Trace.Add(p); }

        public void Add(PointF p)
        {
            var last = Trace[^1];
            float dx = p.X - last.X, dy = p.Y - last.Y;
            if (dx * dx + dy * dy >= 2.25f) Trace.Add(p);
        }

        public float TraceLength()
        {
            float len = 0;
            for (int i = 1; i < Trace.Count; i++) len += Dist(Trace[i - 1], Trace[i]);
            return len;
        }

        static double Sq(float v) => (double)v * v;
        static float Dist(PointF a, PointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return MathF.Sqrt(dx * dx + dy * dy); }

        List<PointF> Resample()
        {
            float step = STEP * pitch;
            float total = TraceLength();
            if (total / step > MAX_POINTS - 1) step = total / (MAX_POINTS - 1);
            var outp = new List<PointF> { Trace[0] };
            float carry = 0;
            for (int i = 1; i < Trace.Count; i++)
            {
                PointF a = Trace[i - 1], b = Trace[i];
                float seg = Dist(a, b);
                float pos = step - carry;
                while (pos <= seg)
                {
                    float t = pos / seg;
                    outp.Add(new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                    pos += step;
                }
                carry = seg - (pos - step);
            }
            if (Dist(outp[^1], Trace[^1]) > 0.01f) outp.Add(Trace[^1]);
            return outp;
        }

        List<char> NearLetters(PointF p)
        {
            var list = new List<(float d, char c)>();
            foreach (var kv in centers)
            {
                float d = Dist(kv.Value, p);
                if (d <= END_RADIUS * pitch) list.Add((d, kv.Key));
            }
            list.Sort((a, b) => a.d.CompareTo(b.d));
            var r = new List<char>();
            for (int i = 0; i < list.Count && i < MAX_ENDS; i++) r.Add(list[i].c);
            if (r.Count == 0) r.Add(Nearest(p));
            return r;
        }

        public char Nearest(PointF p)
        {
            char best = 'a'; float bd = float.MaxValue;
            foreach (var kv in centers)
            {
                float d = Dist(kv.Value, p);
                if (d < bd) { bd = d; best = kv.Key; }
            }
            return best;
        }

        sealed class Scratch
        {
            public double[] prev = new double[64], cur = new double[64];
            public float[] px, py, ux, uy; // resampled points + unit direction of travel
            public int n;
        }

        public SwipeResult Recognize(int top = 5)
        {
            var res = new SwipeResult { Raw = Raw() };
            if (Trace.Count < 2 || Dict.Count == 0) return res;
            var pts = Resample();
            int n = pts.Count;
            if (n < 2) return res;
            float traceLen = TraceLength();

            var sc = new Scratch { n = n, px = new float[n], py = new float[n], ux = new float[n], uy = new float[n] };
            for (int i = 0; i < n; i++) { sc.px[i] = pts[i].X; sc.py[i] = pts[i].Y; }
            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
                float dx = sc.px[b] - sc.px[a], dy = sc.py[b] - sc.py[a];
                float l = MathF.Sqrt(dx * dx + dy * dy);
                if (l > 1e-3f) { sc.ux[i] = dx / l; sc.uy[i] = dy / l; }
            }

            // letters the trace passes near; a word with an inner letter the trace never approached is skipped
            // closest approach of the trace to each letter: a word's alignment costs at least the
            // sum of these over its letters (each letter must claim a point), so most candidates
            // are rejected in O(L) without running the DP
            var minD = new double[26];
            var near = new bool[26];
            float nearR2 = LETTER_REACH * pitch * LETTER_REACH * pitch;
            for (int c = 0; c < 26; c++)
            {
                float m = float.MaxValue;
                if (hasKey[c])
                    for (int i = 0; i < n; i++)
                    {
                        float dx = sc.px[i] - kx[c], dy = sc.py[i] - ky[c];
                        float d2 = dx * dx + dy * dy;
                        if (d2 < m) m = d2;
                    }
                near[c] = m <= nearR2;
                minD[c] = m * inv2s2;
            }
            PointF p0 = pts[0], pn = pts[^1];

            var best = new List<(double score, double cost, int idx)>();
            double worstKept = double.MaxValue;
            float maxLen = traceLen * 1.5f + pitch, minLen = traceLen * 0.55f - 1.2f * pitch;

            foreach (char f in NearLetters(pts[0]))
                foreach (char l in NearLetters(pts[^1]))
                    foreach (int idx in Dict.WithEnds(f, l))
                    {
                        double freq = FREQ * Math.Log(idx + 1);
                        if (freq + 2 * CHAR_PRIOR >= worstKept) break; // bucket is in rank order: nothing later can win
                        float il = idealLen[idx];
                        if (il > maxLen || il < minLen) continue;
                        string k = Dict.Keys[idx];
                        bool ok = true;
                        for (int j = 1; j < k.Length - 1; j++) if (!near[k[j] - 'a']) { ok = false; break; }
                        if (!ok) continue;

                        double prior = freq + CHAR_PRIOR * k.Length;
                        if (prior >= worstKept) continue;
                        int a0 = k[0] - 'a', aL = k[^1] - 'a';
                        double lb = prior + ENDPOINT_W * inv2s2 * (Sq(p0.X - kx[a0]) + Sq(p0.Y - ky[a0]) + Sq(pn.X - kx[aL]) + Sq(pn.Y - ky[aL]));
                        for (int j = 1; j < k.Length - 1; j++) lb += minD[k[j] - 'a'];
                        if (lb >= worstKept) continue;
                        double cost = Align(k, sc, worstKept - prior);
                        if (double.IsInfinity(cost)) continue;
                        double score = cost + prior;
                        if (best.Count < top || score < worstKept)
                        {
                            best.Add((score, cost, idx));
                            best.Sort((a, b) => a.score.CompareTo(b.score));
                            if (best.Count > top) best.RemoveAt(best.Count - 1);
                            worstKept = best.Count >= top ? best[^1].score : double.MaxValue;
                        }
                    }

            foreach (var b in best) res.Words.Add(Dict.Words[b.idx]);
            if (best.Count > 0) res.BestAvg = best[0].cost / n;
            return res;
        }

        // DP over states 0..2L-2: even s = letter s/2, odd s = run from letter (s-1)/2 to (s+1)/2.
        // The first point is on letter 0 and the last point on the last letter. Letters are
        // mandatory, runs may be empty. Returns +inf once every partial path exceeds `bound`.
        double Align(string k, Scratch sc, double bound)
        {
            int L = k.Length, S = 2 * L - 1, n = sc.n;
            if (n < L) return double.PositiveInfinity;
            if (sc.prev.Length < S) { sc.prev = new double[S * 2]; sc.cur = new double[S * 2]; }
            var prev = sc.prev; var cur = sc.cur;

            Span<float> cx = stackalloc float[L], cy = stackalloc float[L];
            Span<float> sx = stackalloc float[L], sy = stackalloc float[L], slen2 = stackalloc float[L];
            for (int j = 0; j < L; j++) { int c = k[j] - 'a'; cx[j] = kx[c]; cy[j] = ky[c]; }
            for (int j = 0; j < L - 1; j++)
            {
                sx[j] = cx[j + 1] - cx[j]; sy[j] = cy[j + 1] - cy[j];
                slen2[j] = sx[j] * sx[j] + sy[j] * sy[j];
            }

            const double INF = double.PositiveInfinity;
            for (int s = 0; s < S; s++) prev[s] = INF;
            {
                float dx = sc.px[0] - cx[0], dy = sc.py[0] - cy[0];
                prev[0] = ENDPOINT_W * (dx * dx + dy * dy) * inv2s2;
            }

            for (int i = 1; i < n; i++)
            {
                float x = sc.px[i], y = sc.py[i];
                double rowMin = INF;
                int pointsLeft = n - 1 - i;
                for (int s = 0; s < S; s++)
                {
                    // the letters after state s each need a point of their own
                    if (pointsLeft < (S - 1 - s) / 2) { cur[s] = INF; continue; }
                    double from = prev[s];
                    if (s >= 1 && prev[s - 1] < from) from = prev[s - 1];
                    if ((s & 1) == 0 && s >= 2 && prev[s - 2] < from) from = prev[s - 2]; // empty run
                    if (from == INF) { cur[s] = INF; continue; }

                    double c;
                    int j = s >> 1;
                    if ((s & 1) == 0)
                    {
                        float dx = x - cx[j], dy = y - cy[j];
                        c = (dx * dx + dy * dy) * inv2s2;
                    }
                    else
                    {
                        float len2 = slen2[j];
                        float t = len2 > 0 ? ((x - cx[j]) * sx[j] + (y - cy[j]) * sy[j]) / len2 : 0;
                        t = t < 0 ? 0 : t > 1 ? 1 : t;
                        float dx = x - (cx[j] + t * sx[j]), dy = y - (cy[j] + t * sy[j]);
                        c = (dx * dx + dy * dy) * inv2s2;
                        if (len2 > 0)
                        {
                            float cos = (sc.ux[i] * sx[j] + sc.uy[i] * sy[j]) / MathF.Sqrt(len2);
                            if (cos < 0.5f) c += DIR_W * (0.5f - cos) / 1.5f; // 0 when roughly along the run, DIR_W when reversed
                        }
                    }
                    if (i == n - 1) c *= ENDPOINT_W;
                    cur[s] = from + c;
                    if (cur[s] < rowMin) rowMin = cur[s];
                }
                if (rowMin > bound) { sc.prev = prev; sc.cur = cur; return INF; }
                var tmp = prev; prev = cur; cur = tmp;
            }
            sc.prev = prev; sc.cur = cur;
            return prev[S - 1];
        }

        // Nearest-key letters along the trace with hysteresis — typed when no word fits.
        public string Raw()
        {
            if (Trace.Count == 0) return "";
            var sb = new StringBuilder();
            char cur = Nearest(Trace[0]);
            sb.Append(cur);
            float hys = 0.2f * pitch;
            foreach (var p in Trace)
            {
                char nn = Nearest(p);
                if (nn == cur) continue;
                if (Dist(p, centers[nn]) + hys < Dist(p, centers[cur]) && Dist(p, centers[nn]) < 0.45f * pitch)
                {
                    cur = nn;
                    sb.Append(nn);
                }
            }
            return sb.ToString();
        }
    }
}
