using System;
using System.Collections.Generic;
using System.Linq;
using IndustryPlanner.Core;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI.GraphEditor
{
    /// <summary>
    /// Shapez 2 style building bar. Each slot is a category bound to a key (per game, see Keymap).
    /// Press a slot's key to pick it, press it again to cycle its buildings, Esc or right click puts it away.
    /// GraphEditorView reads the keys and calls Activate.
    /// </summary>
    public class Hotbar : VisualElement
    {
        public event Action<BuildingDef> ToolChanged;
        public event Action<string> LockedPicked; // Message for a slot whose buildings are all locked.
        public BuildingDef ActiveTool { get; private set; }
        public int SlotCount => m_Slots.Count;

        readonly List<(HotbarSlotDef def, Button button)> m_Slots = new();
        readonly VisualElement m_Variants, m_SlotRow;
        int m_VariantsSlot = -1; // Slot whose buildings the variant strip currently shows.
        readonly Dictionary<int, int> m_VariantBySlot = new(); // Remembers the last building used in each slot.
        GameDatabase m_Db;
        Keymap m_Keys;
        Func<BuildingDef, bool> m_Unlocked;
        int m_Active = -1;

        public Hotbar()
        {
            AddToClassList("hotbar");
            pickingMode = PickingMode.Ignore;
            m_Variants = UiKit.El("hotbar__variants", this);
            m_Variants.pickingMode = PickingMode.Ignore;
            m_SlotRow = UiKit.El("hotbar__slots", this);
        }

        public void Bind(GameDatabase db, Keymap keys, Func<BuildingDef, bool> unlocked)
        {
            m_Db = db;
            m_Keys = keys;
            m_Unlocked = unlocked;
            m_Slots.Clear();
            m_SlotRow.Clear();
            m_VariantBySlot.Clear();
            for (int i = 0; i < db.Def.hotbar.Length; i++)
            {
                HotbarSlotDef def = db.Def.hotbar[i];
                int index = i;
                var button = new Button(() => Activate(index));
                button.AddToClassList("hotbar-slot");
                UiKit.Text(keys.Label($"hotbar.{i}"), "hotbar-slot__key t-semibold", button);
                BuildingDef first = db.Building(def.buildings[0]);
                button.Add(UiKit.Icon(db.Name, true, first?.id ?? def.label, def.label, "l"));
                UiKit.Text(def.label, "hotbar-slot__label t-semibold", button);
                button.EnableInClassList("hotbar-slot--locked", !def.buildings.Any(id => IsUnlocked(id)));
                m_SlotRow.Add(button);
                m_Slots.Add((def, button));
            }
            Deselect();
        }

        /// <summary>Picks a slot; picking the active slot again cycles to its next building.</summary>
        public void Activate(int slot)
        {
            string[] ids = m_Slots[slot].def.buildings;
            if (!ids.Any(id => IsUnlocked(id)))
            {
                int tier = ids.Min(id => m_Db.Building(id)?.tier ?? 0);
                LockedPicked?.Invoke($"{m_Slots[slot].def.label} unlocks at {m_Db.LevelName(tier)}.");
                return;
            }
            int variant = m_VariantBySlot.GetValueOrDefault(slot);
            bool sameSlot = slot == m_Active;
            if (sameSlot || !IsUnlocked(ids[variant]))
                do variant = (variant + 1) % ids.Length; while (!IsUnlocked(ids[variant])); // Cycle through unlocked ones.
            m_Active = slot;
            m_VariantBySlot[slot] = variant;
            if (!sameSlot) Pulse(m_Slots[slot].button); // Only animate when the category changes, not when cycling.
            SetTool(m_Db.Building(m_Slots[slot].def.buildings[variant]));
        }

        public void Deselect()
        {
            m_Active = -1;
            m_VariantsSlot = -1;
            SetTool(null);
        }

        void SetTool(BuildingDef tool)
        {
            ActiveTool = tool;
            for (int i = 0; i < m_Slots.Count; i++) m_Slots[i].button.EnableInClassList("hotbar-slot--active", i == m_Active);

            // Variant strip above the bar shows every building in the active slot. It is rebuilt (and animates in)
            // only when the slot changes; cycling within a slot just moves the highlight.
            if (m_Active != m_VariantsSlot)
            {
                m_VariantsSlot = m_Active;
                m_Variants.Clear();
                if (m_Active >= 0 && m_Slots[m_Active].def.buildings.Length > 1)
                {
                    string[] ids = m_Slots[m_Active].def.buildings;
                    for (int v = 0; v < ids.Length; v++)
                    {
                        int slot = m_Active, variant = v;
                        Button chip = UiKit.Btn(m_Db.Building(ids[v])?.name ?? ids[v], "variant t-semibold", () =>
                        {
                            m_VariantBySlot[slot] = variant;
                            SetTool(m_Db.Building(ids[variant]));
                        }, m_Variants);
                        chip.userData = ids[v];
                        if (!IsUnlocked(ids[v]))
                        {
                            chip.text += $"  ({m_Db.LevelName(m_Db.Building(ids[v])?.tier ?? 0)})";
                            chip.SetEnabled(false);
                        }
                        chip.AddToClassList("variant--enter");
                        chip.schedule.Execute(() => chip.RemoveFromClassList("variant--enter")).StartingIn(16 + v * 30);
                    }
                }
            }
            foreach (VisualElement chip in m_Variants.Children())
                chip.EnableInClassList("variant--active", (string)chip.userData == tool?.id);

            ToolChanged?.Invoke(tool);
        }

        bool IsUnlocked(string id) => m_Db.Building(id) is { } b && (m_Unlocked?.Invoke(b) ?? true);

        /// <summary>Short scale pop on the slot that was just picked.</summary>
        static void Pulse(VisualElement e)
        {
            e.AddToClassList("hotbar-slot--pulse");
            e.schedule.Execute(() => e.RemoveFromClassList("hotbar-slot--pulse")).StartingIn(120);
        }
    }
}
