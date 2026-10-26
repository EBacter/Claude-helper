using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ClaudeHelper
{
    enum MessageKind
    {
        User,
        Assistant,
        Error,
        Info,
    }

    [Serializable]
    class ChatMessage
    {
        public MessageKind kind;
        public string text;
        public string context;

        // Answers only: what Claude did, and a short line with how long it took and how big the conversation is.
        public List<string> steps = new List<string>();
        public string stats;
    }

    [Serializable]
    class SavedConversation
    {
        public string id;
        public string title;
        public long createdUtcTicks;
        public long updatedUtcTicks;
        public string sessionId;
        public List<ChatMessage> messages = new List<ChatMessage>();

        // The editor context sections Claude last received in full, so unchanged ones aren't sent again.
        public List<string> sentContext = new List<string>();

        public bool longConversationNoted;
    }

    /// <summary>
    /// Keeps each conversation shown in the window as a JSON file in the project's UserSettings folder,
    /// which is per person and not committed to git. Claude Code keeps its own copy of the conversation,
    /// which is what lets "--resume" continue it.
    /// </summary>
    static class ConversationStore
    {
        const int MaxTitleLength = 60;

        public static string Folder(string projectFolder) =>
            Path.Combine(projectFolder, "UserSettings", "ClaudeHelper", "Conversations");

        public static string NewId() => Guid.NewGuid().ToString("N");

        public static void Save(string projectFolder, SavedConversation conversation)
        {
            var folder = Folder(projectFolder);
            Directory.CreateDirectory(folder);

            // Write to a temporary file first so a crash never leaves half a conversation behind.
            var path = PathFor(projectFolder, conversation.id);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonUtility.ToJson(conversation, true), new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }

        public static SavedConversation Load(string projectFolder, string id) => Read(PathFor(projectFolder, id));

        /// <summary>The most recently changed conversations, newest first.</summary>
        public static List<SavedConversation> List(string projectFolder, int max)
        {
            var folder = Folder(projectFolder);
            if (!Directory.Exists(folder))
                return new List<SavedConversation>();

            return new DirectoryInfo(folder).GetFiles("*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(max)
                .Select(file => Read(file.FullName))
                .Where(conversation => conversation != null)
                .OrderByDescending(conversation => conversation.updatedUtcTicks)
                .ToList();
        }

        public static void Delete(string projectFolder, string id)
        {
            var path = PathFor(projectFolder, id);
            if (File.Exists(path))
                File.Delete(path);
        }

        public static string MakeTitle(IEnumerable<ChatMessage> messages)
        {
            var question = messages.FirstOrDefault(m => m.kind == MessageKind.User)?.text ?? "";
            var title = Regex.Replace(question, @"\s+", " ").Trim();
            if (title.Length > MaxTitleLength)
                title = title.Substring(0, MaxTitleLength).TrimEnd() + "...";
            return title.Length == 0 ? "Untitled conversation" : title;
        }

        /// <summary>
        /// Whether Claude Code still has the transcript it needs to resume this session. It deletes old
        /// transcripts (after 30 days by default). Returns true when the transcript folder can't be found,
        /// and lets "--resume" report the problem instead.
        /// </summary>
        public static bool CanResume(string projectFolder, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return false;

            // Claude Code names the folder after the project path, with every other character replaced by "-".
            var projectName = Regex.Replace(Path.GetFullPath(projectFolder), "[^A-Za-z0-9]", "-");
            var transcriptFolder = Path.Combine(ClaudeSetup.ConfigFolder, "projects", projectName);
            return !Directory.Exists(transcriptFolder) || File.Exists(Path.Combine(transcriptFolder, sessionId + ".jsonl"));
        }

        static string PathFor(string projectFolder, string id) => Path.Combine(Folder(projectFolder), id + ".json");

        static SavedConversation Read(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                var conversation = JsonUtility.FromJson<SavedConversation>(File.ReadAllText(path, Encoding.UTF8));
                if (conversation == null || string.IsNullOrEmpty(conversation.id))
                    return null;

                conversation.messages ??= new List<ChatMessage>();
                return conversation;
            }
            catch (Exception)
            {
                // A damaged or half-written file: leave it out of the history.
                return null;
            }
        }
    }
}
