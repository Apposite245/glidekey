using System;
using System.Collections.Generic;

namespace GlideKey
{
    // Sticky modifiers, designed for a single pointer (you can't hold Ctrl and click C with one mouse):
    //   tap            -> one-shot: applies to the next key, then turns off
    //   tap again      -> locked: physically held down until tapped again (Alt+Tab+Tab works,
    //                     mouse clicks become Ctrl/Shift-clicks, like Windows' own OSK)
    //   long press     -> locked
    //   Win: tap again while one-shot sends a bare Win press (opens Start) instead of locking
    //   chord          -> on touch screens, a key pressed while a modifier is still held down is
    //                     modified; releasing the modifier afterwards does not latch it
    class ModifierState
    {
        public const int LongPressMs = 450;

        public enum St { Off, OneShot, Locked }

        class Slot
        {
            public St state;
            public int vk;          // side that was used (LShift vs RShift ...)
            public bool physDown;   // we injected a keydown that isn't released yet
            public int holders;     // pointers currently pressing this modifier's key
            public bool usedInChord;
        }

        readonly Dictionary<ModKind, Slot> slots = new()
        {
            [ModKind.Shift] = new Slot { vk = VK.LShift },
            [ModKind.Ctrl] = new Slot { vk = VK.LCtrl },
            [ModKind.Alt] = new Slot { vk = VK.LAlt },
            [ModKind.Win] = new Slot { vk = VK.LWin },
        };

        public St State(ModKind k) => slots[k].state;
        public bool Held(ModKind k) => slots[k].holders > 0;
        public bool Active(ModKind k) => slots[k].state != St.Off || slots[k].holders > 0;
        public bool AnyActive { get { foreach (var s in slots.Values) if (s.state != St.Off || s.holders > 0) return true; return false; } }
        public bool ShiftActive => Active(ModKind.Shift);
        // Shortcut-type modifiers: swipe typing is suppressed while one is active (letters go as shortcuts).
        public bool ShortcutActive => Active(ModKind.Ctrl) || Active(ModKind.Alt) || Active(ModKind.Win);

        public void Press(KeyDef k)
        {
            var s = slots[k.mod];
            if (s.holders == 0) s.usedInChord = false;
            s.holders++;
            if (s.state == St.Off) s.vk = k.vk;
        }

        public void Release(KeyDef k, int heldMs, List<KeyEv> evs)
        {
            var s = slots[k.mod];
            s.holders = Math.Max(0, s.holders - 1);
            if (s.holders > 0) return;
            if (s.usedInChord) { s.usedInChord = false; return; }

            if (heldMs >= LongPressMs)
            {
                if (s.state == St.Locked) Unlock(s, evs); else Lock(s, k.vk, evs);
                return;
            }
            switch (s.state)
            {
                case St.Off: s.state = St.OneShot; s.vk = k.vk; break;
                case St.OneShot:
                    if (k.mod == ModKind.Win)
                    {
                        s.state = St.Off;
                        Injector.AppendTap(evs, s.vk);
                    }
                    else Lock(s, s.vk, evs);
                    break;
                case St.Locked: Unlock(s, evs); break;
            }
        }

        static void Lock(Slot s, int vk, List<KeyEv> evs)
        {
            s.state = St.Locked;
            s.vk = vk;
            if (!s.physDown) { evs.Add(KeyEv.Down(vk)); s.physDown = true; }
        }

        static void Unlock(Slot s, List<KeyEv> evs)
        {
            s.state = St.Off;
            if (s.physDown) { evs.Add(KeyEv.Up(s.vk)); s.physDown = false; }
        }

        // Before a key goes down: press every active modifier that isn't physically down yet.
        // Returns the modifiers pressed, to be released by AfterKey.
        public List<int> BeforeKey(List<KeyEv> evs)
        {
            var pressed = new List<int>();
            foreach (var kv in slots)
            {
                var s = kv.Value;
                if (s.holders > 0) s.usedInChord = true;
                if ((s.state == St.OneShot || s.holders > 0) && !s.physDown)
                {
                    evs.Add(KeyEv.Down(s.vk));
                    pressed.Add(s.vk);
                }
            }
            return pressed;
        }

        // After the key went up: release what BeforeKey pressed and consume one-shots.
        public void AfterKey(List<KeyEv> evs, List<int> pressed)
        {
            for (int i = pressed.Count - 1; i >= 0; i--) evs.Add(KeyEv.Up(pressed[i]));
            ConsumeOneShots();
        }

        public void ConsumeOneShots()
        {
            foreach (var s in slots.Values) if (s.state == St.OneShot) s.state = St.Off;
        }

        // Typing literal text (a swiped word): temporarily lift locked modifiers so e.g. a locked
        // Ctrl doesn't turn letters into shortcuts. Returns what to press again afterwards.
        public List<int> LiftLocked(List<KeyEv> evs)
        {
            var lifted = new List<int>();
            foreach (var s in slots.Values)
                if (s.physDown) { evs.Add(KeyEv.Up(s.vk)); lifted.Add(s.vk); }
            return lifted;
        }

        public static void Restore(List<KeyEv> evs, List<int> lifted)
        {
            foreach (var vk in lifted) evs.Add(KeyEv.Down(vk));
        }

        public void ReleaseAll(List<KeyEv> evs)
        {
            foreach (var s in slots.Values)
            {
                if (s.physDown) { evs.Add(KeyEv.Up(s.vk)); s.physDown = false; }
                s.state = St.Off;
                s.holders = 0;
                s.usedInChord = false;
            }
        }
    }
}
