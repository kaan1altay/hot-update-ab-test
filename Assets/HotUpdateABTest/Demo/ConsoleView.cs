using System;
using System.Collections.Generic;
using FairyGUI;
using HotUpdateABTest.Core;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Telemetry;

namespace HotUpdateABTest.Demo
{
    /// <summary>
    /// Binds the console: the status bar, the metrics table, the log, and every button.
    /// </summary>
    /// <remarks>
    /// Written against child names, so it drives the authored package and the programmatic fallback
    /// identically. That is what lets the PlayMode suite run both paths through the same assertions.
    /// </remarks>
    public sealed class ConsoleView
    {
        private const int MaxLogRows = 60;

        private readonly GComponent _root;
        private readonly FairyBinder _binder;
        private readonly bool _fallback;

        private readonly Dictionary<string, GObject> _buttons = new Dictionary<string, GObject>(StringComparer.Ordinal);
        private readonly List<GComponent> _metricRows = new List<GComponent>();
        private readonly List<GComponent> _logRows = new List<GComponent>();

        private GComponent _chipSource;
        private GComponent _bannerForced;
        private GList _listMetrics;
        private GList _listLog;

        /// <summary>The device frame the shop screen goes into.</summary>
        public GComponent DeviceContainer { get; private set; }

        /// <summary>Creates a view over <paramref name="root"/>.</summary>
        public ConsoleView(GComponent root, FairyBinder binder, bool usingFallback)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _binder = binder ?? throw new ArgumentNullException(nameof(binder));
            _fallback = usingFallback;

            Bind();
        }

        /// <summary>Raised when a button is pressed, with its child name.</summary>
        public event Action<string> ButtonPressed;

        private void Bind()
        {
            _chipSource = FindDeep<GComponent>("chipSource");
            _bannerForced = FindDeep<GComponent>("bannerForced");
            DeviceContainer = FindDeep<GComponent>("containerDevice");
            _listMetrics = FindDeep<GList>("listMetrics");
            _listLog = FindDeep<GList>("listLog");

            foreach (var spec in DemoUiFactory.Buttons)
            {
                var button = FindDeep<GObject>(spec.Name);
                if (button == null) continue;

                _buttons[spec.Name] = button;

                string name = spec.Name;
                button.onClick.Add(() => ButtonPressed?.Invoke(name));
            }
        }

        /// <summary>Sets a toggle button's visual state.</summary>
        public void SetToggle(string buttonName, bool on)
        {
            if (!_buttons.TryGetValue(buttonName, out var button)) return;
            if (!(button is GComponent component)) return;

            _binder.SelectPage(component.GetController("state"), on ? "on" : "off", buttonName);
        }

        /// <summary>Updates the status bar.</summary>
        public void SetStatus(ConfigSnapshot snapshot, string server, string scenario)
        {
            SetTextDeep("txtConfigVersion", "config " + snapshot.ConfigVersion);
            SetTextDeep("txtServer", server);
            SetTextDeep("txtScenario", scenario);

            if (_chipSource == null) return;

            _binder.SelectPage(_chipSource.GetController("state"), ChipPage(snapshot.Source), "chipSource");

            var title = _chipSource.GetChild("title");
            if (title != null) title.text = ChipLabel(snapshot);
        }

        /// <summary>Shows or hides the forced-override banner.</summary>
        public void SetForcedBanner(bool visible, string text)
        {
            if (_bannerForced == null) return;

            _binder.SelectPage(_bannerForced.GetController("state"), visible ? "shown" : "hidden", "bannerForced");
            _bannerForced.visible = visible;

            var title = _bannerForced.GetChild("title");
            if (title != null) title.text = text;
        }

        /// <summary>Redraws the metrics table.</summary>
        /// <remarks>
        /// Rows are reused rather than rebuilt. The demo repaints on every simulated batch and on every
        /// config change, and discarding a hundred display objects each time is the kind of thing that
        /// makes a recording stutter at exactly the wrong moment.
        /// </remarks>
        public void SetMetrics(MetricsReport report)
        {
            if (_listMetrics == null) return;

            var rows = new List<(string Experiment, VariantMetrics Variant, ExperimentMetrics Owner, bool First)>();
            foreach (var experiment in report.Experiments)
            {
                bool first = true;
                foreach (var variant in experiment.Variants)
                {
                    rows.Add((experiment.ExperimentId, variant, experiment, first));
                    first = false;
                }
            }

            EnsureMetricRows(rows.Count);

            for (int i = 0; i < _metricRows.Count; i++)
            {
                var row = _metricRows[i];
                row.visible = i < rows.Count;
                if (!row.visible) continue;

                var data = rows[i];
                FillMetricRow(row, data.Experiment, data.Variant, data.Owner, data.First);
            }
        }

        /// <summary>Appends a line to the log panel.</summary>
        public void AppendLog(AbLogLevel level, string message)
        {
            if (_listLog == null) return;

            GComponent row;
            if (_logRows.Count >= MaxLogRows)
            {
                // Recycle the oldest row rather than growing without bound. The sink keeps the real
                // history; this panel is a window onto the tail of it.
                row = _logRows[0];
                _logRows.RemoveAt(0);
                _listLog.RemoveChild(row);
            }
            else
            {
                row = _fallback ? DemoUiFactory.CreateLogRow() : NewFromList(_listLog);
                if (row == null) return;
            }

            _logRows.Add(row);
            _listLog.AddChild(row);

            var title = row.GetChild("title");
            if (title != null) title.text = message;

            _binder.SelectPage(row.GetController("type"), LogPage(level), "LogRow");
            _listLog.scrollPane?.ScrollBottom();
        }

        /// <summary>Empties the log panel.</summary>
        public void ClearLog()
        {
            if (_listLog == null) return;

            _listLog.RemoveChildren();
            _logRows.Clear();
        }

        private void EnsureMetricRows(int wanted)
        {
            // The header is row zero and is created once.
            if (_metricRows.Count == 0 && _listMetrics != null)
            {
                var header = _fallback ? DemoUiFactory.CreateMetricsHeader() : NewHeaderFromPackage();
                if (header != null) _listMetrics.AddChild(header);
            }

            while (_metricRows.Count < wanted)
            {
                var row = _fallback ? DemoUiFactory.CreateMetricsRow() : NewFromList(_listMetrics);
                if (row == null) return;

                _metricRows.Add(row);
                _listMetrics.AddChild(row);
            }
        }

        private void FillMetricRow(
            GComponent row, string experimentId, VariantMetrics variant, ExperimentMetrics owner, bool first)
        {
            SetChildText(row, "txtExperiment", first ? experimentId : "");
            SetChildText(row, "txtVariant", variant.VariantId + (variant.IsOrphaned ? "*" : ""));
            SetChildText(row, "txtAssignments", variant.UsersAssigned.ToString());
            SetChildText(row, "txtExposures", variant.UsersExposed.ToString());
            SetChildText(row, "txtConversions", variant.Conversions.ToString());
            SetChildText(row, "txtRate", Percent(variant.ConversionRate));

            if (row.GetChild("barShare") is GComponent bar)
            {
                double funnel = variant.ExposureRate;

                if (bar is GProgressBar progress) progress.value = funnel * 100.0;
                SetChildText(bar, "title", variant.UsersAssigned == 0 ? "-" : Percent(funnel));
                _binder.SelectPage(bar.GetController("state"),
                    FunnelPage(variant.UsersAssigned, funnel), "barShare");
            }

            if (row.GetChild("srmLight") is GComponent light)
            {
                // Only the first row of an experiment carries its verdict; the light is a property of the
                // experiment, not of one arm, and repeating it on every row reads as four separate checks.
                light.visible = first;
                if (first) _binder.SelectPage(light.GetController("state"), SrmPage(owner.Srm.State), "srmLight");
            }
        }

        private GComponent NewFromList(GList list)
        {
            try
            {
                return list.GetFromPool(list.defaultItem) as GComponent;
            }
            catch (Exception)
            {
                // A list with no default item, which the fallback path never has and a drifted package
                // might. The binder already reported the shape problem.
                return null;
            }
        }

        private GComponent NewHeaderFromPackage()
        {
            try
            {
                return UIPackage.CreateObject("AbTestDemo", "MetricsHeader") as GComponent;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private T FindDeep<T>(string name) where T : GObject
        {
            var found = FindDeep(_root, name);
            if (found is T typed) return typed;

            if (found == null) _binder.Child<T>(_root, name, "ConsoleMain");
            return null;
        }

        private static GObject FindDeep(GComponent parent, string name)
        {
            if (parent == null) return null;

            var direct = parent.GetChild(name);
            if (direct != null) return direct;

            // The authored package nests buttons inside groups, and a group is a GGroup rather than a
            // container, so a plain GetChild on the root does find them - but the fallback nests them in
            // real components. Searching one level down covers both without caring which.
            for (int i = 0; i < parent.numChildren; i++)
            {
                if (parent.GetChildAt(i) is GComponent child)
                {
                    var found = child.GetChild(name);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private void SetTextDeep(string name, string text)
        {
            var field = FindDeep(_root, name);
            if (field != null) field.text = text;
        }

        private static void SetChildText(GComponent parent, string name, string text)
        {
            var child = parent.GetChild(name);
            if (child != null) child.text = text;
        }

        private static string Percent(double value) => (value * 100.0).ToString("0.0") + "%";

        private static string ChipPage(ConfigSourceKind source)
        {
            switch (source)
            {
                case ConfigSourceKind.Live: return "live";
                case ConfigSourceKind.LastKnownGood: return "lkg";
                case ConfigSourceKind.ShippedDefaults: return "defaults";
                default: return "none";
            }
        }

        private static string ChipLabel(ConfigSnapshot snapshot)
        {
            switch (snapshot.Source)
            {
                case ConfigSourceKind.Live: return "LIVE";
                case ConfigSourceKind.LastKnownGood: return "LAST KNOWN GOOD";
                case ConfigSourceKind.ShippedDefaults: return "SHIPPED DEFAULTS";
                default: return "NOTHING LOADED";
            }
        }

        private static string SrmPage(SrmState state)
        {
            switch (state)
            {
                case SrmState.Healthy: return "healthy";
                case SrmState.Alarm: return "alarm";
                default: return "unknown";
            }
        }

        private static string FunnelPage(long assigned, double funnel)
        {
            if (assigned == 0) return "unknown";
            if (funnel >= 0.9) return "green";
            return funnel >= 0.5 ? "yellow" : "red";
        }

        private static string LogPage(AbLogLevel level)
        {
            switch (level)
            {
                case AbLogLevel.Warning: return "warn";
                case AbLogLevel.Error: return "err";
                default: return "log";
            }
        }
    }
}
