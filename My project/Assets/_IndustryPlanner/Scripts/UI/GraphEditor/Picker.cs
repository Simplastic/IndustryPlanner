using System;
using System.Collections.Generic;
using System.Linq;
using IndustryPlanner.Core;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI.GraphEditor
{
    /// <summary>One choice in the picker: what to build and what it runs.</summary>
    public class PickEntry
    {
        public string Building, Selection, Title, Subtitle, IconId;
        public bool IconIsBuilding, Alternate;
    }

    /// <summary>Searchable modal list used for recipes, resources, fuels and items. Enter picks, Esc closes.</summary>
    public class Picker : VisualElement
    {
        readonly Label m_Title;
        readonly TextField m_Search;
        readonly ListView m_List;
        List<PickEntry> m_All = new(), m_Shown = new();
        Action<PickEntry> m_OnPick;
        string m_GameFolder;

        public bool IsOpen => !ClassListContains("is-hidden");

        public Picker()
        {
            AddToClassList("picker-layer");
            AddToClassList("is-hidden");
            RegisterCallback<PointerDownEvent>(e => { if (e.target == this) Close(); });

            VisualElement box = UiKit.El("picker", this);
            m_Title = UiKit.Text("", "picker__title t-bold", box);
            m_Search = new TextField { textEdition = { placeholder = "Search", hidePlaceholderOnFocus = true } };
            m_Search.AddToClassList("picker__search");
            m_Search.RegisterValueChangedCallback(e => Filter(e.newValue));
            m_Search.RegisterCallback<KeyDownEvent>(OnSearchKey, TrickleDown.TrickleDown);
            box.Add(m_Search);

            m_List = new ListView
            {
                fixedItemHeight = 50,
                selectionType = SelectionType.Single,
                makeItem = MakeRow,
                bindItem = (e, i) => BindRow(e, m_Shown[i]),
            };
            m_List.AddToClassList("picker__list");
            box.Add(m_List);
        }

        public void Open(string title, string gameFolder, IEnumerable<PickEntry> entries, Action<PickEntry> onPick)
        {
            m_Title.text = title;
            m_GameFolder = gameFolder;
            m_All = entries.ToList();
            m_OnPick = onPick;
            RemoveFromClassList("is-hidden");
            AddToClassList("picker-layer--enter"); // Fade the backdrop and lift the box in.
            schedule.Execute(() => RemoveFromClassList("picker-layer--enter")).StartingIn(16);
            m_Search.SetValueWithoutNotify("");
            Filter("");
            m_Search.schedule.Execute(() => m_Search.Focus());
        }

        public void Close()
        {
            AddToClassList("is-hidden");
            m_OnPick = null;
            m_Search.Blur();
        }

        void Choose(PickEntry entry)
        {
            if (entry == null) return;
            Action<PickEntry> pick = m_OnPick;
            Close();
            pick?.Invoke(entry);
        }

        void Filter(string query)
        {
            query = query?.Trim() ?? "";
            m_Shown = query.Length == 0 ? m_All : m_All.Where(e =>
                e.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (e.Subtitle?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0).ToList();
            m_List.itemsSource = m_Shown;
            m_List.Rebuild();
            m_List.SetSelectionWithoutNotify(m_Shown.Count > 0 ? new[] { 0 } : Array.Empty<int>());
        }

        void OnSearchKey(KeyDownEvent e)
        {
            int index = m_List.selectedIndex;
            switch (e.keyCode)
            {
                case UnityEngine.KeyCode.Return or UnityEngine.KeyCode.KeypadEnter:
                    Choose(index >= 0 && index < m_Shown.Count ? m_Shown[index] : null);
                    break;
                case UnityEngine.KeyCode.DownArrow:
                    Move(index + 1);
                    break;
                case UnityEngine.KeyCode.UpArrow:
                    Move(index - 1);
                    break;
                case UnityEngine.KeyCode.Escape:
                    Close();
                    break;
                default:
                    return;
            }
            e.StopPropagation();
        }

        void Move(int index)
        {
            if (m_Shown.Count == 0) return;
            index = Math.Clamp(index, 0, m_Shown.Count - 1);
            m_List.SetSelectionWithoutNotify(new[] { index });
            m_List.ScrollToItem(index);
        }

        VisualElement MakeRow()
        {
            VisualElement row = UiKit.El("picker-row");
            row.RegisterCallback<ClickEvent>(_ => Choose(row.userData as PickEntry)); // Single click picks.
            UiKit.El("picker-row__icon", row);
            VisualElement texts = UiKit.El("picker-row__texts", row);
            UiKit.Text("", "picker-row__title t-semibold", texts);
            UiKit.Text("", "picker-row__subtitle", texts);
            UiKit.Text("Alternate", "picker-row__tag t-semibold", row);
            return row;
        }

        void BindRow(VisualElement row, PickEntry e)
        {
            row.userData = e;
            VisualElement icon = row.Q(className: "picker-row__icon");
            icon.Clear();
            icon.Add(UiKit.Icon(m_GameFolder, e.IconIsBuilding, e.IconId, e.Title, "m"));
            row.Q<Label>(className: "picker-row__title").text = e.Title;
            row.Q<Label>(className: "picker-row__subtitle").text = e.Subtitle;
            row.Q<Label>(className: "picker-row__tag").EnableInClassList("is-hidden", !e.Alternate);
        }

        // ---------------------------------------------------------------- entry builders

        /// <summary>What can be chosen for a building: its recipes, mined resources, fuels, or any item.</summary>
        /// <remarks>Only what the production's progression level unlocks is offered.</remarks>
        public static IEnumerable<PickEntry> ForBuilding(GameDatabase db, BuildingDef b, ProductionGraph g)
        {
            switch (b.Kind)
            {
                case BuildingKind.Producer:
                    foreach (RecipeDef r in db.RecipesFor(b.id))
                        if (db.IsUnlocked(r, g)) yield return Recipe(db, r);
                    break;
                case BuildingKind.Extractor:
                    foreach (string item in b.resources)
                        if (db.IsUnlocked(db.Item(item), g)) yield return ItemEntry(db, b.id, item, $"{UiKit.Rate(b.extractRate)} on a normal node");
                    break;
                case BuildingKind.Generator:
                    foreach (FuelDef f in b.fuels)
                        if (db.IsUnlocked(db.Item(f.item), g)) yield return ItemEntry(db, b.id, f.item, $"{UiKit.Rate(f.perMin)} for {UiKit.Power(-b.power)}");
                    break;
                case BuildingKind.Production:
                    // Other saved productions of the same game, shown with what they deliver.
                    foreach (ProductionGraph p in ProductionStore.LoadAll())
                    {
                        if (p.gameId != g.gameId || p.id == g.id) continue;
                        GraphAnalysis a = ProductionStore.AnalysisFor(p.id);
                        string top = a?.FinalOutputs.OrderByDescending(x => x.Value).FirstOrDefault().Key;
                        yield return new PickEntry
                        {
                            Building = b.id, Selection = p.id, Title = p.name, IconId = top ?? b.id, IconIsBuilding = top == null,
                            Subtitle = a == null ? "" : $"Makes {Rates(db, a.FinalOutputs)}" +
                                (a.ExternalInputs.Count > 0 ? $"; needs {Rates(db, a.ExternalInputs)}" : ""),
                        };
                    }
                    break;
                default:
                    foreach (ItemDef i in db.Def.items)
                        if (db.IsUnlocked(i, g)) yield return ItemEntry(db, b.id, i.id, i.fluid ? "Fluid" : "");
                    break;
            }
        }

        /// <summary>
        /// Everything that can be placed, for double-clicking the canvas: every unlocked recipe (the subtitle
        /// names its building), then extractors, generators and logistics, titled "Building: item".
        /// </summary>
        public static IEnumerable<PickEntry> All(GameDatabase db, ProductionGraph g)
        {
            var rest = new List<PickEntry>();
            foreach (BuildingDef b in db.Def.buildings)
            {
                if (!db.IsUnlocked(b, g)) continue;
                foreach (PickEntry e in ForBuilding(db, b, g))
                {
                    if (b.Kind == BuildingKind.Producer) { yield return e; continue; }
                    if (b.Kind is BuildingKind.Extractor or BuildingKind.Generator or BuildingKind.Production)
                        e.Title = $"{b.name}: {e.Title}";
                    else continue; // Input/output/splitter for every item would swamp the list; use the hotbar.
                    rest.Add(e);
                }
            }
            foreach (PickEntry e in rest) yield return e;
        }

        static string Rates(GameDatabase db, Dictionary<string, float> rates) =>
            string.Join(", ", rates.OrderByDescending(x => x.Value).Take(2).Select(x => $"{UiKit.Rate(x.Value)} {db.ItemName(x.Key)}"));

        /// <summary>Buildings that can take (or supply) an item, for a link dropped on empty space.</summary>
        public static IEnumerable<PickEntry> ForItem(GameDatabase db, string item, bool needConsumer, ProductionGraph g)
        {
            foreach (RecipeDef r in db.RecipesWith(item, asOutput: !needConsumer))
                if (db.IsUnlocked(r, g)) yield return Recipe(db, r);
            foreach (BuildingDef b in db.Def.buildings)
            {
                if (!db.IsUnlocked(b, g)) continue;
                bool match = needConsumer
                    ? b.Kind == BuildingKind.Output || GameDatabase.IsPassThrough(b.Kind) || (b.Kind == BuildingKind.Generator && GameDatabase.Fuel(b, item) != null)
                    : b.Kind == BuildingKind.Input || GameDatabase.IsPassThrough(b.Kind) || (b.Kind == BuildingKind.Extractor && b.resources.Contains(item));
                if (match) yield return ItemEntry(db, b.id, item, b.name, title: $"{b.name}: {db.ItemName(item)}");
            }
        }

        static PickEntry Recipe(GameDatabase db, RecipeDef r) => new()
        {
            Building = r.building, Selection = r.id, Title = r.name, Alternate = r.alternate,
            IconId = r.outputs[0].item,
            Subtitle = $"{List(db, r.inputs)}  to  {List(db, r.outputs)}  in {db.Building(r.building)?.name}",
        };

        static PickEntry ItemEntry(GameDatabase db, string building, string item, string subtitle, string title = null) => new()
        {
            Building = building, Selection = item, Title = title ?? db.ItemName(item), Subtitle = subtitle, IconId = item,
        };

        static string List(GameDatabase db, ItemRateDef[] rates) =>
            string.Join(", ", rates.Select(x => $"{UiKit.Num(x.perMin)} {db.ItemName(x.item)}"));
    }
}
