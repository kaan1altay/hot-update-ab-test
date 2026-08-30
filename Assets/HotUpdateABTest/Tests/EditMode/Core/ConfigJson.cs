using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Builds config payloads as text, so the config tests exercise the real reader rather than
    /// hand-constructed model objects.
    /// </summary>
    /// <remarks>
    /// Every rule in this slice is about what the framework does with a payload that arrived from
    /// somewhere else, so the tests start from JSON. Building the model directly would skip the reader,
    /// which is precisely the component that has to tell an absent field from a zero.
    /// </remarks>
    internal sealed class ConfigJson
    {
        private readonly List<string> _layers = new List<string>();
        private readonly List<string> _experiments = new List<string>();

        public int SchemaVersion { get; set; } = 1;

        public string Version { get; set; } = "1";

        public static ConfigJson New(string version = "1") => new ConfigJson { Version = version };

        /// <summary>The two-layer shape the demo uses, with both experiments running and split evenly.</summary>
        public static ConfigJson Demo(string version = "1")
        {
            return New(version)
                .Layer("offer_layout")
                .Layer("pricing_cta")
                .Experiment("exp_offer_layout", "offer_layout")
                .Experiment("exp_pricing_cta", "pricing_cta");
        }

        public ConfigJson Layer(string id, string salt = null)
        {
            _layers.Add("{\"id\":\"" + id + "\",\"salt\":\"" + (salt ?? id + ".salt.v1") + "\"}");
            return this;
        }

        /// <summary>Adds a layer entry verbatim, for testing malformed input.</summary>
        public ConfigJson RawLayer(string json)
        {
            _layers.Add(json);
            return this;
        }

        /// <summary>Adds an experiment entry verbatim, for testing malformed input.</summary>
        public ConfigJson RawExperiment(string json)
        {
            _experiments.Add(json);
            return this;
        }

        public ConfigJson Experiment(
            string id,
            string layerId,
            string status = "running",
            int from = 0,
            int to = 10000,
            string stickiness = "sticky_after_exposure",
            IEnumerable<string> variants = null,
            string audience = null,
            string salt = null)
        {
            var text = new StringBuilder();
            text.Append("{\"id\":\"").Append(id).Append("\"");
            text.Append(",\"layer\":\"").Append(layerId).Append("\"");
            text.Append(",\"status\":\"").Append(status).Append("\"");
            text.Append(",\"salt\":\"").Append(salt ?? id + ".salt.v1").Append("\"");
            text.Append(",\"allocation\":{\"from\":").Append(from).Append(",\"to\":").Append(to).Append("}");
            if (stickiness != null) text.Append(",\"stickiness\":\"").Append(stickiness).Append("\"");
            if (audience != null) text.Append(",\"audience\":").Append(audience);

            text.Append(",\"variants\":[");
            bool first = true;
            foreach (string variant in variants ?? DefaultVariants())
            {
                if (!first) text.Append(',');
                text.Append(variant);
                first = false;
            }

            text.Append("]}");

            _experiments.Add(text.ToString());
            return this;
        }

        /// <summary>A variant entry.</summary>
        public static string Variant(string id, int weight, string behavior = null) =>
            "{\"id\":\"" + id + "\",\"weight\":" + weight +
            ",\"behavior\":\"" + (behavior ?? "shop." + id) + "\"}";

        /// <summary>A variant entry with no <c>weight</c> field at all.</summary>
        public static string VariantWithoutWeight(string id) =>
            "{\"id\":\"" + id + "\",\"behavior\":\"shop." + id + "\"}";

        private static IEnumerable<string> DefaultVariants()
        {
            yield return Variant("control", 5000);
            yield return Variant("treatment", 5000);
        }

        public override string ToString()
        {
            return "{\"schemaVersion\":" + SchemaVersion +
                   ",\"configVersion\":\"" + Version + "\"" +
                   ",\"layers\":[" + string.Join(",", _layers.ToArray()) + "]" +
                   ",\"experiments\":[" + string.Join(",", _experiments.ToArray()) + "]}";
        }

        public string Build() => ToString();
    }

    /// <summary>
    /// Finds files that live in the repository, from either compilation.
    /// </summary>
    /// <remarks>
    /// The core tests are compiled twice, and the two runs sit in very different places. Under
    /// <c>dotnet test</c> the assembly is inside the repository; under Unity it is in
    /// <c>Library/ScriptAssemblies</c> while <c>AppContext.BaseDirectory</c> points at the Editor
    /// installation entirely outside the project. Trying several starting points and walking up until the
    /// project markers appear works in both, which is what makes it possible to test a shipped artifact by
    /// reading the real file rather than a copy pasted into a string.
    /// </remarks>
    internal static class RepoPaths
    {
        /// <summary>The repository root.</summary>
        public static string Root { get; } = FindRoot();

        /// <summary>Resolves a repository-relative path.</summary>
        public static string Resolve(string relativePath)
        {
            if (Root == null) return null;
            return Path.GetFullPath(Path.Combine(Root, relativePath));
        }

        private static string FindRoot()
        {
            // Three starting points, because the two compilations sit in very different places. Under
            // `dotnet test` the assembly lives in bin/Debug/net9.0 inside the repository, so walking up
            // from it works. Under Unity, AppContext.BaseDirectory is the *Editor installation*, not the
            // project - that one only resolves via the current directory or the assembly under
            // Library/ScriptAssemblies.
            foreach (string start in StartingPoints())
            {
                if (string.IsNullOrEmpty(start)) continue;

                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    if (Directory.Exists(Path.Combine(directory.FullName, "Assets")) &&
                        Directory.Exists(Path.Combine(directory.FullName, "ProjectSettings")))
                    {
                        return directory.FullName;
                    }

                    directory = directory.Parent;
                }
            }

            return null;
        }

        private static IEnumerable<string> StartingPoints()
        {
            yield return Directory.GetCurrentDirectory();

            string assembly = typeof(RepoPaths).Assembly.Location;
            yield return string.IsNullOrEmpty(assembly) ? null : Path.GetDirectoryName(assembly);

            yield return AppContext.BaseDirectory;
        }
    }
}
