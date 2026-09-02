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

            // Flush the list's pending layout before writing any cell. GProgressBar rewrites its own title
            // from titleType inside HandleSizeChanged, so a title written while a resize is still queued is
            // silently replaced by a bare percentage one frame later - which is how "49.9% / 50.0%" became
            // "0%". Sizing everything first means nothing resizes after the write.
            _listMetrics.EnsureBoundsCorrect();

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
                // Value before title, and it matters: GProgressBar.Update rewrites the title from its
                // titleType whenever the value changes, so setting the title first would have it silently
                // replaced with a bare percentage.
                // Gated on the experiment's own verdict, not on whether anybody at all was exposed. With
                // one user in the system the light reads unknown and the bar used to draw itself full at
                // "100.0% / 50.0%" - two indicators telling opposite stories about the same state. The
                // floor is the whole point of the verdict, so both read from it.
                bool measured = owner.Srm.State != SrmState.Unknown && owner.UsersExposed > 0;

                if (bar is GProgressBar progress) progress.value = measured ? variant.ObservedShare * 100.0 : 0.0;

                // "49.9% / 50.0%" rather than "49.9% (exp 50.0%)": eighteen characters at 16px bold is
                // about 160px in a 130px bar, and shrinking the font would make the one cell a reviewer is
                // meant to scan the smallest text in the table. The word moved into the column header
                // (MetricsHeader.txtBarRate reads "share / expected"), which is where a unit belongs in a
                // table rather than repeated in every row.
                SetShareTitle(bar, measured
                    ? Percent(variant.ObservedShare) + " / " + Percent(variant.ExpectedShare)
                    : "-");

                _binder.SelectPage(bar.GetController("state"), SharePage(owner, variant), "barShare");
            }

            if (row.GetChild("srmLight") is GComponent light)
            {
                // Only the first row of an experiment carries its verdict; the light is a property of the
                // experiment, not of one arm, and repeating it on every row reads as four separate checks.
                //
                // Hidden with alpha rather than visible, deliberately. groupRow lays its children out
                // horizontally with excludeInvisibles and is centred in the row, so setting visible=false
                // removed the light's 100px column from the group, made the group narrower, and the centre
                // relation then shifted every other cell on that row by about half of it - which is exactly
                // the 50px misalignment on the continuation rows. Alpha leaves the layout untouched.
                light.alpha = first ? 1f : 0f;
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

        /// <summary>Writes the share bar's caption, whichever name the package gives that text field.</summary>
        /// <remarks>
        /// <para>
        /// <c>txtShare</c> first, <c>title</c> second. The distinction is not cosmetic: <c>GProgressBar</c>
        /// adopts a child called literally <c>title</c> as its own title object and rewrites it from
        /// <c>titleType</c> inside <c>HandleSizeChanged</c>, so under that name anything written here is
        /// liable to be replaced by a bare percentage the next time the bar is resized. Any other name is
        /// an ordinary text field the component never touches.
        /// </para>
        /// <para>
        /// Both are accepted so the code does not need a flag day with the package. When the field is not
        /// found under either name the binder reports it once, rather than the caption silently never
        /// updating - which is exactly how the original defect presented.
        /// </para>
        /// </remarks>
        private void SetShareTitle(GComponent bar, string text)
        {
            var caption = bar.GetChild("txtShare") ?? bar.GetChild("title");
            if (caption == null)
            {
                _binder.Child<GObject>(bar, "txtShare", "barShare");
                return;
            }

            caption.text = text;
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

        /// <summary>Which page of <c>barShare</c> one arm's share deviation selects.</summary>
        /// <remarks>
        /// <para>
        /// The bar shows observed share against expected, so it sits beside the ratio light and explains
        /// it: the light says the split is not plausible, the bars say <i>which arm</i> is over- or
        /// under-represented and by how much. They share one vocabulary - unknown, healthy, warn, alarm -
        /// so a reader scanning a row does not have to learn two colour languages.
        /// </para>
        /// <para>
        /// Gated on the experiment's own verdict: below the ratio check's data floor the bar reads
        /// <c>unknown</c> rather than alarming on four users, for exactly the reason the check itself has a
        /// floor. Deviation is relative rather than in percentage points, so a 10% arm is judged on the
        /// same terms as a 50% one.
        /// </para>
        /// <para>
        /// Note the asymmetry with the light: <c>warn</c> is reachable here and never there. An arm can be
        /// somewhat off; a sample ratio is either plausible or it is not.
        /// </para>
        /// </remarks>
        private static string SharePage(ExperimentMetrics owner, VariantMetrics variant)
        {
            if (owner.Srm.State == SrmState.Unknown) return "unknown";
            if (variant.ExpectedShare <= 0) return "unknown";

            double deviation = System.Math.Abs(variant.ObservedShare - variant.ExpectedShare) /
                               variant.ExpectedShare;

            if (deviation < 0.05) return "healthy";
            return deviation < 0.20 ? "warn" : "alarm";
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
