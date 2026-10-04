# Claude Helper

A Unity Editor window (**Window > Claude Helper**) that runs [Claude Code](https://claude.com/claude-code) in your project. Ask it to explain Unity, or to make changes for you. It is written for beginners, and it's Editor-only, so it is never included in builds.

## What you need

- Windows
- Unity 6 (6000.0 or newer)
- [Git for Windows](https://git-scm.com/download/win). Unity uses it to download this package from GitHub.
- A paid Claude plan, such as Pro or Max. The window uses your Claude subscription, never an API key.

You don't need to install Claude Code yourself. The window does that for you (see below).

## Install

In your Unity project, open **Window > Package Manager**, press **+**, choose **Install package from git URL...**, and paste:

```
https://github.com/EBacter/claude-helper.git#v1.1.0
```

Or add this line to the `dependencies` in your project's `Packages/manifest.json`:

```json
"com.local.claude-helper": "https://github.com/EBacter/claude-helper.git#v1.1.0",
```

The `#v1.1.0` at the end pins a release, so your project only changes when you choose to update.

## First run

Open **Window > Claude Helper**. If Claude Code isn't set up on this computer yet, the window shows a short setup panel:

1. **Install Claude Code** opens PowerShell and runs Anthropic's official installer (`irm https://claude.ai/install.ps1 | iex`). It installs for your Windows account only and doesn't need administrator rights.
2. **Sign in** opens a terminal and signs Claude Code in with your Claude account in the browser.

The panel checks again when you come back to the window, then disappears. If you already have Claude Code installed, the window finds it on its own. You can also set the path yourself in **Settings**.

## What it does, and what to know

- Every question is sent with a short description of your Editor state: the open scene and its hierarchy, the selected object and its settings, and recent Console errors. You can see exactly what was sent under **Editor context sent with this question**.
- Claude Code runs in your project folder with all its tools, settings, skills and MCP servers, in **auto mode**. Auto mode approves routine actions and blocks risky ones. Nobody can answer a permission prompt in the background, so anything that would need one is skipped and listed in the window. Use **Open in terminal** to continue the conversation where Claude can ask you first.
- Claude can change your project's files, so commit your work to version control often. That makes any change easy to undo.
- Conversations are saved in the project's `UserSettings/ClaudeHelper` folder, which Unity's standard `.gitignore` doesn't commit.

## License

[MIT](LICENSE.md)
