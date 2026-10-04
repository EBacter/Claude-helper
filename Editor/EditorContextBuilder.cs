using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ClaudeHelper
{
    /// <summary>
    /// Builds a short text snapshot of the Editor (version, selection, hierarchy, console errors)
    /// that is attached to every question. Must be called on the main thread.
    /// </summary>
    public static class EditorContextBuilder
    {
        const int MaxSelectedObjects = 5;
        const int MaxPropertiesPerComponent = 12;
        const int MaxHierarchyLines = 60;
        const int MaxHierarchyDepth = 3;
        const int MaxChildrenPerObject = 8;
        const int MaxConsoleErrors = 10;

        static readonly string[] SectionTitles = { "Editor", "Selection", "Scene hierarchy (summary)", "Recent Console errors" };

        /// <summary>The current text of each section, in a fixed order. Compare them to see what changed.</summary>
        public static List<string> Capture() => new List<string>
        {
            BuildSection(SectionTitles[0], AppendEditorState),
            BuildSection(SectionTitles[1], AppendSelection),
            BuildSection(SectionTitles[2], AppendHierarchy),
            BuildSection(SectionTitles[3], AppendConsoleErrors),
        };

        /// <summary>
        /// The context block for a question. Sections that are the same as in <paramref name="previous"/>, which
        /// Claude has already seen in this conversation, are replaced by a one-line note to keep the prompt short.
        /// </summary>
        public static string Format(List<string> sections, List<string> previous)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<unity_editor_context>");
            for (var i = 0; i < sections.Count; i++)
            {
                if (previous != null && i < previous.Count && previous[i] == sections[i])
                    sb.AppendLine($"## {SectionTitles[i]}: unchanged since the previous message.").AppendLine();
                else
                    sb.Append(sections[i]);
            }

            sb.AppendLine("</unity_editor_context>");
            return sb.ToString();
        }

        static string BuildSection(string title, Action<StringBuilder> append)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## {title}");
            try
            {
                append(sb);
            }
            catch (Exception e)
            {
                sb.AppendLine($"(Could not read this part of the editor: {e.Message})");
            }

            sb.AppendLine();
            return sb.ToString();
        }

        static void AppendEditorState(StringBuilder sb)
        {
            sb.AppendLine($"Unity version: {Application.unityVersion}");
            var pipeline = GraphicsSettings.currentRenderPipeline;
            sb.AppendLine($"Render pipeline: {(pipeline != null ? $"{pipeline.name} ({pipeline.GetType().Name})" : "Built-in Render Pipeline")}");
            sb.AppendLine($"Editor state: {(EditorApplication.isPlaying ? "Play mode" : "Edit mode")}{(EditorApplication.isPaused ? " (paused)" : "")}");
        }

        static void AppendSelection(StringBuilder sb)
        {
            var gameObjects = Selection.gameObjects;
            var otherObjects = Selection.objects.Where(o => o != null && !(o is GameObject)).ToArray();
            if (gameObjects.Length == 0 && otherObjects.Length == 0)
            {
                sb.AppendLine("Nothing is selected.");
                return;
            }

            foreach (var go in gameObjects.Take(MaxSelectedObjects))
                AppendGameObject(sb, go);
            if (gameObjects.Length > MaxSelectedObjects)
                sb.AppendLine($"(+{gameObjects.Length - MaxSelectedObjects} more selected GameObjects)");

            foreach (var asset in otherObjects.Take(MaxSelectedObjects))
                sb.AppendLine($"Selected asset: \"{asset.name}\" ({asset.GetType().Name}) at {AssetDatabase.GetAssetPath(asset)}");
        }

        static void AppendGameObject(StringBuilder sb, GameObject go)
        {
            var isAsset = EditorUtility.IsPersistent(go);
            sb.AppendLine(isAsset
                ? $"Selected prefab asset: \"{go.name}\" at {AssetDatabase.GetAssetPath(go)}"
                : $"Selected GameObject: \"{GetHierarchyPath(go.transform)}\" in scene \"{go.scene.name}\"");

            var state = go.activeSelf ? (go.activeInHierarchy ? "active" : "active, but a parent is inactive") : "inactive";
            sb.AppendLine($"  State: {state}; Tag: {go.tag}; Layer: {LayerMask.LayerToName(go.layer)}; Static: {go.isStatic}");
            if (!isAsset && PrefabUtility.IsPartOfPrefabInstance(go))
                sb.AppendLine($"  Prefab instance of: {PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go)}");
            if (go.transform.childCount > 0)
                sb.AppendLine($"  Children: {go.transform.childCount}");

            sb.AppendLine("  Components:");
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    sb.AppendLine("  - Missing Script (the script file was deleted, renamed, or has compile errors)");
                    continue;
                }

                var enabled = EditorUtility.GetObjectEnabled(component);
                sb.AppendLine($"  - {component.GetType().Name}{(enabled == 0 ? " (disabled)" : "")}");

                if (component.GetType() == typeof(Transform))
                    AppendTransform(sb, (Transform)component);
                else
                    AppendSerializedProperties(sb, component);
            }
        }

        static void AppendTransform(StringBuilder sb, Transform transform)
        {
            sb.AppendLine($"      Position: {Format(transform.localPosition)}");
            sb.AppendLine($"      Rotation: {Format(transform.localEulerAngles)}");
            sb.AppendLine($"      Scale: {Format(transform.localScale)}");
        }

        static void AppendSerializedProperties(StringBuilder sb, Object target)
        {
            using (var serializedObject = new SerializedObject(target))
            {
                var property = serializedObject.GetIterator();
                var enterChildren = true;
                var count = 0;
                while (property.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (property.propertyPath == "m_Script")
                        continue;
                    if (count++ >= MaxPropertiesPerComponent)
                    {
                        sb.AppendLine("      ...");
                        break;
                    }

                    sb.AppendLine($"      {property.displayName}: {FormatProperty(property)}");
                }
            }
        }

        static string FormatProperty(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return Format(property.floatValue);
                case SerializedPropertyType.String:
                    return "\"" + Truncate(property.stringValue, 80) + "\"";
                case SerializedPropertyType.Color:
                    var c = property.colorValue;
                    return $"RGBA({Format(c.r)}, {Format(c.g)}, {Format(c.b)}, {Format(c.a)})";
                case SerializedPropertyType.ObjectReference:
                    var reference = property.objectReferenceValue;
                    return reference != null ? $"{reference.name} ({reference.GetType().Name})" : "None";
                case SerializedPropertyType.LayerMask:
                    return FormatLayerMask(property.intValue);
                case SerializedPropertyType.Enum:
                    var names = property.enumDisplayNames;
                    var index = property.enumValueIndex;
                    return index >= 0 && index < names.Length ? names[index] : property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return Format(property.vector2Value);
                case SerializedPropertyType.Vector3:
                    return Format(property.vector3Value);
                case SerializedPropertyType.Vector4:
                    return Format(property.vector4Value);
                case SerializedPropertyType.Quaternion:
                    return Format(property.quaternionValue.eulerAngles) + " (as rotation angles)";
                case SerializedPropertyType.Vector2Int:
                    return property.vector2IntValue.ToString();
                case SerializedPropertyType.Vector3Int:
                    return property.vector3IntValue.ToString();
                case SerializedPropertyType.Rect:
                    return property.rectValue.ToString();
                case SerializedPropertyType.Bounds:
                    return property.boundsValue.ToString();
                default:
                    return property.isArray ? $"list with {property.arraySize} item(s)" : $"({property.type})";
            }
        }

        static string FormatLayerMask(int mask)
        {
            if (mask == 0)
                return "Nothing";
            if (mask == -1)
                return "Everything";

            var names = new List<string>();
            for (var layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) != 0)
                {
                    var name = LayerMask.LayerToName(layer);
                    names.Add(string.IsNullOrEmpty(name) ? $"Layer {layer}" : name);
                }
            }

            return string.Join(", ", names);
        }

        static void AppendHierarchy(StringBuilder sb)
        {
            var lines = 0;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    sb.AppendLine($"Scene \"{scene.name}\" (not loaded)");
                    continue;
                }

                var roots = scene.GetRootGameObjects();
                var activeMarker = scene == SceneManager.GetActiveScene() ? ", active scene" : "";
                var dirtyMarker = scene.isDirty ? ", has unsaved changes" : "";
                sb.AppendLine($"Scene \"{scene.name}\" ({(string.IsNullOrEmpty(scene.path) ? "never saved" : scene.path)}{activeMarker}{dirtyMarker}), {roots.Length} root GameObject(s):");

                foreach (var root in roots)
                {
                    if (!AppendHierarchyNode(sb, root.transform, 0, ref lines))
                    {
                        sb.AppendLine("  ... (hierarchy too large; the rest is not listed)");
                        return;
                    }
                }
            }
        }

        static bool AppendHierarchyNode(StringBuilder sb, Transform node, int depth, ref int lines)
        {
            if (lines >= MaxHierarchyLines)
                return false;
            lines++;

            var components = node.GetComponents<Component>()
                .Where(c => !(c is Transform))
                .Select(c => c == null ? "Missing Script" : c.GetType().Name)
                .ToList();
            var componentText = components.Count == 0
                ? ""
                : " [" + string.Join(", ", components.Take(4)) + (components.Count > 4 ? ", ..." : "") + "]";
            var inactive = node.gameObject.activeSelf ? "" : " (inactive)";
            var hiddenChildren = depth + 1 >= MaxHierarchyDepth && node.childCount > 0 ? $" (+{node.childCount} children)" : "";
            sb.AppendLine($"{new string(' ', 2 + depth * 2)}- {node.name}{componentText}{inactive}{hiddenChildren}");

            if (depth + 1 >= MaxHierarchyDepth)
                return true;

            var shown = Math.Min(node.childCount, MaxChildrenPerObject);
            for (var i = 0; i < shown; i++)
            {
                if (!AppendHierarchyNode(sb, node.GetChild(i), depth + 1, ref lines))
                    return false;
            }

            if (node.childCount > shown)
            {
                sb.AppendLine($"{new string(' ', 4 + depth * 2)}... and {node.childCount - shown} more children of {node.name}");
                lines++;
            }

            return true;
        }

        static void AppendConsoleErrors(StringBuilder sb)
        {
            var errors = ConsoleErrorReader.GetRecentErrors(MaxConsoleErrors, out var totalErrors);
            if (errors.Count == 0)
            {
                sb.AppendLine("No errors in the Console.");
                return;
            }

            if (totalErrors > errors.Count)
                sb.AppendLine($"The Console has {totalErrors} errors; the {errors.Count} most recent are:");
            foreach (var error in errors)
                sb.AppendLine("- " + error.Replace("\n", "\n  "));
        }

        static string GetHierarchyPath(Transform transform)
        {
            var path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }

            return path;
        }

        static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string Format(Vector2 v) => $"({Format(v.x)}, {Format(v.y)})";
        static string Format(Vector3 v) => $"({Format(v.x)}, {Format(v.y)}, {Format(v.z)})";
        static string Format(Vector4 v) => $"({Format(v.x)}, {Format(v.y)}, {Format(v.z)}, {Format(v.w)})";

        internal static string Truncate(string text, int maxLength) =>
            text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
    }

    /// <summary>
    /// Reads errors that are currently shown in the Console window. Unity has no public API for this,
    /// so it uses the Console's internal LogEntries class and falls back to errors logged since the last script reload.
    /// </summary>
    [InitializeOnLoad]
    static class ConsoleErrorReader
    {
        // Mode flags from Unity's internal console: Error, Assert, Fatal, AssetImportError, ScriptingError,
        // ScriptCompileError, ScriptingException, GraphCompileError, ScriptingAssertion, VisualScriptingError.
        const int ErrorModeMask = (1 << 0) | (1 << 1) | (1 << 4) | (1 << 6) | (1 << 8) | (1 << 11) | (1 << 17) | (1 << 20) | (1 << 21) | (1 << 22);
        const int MaxLinesPerError = 3;
        const int MaxErrorLength = 400;

        static readonly List<string> CapturedErrors = new List<string>();

        static ConsoleErrorReader()
        {
            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;

            var firstFrame = string.IsNullOrEmpty(stackTrace) ? "" : "\n" + stackTrace.Split('\n')[0];
            lock (CapturedErrors)
            {
                CapturedErrors.Add(Shorten(message + firstFrame));
                if (CapturedErrors.Count > 50)
                    CapturedErrors.RemoveAt(0);
            }
        }

        public static List<string> GetRecentErrors(int max, out int total)
        {
            var fromConsole = TryReadConsole(max, out total);
            if (fromConsole != null)
                return fromConsole;

            lock (CapturedErrors)
            {
                total = CapturedErrors.Count;
                return CapturedErrors.Skip(Math.Max(0, CapturedErrors.Count - max)).ToList();
            }
        }

        static List<string> TryReadConsole(int max, out int total)
        {
            total = 0;
            try
            {
                var editorAssembly = typeof(EditorWindow).Assembly;
                var logEntries = editorAssembly.GetType("UnityEditor.LogEntries");
                var logEntryType = editorAssembly.GetType("UnityEditor.LogEntry");
                if (logEntries == null || logEntryType == null)
                    return null;

                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var start = logEntries.GetMethod("StartGettingEntries", flags);
                var end = logEntries.GetMethod("EndGettingEntries", flags);
                var getEntry = logEntries.GetMethod("GetEntryInternal", flags);
                var messageField = logEntryType.GetField("message");
                var modeField = logEntryType.GetField("mode");
                if (start == null || end == null || getEntry == null || messageField == null || modeField == null)
                    return null;

                var errors = new List<string>();
                var entry = Activator.CreateInstance(logEntryType);
                var count = (int)start.Invoke(null, null);
                try
                {
                    for (var i = count - 1; i >= 0; i--)
                    {
                        getEntry.Invoke(null, new[] { (object)i, entry });
                        var mode = Convert.ToInt32(modeField.GetValue(entry));
                        if ((mode & ErrorModeMask) == 0)
                            continue;

                        total++;
                        if (errors.Count < max)
                            errors.Add(Shorten((string)messageField.GetValue(entry)));
                    }
                }
                finally
                {
                    end.Invoke(null, null);
                }

                errors.Reverse();
                return errors;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string Shorten(string message)
        {
            var lines = (message ?? "").Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Take(MaxLinesPerError);
            return EditorContextBuilder.Truncate(string.Join("\n", lines), MaxErrorLength);
        }
    }
}
