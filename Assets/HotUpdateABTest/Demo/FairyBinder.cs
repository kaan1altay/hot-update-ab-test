using System;
using System.Collections.Generic;
using FairyGUI;
using HotUpdateABTest.Core;

namespace HotUpdateABTest.Demo
{
    /// <summary>
    /// Looks children up by name and degrades to a named warning when one is missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binding is by name at runtime rather than through generated classes. The package can then be
    /// re-authored without regenerating anything, and a renamed child produces one specific line - "the
    /// component 'ConsoleMain' has no child named 'btnRefresh'" - instead of a compile error against a
    /// stale generated file, or worse, a silent null that surfaces three interactions later.
    /// </para>
    /// <para>
    /// Every miss is reported once per name. A screen whose package has drifted should say so once per
    /// missing thing and keep working, not fill the log with the same line every frame.
    /// </para>
    /// </remarks>
    public sealed class FairyBinder
    {
        private readonly IAbLog _log;
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>How many lookups have failed. Shown on the debug panel.</summary>
        public int MissCount { get; private set; }

        /// <summary>Creates a binder.</summary>
        public FairyBinder(IAbLog log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Finds a child of <paramref name="parent"/>, or null.</summary>
        public T Child<T>(GComponent parent, string name, string parentName = null) where T : GObject
        {
            if (parent == null) return null;

            var child = parent.GetChild(name);
            if (child == null)
            {
                Miss(parentName ?? parent.name, "child", name);
                return null;
            }

            if (child is T typed) return typed;

            Miss(parentName ?? parent.name, "child",
                name + "' is a " + child.GetType().Name + " where a " + typeof(T).Name + " was expected; '" + name);
            return null;
        }

        /// <summary>Finds a controller on <paramref name="parent"/>, or null.</summary>
        public Controller Controller(GComponent parent, string name, string parentName = null)
        {
            if (parent == null) return null;

            var controller = parent.GetController(name);
            if (controller == null) Miss(parentName ?? parent.name, "controller", name);
            return controller;
        }

        /// <summary>
        /// Selects a controller page by name, reporting rather than throwing when the page is absent.
        /// </summary>
        /// <remarks>
        /// By name, never by index. <c>barShare</c> declares its pages as <c>4,unknown,0,green,...</c>, so
        /// the page whose id is 4 sits at index 0 - anything positional silently picks the wrong colour.
        /// </remarks>
        public void SelectPage(Controller controller, string page, string owner = null)
        {
            if (controller == null || page == null) return;

            if (controller.GetPageIdByName(page) == null)
            {
                Miss(owner ?? "controller", "page", page);
                return;
            }

            controller.selectedPage = page;
        }

        /// <summary>Sets a text field's text when it exists.</summary>
        public void SetText(GComponent parent, string name, string text, string parentName = null)
        {
            var field = Child<GObject>(parent, name, parentName);
            if (field == null) return;

            field.text = text;
        }

        private void Miss(string owner, string kind, string name)
        {
            MissCount++;

            string key = owner + "|" + kind + "|" + name;
            if (!_reported.Add(key)) return;

            _log.Log(AbLogLevel.Warning,
                "'" + owner + "' has no " + kind + " named '" + name +
                "'; that part of the screen will not update. Check docs/PACKAGE_SPEC.md.");
        }
    }
}
