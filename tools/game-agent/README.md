# Dev Bot game agent

A small Python script for the home server that lets the Discord bot restart game servers without opening anything on
the home network. The agent connects out to the dashboard over HTTPS and waits for work. When an allowed Discord user
sends a Remote TaskAgent command's keyword (for example `restart valheim`), the bot queues the command's alias, the
agent picks it up, runs the matching action from **its own** config, and reports back. The bot then edits its Discord
reply with the outcome.

- No inbound connections, port forwards or tunnels. The agent only makes outbound HTTPS requests to `apiBaseUrl`.
- The server only picks an action **name**. Nothing it sends is ever passed to a command. Unknown names are refused.
- Actions are argv lists run without a shell, so there's no quoting and no injection.
- The token sits in its own file and is never logged. Output is redacted with basic patterns and cut to its last
  1000 characters before it's sent. It shows on the dashboard's Game servers page, never in Discord.
- Standard library only. Needs Python 3.8 or newer on Linux or Windows.

## 1. Create a token

1. Sign in to the dashboard as an admin and open **Game servers** (in the Bot admin section).
2. Add an agent and give it a name, such as the server's name. The page shows a `dga_...` token **once**. Copy it now,
   because only its hash is kept, so it can't be shown again. If it's lost, revoke the agent and add a new one.
3. On the home server, save the token as the only content of `token.txt` next to the config, then lock the file down:

   Linux:
   ```sh
   sudo install -o gameagent -g gameagent -m 600 /dev/null /opt/game-agent/token.txt
   sudoedit /opt/game-agent/token.txt      # paste the token, save
   ```

   Windows (PowerShell, as an administrator; `GameAgent` is the account the agent runs as):
   ```powershell
   icacls C:\GameAgent\token.txt /inheritance:r /grant:r "GameAgent:R" "Administrators:F" "SYSTEM:F"
   ```

Revoking the agent on the Game servers page stops it straight away: its next request gets 401 and it exits with a
message saying so.

## 2. Configure

Copy `config.example.json` to `config.json` and list the actions. Each name must match the **Task alias** of a Remote
TaskAgent command on the dashboard's Commands page (1-64 letters, numbers, spaces, underscores or hyphens).

```json
{
  "apiBaseUrl": "https://devbot.gruulsgonewild.com",
  "tokenFile": "token.txt",
  "actions": {
    "valheim": { "argv": ["docker", "restart", "valheim"], "timeoutSeconds": 120 }
  }
}
```

- `apiBaseUrl` must be https. Plain `http://localhost` is allowed for local testing only. Certificates are checked
  with the system's trusted CAs, and redirects are refused.
- `tokenFile` is relative to the config file unless it's an absolute path.
- `timeoutSeconds` (1-3600, default 120): after this long the action is killed and reported as failed.
- `cwd` (optional): the folder the action runs in.

### Example actions

Docker:
```json
"valheim": { "argv": ["docker", "restart", "valheim"], "timeoutSeconds": 120 }
```

systemd unit (with the sudoers line below):
```json
"valheim": { "argv": ["sudo", "-n", "/usr/bin/systemctl", "restart", "valheim.service"], "timeoutSeconds": 120 }
```

LinuxGSM, run as the game's own user:
```json
"valheim": { "argv": ["sudo", "-n", "-u", "vhserver", "/home/vhserver/vhserver", "restart"], "cwd": "/tmp", "timeoutSeconds": 300 }
```
If the agent already runs as `vhserver`, use `"argv": ["./vhserver", "restart"], "cwd": "/home/vhserver"` instead.

Windows service:
```json
"valheim": { "argv": ["powershell", "-NoProfile", "-NonInteractive", "-Command", "Restart-Service -Name ValheimServer"], "timeoutSeconds": 120 }
```

Windows Task Scheduler task (what the old TaskAgent mapped aliases to; JSON needs the backslashes doubled):
```json
"valheim": { "argv": ["schtasks", "/Run", "/TN", "\\Servers\\ValheimRestart"], "timeoutSeconds": 60 }
```
`schtasks /Run` only starts the task, so "Done" means it started, not that it finished.

Several aliases can live in one config, such as `"valheim"` and `"valheim backup"`. With more than one agent, the bot
sends each command to the online agent that lists its alias. With a single agent, everything goes to it.

## 3. Try it

```sh
python3 game_agent.py --config config.json
```

You should see `Connected to https://devbot.gruulsgonewild.com offering valheim`. The Game servers page then shows the
agent online with its actions. Send the bot the command's keyword in a DM, and the run shows in the agent's log and on
the page. Stop it with Ctrl+C.

If the connection drops, the agent backs off (1s, doubling up to 60s, with jitter) and reconnects. On 401 it stops,
because a wrong or revoked token won't fix itself.

## 4. Run it as a service

### Linux (systemd)

Run the agent as an unprivileged user that can only do the exact commands it needs:

```sh
sudo useradd --system --home /opt/game-agent --shell /usr/sbin/nologin gameagent
sudo mkdir -p /opt/game-agent
sudo cp game_agent.py /opt/game-agent/
sudo cp config.example.json /opt/game-agent/config.json   # then edit it
sudo chown -R gameagent:gameagent /opt/game-agent
sudo chmod 600 /opt/game-agent/token.txt /opt/game-agent/config.json
```

Allow exactly the configured command and nothing else with `sudo visudo -f /etc/sudoers.d/game-agent`:

```text
gameagent ALL=(root) NOPASSWD: /usr/bin/systemctl restart valheim.service
# or, for Docker:
gameagent ALL=(root) NOPASSWD: /usr/bin/docker restart valheim
# or, for LinuxGSM:
gameagent ALL=(vhserver) NOPASSWD: /home/vhserver/vhserver restart
```

With the Docker line, the action's argv is `["sudo", "-n", "/usr/bin/docker", "restart", "valheim"]`. Adding
`gameagent` to the `docker` group also works, but that group is root-equivalent on the host, so the sudoers line is
the safer choice.

`/etc/systemd/system/game-agent.service`:

```ini
[Unit]
Description=Dev Bot game agent
After=network-online.target
Wants=network-online.target

[Service]
User=gameagent
WorkingDirectory=/opt/game-agent
ExecStart=/usr/bin/python3 /opt/game-agent/game_agent.py --config /opt/game-agent/config.json
Restart=on-failure
RestartSec=10
# Exit code 2 means the token was refused: don't loop on it.
RestartPreventExitStatus=2

[Install]
WantedBy=multi-user.target
```

Don't add `NoNewPrivileges=true` if actions use sudo, because it blocks sudo. Then run:

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now game-agent
journalctl -u game-agent -f
```

### Windows (Scheduled Task at startup)

Run it as a dedicated local account that has rights to the service or task it restarts. Put the files in
`C:\GameAgent`, lock down `token.txt` as shown above, then in an administrator PowerShell:

```powershell
$action  = New-ScheduledTaskAction -Execute "C:\Program Files\Python312\python.exe" `
             -Argument '"C:\GameAgent\game_agent.py" --config "C:\GameAgent\config.json"' -WorkingDirectory "C:\GameAgent"
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
             -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable
Register-ScheduledTask -TaskName "Dev Bot game agent" -Action $action -Trigger $trigger -Settings $settings `
  -User "GameAgent" -Password (Read-Host "GameAgent password") -RunLevel Limited
Start-ScheduledTask -TaskName "Dev Bot game agent"
```

To run it as a real Windows service instead, wrap the same command line with a service wrapper such as WinSW or NSSM,
running as the same account.

`Restart-Service` needs rights to that service. Either run the agent as an account that has them, or grant the
account start/stop on just that service (for example with `sc.exe sdset`) rather than making it an administrator.

## Tests

```sh
python -m unittest
```

They run from this folder, need no network, and start a local fake API for the end-to-end test.
