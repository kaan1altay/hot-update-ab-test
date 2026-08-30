using System;
using System.Collections.Generic;
using HotUpdateABTest.Core.Config;

namespace HotUpdateABTest.Core.Presentation
{
    /// <summary>The outcome of reading a spec table returned by a behavior.</summary>
    public sealed class SpecReadResult
    {
        /// <summary>The spec to render: the merged result, or the fallback when the table was rejected.</summary>
        public PresentationSpec Spec { get; }

        /// <summary>Everything wrong with the table.</summary>
        public ValidationResult Issues { get; }

        /// <summary>True when the table was accepted whole.</summary>
        public bool IsValid => Issues.IsValid;

        /// <summary>Creates a result.</summary>
        public SpecReadResult(PresentationSpec spec, ValidationResult issues)
        {
            Spec = spec;
            Issues = issues ?? ValidationResult.Ok;
        }
    }

    /// <summary>
    /// Validates the table a Lua behavior returned and merges it onto a baseline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately engine-free and Lua-free: it reads a plain dictionary. The marshalling from a
    /// <c>LuaTable</c> happens in the Unity assembly, which means every validation rule here is covered by
    /// the fast CI suite rather than only by a run that needs the native Lua VM. It also means the rules can
    /// be exercised against inputs no Lua patch could easily produce.
    /// </para>
    /// <para>
    /// <b>Rejection is whole-table.</b> A spec with one bad field is not partially applied - the caller
    /// falls back to control. A half-applied presentation is the visual equivalent of a half-applied
    /// config: a state the framework should never be observable in, and the one most likely to be blamed on
    /// something else.
    /// </para>
    /// <para>
    /// <b>An unrecognised enum value is a rejection, not a fallback to default.</b> If a patch asks for
    /// <c>layout = "carousel"</c> and no carousel was ever drawn, quietly rendering a list would hide a
    /// broken patch behind a plausible screen. The whole point of a closed vocabulary is that the set of
    /// accepted values equals the set of things that exist.
    /// </para>
    /// </remarks>
    public static class PresentationSpecReader
    {
        /// <summary>
        /// Validates <paramref name="table"/> as a partial spec from a behavior owning
        /// <paramref name="group"/>, merged onto <paramref name="baseline"/>.
        /// </summary>
        /// <remarks>
        /// Fields the behavior does not mention keep their baseline value. Fields outside its group are an
        /// error even when the value is otherwise legal - a pricing behavior must not be able to change the
        /// layout, or the two layers would not be independent and one experiment would be measuring the
        /// other's effect.
        /// </remarks>
        public static SpecReadResult Read(
            IReadOnlyDictionary<string, object> table,
            SpecFieldGroup group,
            PresentationSpec baseline,
            string entity = null)
        {
            entity = entity ?? "spec";
            var issues = new ValidationBuilder();

            if (table == null)
            {
                issues.Error("spec.null", entity, "the behavior returned nothing; a behavior must return a table");
                return new SpecReadResult(baseline, issues.Build());
            }

            var spec = baseline;

            foreach (var pair in table)
            {
                string field = pair.Key;
                var owner = SpecFields.GroupOf(field);

                if (owner == null)
                {
                    issues.Error("spec.unknownField", entity,
                        "field '" + field + "' is not part of the presentation spec; the spec is a closed " +
                        "set (" + string.Join(", ", SpecFields.Names) + ") because a hot update may only " +
                        "choose among presentations the screen can already render");
                    continue;
                }

                if (owner.Value != group)
                {
                    issues.Error("spec.foreignField", entity,
                        "field '" + field + "' belongs to the " + owner.Value.ToString().ToLowerInvariant() +
                        " group, but this behavior owns the " + group.ToString().ToLowerInvariant() +
                        " group; layers must not be able to overwrite each other");
                    continue;
                }

                switch (field)
                {
                    case SpecFields.Layout:
                        if (TryReadLayout(pair.Value, entity, issues, out var layout)) spec = spec.WithLayout(layout);
                        break;

                    case SpecFields.PriceStyle:
                        if (TryReadPriceStyle(pair.Value, entity, issues, out var priceStyle))
                        {
                            spec = spec.WithPriceStyle(priceStyle);
                        }

                        break;

                    case SpecFields.BadgeText:
                        if (TryReadText(pair.Value, field, PresentationSpec.MaxBadgeLength, entity, issues,
                                allowNull: true, out string badge))
                        {
                            spec = spec.WithBadgeText(badge);
                        }

                        break;

                    case SpecFields.CtaText:
                        if (TryReadText(pair.Value, field, PresentationSpec.MaxCtaLength, entity, issues,
                                allowNull: false, out string cta))
                        {
                            spec = spec.WithCtaText(cta);
                        }

                        break;
                }
            }

            var result = issues.Build();
            return new SpecReadResult(result.IsValid ? spec : baseline, result);
        }

        private static bool TryReadLayout(object value, string entity, ValidationBuilder issues, out OfferLayout layout)
        {
            layout = OfferLayout.List;

            if (!(value is string text))
            {
                issues.Error("spec.layout.notAString", entity,
                    "field 'layout' expected a string, found " + Describe(value));
                return false;
            }

            switch (text)
            {
                case "list": layout = OfferLayout.List; return true;
                case "grid": layout = OfferLayout.Grid; return true;
                default:
                    issues.Error("spec.layout.unknown", entity,
                        "layout '" + text + "' is not one the screen can render; the only arrangements " +
                        "authored are 'list' and 'grid'");
                    return false;
            }
        }

        private static bool TryReadPriceStyle(
            object value, string entity, ValidationBuilder issues, out PriceStyle style)
        {
            style = PriceStyle.Plain;

            if (!(value is string text))
            {
                issues.Error("spec.priceStyle.notAString", entity,
                    "field 'priceStyle' expected a string, found " + Describe(value));
                return false;
            }

            switch (text)
            {
                case "plain": style = PriceStyle.Plain; return true;
                case "discounted": style = PriceStyle.Discounted; return true;
                default:
                    issues.Error("spec.priceStyle.unknown", entity,
                        "price style '" + text + "' is not one the screen can render; the only presentations " +
                        "authored are 'plain' and 'discounted'");
                    return false;
            }
        }

        private static bool TryReadText(
            object value,
            string field,
            int maxLength,
            string entity,
            ValidationBuilder issues,
            bool allowNull,
            out string text)
        {
            text = null;

            if (value == null)
            {
                if (allowNull) return true;

                issues.Error("spec." + field + ".null", entity,
                    "field '" + field + "' may not be nil");
                return false;
            }

            if (!(value is string raw))
            {
                issues.Error("spec." + field + ".notAString", entity,
                    "field '" + field + "' expected a string, found " + Describe(value));
                return false;
            }

            if (raw.Length > maxLength)
            {
                // Rejected rather than truncated. Silently clipping copy produces a screen that looks
                // deliberate and reads as nonsense, and the patch author never finds out.
                issues.Error("spec." + field + ".tooLong", entity,
                    "field '" + field + "' is " + raw.Length + " characters; the screen has room for " +
                    maxLength);
                return false;
            }

            if (!allowNull && raw.Length == 0)
            {
                issues.Error("spec." + field + ".empty", entity,
                    "field '" + field + "' may not be empty");
                return false;
            }

            text = raw;
            return true;
        }

        private static string Describe(object value)
        {
            if (value == null) return "nil";
            if (value is string s) return "the string '" + s + "'";
            if (value is bool b) return "the boolean " + (b ? "true" : "false");
            if (value is double || value is long || value is int) return "the number " + value;
            return "a " + value.GetType().Name;
        }
    }
}
