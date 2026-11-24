#!/usr/bin/env bash
# Is Skyrim running and responding? Prints "responding", "not responding" or "not running" (Windows' own view of the window).
powershell.exe -NoProfile -Command '$p = Get-Process SkyrimSE -ErrorAction SilentlyContinue; if (-not $p) { "not running" } elseif ($p.Responding) { "responding" } else { "not responding" }' | tr -d '\r'
