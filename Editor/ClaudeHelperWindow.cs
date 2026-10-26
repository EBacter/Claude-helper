using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ClaudeHelper
{
    public class ClaudeHelperWindow : EditorWindow
    {
        const string ClaudePathPrefKey = "ClaudeHelper.ClaudePath";
        const string ModelPrefKey = "ClaudeHelper.Model";
        const string EffortPrefKey = "ClaudeHelper.Effort";
        const string SetupReadySessionKey = "ClaudeHelper.SetupReady";
        const string PackageUxmlPath = "Packages/com.local.claude-helper/Editor/ClaudeHelperWindow.uxml";
        const double PollIntervalSeconds = 0.25;
        const int MaxHistoryItems = 200;

        // A new conversation already starts at about 27k tokens (Claude Code's own instructions and tools).
        const int LongConversationTokens = 100_000;

        // Claude Code's model aliases. An empty ID leaves the choice to the person's Claude Code settings.
        static readonly string[] ModelIds = { "", "opus", "sonnet", "haiku" };
        static readonly List<string> ModelLabels = new List<string>
        {
            "Your Claude Code default",
            "Opus: most capable",
            "Sonnet: faster, uses less of your plan",
            "Haiku: fastest, for simple questions",
        };

        // Claude Code's effort levels. An empty ID leaves the choice to the person's Claude Code settings.
        static readonly string[] EffortIds = { "", "low", "medium", "high", "xhigh", "max" };
        static readonly List<string> EffortLabels = new List<string>
        {
            "Your Claude Code default",
            "Low: quickest answers",
            "Medium: a good balance",
            "High: thinks harder",
            "Extra high: for tricky problems",
            "Max: slowest, uses the most of your plan",
        };

        static readonly string[] ExampleQuestions =
        {
            "What does the selected object do, and what are its components for?",
            "Explain the errors in my Console and how to fix them.",
            "How do I make a cube move with the arrow keys?",
        };

        const string SystemPrompt =
            "You are a friendly Unity tutor answering questions inside the Unity Editor, in a window called \"Claude Helper\". " +
            "The person asking is a complete beginner who is learning Unity.\n" +
            "- Explain simply, in short sentences. Explain any jargon the first time you use it.\n" +
            "- Refer to Unity's windows, menus and buttons by their exact names, for example the Hierarchy window, " +
            "the Inspector window, the Project window, or the menu [[menu:Window > General > Console]].\n" +
            "- When it helps, end with one small hands-on step they can try right now, as its own paragraph starting with \"Try this:\".\n" +
            "- Keep answers short and focused on what they asked.\n" +
            "- Every message starts with an automatically generated <unity_editor_context> block describing what is open, selected and " +
            "shown in the Console. Use it when it is relevant and ignore it otherwise. Don't repeat it back. A section marked " +
            "\"unchanged since the previous message\" is the same as the last time it was shown in full.\n" +
            "- You have your full set of tools and can do anything you normally can, including changing the project when they ask. " +
            "When they only ask a question, answer it rather than changing things.\n" +
            "- The Unity Editor is open on this project. For scenes, GameObjects, components, materials and other assets, change the open Editor " +
            "with the Unity CLI (\"unity command ...\"; load the unity-cli skill first to learn the commands) rather than editing .unity, .prefab " +
            "or .asset files by hand. After changing things, say what you changed, where to see it (for example which object in the Hierarchy " +
            "window), that Edit > Undo can usually undo Editor changes, and remind them to save with File > Save.\n" +
            "- You run in the background with no way to ask for permission. If an action is blocked, explain what you wanted to do and why, " +
            "so they can decide whether to do it themselves.\n" +
            "- Your answer is shown in a simple text view that doesn't render markdown, so don't use headings, bold, or tables. " +
            "Use short paragraphs and simple \"-\" or numbered lists. The window turns these marks into colours and clickable links, so use them:\n" +
            "  - A GameObject in the open scene: its full Hierarchy path in double square brackets, for example [[Forest/Campfire/Flames]]. " +
            "Use the exact names and nesting from the editor context. Clicking it selects the object.\n" +
            "  - An asset: its path in double square brackets, for example [[Assets/Forest/Materials/Embers.mat]].\n" +
            "  - A menu path: [[menu:Window > General > Console]].\n" +
            "  - Code, script names and component fields in code: `backticks`. Longer code goes in ``` fenced blocks.";

        const string WelcomeTitle = "Ask Claude about Unity";

        const string WelcomeText =
            "Ask anything, from \"what is a prefab?\" to \"add a jump to my player\". Claude sees what is open and selected in the " +
            "Editor and what the Console says. Try one of these:";

        const string NotInstalledText =
            "Claude Helper answers with Claude Code, Anthropic's coding assistant, which isn't on this computer yet. " +
            "You need a paid Claude plan, such as Pro or Max.\n\n" +
            "1. Press Install Claude Code and wait until the PowerShell window says it has finished.\n" +
            "2. Press Sign in and sign in with your Claude account in the browser that opens.\n\n" +
            "This panel checks again whenever you come back to this window.";

        const string SignedOutText =
            "Claude Code is installed but not signed in. Press Sign in and sign in with your Claude account in the browser " +
            "that opens. This panel checks again whenever you come back to this window.";

        const string SetupDoneText = "Claude Code is installed and signed in. Ask away!";

        const string StoppedText = "Stopped. Claude didn't finish that answer.";

        const string TerminalText =
            "I opened this conversation in a terminal window. There, Claude asks you before risky actions instead of skipping them.\n\n" +
            "Claude will remember what you do there when you come back, but those messages won't show in this window. " +
            "Type /exit in the terminal when you're done, before asking here again, so you're not using both at once.";

        const string ExpiredText =
            "Claude Code no longer has this conversation (it deletes old conversations, after 30 days by default), " +
            "so Claude won't remember the messages above. You can still read them here. Your next question starts a new conversation.";

        const string StatsTooltip =
            "Which model answered and at what effort, how long the answer took, how many steps Claude took, and how much of this conversation Claude reads each time you ask. " +
            "Bigger conversations are slower and use more of your plan.";

        [SerializeField] List<ChatMessage> messages = new List<ChatMessage>();
        [SerializeField] string sessionId;
        [SerializeField] List<string> sentContext = new List<string>();
        [SerializeField] List<string> pendingContext = new List<string>();
        [SerializeField] string conversationId;
        [SerializeField] long conversationCreatedUtcTicks;
        [SerializeField] bool historyRestored;
        [SerializeField] bool longConversationNoted;
        [SerializeField] ClaudeRequest pendingRequest;

        ScrollView messageList;
        TextField questionField;
        Button sendButton;
        ToolbarToggle historyToggle;
        ToolbarButton terminalButton;
        ToolbarMenu modelMenu;
        VisualElement historyPanel;
        ScrollView historyList;
        ToolbarSearchField historySearch;
        VisualElement splitView;
        VisualElement conversationHeader;
        Label conversationTitle;
        Label thinkingLabel;
        VisualElement thinkingRow;
        VisualElement settingsPanel;
        ToolbarToggle settingsToggle;
        TextField claudePathField;
        DropdownField modelField;
        DropdownField effortField;
        ClaudeDefaults claudeDefaults = new ClaudeDefaults();
        VisualElement setupPanel;
        Label setupText;
        Button installButton;
        Button signInButton;

        double nextPollTime;
        SetupState setupState = SetupState.Checking;
        bool setupCheckRunning;

        bool SetupNeeded => setupState == SetupState.NotInstalled || setupState == SetupState.SignedOut;

        // Unity restores serialized fields as empty objects rather than null, so check the process ID.
        bool IsBusy => pendingRequest != null && pendingRequest.processId != 0;

        static string ProjectFolder => Path.GetDirectoryName(Application.dataPath);

        /// <summary>The path typed in Settings. Empty means find Claude Code automatically.</summary>
        static string ClaudePath
        {
            get => EditorPrefs.GetString(ClaudePathPrefKey, "");
            set => EditorPrefs.SetString(ClaudePathPrefKey, value);
        }

        /// <summary>The Claude Code to run: the one set in Settings if it exists, otherwise the first one found, or null.</summary>
        static string ResolveClaudePath()
        {
            var saved = ClaudePath.Trim().Trim('"');
            return saved.Length > 0 && ClaudeSetup.LooksValid(saved) ? saved : ClaudeSetup.Find();
        }

        static string Model
        {
            get => EditorPrefs.GetString(ModelPrefKey, "");
            set => EditorPrefs.SetString(ModelPrefKey, value);
        }

        static string Effort
        {
            get => EditorPrefs.GetString(EffortPrefKey, "");
            set => EditorPrefs.SetString(EffortPrefKey, value);
        }

        /// <summary>The model that will answer: the one chosen here, otherwise Claude Code's own setting, or null if neither is known.</summary>
        string EffectiveModel => Model.Length > 0 ? Model : claudeDefaults.model;

        string EffectiveEffort => Effort.Length > 0 ? Effort : claudeDefaults.EffortFor(EffectiveModel);

        [MenuItem("Window/Claude Helper")]
        public static void Open()
        {
            var window = GetWindow<ClaudeHelperWindow>();
            window.titleContent = new GUIContent("Claude Helper");
        }

        void OnEnable()
        {
            EditorApplication.quitting += LeaveCurrentConversation;
        }

        void OnFocus()
        {
            // Coming back from the installer or the sign-in window.
            if (SetupNeeded)
                RefreshSetup();

            // Coming back from editing Claude Code's settings.
            if (modelMenu != null)
                RefreshClaudeDefaults();
        }

        void OnDisable()
        {
            // Also runs before a script reload. The request keeps running and CreateGUI picks it up again.
            EditorApplication.quitting -= LeaveCurrentConversation;
            EditorApplication.update -= PollRequest;
        }

        void OnDestroy()
        {
            LeaveCurrentConversation();
        }

        void CreateGUI()
        {
            // Wide enough for every toolbar button at full size. Set here so a window restored from the layout gets it too.
            minSize = new Vector2(340, 300);

            var tree = LoadVisualTree();
            if (tree == null)
            {
                rootVisualElement.Add(new HelpBox(
                    "Claude Helper couldn't find ClaudeHelperWindow.uxml. Check that the Claude Helper package is installed in Window > Package Manager.",
                    HelpBoxMessageType.Error));
                return;
            }

            tree.CloneTree(rootVisualElement);

            messageList = rootVisualElement.Q<ScrollView>("messageList");
            questionField = rootVisualElement.Q<TextField>("questionField");
            sendButton = rootVisualElement.Q<Button>("sendButton");
            thinkingLabel = rootVisualElement.Q<Label>("thinkingLabel");
            thinkingRow = rootVisualElement.Q("thinkingRow");
            settingsPanel = rootVisualElement.Q("settingsPanel");
            settingsToggle = rootVisualElement.Q<ToolbarToggle>("settingsToggle");
            claudePathField = rootVisualElement.Q<TextField>("claudePathField");
            historyToggle = rootVisualElement.Q<ToolbarToggle>("historyToggle");
            historyPanel = rootVisualElement.Q("historyPanel");
            historyList = rootVisualElement.Q<ScrollView>("historyList");
            historySearch = rootVisualElement.Q<ToolbarSearchField>("historySearch");
            splitView = rootVisualElement.Q("splitView");
            conversationHeader = rootVisualElement.Q("conversationHeader");
            conversationTitle = rootVisualElement.Q<Label>("conversationTitle");
            terminalButton = rootVisualElement.Q<ToolbarButton>("terminalButton");
            modelMenu = rootVisualElement.Q<ToolbarMenu>("modelMenu");
            modelField = rootVisualElement.Q<DropdownField>("modelField");
            effortField = rootVisualElement.Q<DropdownField>("effortField");
            setupPanel = rootVisualElement.Q("setupPanel");
            setupText = rootVisualElement.Q<Label>("setupText");
            installButton = rootVisualElement.Q<Button>("installButton");
            signInButton = rootVisualElement.Q<Button>("signInButton");

            installButton.clicked += InstallClaude;
            signInButton.clicked += SignIn;
            rootVisualElement.Q<Button>("checkSetupButton").clicked += RefreshSetup;
            rootVisualElement.Q<ToolbarButton>("newConversationButton").clicked += StartNewConversation;
            historyToggle.RegisterValueChangedCallback(evt => ShowHistory(evt.newValue));
            historySearch.RegisterValueChangedCallback(_ => RebuildHistoryList());
            rootVisualElement.Q<Button>("historyFolderButton").clicked += () =>
            {
                var folder = ConversationStore.Folder(ProjectFolder);
                Directory.CreateDirectory(folder);
                EditorUtility.RevealInFinder(folder);
            };
            terminalButton.clicked += OpenInTerminal;
            rootVisualElement.Q<Button>("stopButton").clicked += StopRequest;
            rootVisualElement.Q<Button>("browseButton").clicked += BrowseForClaude;
            sendButton.clicked += SendQuestion;

            settingsToggle.RegisterValueChangedCallback(evt => settingsPanel.EnableInClassList("hidden", !evt.newValue));
            claudePathField.SetValueWithoutNotify(ClaudePath);
            claudePathField.RegisterValueChangedCallback(evt =>
            {
                ClaudePath = evt.newValue.Trim();
                UpdatePathPlaceholder();
            });
            UpdatePathPlaceholder();

            modelField.choices = ModelLabels;
            modelField.index = Mathf.Max(0, Array.IndexOf(ModelIds, Model));
            modelField.RegisterValueChangedCallback(_ => SetModel(ModelIds[Mathf.Max(0, modelField.index)]));
            effortField.choices = EffortLabels;
            effortField.index = Mathf.Max(0, Array.IndexOf(EffortIds, Effort));
            effortField.RegisterValueChangedCallback(_ => SetEffort(EffortIds[Mathf.Max(0, effortField.index)]));
            RefreshClaudeDefaults();

            questionField.textEdition.placeholder = "Ask a question about Unity...";
            questionField.verticalScrollerVisibility = ScrollerVisibility.Auto;
            questionField.RegisterCallback<KeyDownEvent>(OnQuestionKeyDown, TrickleDown.TrickleDown);

            // Check that Claude Code is installed and signed in once per Editor session, in the background.
            if (SessionState.GetBool(SetupReadySessionKey, false) && ResolveClaudePath() != null)
                setupState = SetupState.Ready;
            else
                RefreshSetup();
            UpdateSetupPanel();

            // The first time the window opens, carry on with the latest conversation. After a script reload or an
            // Editor restart the window already has its own conversation back.
            if (!historyRestored)
            {
                historyRestored = true;
                if (messages.Count == 0 && !IsBusy)
                {
                    var latest = ConversationStore.List(ProjectFolder, 1).FirstOrDefault();
                    if (latest != null)
                        LoadConversation(latest);
                }
            }

            RebuildMessages();
            if (IsBusy)
                EditorApplication.update += PollRequest;
            UpdateUi();
            questionField.schedule.Execute(() => questionField.Focus());
        }

        static VisualTreeAsset LoadVisualTree()
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(PackageUxmlPath);
            if (tree != null)
                return tree;

            var path = AssetDatabase.FindAssets("ClaudeHelperWindow t:VisualTreeAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => Path.GetFileName(p) == "ClaudeHelperWindow.uxml");
            return path == null ? null : AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
        }

        void OnQuestionKeyDown(KeyDownEvent evt)
        {
            var isEnter = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter || evt.character == '\n' || evt.character == '\r';
            if (!isEnter)
                return;

            // Unity sends Enter as two events: the key itself, then a newline character that doesn't
            // reliably carry the Shift flag. Decide on the key event and swallow the character event.
            evt.StopImmediatePropagation();
            if (evt.keyCode == KeyCode.None)
                return;

            if (evt.shiftKey)
                InsertNewLine();
            else
                SendQuestion();
        }

        void InsertNewLine()
        {
            var text = questionField.value;
            var start = Mathf.Clamp(Mathf.Min(questionField.cursorIndex, questionField.selectIndex), 0, text.Length);
            var end = Mathf.Clamp(Mathf.Max(questionField.cursorIndex, questionField.selectIndex), start, text.Length);
            questionField.value = text.Substring(0, start) + "\n" + text.Substring(end);

            var caret = start + 1;
            questionField.SelectRange(caret, caret);
            questionField.schedule.Execute(() => questionField.SelectRange(caret, caret));
        }

        void SendQuestion()
        {
            if (IsBusy)
                return;

            var question = questionField.value.Trim();
            if (question.Length == 0)
                return;

            if (!TryGetClaudePath(out var claudePath))
                return;

            List<string> sections = null;
            string context;
            try
            {
                // Only a conversation Claude can resume has seen the earlier sections.
                sections = EditorContextBuilder.Capture();
                context = EditorContextBuilder.Format(sections, string.IsNullOrEmpty(sessionId) ? null : sentContext);
            }
            catch (Exception e)
            {
                context = $"<unity_editor_context>\n(Could not collect editor context: {e.Message})\n</unity_editor_context>";
            }

            AddMessage(new ChatMessage { kind = MessageKind.User, text = question, context = context });
            questionField.value = "";

            var prompt = context + "\nThe beginner's message:\n" + question;
            ClaudeRequest request;
            string error;
            try
            {
                request = ClaudeCodeRunner.Start(claudePath, ProjectFolder, prompt, sessionId, Model, Effort, SystemPrompt, out error);
                if (request != null)
                    request.effort = EffectiveEffort;
            }
            catch (Exception e)
            {
                request = null;
                error = "Couldn't prepare the request:\n" + e.Message;
            }

            if (request == null)
            {
                AddMessage(new ChatMessage { kind = MessageKind.Error, text = error });
                return;
            }

            pendingRequest = request;
            pendingContext = sections ?? new List<string>();
            nextPollTime = 0;
            EditorApplication.update += PollRequest;
            UpdateUi();
        }

        void PollRequest()
        {
            if (!IsBusy)
            {
                EditorApplication.update -= PollRequest;
                UpdateUi();
                return;
            }

            UpdateThinkingLabel();
            if (EditorApplication.timeSinceStartup < nextPollTime)
                return;
            nextPollTime = EditorApplication.timeSinceStartup + PollIntervalSeconds;
            if (ClaudeCodeRunner.IsRunning(pendingRequest))
            {
                ClaudeCodeRunner.ReadProgress(pendingRequest);
                return;
            }

            var request = pendingRequest;
            pendingRequest = null;
            EditorApplication.update -= PollRequest;

            var elapsed = request.ElapsedSeconds;
            var result = ClaudeCodeRunner.ReadResult(request);
            if (result.Success)
            {
                sessionId = result.SessionId;
                sentContext = pendingContext;
                pendingContext = new List<string>();
                AddMessage(new ChatMessage
                {
                    kind = MessageKind.Assistant,
                    text = CleanMarkdown(result.Text),
                    steps = result.Steps,
                    stats = FormatStats(elapsed, result.Steps.Count, result.ContextTokens, result.Model ?? EffectiveModel, request.effort),
                });
            }
            else
            {
                // Keep comparing against the last context Claude is sure to have received.
                pendingContext = new List<string>();
                if (result.SessionNotFound)
                {
                    sessionId = null;
                    sentContext = new List<string>();
                }
                else if (!string.IsNullOrEmpty(result.SessionId))
                    sessionId = result.SessionId;
                AddMessage(new ChatMessage { kind = MessageKind.Error, text = result.Text });

                if (ClaudeCodeRunner.LooksLikeSignInProblem(result.Text))
                {
                    SessionState.SetBool(SetupReadySessionKey, false);
                    setupState = SetupState.SignedOut;
                    UpdateSetupPanel();
                }
            }

            if (result.BlockedActions.Count > 0)
            {
                AddMessage(new ChatMessage
                {
                    kind = MessageKind.Info,
                    text = "Auto mode blocked these actions as too risky to do without asking you:\n- " +
                           string.Join("\n- ", result.BlockedActions) +
                           "\n\nIf you want them done, you can do them yourself, or press Open in terminal at the top of this window, " +
                           "where Claude can ask you first.",
                });
            }

            if (result.Success && !longConversationNoted && result.ContextTokens >= LongConversationTokens)
            {
                longConversationNoted = true;
                AddMessage(new ChatMessage
                {
                    kind = MessageKind.Info,
                    text = $"This conversation is getting long (about {FormatTokens(result.ContextTokens)} tokens). Claude re-reads all of it " +
                           "every time you ask, so answers get slower and use more of your plan. When you move on to something new, " +
                           "press New conversation.",
                });
            }

            // Show any files Claude created or edited in the Project window right away.
            AssetDatabase.Refresh();

            UpdateUi();
        }

        void StopRequest()
        {
            if (!IsBusy)
                return;

            CancelPendingRequest();
            AddMessage(new ChatMessage { kind = MessageKind.Info, text = StoppedText });
            UpdateUi();
        }

        /// <summary>Stops a running answer and notes that in the saved conversation before moving away from it.</summary>
        void LeaveCurrentConversation()
        {
            if (!IsBusy)
                return;

            CancelPendingRequest();
            messages.Add(new ChatMessage { kind = MessageKind.Info, text = StoppedText });
            SaveConversation();
        }

        void CancelPendingRequest()
        {
            EditorApplication.update -= PollRequest;
            if (IsBusy)
                ClaudeCodeRunner.Cancel(pendingRequest);
            pendingRequest = null;
            pendingContext = new List<string>();
        }

        void StartNewConversation()
        {
            LeaveCurrentConversation();
            conversationId = null;
            conversationCreatedUtcTicks = 0;
            sessionId = null;
            sentContext = new List<string>();
            longConversationNoted = false;
            messages = new List<ChatMessage>();
            historyToggle.value = false;
            RebuildMessages();
            UpdateUi();
            questionField.Focus();
        }

        void OpenInTerminal()
        {
            if (IsBusy || string.IsNullOrEmpty(sessionId) || !TryGetClaudePath(out var claudePath))
                return;

            if (ClaudeCodeRunner.OpenInTerminal(claudePath, ProjectFolder, sessionId, Model, Effort, out var error))
                AddMessage(new ChatMessage { kind = MessageKind.Info, text = TerminalText });
            else
                AddMessage(new ChatMessage { kind = MessageKind.Error, text = error });
        }

        void ShowHistory(bool show)
        {
            if (historyToggle.value != show)
                historyToggle.SetValueWithoutNotify(show);

            historyPanel.EnableInClassList("hidden", !show);
            splitView.EnableInClassList("hidden", show);
            if (show)
            {
                historySearch.SetValueWithoutNotify("");
                RebuildHistoryList();
                historySearch.schedule.Execute(() => historySearch.Q<TextField>()?.Focus());
            }
            UpdateUi();
        }

        void RebuildHistoryList()
        {
            historyList.Clear();
            var filter = (historySearch.value ?? "").Trim();
            var saved = ConversationStore.List(ProjectFolder, MaxHistoryItems)
                .Where(c => filter.Length == 0 || Matches(c, filter))
                .ToList();

            if (saved.Count == 0)
            {
                var empty = new Label(filter.Length == 0
                    ? "No saved conversations yet. They appear here after your first question."
                    : $"No conversations mention \"{filter}\".");
                empty.AddToClassList("history-empty");
                historyList.Add(empty);
                return;
            }

            foreach (var conversation in saved)
                historyList.Add(CreateHistoryRow(conversation));
        }

        static bool Matches(SavedConversation conversation, string filter) =>
            (conversation.title ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            conversation.messages.Any(m => m.kind == MessageKind.User &&
                                           (m.text ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

        VisualElement CreateHistoryRow(SavedConversation conversation)
        {
            var id = conversation.id;
            var row = new VisualElement { tooltip = "Open this conversation" };
            row.AddToClassList("history-row");
            row.EnableInClassList("history-row--current", id == conversationId);

            var text = new VisualElement();
            text.AddToClassList("history-row__text");
            var title = new Label(conversation.title);
            title.AddToClassList("history-row__title");
            var questions = conversation.messages.Count(m => m.kind == MessageKind.User);
            var meta = new Label($"{FormatWhen(conversation.updatedUtcTicks)} · {(questions == 1 ? "1 question" : questions + " questions")}");
            meta.AddToClassList("history-row__meta");
            text.Add(title);
            text.Add(meta);
            row.Add(text);

            var delete = new Button(() => DeleteConversation(id, conversation.title)) { text = "×", tooltip = "Delete this conversation" };
            delete.AddToClassList("history-row__delete");
            row.Add(delete);

            // Clicks on the delete button don't reach the row: buttons stop the click themselves.
            row.RegisterCallback<ClickEvent>(_ => OpenConversation(id));
            return row;
        }

        static string FormatWhen(long utcTicks)
        {
            var when = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();
            var today = DateTime.Now.Date;
            if (when.Date == today)
                return $"Today, {when:HH:mm}";
            if (when.Date == today.AddDays(-1))
                return $"Yesterday, {when:HH:mm}";
            return when.Year == today.Year ? $"{when:d MMM, HH:mm}" : $"{when:d MMM yyyy}";
        }

        void OpenConversation(string id)
        {
            if (id == conversationId)
            {
                ShowHistory(false);
                return;
            }

            var saved = ConversationStore.Load(ProjectFolder, id);
            if (saved == null)
            {
                ShowNotification(new GUIContent("Couldn't open that conversation. Its file may have been deleted."));
                return;
            }

            LeaveCurrentConversation();
            LoadConversation(saved);
            ShowHistory(false);
            RebuildMessages();
            UpdateUi();
            questionField.Focus();
        }

        void LoadConversation(SavedConversation saved)
        {
            conversationId = saved.id;
            conversationCreatedUtcTicks = saved.createdUtcTicks;
            sessionId = saved.sessionId;
            messages = saved.messages;
            sentContext = saved.sentContext ?? new List<string>();
            longConversationNoted = saved.longConversationNoted;

            if (!string.IsNullOrEmpty(sessionId) && !ConversationStore.CanResume(ProjectFolder, sessionId))
            {
                sessionId = null;
                sentContext = new List<string>();
                longConversationNoted = false;
                messages.Add(new ChatMessage { kind = MessageKind.Info, text = ExpiredText });
                SaveConversation();
            }
        }

        void DeleteConversation(string id, string title)
        {
            if (!EditorUtility.DisplayDialog("Delete conversation",
                    $"Delete \"{title}\" from Claude Helper's history?\n\nThis can't be undone.", "Delete", "Cancel"))
                return;

            if (id == conversationId)
            {
                StartNewConversation();
                ShowHistory(true);
            }

            try
            {
                ConversationStore.Delete(ProjectFolder, id);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Claude Helper couldn't delete the conversation file: {e.Message}");
            }

            RebuildHistoryList();
        }

        void SaveConversation()
        {
            // Nothing worth keeping until a question has been asked.
            if (!messages.Any(m => m.kind == MessageKind.User))
                return;

            if (string.IsNullOrEmpty(conversationId))
            {
                conversationId = ConversationStore.NewId();
                conversationCreatedUtcTicks = DateTime.UtcNow.Ticks;
            }

            try
            {
                ConversationStore.Save(ProjectFolder, new SavedConversation
                {
                    id = conversationId,
                    title = ConversationStore.MakeTitle(messages),
                    createdUtcTicks = conversationCreatedUtcTicks,
                    updatedUtcTicks = DateTime.UtcNow.Ticks,
                    sessionId = sessionId,
                    sentContext = sentContext,
                    longConversationNoted = longConversationNoted,
                    messages = messages,
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Claude Helper couldn't save this conversation: {e.Message}");
            }
        }

        void BrowseForClaude()
        {
            var current = ResolveClaudePath();
            var folder = current != null && File.Exists(current) ? Path.GetDirectoryName(current) : "";
            var chosen = EditorUtility.OpenFilePanel("Find Claude Code (claude.exe)", folder, "exe,cmd");
            if (!string.IsNullOrEmpty(chosen))
                claudePathField.value = chosen.Replace('/', '\\');
        }

        void UpdatePathPlaceholder()
        {
            var found = ClaudeSetup.Find();
            claudePathField.textEdition.placeholder = found == null ? "Not found. Follow the setup steps in this window." : "Found automatically: " + found;
        }

        bool TryGetClaudePath(out string claudePath)
        {
            claudePath = ResolveClaudePath();
            if (claudePath != null)
                return true;

            var saved = ClaudePath.Trim().Trim('"');
            AddMessage(new ChatMessage
            {
                kind = MessageKind.Error,
                text = saved.Length > 0
                    ? $"I couldn't find Claude Code at:\n{saved}\n\nFix the path in Settings (top right of this window), or leave it empty to find it automatically."
                    : "Claude Code isn't installed on this computer yet. Follow the steps at the top of this window, then ask again.",
            });
            setupState = SetupState.NotInstalled;
            UpdateSetupPanel();
            return false;
        }

        /// <summary>Checks in the background whether Claude Code is installed and signed in, and updates the setup panel.</summary>
        async void RefreshSetup()
        {
            if (setupCheckRunning)
                return;

            var wasNeeded = SetupNeeded;
            setupCheckRunning = true;
            SetupState state;
            try
            {
                // The await comes back on the main thread.
                state = await ClaudeSetup.CheckAsync(ResolveClaudePath());
            }
            finally
            {
                setupCheckRunning = false;
            }

            // The window was closed while checking.
            if (this == null || setupPanel == null)
                return;

            setupState = state;
            SessionState.SetBool(SetupReadySessionKey, state == SetupState.Ready);
            UpdateSetupPanel();
            UpdatePathPlaceholder();
            if (wasNeeded && state == SetupState.Ready)
                AddMessage(new ChatMessage { kind = MessageKind.Info, text = SetupDoneText });
        }

        void UpdateSetupPanel()
        {
            if (setupPanel == null)
                return;

            setupPanel.EnableInClassList("hidden", !SetupNeeded);
            setupText.text = setupState == SetupState.NotInstalled ? NotInstalledText : SignedOutText;
            installButton.SetEnabled(setupState == SetupState.NotInstalled);
            signInButton.SetEnabled(setupState == SetupState.SignedOut);
        }

        void InstallClaude()
        {
            if (!EditorUtility.DisplayDialog("Install Claude Code",
                    "This opens a PowerShell window that downloads Claude Code from claude.ai and installs it for your Windows " +
                    "account. It doesn't need administrator rights.\n\nIt runs Anthropic's official install command:\n" +
                    ClaudeSetup.InstallCommand,
                    "Install", "Cancel"))
                return;

            if (!ClaudeSetup.OpenInstaller(out var error))
                AddMessage(new ChatMessage { kind = MessageKind.Error, text = error });
        }

        void SignIn()
        {
            var claudePath = ResolveClaudePath();
            if (claudePath == null)
            {
                RefreshSetup();
                return;
            }

            if (!ClaudeSetup.OpenSignIn(claudePath, out var error))
                AddMessage(new ChatMessage { kind = MessageKind.Error, text = error });
        }

        void AddMessage(ChatMessage message)
        {
            if (messages.Count == 0)
            {
                messageList.Clear();
                messageList.RemoveFromClassList("message-list--empty");
            }
            messages.Add(message);
            messageList.Add(CreateMessageElement(message));
            ScrollToBottom();
            SaveConversation();
        }

        void RebuildMessages()
        {
            messageList.Clear();
            messageList.EnableInClassList("message-list--empty", messages.Count == 0);
            if (messages.Count == 0)
                messageList.Add(CreateEmptyState());

            foreach (var message in messages)
                messageList.Add(CreateMessageElement(message));
            ScrollToBottom();
        }

        VisualElement CreateEmptyState()
        {
            var empty = new VisualElement();
            empty.AddToClassList("empty-state");

            var title = new Label(WelcomeTitle);
            title.AddToClassList("empty-state__title");
            empty.Add(title);

            var text = new Label(WelcomeText);
            text.AddToClassList("empty-state__text");
            empty.Add(text);

            var examples = new VisualElement();
            examples.AddToClassList("example-list");
            foreach (var example in ExampleQuestions)
            {
                var chip = new Button(() =>
                {
                    questionField.value = example;
                    questionField.Focus();
                    questionField.SelectRange(example.Length, example.Length);
                }) { text = example, tooltip = "Put this question in the box below, ready to send or change" };
                chip.AddToClassList("example-chip");
                examples.Add(chip);
            }
            empty.Add(examples);
            return empty;
        }

        VisualElement CreateMessageElement(ChatMessage message)
        {
            var bubble = new VisualElement();
            bubble.AddToClassList("message");
            bubble.AddToClassList("message--" + message.kind.ToString().ToLowerInvariant());

            var author = new Label(AuthorName(message.kind));
            author.AddToClassList("message-author");
            bubble.Add(author);

            if (message.kind == MessageKind.Assistant)
            {
                bubble.Add(MessageFormatter.BuildAnswer(message.text, OnLinkClicked));
            }
            else
            {
                var body = new Label(message.text) { enableRichText = false };
                body.selection.isSelectable = true;
                body.AddToClassList("message-text");
                bubble.Add(body);
            }

            if (!string.IsNullOrEmpty(message.context))
            {
                var foldout = new Foldout { text = "Editor context sent with this question", value = false };
                foldout.AddToClassList("context-foldout");
                var contextLabel = new Label(message.context) { enableRichText = false };
                contextLabel.selection.isSelectable = true;
                contextLabel.AddToClassList("context-text");
                foldout.Add(contextLabel);
                bubble.Add(foldout);
            }

            if (message.steps != null && message.steps.Count > 0)
            {
                var foldout = new Foldout { text = $"Steps Claude took ({message.steps.Count})", value = false };
                foldout.AddToClassList("context-foldout");
                var stepsLabel = new Label("- " + string.Join("\n- ", message.steps)) { enableRichText = false };
                stepsLabel.selection.isSelectable = true;
                stepsLabel.AddToClassList("context-text");
                foldout.Add(stepsLabel);
                bubble.Add(foldout);
            }

            if (!string.IsNullOrEmpty(message.stats))
            {
                var stats = new Label(message.stats) { tooltip = StatsTooltip };
                stats.AddToClassList("message-stats");
                bubble.Add(stats);
            }

            var copyText = message.kind == MessageKind.Assistant ? MessageFormatter.ToPlainText(message.text) : message.text;
            bubble.AddManipulator(new ContextualMenuManipulator(evt =>
                evt.menu.AppendAction("Copy message", _ => EditorGUIUtility.systemCopyBuffer = copyText)));
            return bubble;
        }

        void OnLinkClicked(string target)
        {
            if (!MessageFormatter.Reveal(target))
                ShowNotification(new GUIContent($"Couldn't find \"{target}\". It may have been renamed or deleted."));
        }

        static string AuthorName(MessageKind kind)
        {
            switch (kind)
            {
                case MessageKind.User: return "You";
                case MessageKind.Assistant: return "Claude";
                case MessageKind.Error: return "Problem";
                default: return "Claude Helper";
            }
        }

        // The window shows plain text, so drop the markdown markers that would otherwise clutter answers.
        static string CleanMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "(Claude returned an empty answer.)";

            text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
            return text.Trim();
        }

        void ScrollToBottom()
        {
            messageList.schedule.Execute(() =>
                messageList.verticalScroller.value = messageList.verticalScroller.highValue).StartingIn(50);
        }

        void UpdateUi()
        {
            if (sendButton == null)
                return;

            var busy = IsBusy;
            sendButton.SetEnabled(!busy);
            terminalButton.SetEnabled(!busy && !string.IsNullOrEmpty(sessionId));
            thinkingRow.EnableInClassList("hidden", !busy);
            if (busy)
                UpdateThinkingLabel();

            var hasConversation = messages.Any(m => m.kind == MessageKind.User);
            conversationHeader.EnableInClassList("hidden", !hasConversation || historyToggle.value);
            conversationTitle.text = hasConversation ? ConversationStore.MakeTitle(messages) : "";
            conversationTitle.tooltip = string.IsNullOrEmpty(sessionId)
                ? "Claude doesn't remember this conversation, so your next question starts fresh."
                : "Claude remembers this conversation. Claude Code session: " + sessionId;
        }

        void RefreshClaudeDefaults()
        {
            claudeDefaults = ClaudeSetup.ReadDefaults(ProjectFolder);
            modelField.choices = new List<string>(ModelLabels) { [0] = DefaultLabel(claudeDefaults.model == null ? null : ModelName(claudeDefaults.model)) };
            modelField.SetValueWithoutNotify(modelField.choices[Mathf.Max(0, Array.IndexOf(ModelIds, Model))]);
            UpdateModelMenu();
        }

        static string DefaultLabel(string value) =>
            value == null ? "Your Claude Code default" : $"Your Claude Code default ({value})";

        void SetModel(string id)
        {
            Model = id;
            modelField.SetValueWithoutNotify(modelField.choices[Mathf.Max(0, Array.IndexOf(ModelIds, id))]);
            UpdateModelMenu();
        }

        void SetEffort(string id)
        {
            Effort = id;
            effortField.SetValueWithoutNotify(effortField.choices[Mathf.Max(0, Array.IndexOf(EffortIds, id))]);
            UpdateModelMenu();
        }

        void BuildModelMenu()
        {
            var menu = modelMenu.menu;
            menu.ClearItems();
            for (var i = 0; i < ModelIds.Length; i++)
            {
                var id = ModelIds[i];
                menu.AppendAction("Model/" + modelField.choices[i], _ => SetModel(id),
                    _ => Model == id ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }

            for (var i = 0; i < EffortIds.Length; i++)
            {
                var id = EffortIds[i];
                menu.AppendAction("Effort/" + effortField.choices[i], _ => SetEffort(id),
                    _ => Effort == id ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
        }

        /// <summary>Shows the model and effort that will answer in the toolbar, in Settings and in the menu.</summary>
        void UpdateModelMenu()
        {
            var model = EffectiveModel;
            var effort = EffortName(EffectiveEffort);

            // Claude Code keeps an effort per model, so the default effort changes with the model.
            effortField.choices = new List<string>(EffortLabels) { [0] = DefaultLabel(EffortName(claudeDefaults.EffortFor(model))) };
            effortField.SetValueWithoutNotify(effortField.choices[Mathf.Max(0, Array.IndexOf(EffortIds, Effort))]);
            BuildModelMenu();

            modelMenu.text = (model == null ? "Default model" : ModelName(model)) + " · " + (effort == null ? "auto effort" : effort + " effort");
            var effortSource = Effort.Length > 0 ? EffortName(Effort)
                : effort != null ? DefaultLabel(effort)
                : "not set anywhere, so Claude Code picks its built-in level";
            modelMenu.tooltip =
                $"Model: {(Model.Length > 0 ? ModelName(Model) : DefaultLabel(model == null ? null : ModelName(model)))}\n" +
                $"Effort: {effortSource}\n\n" +
                "Bigger models and higher effort handle hard questions better, but are slower and use more of your plan. Click to change.";
        }

        /// <summary>A short name for a model alias or ID, for example "Opus" for "opus" or "Opus 5.5" for "claude-opus-5-5".</summary>
        static string ModelName(string model)
        {
            var match = Regex.Match(model, @"(opus|sonnet|haiku|fable)(?:-(\d+)(?:-(\d{1,2})(?!\d))?)?", RegexOptions.IgnoreCase);
            if (!match.Success)
                return model;

            var family = match.Groups[1].Value.ToLowerInvariant();
            var name = char.ToUpperInvariant(family[0]) + family.Substring(1);
            if (match.Groups[2].Success)
                name += " " + match.Groups[2].Value + (match.Groups[3].Success ? "." + match.Groups[3].Value : "");
            return name;
        }

        static string EffortName(string effort)
        {
            switch (effort)
            {
                case null:
                case "": return null;
                case "xhigh": return "Extra high";
                default: return char.ToUpperInvariant(effort[0]) + effort.Substring(1);
            }
        }

        void UpdateThinkingLabel()
        {
            if (!IsBusy)
                return;

            // The time goes first so it stays visible when a long step is cut short with "...".
            var elapsed = pendingRequest.ElapsedSeconds;
            var dots = new string('.', 1 + (int)(elapsed * 2) % 3);
            var steps = pendingRequest.steps;
            var activity = steps != null && steps.Count > 0 ? steps[steps.Count - 1] : "Claude is thinking";
            var text = $"{(int)elapsed}s · {activity}{dots}";
            if (thinkingLabel.text != text)
                thinkingLabel.text = text;
        }

        static string FormatStats(double seconds, int steps, int contextTokens, string model, string effort)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(model))
                parts.Add(ModelName(model));
            if (!string.IsNullOrEmpty(effort))
                parts.Add(EffortName(effort).ToLowerInvariant() + " effort");
            parts.Add($"{Math.Max(1, (int)Math.Round(seconds))}s");
            if (steps > 0)
                parts.Add(steps == 1 ? "1 step" : $"{steps} steps");
            if (contextTokens > 0)
                parts.Add($"conversation: {FormatTokens(contextTokens)} tokens");
            return string.Join(" · ", parts);
        }

        static string FormatTokens(int tokens) =>
            tokens >= 1000 ? $"{(int)Math.Round(tokens / 1000.0)}k" : tokens.ToString();
    }
}
