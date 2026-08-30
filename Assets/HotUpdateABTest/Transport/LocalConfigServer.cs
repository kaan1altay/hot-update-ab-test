using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using HotUpdateABTest.Core;

namespace HotUpdateABTest.Transport
{
    /// <summary>What the local server is currently serving.</summary>
    /// <remarks>
    /// Each of these is a button in the demo. They exist so the guardrails can be demonstrated rather than
    /// described: a fallback ladder nobody can watch fall is a paragraph in a README.
    /// </remarks>
    public enum ServerScenario
    {
        /// <summary>A healthy config with both experiments running and split evenly.</summary>
        Normal,

        /// <summary>The same experiments with the offer-layout weights ramped to 90/10.</summary>
        WeightsRamped,

        /// <summary>The offer-layout experiment paused.</summary>
        ExperimentPaused,

        /// <summary>Both experiments stopped. The kill switch.</summary>
        KillSwitch,

        /// <summary>Truncated JSON.</summary>
        MalformedJson,

        /// <summary>Valid JSON announcing a schema version this build does not understand.</summary>
        BadSchemaVersion,

        /// <summary>Every request is refused, as though the server were unreachable.</summary>
        Offline
    }

    /// <summary>
    /// A dev-only HTTP server that serves experiment config from localhost, and can be told to misbehave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dev tooling. It exists to make the LiveOps story demonstrable - change a weight, pause an
    /// experiment, serve rubbish, go offline - not to be part of a shipping game.
    /// </para>
    /// <para>
    /// It is a <i>runtime</i> assembly rather than an Editor-only one, because the demo has to run in play
    /// mode and a MonoBehaviour in an Editor-only assembly cannot be added to a GameObject. What keeps it
    /// out of a real build is therefore folder membership - a game adopting this framework takes
    /// <c>Runtime/</c> and leaves <c>Transport/</c> and <c>Demo/</c> behind - not an assembly definition
    /// platform filter. Worth saying plainly rather than implying a guarantee the build does not make.
    /// </para>
    /// <para><b>The prefix is always <c>http://localhost:port/</c>.</b> Windows requires a URL ACL
    /// reservation for wildcard prefixes such as <c>http://+:8757/</c>, which needs an elevated
    /// <c>netsh</c> command that nobody cloning this repository should have to run. The literal
    /// <c>localhost</c> form is exempt from that reservation, so this binds as an ordinary user. Anything
    /// else is refused rather than attempted.</para>
    /// <para><b>Ports are scanned, not assumed.</b> <see cref="FirstPort"/> upward, first that binds wins,
    /// and the chosen port is public so the demo can display it. A hard-coded port is a demo that fails on
    /// somebody else's machine for reasons that have nothing to do with the code.</para>
    /// <para><b>Threading.</b> The listener runs its accept loop on a background thread and answers from
    /// there. It touches nothing but its own state, guarded by a lock. Nothing here calls into the
    /// framework: the client fetches on its own schedule, which keeps the ownership of the config snapshot
    /// exactly where <c>ConfigService</c> documents it.</para>
    /// </remarks>
    public sealed class LocalConfigServer : IDisposable
    {
        /// <summary>First port tried.</summary>
        public const int FirstPort = 8757;

        /// <summary>How many consecutive ports are tried before giving up.</summary>
        public const int PortsToTry = 10;

        /// <summary>The path config is served from.</summary>
        public const string ConfigPath = "/config";

        private readonly IAbLog _log;
        private readonly object _gate = new object();

        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private ServerScenario _scenario = ServerScenario.Normal;
        private int _configVersion = 1;

        /// <summary>The port in use, or 0 when the server is not running.</summary>
        public int Port { get; private set; }

        /// <summary>True while the server is accepting requests.</summary>
        public bool IsRunning => _running;

        /// <summary>Why the last start attempt failed, or null.</summary>
        public string LastError { get; private set; }

        /// <summary>The URL a client should fetch, or null when not running.</summary>
        public string Url => _running ? "http://localhost:" + Port + ConfigPath : null;

        /// <summary>What is currently being served.</summary>
        public ServerScenario Scenario
        {
            get { lock (_gate) { return _scenario; } }
        }

        /// <summary>How many requests have been answered, including refusals.</summary>
        public int RequestCount { get; private set; }

        /// <summary>Creates a server. Does not start it.</summary>
        public LocalConfigServer(IAbLog log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Binds the first free port in the scan range and starts accepting. Returns false when no port
        /// could be bound, which the demo shows rather than throwing.
        /// </summary>
        public bool Start()
        {
            if (_running) return true;

            LastError = null;

            for (int offset = 0; offset < PortsToTry; offset++)
            {
                int port = FirstPort + offset;
                var listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:" + port + "/");

                try
                {
                    listener.Start();
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                    try
                    {
                        listener.Close();
                    }
                    catch (Exception)
                    {
                        // Already dead; nothing to salvage.
                    }

                    continue;
                }

                _listener = listener;
                Port = port;
                _running = true;

                _thread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "abtest-config-server"
                };
                _thread.Start();

                _log.Log(AbLogLevel.Info, "local config server listening on " + Url);
                return true;
            }

            _log.Log(AbLogLevel.Warning,
                "no port in " + FirstPort + ".." + (FirstPort + PortsToTry - 1) +
                " could be bound (" + (LastError ?? "unknown") +
                "); the demo will fall back to a file config source");
            return false;
        }

        /// <summary>Stops accepting and releases the port.</summary>
        public void Stop()
        {
            if (!_running) return;

            _running = false;

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Warning, "the config server did not stop cleanly: " + e.Message);
            }

            _listener = null;

            // The accept thread is a background thread and will exit on its own once the listener is
            // closed; joining briefly keeps the log tidy without risking a hang on shutdown.
            try
            {
                _thread?.Join(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                // A thread that will not join within a second is a background thread; let it go.
            }

            _thread = null;
            Port = 0;

            _log.Log(AbLogLevel.Info, "local config server stopped");
        }

        /// <summary>Changes what is served from the next request onwards.</summary>
        /// <remarks>
        /// Bumps the config version for every scenario that produces a readable payload, because the client
        /// treats the version as a payload's identity and refuses content that changes underneath an
        /// unchanged label. Not bumping it would make the demo's scenario buttons look broken for a
        /// completely correct reason.
        /// </remarks>
        public void SetScenario(ServerScenario scenario)
        {
            lock (_gate)
            {
                if (_scenario == scenario) return;
                _scenario = scenario;
                _configVersion++;
            }

            _log.Log(AbLogLevel.Info, "config server now serving: " + Describe(scenario));
        }

        /// <summary>The payload the current scenario would serve, without going through HTTP.</summary>
        /// <remarks>
        /// Used by the file-backed fallback source when no port could be bound, and by tests that want the
        /// scenarios without a socket.
        /// </remarks>
        public string CurrentPayload()
        {
            lock (_gate)
            {
                return PayloadFor(_scenario, _configVersion);
            }
        }

        /// <inheritdoc />
        public void Dispose() => Stop();

        private void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext context;

                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    // Stop() closed the listener out from under us, which is the ordinary way this loop
                    // ends. Anything else is equally unrecoverable from in here.
                    return;
                }

                try
                {
                    Answer(context);
                }
                catch (Exception e)
                {
                    _log.Log(AbLogLevel.Warning, "the config server failed to answer a request: " + e.Message);
                }
            }
        }

        private void Answer(HttpListenerContext context)
        {
            ServerScenario scenario;
            string payload;

            lock (_gate)
            {
                scenario = _scenario;
                payload = PayloadFor(_scenario, _configVersion);
                RequestCount++;
            }

            var response = context.Response;

            try
            {
                if (scenario == ServerScenario.Offline)
                {
                    // A refusal rather than a 500 with a body: the client should experience this as the
                    // server being unreachable, which is a different rung of the ladder from a bad payload.
                    response.StatusCode = 503;
                    response.Close();
                    return;
                }

                if (!string.Equals(context.Request.Url.AbsolutePath, ConfigPath, StringComparison.Ordinal))
                {
                    response.StatusCode = 404;
                    response.Close();
                    return;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(payload);

                response.StatusCode = 200;
                response.ContentType = "application/json";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.Close();
            }
            catch (Exception)
            {
                try
                {
                    response.Abort();
                }
                catch (Exception)
                {
                    // The client hung up mid-write. Nothing to report and nothing to do.
                }
            }
        }

        /// <summary>A one-line description of a scenario, for the status bar.</summary>
        public static string Describe(ServerScenario scenario)
        {
            switch (scenario)
            {
                case ServerScenario.WeightsRamped: return "weights 90/10";
                case ServerScenario.ExperimentPaused: return "offer experiment paused";
                case ServerScenario.KillSwitch: return "kill switch (all stopped)";
                case ServerScenario.MalformedJson: return "malformed JSON";
                case ServerScenario.BadSchemaVersion: return "unsupported schema version";
                case ServerScenario.Offline: return "offline (refusing requests)";
                default: return "normal";
            }
        }

        /// <summary>Builds the payload for a scenario.</summary>
        public static string PayloadFor(ServerScenario scenario, int version)
        {
            switch (scenario)
            {
                case ServerScenario.MalformedJson:
                    return "{\"schemaVersion\": 1, \"configVersion\": \"" + version +
                           "\", \"layers\": [ {\"id\": \"offer_layout\",";

                case ServerScenario.BadSchemaVersion:
                    return Config(version, schemaVersion: 99);

                case ServerScenario.WeightsRamped:
                    return Config(version, offerControlWeight: 9000);

                case ServerScenario.ExperimentPaused:
                    return Config(version, offerStatus: "paused");

                case ServerScenario.KillSwitch:
                    return Config(version, offerStatus: "stopped", pricingStatus: "stopped");

                default:
                    return Config(version);
            }
        }

        private static string Config(
            int version,
            int schemaVersion = 1,
            int offerControlWeight = 5000,
            string offerStatus = "running",
            string pricingStatus = "running")
        {
            var text = new StringBuilder();

            text.Append("{\n");
            text.Append("  \"schemaVersion\": ").Append(schemaVersion).Append(",\n");
            text.Append("  \"configVersion\": \"").Append(version).Append("\",\n");
            text.Append("  \"layers\": [\n");
            text.Append("    { \"id\": \"offer_layout\", \"salt\": \"offer_layout.2026q3\" },\n");
            text.Append("    { \"id\": \"pricing_cta\", \"salt\": \"pricing_cta.2026q3\" }\n");
            text.Append("  ],\n");
            text.Append("  \"experiments\": [\n");

            text.Append("    {\n");
            text.Append("      \"id\": \"exp_offer_layout\",\n");
            text.Append("      \"layer\": \"offer_layout\",\n");
            text.Append("      \"status\": \"").Append(offerStatus).Append("\",\n");
            text.Append("      \"salt\": \"exp_offer_layout.v1\",\n");
            text.Append("      \"allocation\": { \"from\": 0, \"to\": 10000 },\n");
            text.Append("      \"stickiness\": \"sticky_after_exposure\",\n");
            text.Append("      \"variants\": [\n");
            text.Append("        { \"id\": \"control\", \"weight\": ").Append(offerControlWeight)
                .Append(", \"behavior\": \"shop.offer_layout.control\" },\n");
            text.Append("        { \"id\": \"grid_v2\", \"weight\": ").Append(10000 - offerControlWeight)
                .Append(", \"behavior\": \"shop.offer_layout.grid_v2\" }\n");
            text.Append("      ]\n");
            text.Append("    },\n");

            text.Append("    {\n");
            text.Append("      \"id\": \"exp_pricing_cta\",\n");
            text.Append("      \"layer\": \"pricing_cta\",\n");
            text.Append("      \"status\": \"").Append(pricingStatus).Append("\",\n");
            text.Append("      \"salt\": \"exp_pricing_cta.v1\",\n");
            text.Append("      \"allocation\": { \"from\": 0, \"to\": 10000 },\n");
            text.Append("      \"stickiness\": \"sticky_after_exposure\",\n");
            text.Append("      \"variants\": [\n");
            text.Append("        { \"id\": \"control\", \"weight\": 5000, ")
                .Append("\"behavior\": \"shop.pricing_cta.control\" },\n");
            text.Append("        { \"id\": \"urgency\", \"weight\": 5000, ")
                .Append("\"behavior\": \"shop.pricing_cta.urgency\" }\n");
            text.Append("      ]\n");
            text.Append("    }\n");

            text.Append("  ]\n");
            text.Append("}\n");

            return text.ToString();
        }
    }

    /// <summary>Fetches config over HTTP from the local server.</summary>
    /// <remarks>
    /// Blocking by design. <see cref="IConfigSource.Fetch"/> is documented as callable off the main thread,
    /// and the demo honours that: the fetch runs on a worker and the resulting inert payload is handed to
    /// <c>ConfigService.Apply</c> on the player loop, which is the contract Slice 2 wrote down.
    /// </remarks>
    public sealed class HttpConfigSource : Core.Config.IConfigSource
    {
        private readonly Func<string> _url;
        private readonly int _timeoutMilliseconds;

        /// <summary>Creates a source reading from whatever <paramref name="url"/> returns each time.</summary>
        /// <remarks>
        /// A function rather than a string because the port is not known until the server binds, and the
        /// server can be stopped and restarted onto a different port while the demo runs.
        /// </remarks>
        public HttpConfigSource(Func<string> url, int timeoutMilliseconds = 2000)
        {
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _timeoutMilliseconds = timeoutMilliseconds;
        }

        /// <inheritdoc />
        public string Description
        {
            get
            {
                string url = SafeUrl();
                return url ?? "http (server not running)";
            }
        }

        /// <inheritdoc />
        public Core.Config.ConfigFetchResult Fetch()
        {
            string url = SafeUrl();
            if (url == null) return Core.Config.ConfigFetchResult.Unreachable("the local server is not running");

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = _timeoutMilliseconds;
                request.ReadWriteTimeout = _timeoutMilliseconds;
                request.Method = "GET";

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return Core.Config.ConfigFetchResult.Unreachable("HTTP " + (int)response.StatusCode);
                    }

                    using (var stream = response.GetResponseStream())
                    {
                        if (stream == null) return Core.Config.ConfigFetchResult.Unreachable("empty response");

                        using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                        {
                            return Core.Config.ConfigFetchResult.Fetched(reader.ReadToEnd());
                        }
                    }
                }
            }
            catch (WebException e)
            {
                // The offline scenario answers 503, which arrives here. Reporting it as unreachable rather
                // than as a bad payload is the point: it is a different rung of the fallback ladder.
                return Core.Config.ConfigFetchResult.Unreachable(DescribeWebException(e));
            }
            catch (Exception e)
            {
                return Core.Config.ConfigFetchResult.Unreachable(e.GetType().Name + ": " + e.Message);
            }
        }

        private string SafeUrl()
        {
            try
            {
                return _url();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string DescribeWebException(WebException e)
        {
            if (e.Response is HttpWebResponse response)
            {
                return "HTTP " + (int)response.StatusCode + " " + response.StatusDescription;
            }

            return e.Status + ": " + e.Message;
        }
    }

    /// <summary>Serves whatever payload the local server would, without a socket.</summary>
    /// <remarks>
    /// The fallback when no port could be bound. Every scenario button still works and the whole demo still
    /// tells its story - it just does so without HTTP. Having this means a firewall prompt or a locked-down
    /// machine degrades the demo rather than ending it.
    /// </remarks>
    public sealed class DirectConfigSource : Core.Config.IConfigSource
    {
        private readonly LocalConfigServer _server;

        /// <summary>Creates a source reading directly from <paramref name="server"/>.</summary>
        public DirectConfigSource(LocalConfigServer server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        /// <inheritdoc />
        public string Description => "in-process (no socket)";

        /// <inheritdoc />
        public Core.Config.ConfigFetchResult Fetch()
        {
            if (_server.Scenario == ServerScenario.Offline)
            {
                return Core.Config.ConfigFetchResult.Unreachable("offline scenario");
            }

            return Core.Config.ConfigFetchResult.Fetched(_server.CurrentPayload());
        }
    }
}
