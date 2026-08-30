using System;

namespace HotUpdateABTest.Core.Config
{
    /// <summary>What happened when a source was asked for the current payload.</summary>
    public enum ConfigFetchOutcome
    {
        /// <summary>A payload came back. It may still turn out to be rubbish; that is not the source's job.</summary>
        Fetched,

        /// <summary>The source knows nothing has changed and did not transfer a payload.</summary>
        NotModified,

        /// <summary>The source could not be reached, or answered with something that is not a payload.</summary>
        Unreachable
    }

    /// <summary>The result of one fetch.</summary>
    public sealed class ConfigFetchResult
    {
        /// <summary>What happened.</summary>
        public ConfigFetchOutcome Outcome { get; }

        /// <summary>The raw payload text, when one was transferred.</summary>
        public string Payload { get; }

        /// <summary>Why the fetch failed, when it did. Goes into the log-once key.</summary>
        public string Error { get; }

        private ConfigFetchResult(ConfigFetchOutcome outcome, string payload, string error)
        {
            Outcome = outcome;
            Payload = payload;
            Error = error;
        }

        /// <summary>A payload came back.</summary>
        public static ConfigFetchResult Fetched(string payload) =>
            new ConfigFetchResult(ConfigFetchOutcome.Fetched, payload ?? string.Empty, null);

        /// <summary>Nothing has changed since the last fetch.</summary>
        public static ConfigFetchResult NotModified() =>
            new ConfigFetchResult(ConfigFetchOutcome.NotModified, null, null);

        /// <summary>The source could not be reached.</summary>
        public static ConfigFetchResult Unreachable(string error) =>
            new ConfigFetchResult(ConfigFetchOutcome.Unreachable, null, error ?? "unspecified transport failure");
    }

    /// <summary>
    /// Somewhere a config payload can be obtained from: an HTTP endpoint, a file, or a value held in
    /// memory by a test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The abstraction exists so the automated suite never depends on a live socket. The transport that
    /// makes the demo interesting - a local HTTP server whose responses can be mutated live - is one
    /// implementation among several, and every rule about validation, fallback and kill switches is tested
    /// against an in-memory source that cannot flake.
    /// </para>
    /// <para>
    /// <b>Threading.</b> <see cref="Fetch"/> is allowed to block and is expected to be called off the main
    /// thread by transports that do real I/O. It must not touch anything the main thread owns. The result
    /// it returns is an inert value, and handing that value to <see cref="ConfigService.Apply"/> is what
    /// re-enters the framework proper.
    /// </para>
    /// </remarks>
    public interface IConfigSource
    {
        /// <summary>A short label for logs and the status panel, for example <c>http://localhost:8757</c>.</summary>
        string Description { get; }

        /// <summary>Asks for the current payload. May block. Must not throw.</summary>
        ConfigFetchResult Fetch();
    }

    /// <summary>A source that hands back whatever it was last given. The default source for tests.</summary>
    public sealed class InMemoryConfigSource : IConfigSource
    {
        private string _payload;
        private string _error;
        private ConfigFetchOutcome _outcome;

        /// <summary>How many times <see cref="Fetch"/> has been called.</summary>
        public int FetchCount { get; private set; }

        /// <summary>Creates a source serving <paramref name="payload"/>, or unreachable when null.</summary>
        public InMemoryConfigSource(string payload = null)
        {
            if (payload == null) GoOffline("no payload configured");
            else Serve(payload);
        }

        /// <inheritdoc />
        public string Description => "in-memory";

        /// <summary>Serves <paramref name="payload"/> from the next fetch onwards.</summary>
        public void Serve(string payload)
        {
            _payload = payload ?? throw new ArgumentNullException(nameof(payload));
            _error = null;
            _outcome = ConfigFetchOutcome.Fetched;
        }

        /// <summary>Answers <see cref="ConfigFetchOutcome.NotModified"/> from the next fetch onwards.</summary>
        public void ServeNotModified()
        {
            _outcome = ConfigFetchOutcome.NotModified;
        }

        /// <summary>Fails every fetch from now on with <paramref name="error"/>.</summary>
        public void GoOffline(string error = "connection refused")
        {
            _error = error;
            _outcome = ConfigFetchOutcome.Unreachable;
        }

        /// <inheritdoc />
        public ConfigFetchResult Fetch()
        {
            FetchCount++;

            switch (_outcome)
            {
                case ConfigFetchOutcome.Fetched: return ConfigFetchResult.Fetched(_payload);
                case ConfigFetchOutcome.NotModified: return ConfigFetchResult.NotModified();
                default: return ConfigFetchResult.Unreachable(_error);
            }
        }
    }
}
