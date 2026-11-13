using System.Linq;
using IndustryPlanner.Core;
using IndustryPlanner.UI.GraphEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI
{
    /// <summary>
    /// Entry point. Put it next to the UIDocument that shows HomeScreen.uxml.
    /// Owns navigation between Home, Games, a game's productions and the graph editor.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AppController : MonoBehaviour
    {
        VisualElement m_App, m_Sidebar, m_Content;
        Button m_HomeNav, m_GamesNav, m_SettingsNav;
        VisualElement m_Toast;
        Label m_ToastText;
        Button m_ToastAction;
        System.Action m_ToastCallback;
        ProductionsView m_Productions;
        GamesView m_Games;
        SettingsView m_Settings;
        GraphEditorView m_Editor;
        string m_ThemeClass;
        IVisualElementScheduledItem m_ToastHide;

        /// <summary>Game used by "New production" on Home: the last one the user worked in.</summary>
        public GameDatabase CurrentGame { get; private set; }

        void OnEnable()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            m_App = root.Q("app");
            m_Sidebar = root.Q("sidebar");
            m_Content = root.Q("content");
            m_Toast = root.Q("toast");
            m_ToastText = root.Q<Label>("toastText");
            m_ToastAction = root.Q<Button>("toastAction");
            m_ToastAction.clicked += () =>
            {
                System.Action callback = m_ToastCallback;
                m_ToastCallback = null;
                m_Toast.AddToClassList("toast--hidden");
                callback?.Invoke();
            };
            m_HomeNav = root.Q<Button>("homeNav");
            m_GamesNav = root.Q<Button>("gamesNav");
            m_SettingsNav = root.Q<Button>("settingsNav");
            m_HomeNav.clicked += ShowHome;
            m_GamesNav.clicked += ShowGames;
            m_SettingsNav.clicked += ShowSettings;
            AppSettings.Changed += ApplySettings;
            ApplySettings();

            CurrentGame = GameDatabase.LoadAll().Values.FirstOrDefault();
            if (CurrentGame == null) Debug.LogError("No game data found in Resources/Games.");

            m_Productions = new ProductionsView(this);
            m_Games = new GamesView(this);
            m_Editor = new GraphEditorView(this);
            m_Settings = new SettingsView(this);
            ShowHome();
        }

        void OnDisable()
        {
            AppSettings.Changed -= ApplySettings;
            m_Editor?.Dispose();
        }

        void Update()
        {
            if (m_Editor.Root.parent != null) m_Editor.Tick();
            else if (m_Settings.Root.parent != null) m_Settings.Tick();
        }

        void OnApplicationQuit() => m_Editor?.SaveIfDirty();

        public void ShowHome() => ShowProductions(null);

        /// <summary>Productions of one game, or the latest designs of every game when game is null.</summary>
        public void ShowProductions(GameDatabase game)
        {
            if (game != null) CurrentGame = game;
            SetTheme(game ?? CurrentGame);
            Show(m_Productions.Root, sidebar: true, game == null ? m_HomeNav : m_GamesNav);
            m_Productions.Show(game);
        }

        public void ShowGames()
        {
            SetTheme(CurrentGame);
            Show(m_Games.Root, sidebar: true, m_GamesNav);
            m_Games.Refresh();
        }

        public void ShowSettings()
        {
            SetTheme(CurrentGame);
            Show(m_Settings.Root, sidebar: true, m_SettingsNav);
            m_Settings.Show(CurrentGame);
        }

        /// <summary>
        /// Asks for the name, the game (preselected when given) and, for games with progression, the tier and
        /// alternate recipes. These stay editable from the editor's top bar.
        /// </summary>
        public void CreateProduction(GameDatabase preselected)
        {
            var games = GameDatabase.LoadAll().Values.ToList();
            GameDatabase game = preselected ?? CurrentGame ?? games.FirstOrDefault();
            if (game == null) return;
            string name = "New production";
            int tier = ProductionStore.LastTier(game.Id);
            bool alternates = false;

            Dialog.Show(m_App, "New production", body =>
            {
                void Build()
                {
                    body.Clear();
                    UiKit.Text("Name", "dialog__label", body);
                    var nameField = new TextField { value = name };
                    nameField.RegisterValueChangedCallback(e => name = e.newValue);
                    body.Add(nameField);

                    UiKit.Text("Game", "dialog__label", body);
                    VisualElement gameRow = UiKit.El("dialog__choices", body);
                    foreach (GameDatabase g in games)
                        UiKit.Btn(g.Name, "btn segment t-semibold", () => { game = g; tier = ProductionStore.LastTier(g.Id); Build(); }, gameRow)
                            .EnableInClassList("segment--active", g == game);

                    if (!game.HasProgression) return;
                    UiKit.Text($"{game.Def.progressionLabel} you have reached", "dialog__label", body);
                    VisualElement tiers = UiKit.El("dialog__choices dialog__choices--wrap", body);
                    foreach (ProgressionLevel level in game.Def.progression)
                        UiKit.Btn(level.value.ToString(), "btn segment segment--square t-semibold", () => { tier = level.value; Build(); }, tiers)
                            .EnableInClassList("segment--active", level.value == tier);
                    ProgressionLevel current = System.Array.Find(game.Def.progression, l => l.value == tier);
                    UiKit.Text($"{current?.name}: {current?.detail}. Buildings and recipes above it stay hidden.", "dialog__hint", body);

                    VisualElement altRow = UiKit.El("dialog__switch-row", body);
                    UiKit.Text("Alternate recipes unlocked", "dialog__switch-label", altRow);
                    Button sw = UiKit.Btn("", alternates ? "switch switch--on" : "switch", null, altRow);
                    UiKit.El("switch__knob", sw);
                    sw.clicked += () => { alternates = !alternates; sw.EnableInClassList("switch--on", alternates); };
                }
                Build();
            }, "Create", () =>
            {
                var graph = new ProductionGraph
                {
                    gameId = game.Id, name = string.IsNullOrWhiteSpace(name) ? "New production" : name.Trim(),
                    tier = game.HasProgression ? tier : 99, alternates = alternates,
                };
                ProductionStore.Save(graph);
                OpenEditor(graph);
                return true;
            });
        }

        public void OpenEditor(ProductionGraph graph)
        {
            GameDatabase game = GameDatabase.Get(graph.gameId);
            if (game == null) { ShowToast($"Game data for \"{graph.gameId}\" is missing."); return; }
            CurrentGame = game;
            SetTheme(game);
            Show(m_Editor.Root, sidebar: false, null);
            m_Editor.Open(game, graph);
        }

        /// <summary>Short message at the bottom; with an action (e.g. Undo) it stays a little longer.</summary>
        public void ShowToast(string message) => ShowToast(message, null, null);

        public void ShowToast(string message, string actionLabel, System.Action action)
        {
            m_ToastText.text = message;
            m_ToastCallback = action;
            m_ToastAction.text = actionLabel ?? "";
            m_ToastAction.EnableInClassList("is-hidden", action == null);
            m_Toast.RemoveFromClassList("toast--hidden");
            m_ToastHide?.Pause();
            m_ToastHide = m_Toast.schedule.Execute(() =>
            {
                m_Toast.AddToClassList("toast--hidden");
                m_ToastCallback = null;
            }).StartingIn(action != null ? 6000 : 2500);
        }

        /// <summary>Swaps the visible view with a short fade and slide (see .view--enter in Theme.uss).</summary>
        void Show(VisualElement view, bool sidebar, Button activeNav)
        {
            m_Content.Clear();
            m_Content.Add(view);
            view.AddToClassList("view--enter");
            view.schedule.Execute(() => view.RemoveFromClassList("view--enter")).StartingIn(16);
            m_Sidebar.EnableInClassList("sidebar--hidden", !sidebar);
            foreach (Button nav in new[] { m_HomeNav, m_GamesNav, m_SettingsNav })
                nav.EnableInClassList("nav-item--active", nav == activeNav);
        }

        void ApplySettings() => m_App.EnableInClassList("reduce-motion", AppSettings.Current.reduceMotion);

        /// <summary>Swaps the per-game USS class that defines --accent (see Theme.uss).</summary>
        void SetTheme(GameDatabase game)
        {
            if (m_ThemeClass != null) m_App.RemoveFromClassList(m_ThemeClass);
            m_ThemeClass = game?.Def.themeClass;
            if (m_ThemeClass != null) m_App.AddToClassList(m_ThemeClass);
        }
    }
}
