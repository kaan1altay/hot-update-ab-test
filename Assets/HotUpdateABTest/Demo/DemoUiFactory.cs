using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

namespace HotUpdateABTest.Demo
{
    /// <summary>
    /// Builds the whole console in code, with the same child names and the same layout as the authored
    /// package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a courtesy. Because the names match, <c>ConsoleView</c> binds to this exactly as it binds to the
    /// authored package, which means the PlayMode suite can run <b>both paths through the same assertions</b>
    /// - so a broken binding fails a test rather than showing up as an empty panel somebody notices later.
    /// It is also what let this slice be built while the package was still being drawn.
    /// </para>
    /// <para>
    /// Deliberately plain: flat colours, no atlas, no fonts beyond the built-in one. It has to be legible
    /// and correctly named, not attractive. The authored package is where the design lives.
    /// </para>
    /// </remarks>
    public static class DemoUiFactory
    {
        /// <summary>Design width, matching the authored package.</summary>
        public const int Width = 1600;

        /// <summary>Design height, matching the authored package.</summary>
        public const int Height = 900;

        private static readonly Color Ink = new Color32(0xE8, 0xE4, 0xDC, 0xFF);
        private static readonly Color Dim = new Color32(0x99, 0x99, 0x99, 0xFF);
        private static readonly Color Panel = new Color32(0x1A, 0x16, 0x12, 0xFF);
        private static readonly Color Accent = new Color32(0x66, 0x33, 0x00, 0xFF);
        private static readonly Color Frame = new Color32(0x66, 0x00, 0x00, 0xFF);

        /// <summary>Every button the console declares, in the order the authored package lays them out.</summary>
        /// <remarks>
        /// Shared with <c>ConsoleView</c> so the two cannot drift: the same list drives what the fallback
        /// builds and what the binder wires up.
        /// </remarks>
        public static readonly (string Name, string Title, bool Toggle, string TitleOn)[] Buttons =
        {
            ("btnServerToggle", "Start server", true, "Stop Server"),
            ("btnRefresh", "Refresh config", false, null),
            ("btnScenarioNormal", "Scenario: normal", false, null),
            ("btnScenarioWeights", "Scenario: weights 90/10", false, null),
            ("btnScenarioPause", "Scenario: pause exp", false, null),
            ("btnScenarioKill", "Scenario: kill switch", false, null),
            ("btnScenarioMalformed", "Scenario: malformed JSON", false, null),
            ("btnScenarioBadSchema", "Scenario: bad schema", false, null),
            ("btnScenarioOffline", "Scenario: offline", false, null),
            ("btnSimulate", "Simulate 5000 users", false, null),
            ("btnForceVariant", "Force variant", false, null),
            ("btnClearForce", "Clear override", false, null),
            ("btnInjectSkew", "Break: bucketing skew", true, "Fix: bucketing skew"),
            ("btnSkipExposure", "Break: skip exposure", true, "Fix: skip exposure"),
            ("btnReloadPatches", "Reload Lua patches", false, null),
            ("btnDumpState", "Dump state", false, null),
            ("btnClearState", "Clear saved state", false, null)
        };

        /// <summary>Builds a stand-in for <c>ConsoleMain</c>.</summary>
        public static GComponent CreateConsole()
        {
            var root = new GComponent { name = "ConsoleMain" };
            root.SetSize(Width, Height);
            root.AddChild(Rect(Width, Height, new Color32(0x0D, 0x0B, 0x0A, 0xFF), "background"));

            BuildTopBar(root);
            BuildDevice(root);
            BuildMetrics(root);
            BuildButtons(root);
            BuildLog(root);

            return root;
        }


        // ---------------------------------------------------------------------------------------------
        // Shop screen
        // ---------------------------------------------------------------------------------------------

        /// <summary>Builds a stand-in for <c>ShopScreen</c>: an empty 375x667 container.</summary>
        /// <remarks>Empty, matching the authored component. The interior is added by
        /// <see cref="BuildShopScreenInterior"/> only when the authored one is absent.</remarks>
        public static GComponent CreateShopScreen()
        {
            var screen = new GComponent { name = "ShopScreen" };
            screen.SetSize(375, 667);
            return screen;
        }

        /// <summary>Fills a shop screen with the same children the authored one declares.</summary>
        /// <remarks>
        /// Same names, so <c>ShopScreenView</c> cannot tell the two apart. That is the whole point: one
        /// code path, one set of assertions, and a binding break shows up as a failing test rather than an
        /// empty phone frame.
        /// </remarks>
        public static void BuildShopScreenInterior(GComponent screen)
        {
            screen.AddChild(Rect(375, 667, new Color32(0x12, 0x10, 0x0E, 0xFF), "bg"));
            screen.AddChild(Text("txtShopTitle", "SHOP", 0, 10, 375, 30, 18, Ink, true));

            var list = new GList { name = "listOffers" };
            list.SetSize(335, 520);
            list.SetXY(20, 46);
            list.layout = ListLayoutType.SingleColumn;
            list.lineGap = 9;
            list.itemRenderer = null;
            screen.AddChild(list);

            var cta = new GComponent { name = "btnCta" };
            cta.SetSize(335, 44);
            cta.SetXY(20, 576);
            cta.AddChild(Rect(335, 44, Accent, "bg"));
            cta.AddChild(Text("title", "Buy", 0, 0, 335, 44, 17, new Color32(0xFF, 0xCC, 0x00, 0xFF), true));
            screen.AddChild(cta);

            screen.AddChild(Text("txtSpec", "", 8, 630, 359, 30, 12, Dim, true));
        }

        /// <summary>Builds a stand-in for one <c>OfferCard</c>.</summary>
        /// <remarks>
        /// The authored card gears its children across the layout pages. A component built in code has no
        /// gears, so it listens to its own controller and repositions instead - which keeps
        /// <c>ShopScreenView</c> identical for both, setting a page and nothing else.
        /// </remarks>
        public static GComponent CreateOfferCard()
        {
            var card = new GComponent { name = "OfferCard" };
            card.SetSize(335, 96);

            card.AddChild(Rect(335, 96, new Color32(0x1E, 0x1A, 0x16, 0xFF), "cardBg"));
            card.AddChild(Rect(64, 64, new Color32(0x3A, 0x32, 0x2A, 0xFF), "imgIcon"));
            card.AddChild(Text("txtName", "", 0, 0, 160, 22, 14, Ink));

            var original = Text("txtOriginal", "", 0, 0, 70, 18, 12, Dim);
            original.autoSize = AutoSizeType.Both;
            card.AddChild(original);

            card.AddChild(Rect(60, 2, Dim, "graphStrike"));
            card.AddChild(Text("txtPrice", "", 0, 0, 90, 20, 14, new Color32(0x8F, 0xD6, 0x00, 0xFF)));
            card.AddChild(Rect(74, 18, new Color32(0xB2, 0x00, 0x00, 0xFF), "imgBadgeBg"));
            card.AddChild(Text("txtBadge", "", 0, 0, 74, 18, 11, Ink, true));

            AddNamedController(card, "layout", "list", "grid");
            AddNamedController(card, "price", "plain", "discounted");
            AddNamedController(card, "badge", "none", "shown");

            var layout = card.GetController("layout");
            var price = card.GetController("price");
            var badge = card.GetController("badge");

            layout.onChanged.Add(() => LayOutOfferCard(card, layout.selectedPage == "grid"));
            price.onChanged.Add(() => ShowDiscount(card, price.selectedPage == "discounted"));
            badge.onChanged.Add(() => ShowBadge(card, badge.selectedPage == "shown"));

            LayOutOfferCard(card, false);
            ShowDiscount(card, false);
            ShowBadge(card, false);

            return card;
        }

        private static void LayOutOfferCard(GComponent card, bool grid)
        {
            Resize(card, "cardBg", grid ? 163 : 335, grid ? 190 : 96,
                new Color32(0x1E, 0x1A, 0x16, 0xFF));

            if (grid)
            {
                Place(card, "imgIcon", 50, 12, 64, 64);
                Place(card, "txtName", 6, 84, 151, 22);
                Place(card, "txtOriginal", 6, 110, 70, 18);
                Place(card, "txtPrice", 6, 132, 90, 20);
                Place(card, "imgBadgeBg", 83, 8, 74, 18);
                Place(card, "txtBadge", 83, 8, 74, 18);
            }
            else
            {
                Place(card, "imgIcon", 12, 16, 64, 64);
                Place(card, "txtName", 88, 14, 160, 22);
                Place(card, "txtOriginal", 88, 44, 70, 18);
                Place(card, "txtPrice", 88, 66, 90, 20);
                Place(card, "imgBadgeBg", 253, 14, 74, 18);
                Place(card, "txtBadge", 253, 14, 74, 18);
            }
        }

        private static void ShowDiscount(GComponent card, bool discounted)
        {
            SetVisible(card, "txtOriginal", discounted);
            SetVisible(card, "graphStrike", discounted);
        }

        private static void ShowBadge(GComponent card, bool shown)
        {
            SetVisible(card, "imgBadgeBg", shown);
            SetVisible(card, "txtBadge", shown);
        }

        private static void Place(GComponent card, string name, int x, int y, int width, int height)
        {
            var child = card.GetChild(name);
            if (child == null) return;

            child.SetXY(x, y);
            if (child is GTextField field && field.autoSize != AutoSizeType.None) return;
            child.SetSize(width, height);
        }

        private static void SetVisible(GComponent card, string name, bool visible)
        {
            var child = card.GetChild(name);
            if (child != null) child.visible = visible;
        }

        private static void Resize(GComponent card, string name, int width, int height, Color color)
        {
            if (!(card.GetChild(name) is GGraph graph)) return;

            graph.SetSize(width, height);
            graph.DrawRect(width, height, 0, Color.clear, color);
        }


        private static void BuildTopBar(GComponent root)
        {
            var bar = new GComponent { name = "groupTopBar" };
            bar.SetSize(Width - 66, 90);
            bar.SetXY(33, 4);
            root.AddChild(bar);

            bar.AddChild(Text("txtTitle", "LiveOps A/B Console", 0, 0, 325, 90, 28, Ink, true));

            var chip = Chip("chipSource");
            chip.SetXY(333, 28);
            bar.AddChild(chip);

            bar.AddChild(Text("txtConfigVersion", "config -", 541, 0, 325, 90, 20, Dim));
            bar.AddChild(Text("txtServer", "server -", 874, 0, 325, 90, 20, Dim));
            bar.AddChild(Text("txtScenario", "scenario -", 1207, 0, 325, 90, 20, Dim));
        }

        private static void BuildDevice(GComponent root)
        {
            var device = new GComponent { name = "containerDevice" };
            device.SetSize(375, 667);
            device.SetXY(40, 110);
            device.AddChild(Outlined(375, 667, Color.black, Frame, "frame"));
            root.AddChild(device);

            var banner = new GComponent { name = "bannerForced" };
            banner.SetSize(420, 34);
            banner.SetXY(17, 823);
            banner.AddChild(Rect(420, 34, Frame, "bg"));
            banner.AddChild(Text("title", "", 0, 0, 420, 34, 18, Ink, true));
            AddNamedController(banner, "state", "hidden", "shown");
            banner.visible = false;
            root.AddChild(banner);
        }

        private static void BuildMetrics(GComponent root)
        {
            var list = new GList { name = "listMetrics" };
            list.SetSize(1100, 340);
            list.SetXY(450, 110);
            list.layout = ListLayoutType.SingleColumn;
            list.itemRenderer = null;
            root.AddChild(list);
        }

        private static void BuildButtons(GComponent root)
        {
            var group = new GComponent { name = "groupButtons" };
            group.SetSize(1141, 230);
            group.SetXY(429, 457);
            root.AddChild(group);

            const int columns = 5;
            const int cellWidth = 230;
            const int cellHeight = 60;

            for (int i = 0; i < Buttons.Length; i++)
            {
                var spec = Buttons[i];
                var button = spec.Toggle
                    ? Toggle(spec.Name, spec.Title, spec.TitleOn)
                    : Action(spec.Name, spec.Title);

                button.SetXY((i % columns) * cellWidth, (i / columns) * cellHeight);
                group.AddChild(button);
            }
        }

        private static void BuildLog(GComponent root)
        {
            var list = new GList { name = "listLog" };
            list.SetSize(1117, 190);
            list.SetXY(443, 696);
            list.layout = ListLayoutType.SingleColumn;
            list.itemRenderer = null;
            root.AddChild(list);
        }

        /// <summary>Builds a stand-in for one <c>MetricsRow</c>.</summary>
        public static GComponent CreateMetricsRow()
        {
            var row = new GComponent { name = "MetricsRow" };
            row.SetSize(1090, 40);

            int[] widths = { 140, 140, 140, 140, 140, 140 };
            string[] names =
            {
                "txtExperiment", "txtVariant", "txtAssignments", "txtExposures", "txtConversions", "txtRate"
            };

            int x = 0;
            for (int i = 0; i < names.Length; i++)
            {
                row.AddChild(Text(names[i], "", x, 0, widths[i], 40, 15, Ink));
                x += widths[i] + 4;
            }

            var bar = new GComponent { name = "barShare" };
            bar.SetSize(130, 25);
            bar.SetXY(x, 8);
            bar.AddChild(Rect(130, 10, new Color32(0x33, 0x2F, 0x2A, 0xFF), "track"));

            var fill = Rect(130, 10, new Color32(0x00, 0xCC, 0x00, 0xFF), "bar");
            bar.AddChild(fill);
            bar.AddChild(Text("txtShare", "", 0, -12, 130, 24, 14, Ink, true));
            AddNamedController(bar, "state", "unknown", "healthy", "warn", "alarm");
            row.AddChild(bar);

            var light = new GComponent { name = "srmLight" };
            light.SetSize(28, 28);
            light.SetXY(x + 140, 6);
            light.AddChild(Rect(28, 28, new Color32(0x66, 0x66, 0x66, 0xFF), "n0"));
            AddNamedController(light, "state", "unknown", "healthy", "warn", "alarm");
            row.AddChild(light);

            return row;
        }

        /// <summary>Builds a stand-in for <c>MetricsHeader</c>.</summary>
        public static GComponent CreateMetricsHeader()
        {
            var header = new GComponent { name = "MetricsHeader" };
            header.SetSize(1090, 40);
            header.AddChild(Rect(1090, 40, Panel, "bg"));

            string[] labels = { "Experiment", "Variant", "Assigned", "Exposed", "Conv", "Rate" };
            int x = 0;
            for (int i = 0; i < labels.Length; i++)
            {
                header.AddChild(Text("h" + i, labels[i], x, 0, 140, 40, 15, Dim, true));
                x += 144;
            }

            header.AddChild(Text("hBar", "Exp/Asgn", x, 0, 130, 40, 15, Dim, true));
            header.AddChild(Text("hSrm", "SRM", x + 140, 0, 100, 40, 15, Dim, true));

            return header;
        }

        /// <summary>Builds a stand-in for one <c>LogRow</c>.</summary>
        public static GComponent CreateLogRow()
        {
            var row = new GComponent { name = "LogRow" };
            row.SetSize(1100, 26);
            row.AddChild(Text("titleLogHeader", "Log:", 0, 0, 60, 26, 14, Dim, true));
            row.AddChild(Text("title", "", 64, 0, 1030, 26, 14, Ink));
            AddNamedController(row, "type", "log", "warn", "err");
            return row;
        }

        private static GComponent Chip(string name)
        {
            var chip = new GComponent { name = name };
            chip.SetSize(200, 34);
            chip.AddChild(Rect(200, 34, new Color32(0x00, 0x99, 0x00, 0xFF), "bg"));
            chip.AddChild(Text("title", "", 0, 0, 200, 34, 14, Ink, true));
            AddNamedController(chip, "state", "live", "lkg", "defaults", "none");
            return chip;
        }

        private static GComponent Action(string name, string title)
        {
            var button = new GComponent { name = name };
            button.SetSize(221, 50);
            button.AddChild(Rect(221, 50, Accent, "bg"));
            button.AddChild(Text("title", title, 0, 0, 221, 50, 14, new Color32(0xFF, 0xCC, 0x00, 0xFF), true));
            return button;
        }

        private static GComponent Toggle(string name, string titleOff, string titleOn)
        {
            var button = new GComponent { name = name };
            button.SetSize(221, 50);
            button.AddChild(Rect(221, 50, new Color32(0x66, 0x99, 0x00, 0xFF), "bg"));
            button.AddChild(Text("titleOff", titleOff, 0, 0, 221, 50, 14, Ink, true));
            button.AddChild(Text("titleOn", titleOn, 0, 0, 221, 50, 14, Ink, true));
            AddNamedController(button, "state", "off", "on");
            return button;
        }

        /// <summary>Adds a controller with named pages to <paramref name="owner"/> and selects the first.</summary>
        /// <remarks>
        /// The controller has to belong to its component before a page can be selected: setting
        /// selectedIndex reaches through to parent.ApplyController, which is a null reference on a
        /// controller nobody owns yet. Ordering these the other way around is the kind of mistake that
        /// only shows up once something actually builds the UI, which is why the PlayMode suite exists.
        /// </remarks>
        private static void AddNamedController(GComponent owner, string name, params string[] pages)
        {
            var controller = new Controller { name = name };
            for (int i = 0; i < pages.Length; i++) controller.AddPage(pages[i]);

            owner.AddController(controller);
            controller.selectedIndex = 0;
        }

        private static GGraph Rect(int width, int height, Color color, string name)
        {
            var graph = new GGraph { name = name };
            graph.SetSize(width, height);
            graph.DrawRect(width, height, 0, Color.clear, color);
            return graph;
        }

        private static GGraph Outlined(int width, int height, Color fill, Color line, string name)
        {
            var graph = new GGraph { name = name };
            graph.SetSize(width, height);
            graph.DrawRect(width, height, 4, line, fill);
            return graph;
        }

        private static GTextField Text(
            string name, string text, int x, int y, int width, int height, int size, Color color,
            bool centred = false)
        {
            var field = new GTextField { name = name };
            field.SetSize(width, height);
            field.SetXY(x, y);
            field.textFormat = new TextFormat
            {
                size = size,
                color = color,
                align = centred ? AlignType.Center : AlignType.Left
            };
            field.verticalAlign = VertAlignType.Middle;
            field.text = text;
            return field;
        }
    }
}
