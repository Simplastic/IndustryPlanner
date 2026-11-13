using System.Linq;
using IndustryPlanner.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI
{
    /// <summary>One card per game found in Resources/Games.</summary>
    public class GamesView
    {
        public readonly VisualElement Root;
        readonly AppController m_App;
        readonly VisualTreeAsset m_CardTemplate = UiKit.Template("GameCard");
        readonly ScrollView m_Grid;

        public GamesView(AppController app)
        {
            m_App = app;
            Root = UiKit.View("GamesView");
            m_Grid = Root.Q<ScrollView>("gameGrid");
        }

        public void Refresh()
        {
            VisualElement container = m_Grid.contentContainer;
            container.Clear();
            var graphs = ProductionStore.LoadAll();

            foreach (GameDatabase game in GameDatabase.LoadAll().Values)
            {
                m_CardTemplate.CloneTree(container, out int index, out _);
                VisualElement card = container[index];
                int count = graphs.Count(g => g.gameId == game.Id);

                card.Q<Label>("gameName").text = game.Name;
                card.Q<Label>("tagline").text = game.Def.tagline;
                card.Q<Label>("version").text = string.IsNullOrEmpty(game.Def.version) ? "" : $"Game version {game.Def.version}";
                card.Q<Label>("count").text = count == 1 ? "1 production" : $"{count} productions";

                // Logo comes from the game file (Resources path). The card hides it if the art is missing.
                VisualElement logo = card.Q("logo");
                var tex = Resources.Load<Texture2D>(game.Def.logo ?? "");
                if (tex != null) logo.style.backgroundImage = new StyleBackground(tex);
                else logo.Add(UiKit.Text(game.Name, "game-card__name t-bold"));

                Button view = card.Q<Button>("viewButton"), create = card.Q<Button>("newButton");
                view.clicked += () => m_App.ShowProductions(game);
                create.clicked += () => m_App.CreateProduction(game);
                card.RegisterCallback<ClickEvent>(e =>
                {
                    var t = e.target as VisualElement;
                    if (!UiKit.IsInside(t, view) && !UiKit.IsInside(t, create)) m_App.ShowProductions(game);
                });
            }
        }
    }
}
