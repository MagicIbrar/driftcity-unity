using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace DriftCity.Game
{
    /// <summary>The ten bindable actions of OPTION 3000000 (ID_OPTION_CONTROLLER_KEY_*), in goption.ini [Controller] order.</summary>
    public enum BindAction { Accel, Brake, Booster, Drift, Enter, Esc, Space, Tab, Item1, Item2 }

    /// <summary>
    /// The driving keys. The client reads the pad setup from goption.ini [Controller] (0x4c9440): Use, Index, Type
    /// (stick: 0 none / 1 type 1 / 2 type 2) and ButtonAccel .. ButtonItem2, DirectInput button numbers 1..10 by
    /// default; the keyboard keys are fixed in the client (arrows, Ctrl booster, Shift drift). The remake keeps both
    /// tables: pad buttons in [Controller] exactly as the client names them, and keyboard keys in its own [Keyboard]
    /// section (remake extension: OPTION 3000000 rebinds the keyboard while "not use the pad" is ticked).
    /// DriftCar reads its throttle, steering, drift and booster here.
    /// </summary>
    public static class KeyBindings
    {
        public static readonly string[] IniNames = { "Accel", "Brake", "Booster", "Drift", "Enter", "ESC", "Space", "Tab", "Item1", "Item2" };

#if ENABLE_INPUT_SYSTEM
        /// <summary>Default keyboard keys: the client's (and the remake's) fixed layout.</summary>
        public static readonly Key[] DefaultKeys = { Key.UpArrow, Key.DownArrow, Key.LeftCtrl, Key.LeftShift, Key.Enter, Key.Escape, Key.Space, Key.Tab, Key.Digit1, Key.Digit2 };
        /// <summary>Second keys the remake always accepts while an action keeps its default key (WASD, right Ctrl/Shift, Space drift).</summary>
        static readonly Key[][] DefaultAlternates =
        {
            new[] { Key.W }, new[] { Key.S }, new[] { Key.RightCtrl }, new[] { Key.RightShift, Key.Space }, new[] { Key.NumpadEnter }, new Key[0], new Key[0], new Key[0], new Key[0], new Key[0],
        };

        static Key[] keys;

        public static Key KeyFor(BindAction a)
        {
            EnsureKeys();
            return keys[(int)a];
        }

        public static void SetKey(BindAction a, Key key)
        {
            EnsureKeys();
            keys[(int)a] = key;
            GameOptions.Set("Keyboard", "Key" + IniNames[(int)a], key.ToString());
        }

        static void EnsureKeys()
        {
            if (keys != null && loadedVersion == version) return;
            loadedVersion = version;
            keys = new Key[DefaultKeys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                var s = GameOptions.Get("Keyboard", "Key" + IniNames[i], null);
                keys[i] = s != null && Enum.TryParse(s, true, out Key k) && k != Key.None ? k : DefaultKeys[i];
            }
        }
#endif
        static int version, loadedVersion = -1;

        /// <summary>Forget the cached tables (after goption.ini was reloaded).</summary>
        public static void Reload() => version++;

        // ---------------- pad ([Controller]) ----------------
        public static bool PadUse { get => GameOptions.GetBool("Controller", "Use"); set => GameOptions.Set("Controller", "Use", value); }
        /// <summary>0 = no stick (d-pad steers), 1 = type 1 (left stick steers), 2 = type 2 (left stick steers and its Y axis drives) [inferred].</summary>
        public static int StickType { get => Mathf.Clamp(GameOptions.GetInt("Controller", "Type"), 0, 2); set => GameOptions.Set("Controller", "Type", Mathf.Clamp(value, 0, 2)); }

        /// <summary>Pad button number (1-based DirectInput order) for an action.</summary>
        public static int ButtonFor(BindAction a) => Mathf.Max(1, GameOptions.GetInt("Controller", "Button" + IniNames[(int)a], (int)a + 1));
        public static void SetButton(BindAction a, int button) => GameOptions.Set("Controller", "Button" + IniNames[(int)a], Mathf.Clamp(button, 1, PadButtonCount));

        /// <summary>DirectInput's numbering of an XInput pad: 1 A, 2 B, 3 X, 4 Y, 5 LB, 6 RB, 7 Back, 8 Start, 9 L-stick, 10 R-stick; 11/12 the triggers (remake).</summary>
        public const int PadButtonCount = 12;
        public static readonly string[] PadButtonNames = { "", "A", "B", "X", "Y", "LB", "RB", "Back", "Start", "LS", "RS", "LT", "RT" };

#if ENABLE_INPUT_SYSTEM
        static ButtonControl PadButton(Gamepad g, int n)
        {
            switch (n)
            {
                case 1: return g.buttonSouth;
                case 2: return g.buttonEast;
                case 3: return g.buttonWest;
                case 4: return g.buttonNorth;
                case 5: return g.leftShoulder;
                case 6: return g.rightShoulder;
                case 7: return g.selectButton;
                case 8: return g.startButton;
                case 9: return g.leftStickButton;
                case 10: return g.rightStickButton;
                case 11: return g.leftTrigger;
                case 12: return g.rightTrigger;
            }
            return null;
        }

        /// <summary>The pad button number pressed this frame (1..12), or 0.</summary>
        public static int PadButtonPressedThisFrame()
        {
            var g = Gamepad.current;
            if (g == null) return 0;
            for (int n = 1; n <= PadButtonCount; n++) { var b = PadButton(g, n); if (b != null && b.wasPressedThisFrame) return n; }
            return 0;
        }

        /// <summary>The keyboard key pressed this frame (rebinding), or Key.None.</summary>
        public static Key KeyPressedThisFrame()
        {
            var k = Keyboard.current;
            if (k == null) return Key.None;
            foreach (var c in k.allKeys) if (c != null && c.wasPressedThisFrame && c.keyCode != Key.None) return c.keyCode;
            return Key.None;
        }

        static bool KeyHeld(Keyboard k, BindAction a)
        {
            var key = KeyFor(a);
            if (key != Key.None && k[key].isPressed) return true;
            if (key == DefaultKeys[(int)a]) foreach (var alt in DefaultAlternates[(int)a]) if (k[alt].isPressed) return true;
            return false;
        }

        static bool KeyDown(Keyboard k, BindAction a)
        {
            var key = KeyFor(a);
            if (key != Key.None && k[key].wasPressedThisFrame) return true;
            if (key == DefaultKeys[(int)a]) foreach (var alt in DefaultAlternates[(int)a]) if (k[alt].wasPressedThisFrame) return true;
            return false;
        }

        static bool PadHeld(Gamepad g, BindAction a) { var b = PadButton(g, ButtonFor(a)); return b != null && b.isPressed; }
#endif

        /// <summary>Whether an action's key (or, with the pad in use, its button) is held.</summary>
        public static bool Held(BindAction a)
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null && KeyHeld(k, a)) return true;
            var g = Gamepad.current;
            return g != null && PadUse && PadHeld(g, a);
#else
            return false;
#endif
        }

        /// <summary>Whether an action's key (or pad button) went down this frame (Enter / Esc / Tab / items for the UI code).</summary>
        public static bool Pressed(BindAction a)
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null && KeyDown(k, a)) return true;
            var g = Gamepad.current;
            if (g == null || !PadUse) return false;
            var b = PadButton(g, ButtonFor(a));
            return b != null && b.wasPressedThisFrame;
#else
            return false;
#endif
        }

        /// <summary>
        /// The driving input DriftCar samples each physics step. Keyboard: the bound keys plus the left/right arrows (A/D)
        /// for steering. Pad, not in use (the remake's default): right/left trigger throttle and brake, left stick steers,
        /// A drifts, RB boosts. Pad in use: the [Controller] buttons, steering by d-pad (type 0) or left stick (1, 2),
        /// type 2 also drives with the stick's Y axis.
        /// </summary>
        public static void ReadDriving(out float throttle, out float steer, out bool drift, out bool boost)
        {
            throttle = steer = 0; drift = boost = false;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null)
            {
                if (KeyHeld(k, BindAction.Accel)) throttle += 1;
                if (KeyHeld(k, BindAction.Brake)) throttle -= 1;
                if (k.rightArrowKey.isPressed || k.dKey.isPressed) steer += 1;
                if (k.leftArrowKey.isPressed || k.aKey.isPressed) steer -= 1;
                drift = KeyHeld(k, BindAction.Drift);
                boost = KeyHeld(k, BindAction.Booster);
            }
            var g = Gamepad.current;
            if (g != null)
            {
                if (!PadUse)
                {
                    throttle += g.rightTrigger.ReadValue() - g.leftTrigger.ReadValue();
                    steer += g.leftStick.x.ReadValue();
                    drift |= g.buttonSouth.isPressed;
                    boost |= g.rightShoulder.isPressed;
                }
                else
                {
                    if (PadHeld(g, BindAction.Accel)) throttle += 1;
                    if (PadHeld(g, BindAction.Brake)) throttle -= 1;
                    int type = StickType;
                    steer += type == 0 ? g.dpad.x.ReadValue() : g.leftStick.x.ReadValue();
                    if (type == 2) throttle += g.leftStick.y.ReadValue();
                    drift |= PadHeld(g, BindAction.Drift);
                    boost |= PadHeld(g, BindAction.Booster);
                }
            }
#else
            throttle = Input.GetAxis("Vertical");
            steer = Input.GetAxis("Horizontal");
            drift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) || Input.GetKey(KeyCode.Space);
            boost = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
#endif
            throttle = Mathf.Clamp(throttle, -1, 1);
            steer = Mathf.Clamp(steer, -1, 1);
        }

        /// <summary>The text the KEY_ field shows: the key's name, or "Button n" for the pad.</summary>
        public static string Label(BindAction a, bool pad)
        {
            if (pad) { int b = ButtonFor(a); return b < PadButtonNames.Length ? $"{b} ({PadButtonNames[b]})" : b.ToString(); }
#if ENABLE_INPUT_SYSTEM
            return KeyName(KeyFor(a));
#else
            return "-";
#endif
        }

#if ENABLE_INPUT_SYSTEM
        public static string KeyName(Key k)
        {
            switch (k)
            {
                case Key.UpArrow: return "Up";
                case Key.DownArrow: return "Down";
                case Key.LeftArrow: return "Left";
                case Key.RightArrow: return "Right";
                case Key.LeftCtrl: return "Ctrl";
                case Key.RightCtrl: return "R-Ctrl";
                case Key.LeftShift: return "Shift";
                case Key.RightShift: return "R-Shift";
                case Key.Escape: return "Esc";
                case Key.Digit1: return "1";
                case Key.Digit2: return "2";
            }
            return k.ToString();
        }
#endif
    }
}
