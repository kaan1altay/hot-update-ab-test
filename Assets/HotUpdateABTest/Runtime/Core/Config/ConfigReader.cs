using System;
using System.Collections.Generic;
using System.Globalization;
using HotUpdateABTest.Core.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HotUpdateABTest.Core.Config
{
    /// <summary>The outcome of reading a payload: either a config, or the reasons there is not one.</summary>
    public sealed class ConfigReadResult
    {
        /// <summary>The parsed config, or null when <see cref="Issues"/> contains an error.</summary>
        public ExperimentConfig Config { get; }

        /// <summary>Everything found wrong, plus any warnings.</summary>
        public ValidationResult Issues { get; }

        /// <summary>True when a config was produced.</summary>
        public bool IsValid => Config != null && Issues.IsValid;

        /// <summary>Creates a result.</summary>
        public ConfigReadResult(ExperimentConfig config, ValidationResult issues)
        {
            Config = config;
            Issues = issues ?? ValidationResult.Ok;
        }
    }

    /// <summary>
    /// Turns a JSON payload into an <see cref="ExperimentConfig"/>, or into a precise list of reasons why
    /// it could not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading is done field by field against a <see cref="JObject"/> rather than by attribute-driven
    /// deserialization, for one reason: a serializer that maps fields automatically cannot tell an absent
    /// field from a field holding the type's default. For an experiment framework that distinction is not
    /// pedantry. A variant whose <c>weight</c> was forgotten and a variant whose <c>weight</c> is
    /// deliberately <c>0</c> mean opposite things - one is a config bug that should reject the payload,
    /// the other is a retired arm that should be honoured - and a reader that silently turns both into
    /// zero will apply the bug.
    /// </para>
    /// <para>
    /// The schema version gate runs first and short-circuits. If the payload announces a shape this build
    /// does not understand, every other complaint would be noise about a contract that does not apply, and
    /// worse, might read as though the operator's config were wrong when in fact the client is old.
    /// </para>
    /// <para>
    /// Everything after that gate is collected rather than thrown. An operator who has broken three things
    /// should learn all three from one refresh.
    /// </para>
    /// <para>
    /// Unknown fields are ignored rather than rejected. Within a schema version the server is allowed to
    /// add fields that older clients do not know about - that is most of the value of having a version
    /// number at all - and a client that refused them would turn every additive server change into a
    /// forced app update. The strictness is spent on the fields that are declared, where it buys the
    /// absent-versus-zero distinction; it is not spent on forbidding the ones that are not.
    /// </para>
    /// </remarks>
    public static class ConfigReader
    {
        /// <summary>Reads a payload. Never throws; malformed input comes back as errors.</summary>
        public static ConfigReadResult Read(string json)
        {
            var issues = new ValidationBuilder();

            if (string.IsNullOrWhiteSpace(json))
            {
                issues.Error("payload.empty", "payload", "the payload is empty");
                return new ConfigReadResult(null, issues.Build());
            }

            JObject root;
            try
            {
                // DateParseHandling.None keeps anything that looks like a date as the string it actually
                // was; the config has no date fields, and silent coercion would only ever surprise.
                using (var reader = new JsonTextReader(new System.IO.StringReader(json)))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    root = JObject.Load(reader);
                }
            }
            catch (JsonException e)
            {
                issues.Error("payload.malformed", "payload", "not valid JSON (" + FirstLine(e.Message) + ")");
                return new ConfigReadResult(null, issues.Build());
            }

            // --- schema gate, before anything else is trusted -----------------------------------------

            if (!TryReadInt(root, "schemaVersion", "payload", issues, out int schemaVersion))
            {
                return new ConfigReadResult(null, issues.Build());
            }

            if (schemaVersion != ExperimentConfig.SupportedSchemaVersion)
            {
                issues.Error(
                    "payload.schemaVersion.unsupported",
                    "payload",
                    "schema version " + schemaVersion + " is not supported by this build, which understands " +
                    ExperimentConfig.SupportedSchemaVersion + " - the whole payload is rejected rather than " +
                    "guessed at");
                return new ConfigReadResult(null, issues.Build());
            }

            // --- the rest is collected --------------------------------------------------------------

            string configVersion = ReadRequiredString(root, "configVersion", "payload", issues);
            var layers = ReadLayers(root, issues);
            var experiments = ReadExperiments(root, issues);

            var result = issues.Build();
            if (!result.IsValid) return new ConfigReadResult(null, result);

            var config = new ExperimentConfig(schemaVersion, configVersion, layers, experiments);
            return new ConfigReadResult(config, result);
        }

        private static List<LayerDef> ReadLayers(JObject root, ValidationBuilder issues)
        {
            var layers = new List<LayerDef>();

            if (!TryReadArray(root, "layers", "payload", issues, required: true, out JArray array)) return layers;

            for (int i = 0; i < array.Count; i++)
            {
                var token = array[i];
                string entity = "layer #" + i;

                if (!(token is JObject item))
                {
                    issues.Error("layer.notAnObject", entity, "expected an object, found " + Describe(token));
                    continue;
                }

                string id = ReadRequiredString(item, "id", entity, issues);
                if (id != null) entity = "layer '" + id + "'";

                string salt = ReadRequiredString(item, "salt", entity, issues);

                if (id == null || salt == null) continue;
                layers.Add(new LayerDef(id, salt));
            }

            return layers;
        }

        private static List<ExperimentDef> ReadExperiments(JObject root, ValidationBuilder issues)
        {
            var experiments = new List<ExperimentDef>();

            if (!TryReadArray(root, "experiments", "payload", issues, required: true, out JArray array))
            {
                return experiments;
            }

            for (int i = 0; i < array.Count; i++)
            {
                var token = array[i];
                string entity = "experiment #" + i;

                if (!(token is JObject item))
                {
                    issues.Error("experiment.notAnObject", entity, "expected an object, found " + Describe(token));
                    continue;
                }

                string id = ReadRequiredString(item, "id", entity, issues);
                if (id != null) entity = "experiment '" + id + "'";

                string layerId = ReadRequiredString(item, "layer", entity, issues);
                string salt = ReadRequiredString(item, "salt", entity, issues);

                bool statusOk = TryReadEnum(item, "status", entity, issues, out ExperimentStatus status);
                bool allocationOk = TryReadAllocation(item, entity, issues, out BucketRange allocation);
                bool stickinessOk = TryReadStickiness(item, entity, issues, out StickinessPolicy stickiness);
                var audience = ReadAudience(item, entity, issues);
                var variants = ReadVariants(item, entity, issues);

                if (id == null || layerId == null || salt == null || !statusOk || !allocationOk || !stickinessOk)
                {
                    continue;
                }

                experiments.Add(new ExperimentDef(
                    id, layerId, status, salt, allocation, stickiness, variants, audience));
            }

            return experiments;
        }

        private static List<VariantDef> ReadVariants(JObject item, string experimentEntity, ValidationBuilder issues)
        {
            var variants = new List<VariantDef>();

            if (!TryReadArray(item, "variants", experimentEntity, issues, required: true, out JArray array))
            {
                return variants;
            }

            for (int i = 0; i < array.Count; i++)
            {
                var token = array[i];
                string entity = experimentEntity + " > variant #" + i;

                if (!(token is JObject variantItem))
                {
                    issues.Error("variant.notAnObject", entity, "expected an object, found " + Describe(token));
                    continue;
                }

                string id = ReadRequiredString(variantItem, "id", entity, issues);
                if (id != null) entity = experimentEntity + " > variant '" + id + "'";

                // The distinction this whole reader exists for. A missing weight is a config bug; a weight
                // of zero is a retired arm the operator meant to keep declared.
                bool weightOk = TryReadInt(variantItem, "weight", entity, issues, out int weight,
                    absentDetail: "field 'weight' is missing (absent is not the same as 0; declare 0 " +
                                  "explicitly to retire an arm while keeping it in the config)");

                string behavior = ReadRequiredString(variantItem, "behavior", entity, issues);

                if (weightOk && weight < 0)
                {
                    issues.Error("variant.weight.negative", entity,
                        "weight " + weight + " is negative; use 0 to retire an arm");
                    weightOk = false;
                }

                if (id == null || !weightOk || behavior == null) continue;
                variants.Add(new VariantDef(id, weight, behavior));
            }

            return variants;
        }

        private static AudienceSpec ReadAudience(JObject item, string entity, ValidationBuilder issues)
        {
            if (!item.TryGetValue("audience", out JToken token) || token.Type == JTokenType.Null)
            {
                return AudienceSpec.Everyone;
            }

            if (!(token is JObject audience))
            {
                issues.Error("audience.notAnObject", entity,
                    "field 'audience' expected an object, found " + Describe(token));
                return AudienceSpec.Everyone;
            }

            int? minAccountLevel = null;
            if (audience.TryGetValue("minAccountLevel", out JToken levelToken) &&
                levelToken.Type != JTokenType.Null)
            {
                if (levelToken.Type == JTokenType.Integer)
                {
                    minAccountLevel = levelToken.Value<int>();
                }
                else
                {
                    issues.Error("audience.minAccountLevel.notAnInteger", entity,
                        "field 'audience.minAccountLevel' expected an integer, found " + Describe(levelToken));
                }
            }

            var platforms = ReadStringArrayOrNull(audience, "platforms", entity + " > audience", issues);
            var countries = ReadStringArrayOrNull(audience, "countries", entity + " > audience", issues);

            string predicate = null;
            if (audience.TryGetValue("predicate", out JToken predicateToken) &&
                predicateToken.Type != JTokenType.Null)
            {
                if (predicateToken.Type == JTokenType.String)
                {
                    predicate = predicateToken.Value<string>();
                }
                else
                {
                    issues.Error("audience.predicate.notAString", entity,
                        "field 'audience.predicate' expected a string naming a Lua predicate, found " +
                        Describe(predicateToken));
                }
            }

            return new AudienceSpec(minAccountLevel, platforms, countries, predicate);
        }

        private static List<string> ReadStringArrayOrNull(
            JObject item, string field, string entity, ValidationBuilder issues)
        {
            if (!item.TryGetValue(field, out JToken token) || token.Type == JTokenType.Null) return null;

            if (!(token is JArray array))
            {
                issues.Error("field.notAnArray", entity,
                    "field '" + field + "' expected an array, found " + Describe(token));
                return null;
            }

            var values = new List<string>();
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i].Type == JTokenType.String)
                {
                    values.Add(array[i].Value<string>());
                }
                else
                {
                    issues.Error("field.notAString", entity,
                        "field '" + field + "[" + i + "]' expected a string, found " + Describe(array[i]));
                }
            }

            // An empty array is not the same as an absent one: absent means "do not filter", empty means
            // "filter, and allow nothing". The latter is almost certainly a mistake, so it is called out.
            if (values.Count == 0)
            {
                issues.Warning("field.emptyArray", entity,
                    "field '" + field + "' is an empty list, which excludes every user; omit the field " +
                    "entirely to match everyone");
            }

            return values;
        }

        private static bool TryReadAllocation(JObject item, string entity, ValidationBuilder issues, out BucketRange range)
        {
            range = BucketRange.Empty;

            if (!item.TryGetValue("allocation", out JToken token) || token.Type == JTokenType.Null)
            {
                issues.Error("experiment.allocation.missing", entity,
                    "field 'allocation' is missing; it is required because it is what makes experiments in " +
                    "one layer mutually exclusive");
                return false;
            }

            if (!(token is JObject allocation))
            {
                issues.Error("experiment.allocation.notAnObject", entity,
                    "field 'allocation' expected an object with 'from' and 'to', found " + Describe(token));
                return false;
            }

            bool fromOk = TryReadInt(allocation, "from", entity + " > allocation", issues, out int from);
            bool toOk = TryReadInt(allocation, "to", entity + " > allocation", issues, out int to);

            if (!fromOk || !toOk) return false;

            range = new BucketRange(from, to);
            return true;
        }

        private static bool TryReadStickiness(
            JObject item, string entity, ValidationBuilder issues, out StickinessPolicy policy)
        {
            // Absent means the safe default. Sticky-after-exposure is the one that protects the analysis,
            // so a config that forgets the field gets protection rather than a reshuffle.
            policy = StickinessPolicy.StickyAfterExposure;

            if (!item.TryGetValue("stickiness", out JToken token) || token.Type == JTokenType.Null) return true;

            if (token.Type != JTokenType.String)
            {
                issues.Error("experiment.stickiness.notAString", entity,
                    "field 'stickiness' expected a string, found " + Describe(token));
                return false;
            }

            string raw = token.Value<string>();
            switch (raw)
            {
                case "sticky_after_exposure":
                    policy = StickinessPolicy.StickyAfterExposure;
                    return true;
                case "stateless":
                    policy = StickinessPolicy.Stateless;
                    return true;
                default:
                    issues.Error("experiment.stickiness.unknown", entity,
                        "stickiness '" + raw + "' is not recognised; expected 'sticky_after_exposure' or " +
                        "'stateless'");
                    return false;
            }
        }

        private static bool TryReadEnum(
            JObject item, string field, string entity, ValidationBuilder issues, out ExperimentStatus status)
        {
            status = ExperimentStatus.Draft;

            if (!item.TryGetValue(field, out JToken token) || token.Type == JTokenType.Null)
            {
                issues.Error("experiment.status.missing", entity,
                    "field 'status' is missing; it must be stated explicitly, because defaulting it either " +
                    "way silently starts or stops an experiment");
                return false;
            }

            if (token.Type != JTokenType.String)
            {
                issues.Error("experiment.status.notAString", entity,
                    "field 'status' expected a string, found " + Describe(token));
                return false;
            }

            string raw = token.Value<string>();
            switch (raw)
            {
                case "draft": status = ExperimentStatus.Draft; return true;
                case "running": status = ExperimentStatus.Running; return true;
                case "paused": status = ExperimentStatus.Paused; return true;
                case "stopped": status = ExperimentStatus.Stopped; return true;
                default:
                    issues.Error("experiment.status.unknown", entity,
                        "status '" + raw + "' is not recognised; expected one of draft, running, paused, " +
                        "stopped");
                    return false;
            }
        }

        private static bool TryReadArray(
            JObject item, string field, string entity, ValidationBuilder issues, bool required, out JArray array)
        {
            array = null;

            if (!item.TryGetValue(field, out JToken token) || token.Type == JTokenType.Null)
            {
                if (required)
                {
                    issues.Error("field.missing", entity, "field '" + field + "' is missing");
                }

                return false;
            }

            array = token as JArray;
            if (array != null) return true;

            issues.Error("field.notAnArray", entity,
                "field '" + field + "' expected an array, found " + Describe(token));
            return false;
        }

        private static string ReadRequiredString(JObject item, string field, string entity, ValidationBuilder issues)
        {
            if (!item.TryGetValue(field, out JToken token) || token.Type == JTokenType.Null)
            {
                issues.Error("field.missing", entity, "field '" + field + "' is missing");
                return null;
            }

            if (token.Type != JTokenType.String)
            {
                issues.Error("field.notAString", entity,
                    "field '" + field + "' expected a string, found " + Describe(token));
                return null;
            }

            string value = token.Value<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                issues.Error("field.blank", entity, "field '" + field + "' is blank");
                return null;
            }

            return value;
        }

        private static bool TryReadInt(
            JObject item,
            string field,
            string entity,
            ValidationBuilder issues,
            out int value,
            string absentDetail = null)
        {
            value = 0;

            if (!item.TryGetValue(field, out JToken token) || token.Type == JTokenType.Null)
            {
                issues.Error("field.missing", entity, absentDetail ?? "field '" + field + "' is missing");
                return false;
            }

            if (token.Type != JTokenType.Integer)
            {
                issues.Error("field.notAnInteger", entity,
                    "field '" + field + "' expected an integer, found " + Describe(token));
                return false;
            }

            try
            {
                value = token.Value<int>();
                return true;
            }
            catch (OverflowException)
            {
                issues.Error("field.outOfRange", entity,
                    "field '" + field + "' does not fit in a 32-bit integer");
                return false;
            }
        }

        /// <summary>A short, human-readable description of what was actually found.</summary>
        private static string Describe(JToken token)
        {
            if (token == null) return "nothing";

            switch (token.Type)
            {
                case JTokenType.String: return "the string '" + token.Value<string>() + "'";
                case JTokenType.Integer: return "the integer " + token.ToString(Formatting.None);
                case JTokenType.Float: return "the number " + token.ToString(Formatting.None) +
                                             " (an integer was expected, not a fraction)";
                case JTokenType.Boolean: return "the boolean " +
                                                token.Value<bool>().ToString(CultureInfo.InvariantCulture).ToLowerInvariant();
                case JTokenType.Array: return "an array";
                case JTokenType.Object: return "an object";
                case JTokenType.Null: return "null";
                default: return token.Type.ToString().ToLowerInvariant();
            }
        }

        private static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return "no detail";
            int newline = message.IndexOf('\n');
            return newline < 0 ? message : message.Substring(0, newline).TrimEnd('\r');
        }
    }
}
