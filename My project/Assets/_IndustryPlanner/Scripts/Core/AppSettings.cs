using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IndustryPlanner.Core
{
    public enum LinkStyle { Curved, RightAngles }

    [Serializable] public class KeyBinding { public string action; public string key; public bool ctrl; }

    [Serializable] public class GameKeymap { public string gameId; public List<KeyBinding> bindings = new(); }

    /// <summary>User preferences, saved to persistentDataPath/settings.json. Key bindings are stored per game.</summary>
    [Serializable]
    public class AppSettings
    {
        public bool snapToGrid = true;
        public int gridSize = 24;            // Matches the dot spacing so nodes line up with the dots.
        public LinkStyle linkStyle = LinkStyle.Curved;
        public bool animateLinks = true;
        public bool reduceMotion;
        public int doubleClickMs = 250;      // Max gap between the two clicks of a double click on the canvas.
        public int homeSort;                 // Productions list: 0 recent, 1 name, 2 power.
        public bool homeListView;            // Productions list: compact rows instead of cards.
        public List<GameKeymap> keymaps = new(); // Only bindings the user changed are stored.

        static AppSettings s_Current;
        static string FilePath => Path.Combine(Application.persistentDataPath, "settings.json");

        /// <summary>Raised after any change so open views can apply it.</summary>
        public static event Action Changed;

        public static AppSettings Current
        {
            get
            {
                if (s_Current != null) return s_Current;
                try { s_Current = File.Exists(FilePath) ? JsonUtility.FromJson<AppSettings>(File.ReadAllText(FilePath)) : null; }
                catch (Exception e) { Debug.LogWarning("Settings were unreadable and have been reset: " + e.Message); }
                return s_Current ??= new AppSettings();
            }
        }

        /// <summary>Saves and notifies listeners. Call after changing any field.</summary>
        public void Apply()
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
            Changed?.Invoke();
        }

        public Keymap KeymapFor(GameDatabase db) => new(db, keymaps.Find(k => k.gameId == db.Id));

        /// <summary>Stores a binding override for one game.</summary>
        public void SetBinding(string gameId, KeyBinding binding)
        {
            GameKeymap map = keymaps.Find(k => k.gameId == gameId);
            if (map == null) keymaps.Add(map = new GameKeymap { gameId = gameId });
            map.bindings.RemoveAll(b => b.action == binding.action);
            map.bindings.Add(binding);
        }

        public void ResetBindings(string gameId) => keymaps.RemoveAll(k => k.gameId == gameId);
    }

    /// <summary>
    /// The resolved key bindings for one game: editor defaults, the game's hotbar keys, then user overrides.
    /// Hotbar actions are named "hotbar.0", "hotbar.1", ...
    /// </summary>
    public class Keymap
    {
        public readonly struct ActionDef
        {
            public readonly string Id, Label, Group;
            public readonly bool Hold; // Held while dragging (can be a modifier key) rather than pressed once.
            public ActionDef(string id, string label, string group, bool hold) { Id = id; Label = label; Group = group; Hold = hold; }
        }

        // Editor-wide actions and their default keys.
        static readonly (string id, string label, Key key, bool ctrl, bool hold)[] EditorDefaults =
        {
            ("editor.cancel", "Cancel or deselect", Key.Escape, false, false),
            ("editor.delete", "Delete selection or link", Key.Delete, false, false),
            ("editor.duplicate", "Duplicate selection", Key.D, true, false),
            ("editor.copy", "Copy selection", Key.C, true, false),
            ("editor.paste", "Paste at the cursor", Key.V, true, false),
            ("editor.save", "Save", Key.S, true, false),
            ("editor.frame", "Frame all or selection", Key.F, false, false),
            ("editor.panel", "Show or hide side panel", Key.Tab, false, false),
            ("editor.analysis", "Open analysis", Key.A, false, false),
            ("editor.blueprints", "Open blueprints", Key.B, false, false),
            ("editor.saveBlueprint", "Save selection as blueprint", Key.B, true, false),
            ("editor.snap", "Toggle snap to grid", Key.G, false, false),
            ("editor.align", "Hold to align with other nodes", Key.LeftCtrl, false, true),
        };

        public readonly List<ActionDef> Actions = new();
        readonly Dictionary<string, (Key key, bool ctrl)> m_Bindings = new();
        readonly Dictionary<string, (Key key, bool ctrl)> m_Defaults = new();

        public Keymap(GameDatabase db, GameKeymap overrides)
        {
            for (int i = 0; i < db.Def.hotbar.Length; i++)
            {
                HotbarSlotDef slot = db.Def.hotbar[i];
                Enum.TryParse(slot.key, out Key key);
                Define($"hotbar.{i}", slot.label, "Building hotbar", key, false, false);
            }
            foreach (var d in EditorDefaults) Define(d.id, d.label, "Editor", d.key, d.ctrl, d.hold);

            if (overrides == null) return;
            foreach (KeyBinding b in overrides.bindings)
                if (m_Bindings.ContainsKey(b.action))
                    m_Bindings[b.action] = (Enum.TryParse(b.key, out Key k) ? k : Key.None, b.ctrl);
        }

        void Define(string id, string label, string group, Key key, bool ctrl, bool hold)
        {
            Actions.Add(new ActionDef(id, label, group, hold));
            m_Bindings[id] = m_Defaults[id] = (key, ctrl);
        }

        public (Key key, bool ctrl) Get(string action) => m_Bindings.TryGetValue(action, out var b) ? b : (Key.None, false);

        /// <summary>True on the frame the action's key goes down with the right Ctrl state.</summary>
        public bool Pressed(Keyboard kb, string action)
        {
            var (key, ctrl) = Get(action);
            if (key == Key.None || !kb[key].wasPressedThisFrame) return false;
            return ctrl == (kb.ctrlKey.isPressed || kb.leftMetaKey.isPressed || kb.rightMetaKey.isPressed);
        }

        /// <summary>True while a hold action's key is down (Ctrl state ignored, so Ctrl itself can be the key).</summary>
        public bool Held(Keyboard kb, string action)
        {
            var (key, _) = Get(action);
            return kb != null && key != Key.None && kb[key].isPressed;
        }

        public bool IsHold(string action) => Actions.Exists(a => a.Id == action && a.Hold);

        /// <summary>The other action already using this key, if any. Hold actions don't clash with key presses.</summary>
        public string ActionUsing(Key key, bool ctrl, string except)
        {
            bool hold = IsHold(except);
            foreach (var pair in m_Bindings)
                if (pair.Key != except && IsHold(pair.Key) == hold && pair.Value.key == key && pair.Value.ctrl == ctrl) return pair.Key;
            return null;
        }

        public string Label(string action) => Format(Get(action));

        public static string Format((Key key, bool ctrl) b)
        {
            if (b.key == Key.None) return "Not set";
            string name = b.key switch
            {
                >= Key.Digit1 and <= Key.Digit0 => b.key.ToString().Substring(5),
                Key.Escape => "Esc",
                Key.Delete => "Del",
                Key.Backspace => "Backspace",
                Key.LeftCtrl => "Left Ctrl",
                Key.RightCtrl => "Right Ctrl",
                Key.LeftShift => "Left Shift",
                Key.LeftAlt => "Left Alt",
                _ => b.key.ToString()
            };
            return b.ctrl ? "Ctrl+" + name : name;
        }
    }
}
