# LLM Git Push Instructions

For this repository, do not run `git push` directly.

When asked to commit or push changes, use the shared PowerShell helper instead:

```powershell
D:\Code\commit-and-push-all.ps1
```

For non-interactive LLM/tool usage, pass a commit message:

```powershell
D:\Code\commit-and-push-all.ps1 -CommitMessage "Describe the change"
```

The script loops through these four repos:

- `D:\Code\dev-api`
- `D:\Code\dev-bot`
- `D:\Code\dev-client`
- `D:\Code\dev-ui`

It prints uncommitted changes for each repo, prompts for a commit message when a repo has changes and no `-CommitMessage` is provided, commits those changes, and then pushes the repo.
