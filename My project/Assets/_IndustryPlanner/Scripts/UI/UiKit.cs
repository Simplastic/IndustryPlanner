using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI
{
    /// <summary>Small helpers shared by every view: element factories, number formatting, icons.</summary>
    public static class UiKit
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Dictionary<string, Texture2D> s_Icons = new();

        public static VisualTreeAsset Template(string name) => Resources.Load<VisualTreeAsset>("UI/" + name);

        /// <summary>Instantiates Resources/UI/{name}.uxml as a full-size view.</summary>
        public static VisualElement View(string name)
        {
            TemplateContainer view = Template(name).Instantiate();
            view.AddToClassList("view");
            return view;
        }

        public static VisualElement El(string cls, VisualElement parent = null)
        {
            var e = new VisualElement();
            AddClasses(e, cls);
            parent?.Add(e);
            return e;
        }

        public static Label Text(string text, string cls, VisualElement parent = null)
        {
            var l = new Label(text);
            AddClasses(l, cls);
            parent?.Add(l);
            return l;
        }

        public static Button Btn(string text, string cls, Action onClick, VisualElement parent = null)
        {
            var b = new Button(onClick) { text = text };
            AddClasses(b, cls);
            parent?.Add(b);
            return b;
        }

        /// <summary>Adds space-separated USS classes; empty entries are ignored.</summary>
        public static void AddClasses(VisualElement e, string cls)
        {
            if (string.IsNullOrEmpty(cls)) return;
            foreach (string c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries)) e.AddToClassList(c);
        }

        /// <summary>"30", "7.5", "0.25" — up to two decimals, trailing zeros dropped.</summary>
        public static string Num(float v) => Mathf.Abs(v) >= 100 ? v.ToString("#,0", Inv) : v.ToString("0.##", Inv);
        public static string Rate(float v) => Num(v) + "/min";
        public static string Power(float mw) => mw.ToString("#,0.#", Inv) + " MW";

        public static string Edited(DateTime edited)
        {
            int days = (DateTime.Now.Date - edited.Date).Days;
            if (days <= 0) return "Edited today";
            if (days == 1) return "Edited yesterday";
            return days < 30 ? $"Edited {days} days ago" : "Edited " + edited.ToString("d MMM yyyy", Inv);
        }

        /// <summary>
        /// Item or building icon. Loads Resources/Icons/{game}/{Items|Buildings}/{id}.png when present,
        /// otherwise a coloured placeholder tile with the name's initials.
        /// </summary>
        public static VisualElement Icon(string gameFolder, bool building, string id, string name, string size = "s")
        {
            VisualElement tile = El("icon-tile icon-tile--" + size);
            string path = $"Icons/{gameFolder}/{(building ? "Buildings" : "Items")}/{id}";
            if (!s_Icons.TryGetValue(path, out Texture2D tex)) s_Icons[path] = tex = Resources.Load<Texture2D>(path);

            // Inline styles here carry data (the art or the id's colour), not layout.
            if (tex != null)
            {
                tile.style.backgroundImage = new StyleBackground(tex);
                return tile;
            }
            tile.style.backgroundColor = Color.HSVToRGB(StableHash(id) % 360 / 360f, 0.45f, building ? 0.4f : 0.55f);
            Text(Initials(name), "icon-tile__initials", tile);
            return tile;
        }

        /// <summary>Same colour for the same id on every run (string.GetHashCode is not guaranteed stable).</summary>
        static uint StableHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") h = (h ^ c) * 16777619;
            return h;
        }

        static string Initials(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            string[] words = name.Split(new[] { ' ', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 1 ? words[0].Substring(0, Mathf.Min(2, words[0].Length)) : $"{words[0][0]}{words[1][0]}";
        }

        /// <summary>True while the user is typing in a text or number field (hotkeys must stay quiet).</summary>
        public static bool IsTyping(IPanel panel)
        {
            for (var e = panel?.focusController?.focusedElement as VisualElement; e != null; e = e.parent)
                if (e.ClassListContains("unity-base-text-field")) return true;
            return false;
        }

        public static bool IsInside(VisualElement element, VisualElement ancestor)
        {
            for (VisualElement e = element; e != null; e = e.parent)
                if (e == ancestor) return true;
            return false;
        }
    }

    /// <summary>Context menus and confirmations, drawn above everything in the app root.</summary>
    public static class Popup
    {
        /// <summary>Opens a menu under the anchor, or at a world position when one is given.</summary>
        public static void Menu(VisualElement anchor, (string label, Action action, bool danger)[] items, string message = null, Vector2? at = null)
        {
            VisualElement root = anchor.panel.visualTree.Q("app") ?? anchor.panel.visualTree;
            VisualElement layer = UiKit.El("popup-layer", root);
            VisualElement menu = UiKit.El("popup-menu", layer);
            menu.AddToClassList("popup-menu--enter");
            menu.schedule.Execute(() => menu.RemoveFromClassList("popup-menu--enter")).StartingIn(16);
            if (message != null) UiKit.Text(message, "popup-menu__message", menu);

            foreach (var (label, action, danger) in items)
                UiKit.Btn(label, danger ? "popup-menu__item popup-menu__item--danger" : "popup-menu__item",
                    () => { layer.RemoveFromHierarchy(); action?.Invoke(); }, menu);

            // Close when clicking outside the menu.
            layer.RegisterCallback<PointerDownEvent>(e => { if (e.target == layer) layer.RemoveFromHierarchy(); });

            // Place under the anchor, kept inside the window once the menu has a size.
            Vector2 pos = root.WorldToLocal(at ?? new Vector2(anchor.worldBound.xMin, anchor.worldBound.yMax + 4));
            menu.style.left = pos.x;
            menu.style.top = pos.y;
            menu.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                menu.style.left = Mathf.Min(pos.x, root.layout.width - menu.layout.width - 8);
                menu.style.top = Mathf.Min(pos.y, root.layout.height - menu.layout.height - 8);
            });
        }

        public static void Confirm(VisualElement anchor, string message, string confirmLabel, Action onConfirm) =>
            Menu(anchor, new (string, Action, bool)[] { (confirmLabel, onConfirm, true), ("Cancel", null, false) }, message);
    }

    /// <summary>Centered modal with a title, a body built by the caller and Cancel / confirm buttons.</summary>
    public static class Dialog
    {
        /// <param name="onConfirm">Return false to keep the dialog open (e.g. invalid input).</param>
        public static void Show(VisualElement anyElement, string title, Action<VisualElement> buildBody, string confirmLabel, Func<bool> onConfirm)
        {
            VisualElement root = anyElement.panel.visualTree.Q("app") ?? anyElement.panel.visualTree;
            VisualElement layer = UiKit.El("dialog-layer dialog-layer--enter", root);
            layer.schedule.Execute(() => layer.RemoveFromClassList("dialog-layer--enter")).StartingIn(16);
            VisualElement box = UiKit.El("dialog", layer);
            UiKit.Text(title, "dialog__title t-bold", box);
            VisualElement body = UiKit.El("dialog__body", box);
            buildBody(body);

            VisualElement actions = UiKit.El("dialog__actions", box);
            UiKit.Btn("Cancel", "btn btn--secondary t-semibold", layer.RemoveFromHierarchy, actions);
            UiKit.Btn(confirmLabel, "btn btn--primary dialog__confirm t-semibold", () => { if (onConfirm()) layer.RemoveFromHierarchy(); }, actions);
            layer.RegisterCallback<PointerDownEvent>(e => { if (e.target == layer) layer.RemoveFromHierarchy(); });
            layer.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Escape) layer.RemoveFromHierarchy();
                else if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter && onConfirm()) layer.RemoveFromHierarchy();
            }, TrickleDown.TrickleDown);
        }

        /// <summary>Asks for one line of text, e.g. a blueprint name.</summary>
        public static void Prompt(VisualElement anyElement, string title, string label, string initial, string confirmLabel, Action<string> onConfirm)
        {
            TextField field = null;
            Show(anyElement, title, body =>
            {
                UiKit.Text(label, "dialog__label", body);
                field = new TextField { value = initial };
                body.Add(field);
                field.schedule.Execute(() => { field.Focus(); field.SelectAll(); });
            }, confirmLabel, () =>
            {
                if (string.IsNullOrWhiteSpace(field.value)) return false;
                onConfirm(field.value.Trim());
                return true;
            });
        }
    }
}
