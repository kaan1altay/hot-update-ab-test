using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace HotUpdateABTest.Tests.Unity
{
    /// <summary>
    /// Asserts that every colour a status surface can select is readable on the ground it sits on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fourth binding hazard in this package, and the only one where the code was right the whole
    /// time. A patch failure was emitted at the correct severity with the correct text and could not be
    /// read: the <c>err</c> page coloured its title <c>#b20000</c> with a black stroke on the console's
    /// <c>#00001e</c>, which measures 2.84 : 1 against a readability floor of 4.5 : 1.
    /// </para>
    /// <para>
    /// It had been that way since the package was authored and had never been seen, because nothing ever
    /// selected that page. Raising the severity from <c>Warning</c> to <c>Error</c> - a correct fix - moved
    /// the message from a 19.65 : 1 colour to a 2.84 : 1 one and made it disappear. A dead branch is not
    /// evidence of correctness, only evidence that nobody has been down it yet.
    /// </para>
    /// <para>
    /// Read from the authoring source rather than the published bytes, deliberately: this is a rule about
    /// what may be authored, it should fail the moment someone picks a colour rather than at the next
    /// publish, and it must not go quiet just because the package has not been republished yet.
    /// </para>
    /// <para>
    /// It covers <c>SrmLight</c> as well as <c>LogRow</c>, and that was added late. The fixture passed
    /// through a ratio light whose alarm state was <c>#660000</c> at 1.54 : 1 - worse than the log colour
    /// it had been written for - because it only ever read one component. A fixture that is green because
    /// it never looked is the failure this repository documents most often; adding a component here is
    /// cheap, and every authored colour that means something belongs in it.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class AuthoredContrastTests
    {
        /// <summary>WCAG AA for body text, and the number this repository argues from.</summary>
        private const double ContrastFloor = 4.5;

        /// <summary>
        /// The floor for a solid indicator rather than text: WCAG AA for non-text contrast.
        /// </summary>
        /// <remarks>
        /// Lower than the text floor because a 28x28 block of colour is far easier to see than a glyph
        /// stroke, and because <c>unknown</c> is deliberately dim - a state meaning "no measurement yet"
        /// should not shout. It is still a floor: the alarm state sat at 1.54 : 1 and was invisible.
        /// </remarks>
        private const double LightFloor = 3.0;

        private static string AuthoringRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "FGUIProject", "assets", "AbTestDemo"));

        [Test]
        public void EveryLogSeverityColourIsReadableOnTheConsoleBackground()
        {
            string background = ConsoleBackground();
            var colours = LogRowTitleColours();

            Assert.That(colours.Count, Is.EqualTo(3),
                "LogRow's title should carry one colour per severity page; found " + colours.Count);

            string[] pages = { "log", "warn", "err" };
            for (int i = 0; i < colours.Count; i++)
            {
                double ratio = Contrast(colours[i], background);

                Assert.That(ratio, Is.GreaterThanOrEqualTo(ContrastFloor),
                    "the '" + pages[i] + "' page is " + colours[i] + " on " + background +
                    ", which measures " + ratio.ToString("F2") + " : 1 against a floor of " +
                    ContrastFloor + " : 1. A row nobody can read is the same as a row nobody wrote.");
            }
        }

        [Test]
        public void TheErrorColourIsTheOneThatWasFixed()
        {
            // Pinned by value as well as by ratio, so the history stays legible: this is the colour that
            // replaced #b20000, and #b20000 measured 2.84 : 1.
            var colours = LogRowTitleColours();

            Assert.That(colours[2].ToLowerInvariant(), Is.Not.EqualTo("#b20000"),
                "the unreadable original is back");
            Assert.That(Contrast("#b20000", ConsoleBackground()), Is.LessThan(ContrastFloor),
                "the original really was below the floor, which is why this fixture exists");
        }

        [Test]
        public void EveryRatioLightStateIsReadableOnTheConsoleBackground()
        {
            // Added after the fixture went green through an SrmLight whose alarm page was #660000 on
            // #00001e - 1.54 : 1, worse than the log colour this fixture was written for. It passed
            // because it only ever read LogRow, which is the failure this repository keeps documenting: a
            // suite that is green because it never looked.
            string background = ConsoleBackground();
            var states = RatioLightColours();

            Assert.That(states.Count, Is.EqualTo(4),
                "SrmLight should carry one colour per state; found " + states.Count);

            string[] pages = { "unknown", "healthy", "warn", "alarm" };
            for (int i = 0; i < states.Count; i++)
            {
                double ratio = Contrast(states[i], background);

                Assert.That(ratio, Is.GreaterThanOrEqualTo(LightFloor),
                    "the '" + pages[i] + "' state is " + states[i] + " on " + background +
                    ", which measures " + ratio.ToString("F2") + " : 1 against a floor of " +
                    LightFloor + " : 1 for a solid indicator");
            }
        }

        [Test]
        public void TheAlarmStateIsTheOneThatWasFixed()
        {
            var states = RatioLightColours();

            Assert.That(states[3].ToLowerInvariant(), Is.Not.EqualTo("#660000"),
                "the unreadable original is back");
            Assert.That(Contrast("#660000", ConsoleBackground()), Is.LessThan(LightFloor),
                "the original really was below the floor, which is why this test exists");
        }

        /// <summary>SrmLight's four state colours, in page order.</summary>
        private static List<string> RatioLightColours()
        {
            string xml = ReadAuthored("SrmLight.xml");

            var match = Regex.Match(xml, "<gearColor controller=\"state\" pages=\"0,1,2,3\" values=\"([^\"]+)\"");
            Assert.That(match.Success, Is.True, "no four-page gearColor on SrmLight");

            var colours = new List<string>();
            foreach (string value in match.Groups[1].Value.Split('|'))
            {
                colours.Add(value.Split(',')[0].Trim());
            }

            return colours;
        }

        /// <summary>The three title colours from LogRow's gearColor, in page order.</summary>
        private static List<string> LogRowTitleColours()
        {
            string xml = ReadAuthored("LogRow.xml");

            // gearColor values are "fill,stroke" pairs separated by "|", one per page.
            var match = Regex.Match(xml, "<gearColor controller=\"type\" pages=\"0,1,2\" values=\"([^\"]+)\"");
            Assert.That(match.Success, Is.True, "no three-page gearColor on LogRow's title");

            var colours = new List<string>();
            foreach (string pair in match.Groups[1].Value.Split('|'))
            {
                colours.Add(pair.Split(',')[0].Trim());
            }

            return colours;
        }

        /// <summary>The console's own fill, as an #rrggbb string.</summary>
        private static string ConsoleBackground()
        {
            string xml = ReadAuthored("ConsoleMain.xml");

            var match = Regex.Match(xml, "fillColor=\"#([0-9a-fA-F]{8})\"");
            Assert.That(match.Success, Is.True, "no background fill found on ConsoleMain");

            // Authored as #aarrggbb; the alpha is not part of the contrast.
            return "#" + match.Groups[1].Value.Substring(2);
        }

        private static string ReadAuthored(string fileName)
        {
            string path = Path.Combine(AuthoringRoot, fileName);
            Assert.That(File.Exists(path), Is.True, "no authoring source at " + path);
            return File.ReadAllText(path);
        }

        /// <summary>WCAG relative luminance.</summary>
        private static double Luminance(string hex)
        {
            string h = hex.TrimStart('#');
            if (h.Length == 8) h = h.Substring(2);

            double[] channel = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double c = int.Parse(h.Substring(i * 2, 2), NumberStyles.HexNumber) / 255.0;
                channel[i] = c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * channel[0] + 0.7152 * channel[1] + 0.0722 * channel[2];
        }

        private static double Contrast(string a, string b)
        {
            double la = Luminance(a);
            double lb = Luminance(b);
            double hi = Math.Max(la, lb);
            double lo = Math.Min(la, lb);

            return (hi + 0.05) / (lo + 0.05);
        }
    }
}
