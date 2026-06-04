# LLM Command Instructions

Follow these repo-level command rules exactly when the user gives short operational commands.

## Push

When the user says `push`, `commit and push`, or otherwise asks to push changes, do not run `git push` directly.

Run the shared PowerShell helper instead:

```powershell
D:\Code\commit-and-push-all.ps1
```

For non-interactive LLM/tool usage, pass a commit message:

```powershell
D:\Code\commit-and-push-all.ps1 -CommitMessage "Describe the change"
```

The push script loops through these four repos:

- `D:\Code\dev-api`
- `D:\Code\dev-bot`
- `D:\Code\dev-client`
- `D:\Code\dev-ui`

It prints uncommitted changes for each repo, prompts for a commit message when a repo has changes and no `-CommitMessage` is provided, commits those changes, and then pushes the repo.

## Deploy

When the user says `deploy` or otherwise asks to deploy the project, run the shared deploy helper:

```powershell
D:\Code\deploy.ps1
```

Do not replace this with repo-specific deploy commands unless the user explicitly asks for a different deploy path.
