using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace GlideKey
{
    // Everything a key press means, independent of the window: taps, typematic repeat, sticky
    // modifiers, swipe recognition and commit, the suggestion strip, smart spacing. The form feeds
    // it pointer events + a clock; tests drive it directly with a SelfTest injector.
    class Controller
    {
        public const int RepeatDelayMs = 480, RepeatEveryMs = 33, LetterHoldMs = 520, DoubleSpaceMs = 450, LiveEveryMs = 55;
        public const float SwipeStartPitch = 0.38f; // movement (in key pitches) that turns a letter press into a swipe

        public readonly Injector Inj;
        public readonly ModifierState Mods = new();
        public SwipeEngine Engine;

        // settings
        public bool AutoSpace = true, DoubleSpacePeriod = true, AutoCapitalize = true, SmartBackspace = true;

        // suggestion strip
        public List<string> Strip = new();
        public bool StripIsLive;      // showing live guesses during a swipe
        public string PendingWord;    // last swiped word, replaceable by tapping a suggestion
        bool pendingSpace;            // ... and it was followed by an automatic space

        // what we last typed, for auto-capitalisation / double-space; null = unknown
        StringBuilder tail = new();
        long lastSpaceAt = -100000;
        IntPtr lastForeground;

        public event Action Changed;
        void Notify() => Changed?.Invoke();

        class Press
        {
            public KeyDef key;
            public PointF start;
            public long t0;
            public bool swiping, repeating, keyDown, swallowed;
            public long nextRepeat;
            public List<int> wrap;
            public int vk; public bool ext;
            public long lastLive;
        }
        readonly Dictionary<int, Press> presses = new();

        public Controller(Injector inj) { Inj = inj; }

        public bool IsPressed(KeyDef k) { foreach (var p in presses.Values) if (p.key == k) return true; return false; }
        public bool Swiping { get { foreach (var p in presses.Values) if (p.swiping) return true; return false; } }

        // ---------------------------------------------------------------- pointer events
        public void Down(int id, KeyDef k, PointF pt, long now)
        {
            if (k == null) return;
            CheckForeground();
            if (presses.ContainsKey(id)) Up(id, pt, now);
            var p = new Press { key = k, start = pt, t0 = now };
            presses[id] = p;
            var evs = new List<KeyEv>();
            Debug.Log($"down {k.id} @{pt.X:0},{pt.Y:0}");

            switch (k.cls)
            {
                case KeyCls.Mod:
                    Mods.Press(k);
                    break;
                case KeyCls.Toggle:
                    Injector.AppendTap(evs, k.vk);
                    ForgetTail();
                    break;
                case KeyCls.Letter:
                    break; // decided on move / hold / release
                case KeyCls.Space:
                    if (DoubleSpacePeriod && now - lastSpaceAt < DoubleSpaceMs && EndsWithWordSpace())
                    {
                        Injector.AppendTap(evs, VK.Back);
                        TypeText(evs, ". ");
                        lastSpaceAt = -100000;
                        p.swallowed = true;
                        ClearPending();
                        break;
                    }
                    lastSpaceAt = now;
                    if (pendingSpace)
                    {
                        // the swipe already typed its space; a quick second tap still makes ". "
                        p.swallowed = true;
                        ClearPending();
                        break;
                    }
                    KeyDown(p, evs, now);
                    break;
                default:
                    if (k.vk == VK.Back && SmartBackspace && PendingWord != null)
                    {
                        // backspace right after a swipe removes the whole word (phone convention)
                        Injector.AppendTap(evs, VK.Back, PendingWord.Length + (pendingSpace ? 1 : 0));
                        TrimTail(PendingWord.Length + (pendingSpace ? 1 : 0));
                        ClearPending();
                        // still held: keep deleting character by character, like a normal backspace
                        p.vk = VK.Back; p.ext = false; p.wrap = new List<int>();
                        p.keyDown = true;
                        p.nextRepeat = now + RepeatDelayMs;
                        break;
                    }
                    if (k.cls == KeyCls.Symbol && pendingSpace && IsClosingPunct(SymbolChar(k)))
                    {
                        // "word ." -> "word. "  (the auto-space moves after the punctuation)
                        Injector.AppendTap(evs, VK.Back);
                        TrimTail(1);
                        var wrap = Mods.BeforeKey(evs);
                        Injector.AppendTap(evs, k.vk);
                        tail?.Append(SymbolChar(k));
                        Mods.AfterKey(evs, wrap);
                        TypeText(evs, " ");
                        p.swallowed = true;
                        ClearPending();
                        break;
                    }
                    KeyDown(p, evs, now);
                    break;
            }
            Inj.Inject(evs);
            Notify();
        }

        public void Move(int id, PointF pt, long now)
        {
            if (!presses.TryGetValue(id, out var p)) return;
            if (p.key.cls != KeyCls.Letter || p.repeating || Engine == null) return;
            if (!p.swiping)
            {
                float dx = pt.X - p.start.X, dy = pt.Y - p.start.Y;
                if (MathF.Sqrt(dx * dx + dy * dy) < SwipeStartPitch * Engine.Pitch) return;
                if (Mods.ShortcutActive) return; // ctrl/alt/win + letter is a shortcut, not a word
                p.swiping = true;
                Engine.Start(p.start);
                ClearPending();
            }
            Engine.Add(pt);
            if (now >= p.lastLive)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var r = Engine.Recognize(3);
                long ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                p.lastLive = now + Math.Max(LiveEveryMs, 3 * ms); // back off if recognition is slow
                Strip = r.Words;
                StripIsLive = true;
            }
            Notify();
        }

        public void Up(int id, PointF pt, long now)
        {
            if (!presses.TryGetValue(id, out var p)) return;
            presses.Remove(id);
            var evs = new List<KeyEv>();
            var k = p.key;
            Debug.Log($"up {k.id} @{pt.X:0},{pt.Y:0} swipe={p.swiping}");

            switch (k.cls)
            {
                case KeyCls.Mod:
                    Mods.Release(k, (int)(now - p.t0), evs);
                    break;
                case KeyCls.Letter:
                    if (p.swiping) { Engine.Add(pt); FinishSwipe(evs); }
                    else if (p.repeating) KeyUp(p, evs);
                    else { KeyDown(p, evs, now); KeyUp(p, evs); }
                    break;
                case KeyCls.Toggle:
                    break;
                default:
                    if (!p.swallowed) KeyUp(p, evs);
                    break;
            }
            Inj.Inject(evs);
            Notify();
        }

        // Typematic repeat; call every ~15 ms from a UI timer.
        public void Tick(long now)
        {
            List<KeyEv> evs = null;
            foreach (var p in presses.Values)
            {
                if (p.key.cls == KeyCls.Letter && !p.swiping && !p.repeating && now - p.t0 >= LetterHoldMs)
                {
                    evs ??= new List<KeyEv>();
                    KeyDown(p, evs, now);
                    p.repeating = true;
                    p.nextRepeat = now + 250;
                }
                else if (p.keyDown && p.key.repeat && !p.swallowed && now >= p.nextRepeat)
                {
                    evs ??= new List<KeyEv>();
                    evs.Add(new KeyEv { vk = p.vk, ext = p.ext });
                    RecordTyped(p.key);
                    p.nextRepeat = now + RepeatEveryMs;
                }
            }
            if (evs != null) { Inj.Inject(evs); Notify(); }
        }

        // Release everything we hold (hide / exit / crash) so no key stays stuck down.
        public void ReleaseAll()
        {
            var evs = new List<KeyEv>();
            foreach (var p in presses.Values) if (p.keyDown) KeyUp(p, evs);
            presses.Clear();
            Mods.ReleaseAll(evs);
            Inj.Inject(evs);
            Notify();
        }

        // ---------------------------------------------------------------- keys
        void KeyDown(Press p, List<KeyEv> evs, long now)
        {
            var k = p.key;
            p.vk = k.vk; p.ext = k.ext;
            if (k.altVk != 0 && !Native.ToggleOn(VK.NumLock) && !Inj.SelfTest) { p.vk = k.altVk; p.ext = false; }
            ClearPending();
            p.wrap = Mods.BeforeKey(evs);
            evs.Add(new KeyEv { vk = p.vk, ext = p.ext });
            p.keyDown = true;
            p.nextRepeat = now + RepeatDelayMs;
            RecordTyped(k);
        }

        void KeyUp(Press p, List<KeyEv> evs)
        {
            if (!p.keyDown) return;
            evs.Add(new KeyEv { vk = p.vk, ext = p.ext, up = true });
            p.keyDown = false;
            Mods.AfterKey(evs, p.wrap ?? new List<int>());
        }

        // ---------------------------------------------------------------- swipe
        void FinishSwipe(List<KeyEv> evs)
        {
            var r = Engine.Recognize(5);
            string word = r.Words.Count > 0 && r.BestAvg < SwipeEngine.RAW_THRESHOLD ? r.Words[0] : r.Raw;
            Debug.Log($"swipe -> [{string.Join(",", r.Words)}] avg={r.BestAvg:0.00} raw={r.Raw}");
            if (string.IsNullOrEmpty(word)) { Strip = new(); StripIsLive = false; return; }

            Strip = r.Words.Count > 0 ? r.Words : new List<string> { word };
            if (!Strip.Contains(word)) Strip.Insert(0, word);
            StripIsLive = false;
            CommitWord(evs, word, replacing: false);
        }

        // Tap on a suggestion: swap the word we just typed for this one.
        public void ChooseSuggestion(int i)
        {
            if (i < 0 || i >= Strip.Count || StripIsLive) return;
            var evs = new List<KeyEv>();
            CommitWord(evs, Strip[i], replacing: true);
            Inj.Inject(evs);
            Notify();
        }

        void CommitWord(List<KeyEv> evs, string word, bool replacing)
        {
            if (replacing && PendingWord != null)
            {
                int n = PendingWord.Length + (pendingSpace ? 1 : 0);
                Injector.AppendTap(evs, VK.Back, n);
                TrimTail(n);
            }
            string shown = Cased(word, replacing);
            var lifted = Mods.LiftLocked(evs);
            string text = shown + (AutoSpace ? " " : "");
            TypeText(evs, text);
            ModifierState.Restore(evs, lifted);
            if (!replacing) Mods.ConsumeOneShots();
            PendingWord = shown;
            pendingSpace = AutoSpace;
        }

        string capsCase; // casing applied to the pending word, reused when it's replaced
        string Cased(string w, bool replacing)
        {
            if (!replacing)
            {
                capsCase = Mods.State(ModKind.Shift) == ModifierState.St.Locked ? "upper"
                    : Mods.ShiftActive || (AutoCapitalize && SentenceStart()) ? "first" : "none";
            }
            return capsCase switch
            {
                "upper" => w.ToUpperInvariant(),
                "first" => char.ToUpperInvariant(w[0]) + w.Substring(1),
                _ => w
            };
        }

        void ClearPending() { PendingWord = null; pendingSpace = false; if (!StripIsLive) Strip = new(); }

        // ---------------------------------------------------------------- typed-text tracking
        void TypeText(List<KeyEv> evs, string s)
        {
            Inj.AppendText(evs, s);
            if (tail != null) { tail.Append(s); Clip(); }
        }

        void RecordTyped(KeyDef k)
        {
            if (tail == null) { if (k.cls == KeyCls.Letter || k.cls == KeyCls.Symbol || k.cls == KeyCls.Space) tail = new(); else return; }
            if (Mods.ShortcutActive) { ForgetTail(); return; }
            switch (k.cls)
            {
                case KeyCls.Letter: tail.Append(k.Letter); break;
                case KeyCls.Symbol: tail.Append(SymbolChar(k)); break;
                case KeyCls.Space: tail.Append(' '); break;
                default:
                    if (k.vk == VK.Enter) tail.Append('\n');
                    else if (k.vk == VK.Back) TrimTail(1);
                    else ForgetTail();
                    break;
            }
            Clip();
        }

        void Clip() { if (tail != null && tail.Length > 16) tail.Remove(0, tail.Length - 16); }
        void TrimTail(int n) { if (tail != null) tail.Length = Math.Max(0, tail.Length - n); }
        void ForgetTail() { tail = null; }

        void CheckForeground()
        {
            if (Inj.SelfTest) return;
            var fg = Native.Foreground;
            if (fg != lastForeground) { lastForeground = fg; ForgetTail(); ClearPending(); }
        }

        bool SentenceStart()
        {
            if (tail == null) return false;
            string t = tail.ToString();
            if (t.Length == 0) return false;
            if (t.EndsWith('\n')) return true;
            string trimmed = t.TrimEnd(' ');
            return trimmed.Length < t.Length && trimmed.Length > 0 && ".!?".IndexOf(trimmed[^1]) >= 0;
        }

        bool EndsWithWordSpace()
        {
            if (tail == null || tail.Length < 2) return false;
            return tail[^1] == ' ' && char.IsLetterOrDigit(tail[^2]);
        }

        char SymbolChar(KeyDef k) => (Mods.ShiftActive ? k.shiftLabel : k.label)[0];
        static bool IsClosingPunct(char c) => ".,?!;:".IndexOf(c) >= 0;

        // ---- test hooks
        public string Tail => tail?.ToString();
    }
}
