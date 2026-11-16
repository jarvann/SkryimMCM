#!/usr/bin/env bash
# Send one request to the running plugin's named pipe (\\.\pipe\SkyrimMCP) from WSL and print the reply.
# Lets a Claude Code session query the live game without Claude Desktop.
#
#   scripts/pipe-call.sh ping
#   scripts/pipe-call.sh get_script_functions '{"className":"NiOverride"}'
#
# The plugin serves one client at a time: this only connects while no MCP server holds the pipe
# (Claude Desktop closed, or its server stopped). Read-only actions are safe at any time; anything
# that changes the game acts on it for real.
set -euo pipefail

action="${1:?usage: pipe-call.sh <action> [params-json] [timeout-ms]}"
params="${2:-{\}}"
timeout="${3:-15000}"

request=$(printf '{"id":"wsl-%s","action":"%s","params":%s}' "$(date +%s%N)" "$action" "$params")
b64=$(printf '%s' "$request" | base64 -w0)

powershell.exe -NoProfile -NonInteractive -Command "
\$req = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$b64'))
\$pipe = New-Object IO.Pipes.NamedPipeClientStream('.', 'SkyrimMCP', [IO.Pipes.PipeDirection]::InOut)
try { \$pipe.Connect(3000) } catch { Write-Output '{\"success\":false,\"error\":\"pipe busy or missing: the game must be running and no other client (Claude Desktop) connected\"}'; exit 2 }
\$w = New-Object IO.StreamWriter(\$pipe, (New-Object Text.UTF8Encoding(\$false))); \$w.AutoFlush = \$true
\$r = New-Object IO.StreamReader(\$pipe, [Text.Encoding]::UTF8)
\$w.WriteLine(\$req)
\$task = \$r.ReadLineAsync()
if (-not \$task.Wait($timeout)) { Write-Output '{\"success\":false,\"error\":\"timed out\"}'; exit 3 }
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Write-Output \$task.Result
\$pipe.Dispose()
" | tr -d '\r'
