using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace ClaudeHelper
{
    /// <summary>
    /// Turns Claude's answers into coloured, clickable text. Claude marks things up like this:
    /// [[Forest/Campfire/Flames]] for a GameObject, [[Assets/Materials/Embers.mat]] for an asset,
    /// [[menu:Window > General > Console]] for a menu path, `code` and ``` blocks for code,
    /// and a paragraph starting with "Try this:" for a hands-on step.
    /// </summary>
    public static class MessageFormatter
    {
        const string MenuPrefix = "menu:";
        const string TryThisPrefix = "Try this:";

        static readonly Regex InlineMarks = new Regex(@"\[\[(.+?)\]\]|`([^`\n]+)`");

        static string MenuColor => EditorGUIUtility.isProSkin ? "#E8B04B" : "#9A5B00";
        static string ObjectColor => EditorGUIUtility.isProSkin ? "#5CB3FF" : "#0B5CAD";
        static string CodeColor => EditorGUIUtility.isProSkin ? "#A5D98A" : "#2E7D32";
        static string TryThisColor => EditorGUIUtility.isProSkin ? "#D7A6FF" : "#7B3FA0";

        public static VisualElement BuildAnswer(string text, Action<string> onLinkClicked)
        {
            var container = new VisualElement();
            foreach (var block in SplitIntoBlocks(text))
            {
                if (block.isCode)
                {
                    var code = new Label(block.text) { enableRichText = false };
                    code.selection.isSelectable = true;
                    code.AddToClassList("code-block");
                    container.Add(code);
                    continue;
                }

                var isTryThis = block.text.StartsWith(TryThisPrefix, StringComparison.OrdinalIgnoreCase);
                var paragraph = CreateRichLabel(block.text, isTryThis, onLinkClicked);
                paragraph.AddToClassList(isTryThis ? "try-this" : "answer-paragraph");
                container.Add(paragraph);
            }

            return container;
        }

        /// <summary>The answer with all marks removed, for copying to the clipboard.</summary>
        public static string ToPlainText(string text)
        {
            return InlineMarks.Replace(text, match =>
            {
                if (match.Groups[2].Success)
                    return match.Groups[2].Value;
                var target = match.Groups[1].Value.Trim();
                return target.StartsWith(MenuPrefix) ? target.Substring(MenuPrefix.Length).Trim() : DisplayName(target);
            }).Replace("```", "");
        }

        struct Block
        {
            public string text;
            public bool isCode;
        }

        static IEnumerable<Block> SplitIntoBlocks(string text)
        {
            var paragraph = new List<string>();
            var lines = text.Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("```"))
                {
                    if (paragraph.Count > 0)
                        yield return new Block { text = string.Join("\n", paragraph) };
                    paragraph.Clear();

                    var code = new List<string>();
                    for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++)
                        code.Add(lines[i]);
                    yield return new Block { text = string.Join("\n", code), isCode = true };
                    continue;
                }

                if (line.Trim().Length == 0)
                {
                    if (paragraph.Count > 0)
                        yield return new Block { text = string.Join("\n", paragraph) };
                    paragraph.Clear();
                    continue;
                }

                paragraph.Add(line);
            }

            if (paragraph.Count > 0)
                yield return new Block { text = string.Join("\n", paragraph) };
        }

        static Label CreateRichLabel(string text, bool isTryThis, Action<string> onLinkClicked)
        {
            var links = new List<string>();
            var rich = new StringBuilder();
            var position = 0;

            if (isTryThis)
            {
                rich.Append($"<b><color={TryThisColor}>{TryThisPrefix}</color></b>");
                position = TryThisPrefix.Length;
            }

            foreach (Match match in InlineMarks.Matches(text))
            {
                if (match.Index < position)
                    continue;
                rich.Append(Escape(text.Substring(position, match.Index - position)));
                position = match.Index + match.Length;

                if (match.Groups[2].Success)
                {
                    rich.Append($"<color={CodeColor}>{Escape(match.Groups[2].Value)}</color>");
                    continue;
                }

                var target = match.Groups[1].Value.Trim();
                if (target.StartsWith(MenuPrefix))
                {
                    rich.Append($"<b><color={MenuColor}>{Escape(target.Substring(MenuPrefix.Length).Trim())}</color></b>");
                    continue;
                }

                // The link ID is an index into this label's list of targets, so paths never need escaping.
                rich.Append($"<link=\"{links.Count}\"><color={ObjectColor}><u>{Escape(DisplayName(target))}</u></color></link>");
                links.Add(target);
            }

            rich.Append(Escape(text.Substring(position)));

            var label = new Label(rich.ToString()) { enableRichText = true };
            label.selection.isSelectable = true;
            label.AddToClassList("message-text");

            if (links.Count > 0)
            {
                label.RegisterCallback<PointerUpLinkTagEvent>(evt =>
                {
                    if (int.TryParse(evt.linkID, out var index) && index >= 0 && index < links.Count)
                        onLinkClicked?.Invoke(links[index]);
                });
                label.RegisterCallback<PointerOverLinkTagEvent>(evt =>
                {
                    label.AddToClassList("link-hover");
                    if (int.TryParse(evt.linkID, out var index) && index >= 0 && index < links.Count)
                        label.tooltip = "Click to show " + links[index];
                });
                label.RegisterCallback<PointerOutLinkTagEvent>(_ =>
                {
                    label.RemoveFromClassList("link-hover");
                    label.tooltip = "";
                });
            }

            return label;
        }

        // Shows the last part of a path: "Forest/Campfire/Flames" becomes "Flames", and an asset path its file name.
        static string DisplayName(string target)
        {
            if (IsAssetPath(target))
                return Path.GetFileName(target);
            var slash = target.LastIndexOf('/');
            return slash >= 0 && slash < target.Length - 1 ? target.Substring(slash + 1) : target;
        }

        // Keeps characters like "<" in answers (for example List<int>) from being read as rich text tags.
        static string Escape(string text) =>
            text.IndexOf('<') < 0 ? text : "<noparse>" + text.Replace("</noparse>", "</ noparse>") + "</noparse>";

        static bool IsAssetPath(string target) =>
            target.StartsWith("Assets/", StringComparison.Ordinal) || target.StartsWith("Packages/", StringComparison.Ordinal);

        /// <summary>
        /// Selects and pings what a link points to: an asset in the Project window, or a GameObject in the
        /// Hierarchy window (also framed in the Scene view). Returns false when nothing matches.
        /// </summary>
        public static bool Reveal(string target)
        {
            if (IsAssetPath(target))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(target);
                if (asset == null)
                    return false;
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                return true;
            }

            var gameObject = FindGameObject(target);
            if (gameObject == null)
                return false;

            Selection.activeGameObject = gameObject;
            EditorGUIUtility.PingObject(gameObject);
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();
            return true;
        }

        // Tries the exact Hierarchy path first, then a path that ends the same way, then just the object's name.
        static GameObject FindGameObject(string path)
        {
            path = path.Trim().Trim('/');
            var all = new List<KeyValuePair<string, GameObject>>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                    Collect(root.transform, root.name, all);
            }

            var name = DisplayName(path);
            return all.FirstOrDefault(p => p.Key == path).Value
                   ?? all.FirstOrDefault(p => p.Key.EndsWith("/" + path, StringComparison.Ordinal)).Value
                   ?? all.FirstOrDefault(p => p.Value.name == name).Value
                   ?? all.FirstOrDefault(p => string.Equals(p.Value.name, name, StringComparison.OrdinalIgnoreCase)).Value;
        }

        static void Collect(Transform node, string path, List<KeyValuePair<string, GameObject>> results)
        {
            results.Add(new KeyValuePair<string, GameObject>(path, node.gameObject));
            foreach (Transform child in node)
                Collect(child, path + "/" + child.name, results);
        }
    }
}
