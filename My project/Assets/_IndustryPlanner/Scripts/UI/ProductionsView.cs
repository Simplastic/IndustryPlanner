using System;
using System.Collections.Generic;
using System.Linq;
using IndustryPlanner.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI
{
    /// <summary>
    /// Production list. Home shows every game; a game shows only its own.
    /// Search by name or item, sort (recent, name, power), grid or compact list, and quick actions on each card:
    /// rename, duplicate, delete (with Undo). Keyboard: Enter opens the focused card, Delete removes it, F2 renames.
    /// </summary>
    public class ProductionsView
    {
        const int MaxPortRows = 3;
        const string EnteringClass = "production-card--entering";
        static readonly string[] SortNames = { "Recent", "Name", "Power" };

        public readonly VisualElement Root;
        readonly AppController m_App;
        readonly VisualTreeAsset m_CardTemplate = UiKit.Template("ProductionCard");
        readonly ScrollView m_List;
        readonly VisualElement m_Empty, m_SortGroup;
        readonly Label m_Title, m_Count, m_EmptyTitle;
        readonly TextField m_Search;
        readonly Button m_GridButton, m_ListButton;
        GameDatabase m_Game;
        bool m_Animate;

        AppSettings S => AppSettings.Current;

        public ProductionsView(AppController app)
        {
            m_App = app;
            Root = UiKit.View("ProductionsView");
            m_List = Root.Q<ScrollView>("productionList");
            m_Empty = Root.Q("emptyState");
            m_Title = Root.Q<Label>("pageTitle");
            m_Count = Root.Q<Label>("productionCount");
            m_EmptyTitle = Root.Q<Label>("emptyTitle");
            m_SortGroup = Root.Q("sortGroup");
            Root.Q<Button>("newProductionButton").clicked += () => m_App.CreateProduction(m_Game);
            Root.Q<Button>("emptyCreateButton").clicked += () => m_App.CreateProduction(m_Game);

            m_Search = Root.Q<TextField>("searchField");
            m_Search.textEdition.placeholder = "Search productions or items";
            m_Search.textEdition.hidePlaceholderOnFocus = true;
            m_Search.RegisterValueChangedCallback(_ => Refresh(animate: false));

            m_GridButton = Root.Q<Button>("gridViewButton");
            m_ListButton = Root.Q<Button>("listViewButton");
            m_GridButton.clicked += () => SetListView(false);
            m_ListButton.clicked += () => SetListView(true);
        }

        public void Show(GameDatabase game)
        {
            m_Game = game;
            m_Title.text = game == null ? "Latest designs" : $"{game.Name} productions";
            m_EmptyTitle.text = game == null ? "No productions yet" : $"No {game.Name} productions yet";
            m_Search.SetValueWithoutNotify("");
            BuildSort();
            SetListView(S.homeListView);
            Refresh(animate: true);
        }

        void BuildSort()
        {
            m_SortGroup.Clear();
            for (int i = 0; i < SortNames.Length; i++)
            {
                int sort = i;
                UiKit.Btn(SortNames[i], "btn segment t-semibold", () =>
                {
                    S.homeSort = sort;
                    S.Apply();
                    BuildSort();
                    Refresh(animate: false);
                }, m_SortGroup).EnableInClassList("segment--active", S.homeSort == i);
            }
        }

        void SetListView(bool list)
        {
            if (S.homeListView != list) { S.homeListView = list; S.Apply(); }
            m_List.EnableInClassList("card-grid--list", list);
            m_GridButton.EnableInClassList("btn--toggled", !list);
            m_ListButton.EnableInClassList("btn--toggled", list);
        }

        void Refresh(bool animate = false)
        {
            m_Animate = animate;
            VisualElement container = m_List.contentContainer;
            container.Clear();

            string query = m_Search.value?.Trim() ?? "";
            var summaries = ProductionStore.LoadAll()
                .Where(g => m_Game == null || g.gameId == m_Game.Id)
                .Select(ProductionStore.Summarize)
                .Where(s => Matches(s, query));
            summaries = S.homeSort switch
            {
                1 => summaries.OrderBy(s => s.Graph.name, StringComparer.OrdinalIgnoreCase),
                2 => summaries.OrderByDescending(s => s.PowerMW),
                _ => summaries.OrderByDescending(s => s.Graph.lastEditedTicks),
            };

            int shown = 0, total = ProductionStore.LoadAll().Count(g => m_Game == null || g.gameId == m_Game.Id);
            foreach (ProductionSummary s in summaries)
            {
                VisualElement card = CreateCard(s, container);
                if (m_Animate)
                {
                    card.AddToClassList(EnteringClass); // One short staggered entrance when the list opens.
                    card.schedule.Execute(() => card.RemoveFromClassList(EnteringClass)).StartingIn(30 + Mathf.Min(shown, 12) * 35);
                }
                shown++;
            }

            m_Count.text = query.Length > 0 ? $"{shown} of {total}" : $"{total} saved";
            m_Count.EnableInClassList("is-hidden", total == 0);
            m_Empty.EnableInClassList("is-hidden", total > 0);
            m_List.EnableInClassList("is-hidden", total == 0);
            if (total > 0 && shown == 0) UiKit.Text($"Nothing matches \"{query}\".", "card-grid__none", container);
        }

        /// <summary>Matches the production name or any item it takes in or delivers.</summary>
        static bool Matches(ProductionSummary s, string query) =>
            query.Length == 0
            || s.Graph.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || s.Inputs.Concat(s.Outputs).Any(x => x.item.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

        VisualElement CreateCard(ProductionSummary s, VisualElement container)
        {
            m_CardTemplate.CloneTree(container, out int index, out _);
            VisualElement card = container[index];
            ProductionGraph graph = s.Graph;
            GameDatabase db = GameDatabase.Get(graph.gameId);

            card.Q<Label>("cardName").text = graph.name;
            string level = db is { HasProgression: true } && graph.tier < 99 ? ", " + db.LevelName(graph.tier) : "";
            card.Q<Label>("cardGame").text = (db?.Name ?? graph.gameId) + level;
            card.Q<Label>("nodeCount").text = graph.nodes.Count == 1 ? "1 node" : $"{graph.nodes.Count} nodes";
            card.Q<Label>("power").text = UiKit.Power(s.PowerMW);
            card.Q<Label>("edited").text = UiKit.Edited(graph.LastEdited);
            FillPorts(card.Q("inputs"), s.Inputs, false);
            FillPorts(card.Q("outputs"), s.Outputs, true);

            VisualElement actions = card.Q(className: "production-card__actions");
            card.Q<Button>("renameButton").clicked += () => Rename(graph);
            card.Q<Button>("duplicateButton").clicked += () => { ProductionStore.Duplicate(graph); Refresh(); };
            card.Q<Button>("deleteButton").clicked += () => Delete(graph);

            card.RegisterCallback<ClickEvent>(e => { if (!UiKit.IsInside(e.target as VisualElement, actions)) m_App.OpenEditor(graph); });
            card.RegisterCallback<NavigationSubmitEvent>(e => { if (e.target == card) m_App.OpenEditor(graph); });
            card.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.target != card) return;
                if (e.keyCode == KeyCode.Delete) Delete(graph);
                else if (e.keyCode == KeyCode.F2) Rename(graph);
            });
            return card;
        }

        void Rename(ProductionGraph graph) =>
            Dialog.Prompt(Root, "Rename production", "Name", graph.name, "Rename", name =>
            {
                graph.name = name;
                ProductionStore.Save(graph, touch: false);
                Refresh();
            });

        /// <summary>Deletes right away and offers Undo, which writes the production back unchanged.</summary>
        void Delete(ProductionGraph graph)
        {
            ProductionStore.Delete(graph.id);
            Refresh();
            m_App.ShowToast($"Deleted \"{graph.name}\".", "Undo", () =>
            {
                ProductionStore.Save(graph, touch: false);
                Refresh();
            });
        }

        static void FillPorts(VisualElement column, IReadOnlyList<(string item, float perMin)> rates, bool isOutput)
        {
            int shown = Mathf.Min(rates.Count, MaxPortRows);
            for (int i = 0; i < shown; i++)
            {
                VisualElement row = UiKit.El("port-row " + (isOutput ? "port-row--out" : "port-row--in"), column);
                UiKit.El("port-dot", row);
                UiKit.Text(rates[i].item, "port-row__item", row);
                UiKit.Text(UiKit.Rate(rates[i].perMin), "port-row__rate t-semibold", row);
            }
            if (rates.Count > shown) UiKit.Text($"+{rates.Count - shown} more", "port-more", column);
            if (rates.Count == 0) UiKit.Text("None yet", "port-more", column);
        }
    }
}
