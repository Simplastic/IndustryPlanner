using System;
using System.Linq;
using IndustryPlanner.Core;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI
{
    /// <summary>Editor preferences and per-game key bindings. Click a binding, then press the new key.</summary>
    public class SettingsView
    {
        public readonly VisualElement Root;
        readonly AppController m_App;
        readonly VisualElement m_Body;
        GameDatabase m_Game;
        string m_Listening;      // Action waiting for a key press, or null.
        Button m_ListeningButton;

        AppSettings S => AppSettings.Current;

        public SettingsView(AppController app)
        {
            m_App = app;
            Root = UiKit.View("SettingsView");
            m_Body = Root.Q<ScrollView>("settingsBody").contentContainer;
        }

        public void Show(GameDatabase game)
        {
            m_Game ??= game;
            m_Listening = null;
            Build();
        }

        void Build()
        {
            m_Body.Clear();
            VisualElement column = UiKit.El("settings__column", m_Body);

            VisualElement editor = Section(column, "Editor");
            Switch(editor, "Snap nodes to the grid", "Nodes land on the canvas dots. Toggle in the editor with G.",
                S.snapToGrid, v => S.snapToGrid = v);
            VisualElement style = Row(editor, "Link style", "How belts and pipes are drawn between nodes.");
            Segmented(style, Enum.GetNames(typeof(LinkStyle)).Select(n => n == "RightAngles" ? "Right angles" : n).ToArray(),
                (int)S.linkStyle, i => S.linkStyle = (LinkStyle)i);
            Switch(editor, "Animate flow arrows", "Arrows move along links towards the receiving node.",
                S.animateLinks, v => S.animateLinks = v);
            int[] speeds = { 200, 250, 350, 500 };
            Segmented(Row(editor, "Double-click speed", "How quickly two clicks must follow each other to count as a double click."),
                new[] { "Fast", "Normal", "Relaxed", "Slow" }, Array.IndexOf(speeds, S.doubleClickMs), i => S.doubleClickMs = speeds[i]);
            Switch(editor, "Reduce motion", "Turns off transitions and entrance animations.",
                S.reduceMotion, v => S.reduceMotion = v);

            VisualElement keys = Section(column, "Key bindings");
            var games = GameDatabase.LoadAll().Values.ToList();
            m_Game ??= games.FirstOrDefault();
            if (m_Game == null) return;
            if (games.Count > 1)
                Segmented(Row(keys, "Game", "Each game has its own bindings."), games.Select(g => g.Name).ToArray(),
                    games.IndexOf(m_Game), i => { m_Game = games[i]; });

            Keymap map = S.KeymapFor(m_Game);
            foreach (var group in map.Actions.GroupBy(a => a.Group))
            {
                UiKit.Text(group.Key, "settings__group t-semibold", keys);
                foreach (Keymap.ActionDef action in group)
                {
                    VisualElement row = UiKit.El("settings-row settings-row--compact", keys);
                    UiKit.Text(action.Label, "settings-row__label", row);
                    Button key = UiKit.Btn(map.Label(action.Id), "btn btn--secondary key-button t-semibold", null, row);
                    string id = action.Id;
                    key.clicked += () => StartListening(id, key);
                }
            }
            VisualElement actions = UiKit.El("settings__actions", keys);
            UiKit.Btn("Reset to defaults", "btn btn--secondary t-semibold", () =>
            {
                S.ResetBindings(m_Game.Id);
                S.Apply();
                Build();
                m_App.ShowToast($"{m_Game.Name} key bindings reset.");
            }, actions);
        }

        void StartListening(string action, Button button)
        {
            if (m_ListeningButton != null) m_ListeningButton.RemoveFromClassList("key-button--listening");
            m_Listening = action;
            m_ListeningButton = button;
            button.text = "Press a key";
            button.AddToClassList("key-button--listening");
        }

        /// <summary>Called every frame while settings are visible; captures the next key for rebinding.</summary>
        public void Tick()
        {
            Keyboard kb = Keyboard.current;
            if (m_Listening == null || kb == null) return;
            foreach (KeyControl k in kb.allKeys)
            {
                Keymap map = S.KeymapFor(m_Game);
                bool hold = map.IsHold(m_Listening);
                // Hold actions take a single key, modifiers included; other actions take a key plus optional Ctrl.
                if (k == null || !k.wasPressedThisFrame || (!hold && IsModifier(k.keyCode))) continue;
                if (k.keyCode == Key.Escape) { m_Listening = null; Build(); return; }

                bool ctrl = !hold && (kb.ctrlKey.isPressed || kb.leftMetaKey.isPressed || kb.rightMetaKey.isPressed);
                // A key can only do one thing per game: the previous owner is unbound.
                string clash = map.ActionUsing(k.keyCode, ctrl, m_Listening);
                if (clash != null)
                {
                    S.SetBinding(m_Game.Id, new KeyBinding { action = clash, key = Key.None.ToString() });
                    m_App.ShowToast($"{Keymap.Format((k.keyCode, ctrl))} was removed from \"{map.Actions.First(a => a.Id == clash).Label}\".");
                }
                S.SetBinding(m_Game.Id, new KeyBinding { action = m_Listening, key = k.keyCode.ToString(), ctrl = ctrl });
                S.Apply();
                m_Listening = null;
                Build();
                return;
            }
        }

        static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LeftMeta or Key.RightMeta;

        // ---------------------------------------------------------------- builders

        static VisualElement Section(VisualElement parent, string title)
        {
            VisualElement s = UiKit.El("settings__section", parent);
            UiKit.Text(title, "settings__title t-bold", s);
            return s;
        }

        static VisualElement Row(VisualElement parent, string label, string hint)
        {
            VisualElement row = UiKit.El("settings-row", parent);
            VisualElement texts = UiKit.El("settings-row__texts", row);
            UiKit.Text(label, "settings-row__label t-semibold", texts);
            if (hint != null) UiKit.Text(hint, "settings-row__hint", texts);
            return row;
        }

        void Switch(VisualElement parent, string label, string hint, bool value, Action<bool> set)
        {
            VisualElement row = Row(parent, label, hint);
            Button sw = UiKit.Btn("", value ? "switch switch--on" : "switch", null, row);
            UiKit.El("switch__knob", sw);
            sw.clicked += () =>
            {
                bool on = !sw.ClassListContains("switch--on");
                sw.EnableInClassList("switch--on", on);
                set(on);
                S.Apply();
            };
        }

        void Segmented(VisualElement row, string[] options, int selected, Action<int> set)
        {
            VisualElement seg = UiKit.El("segmented segmented--inline", row);
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                UiKit.Btn(options[i], "btn segment t-semibold", () => { set(index); S.Apply(); Build(); }, seg)
                    .EnableInClassList("segment--active", i == selected);
            }
        }
    }
}
