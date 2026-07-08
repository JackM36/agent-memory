#!/usr/bin/env bash
# Shared Stop-hook backstop for the .agent-memory system. Used by BOTH Claude Code and Codex CLI
# (their Stop-hook contracts are identical: JSON on stdin with transcript_path + stop_hook_active,
# and a {"decision":"block","reason":"..."} response blocks the stop and re-prompts once).
#
# If the session edited project files but wrote nothing under .agent-memory/reports/, nudge ONCE to
# write a report (or explicitly skip if the change was trivial). The stop_hook_active guard prevents
# an infinite block loop, so the agent gets exactly one reminder.

input="$(cat)"

stop_active="$(printf '%s' "$input" | grep -o '"stop_hook_active"[[:space:]]*:[[:space:]]*true' | head -n1)"
if [ -n "$stop_active" ]; then
	# Already nudged once this stop cycle — let the agent's decision stand.
	exit 0
fi

transcript="$(printf '%s' "$input" | grep -o '"transcript_path"[[:space:]]*:[[:space:]]*"[^"]*"' | head -n1 | sed 's/.*:[[:space:]]*"//; s/"$//')"
if [ -z "$transcript" ]; then
	exit 0
fi
# Normalize Windows path (C:\\Users\\... -> C:/Users/...) so bash can open it.
transcript="$(printf '%s' "$transcript" | tr -s '\\' '/')"
if [ ! -f "$transcript" ]; then
	exit 0
fi

# Collect paths the session actually EDITED (not merely read or mentioned):
#   Claude: tool_use blocks named Edit/Write/MultiEdit -> the file_path inside their "input" object.
#           file_path is only captured when it directly follows one of those tool names, so Read
#           calls and paths quoted in conversation text never count.
#   Codex:  apply_patch hunks -> "*** Add/Update/Delete File:" target paths (path ends at the JSON
#           string escape for the newline).
edited_paths="$(
	{
		grep -oE '"name"[[:space:]]*:[[:space:]]*"(Edit|Write|MultiEdit)"[[:space:]]*,[[:space:]]*"input"[[:space:]]*:[[:space:]]*\{[[:space:]]*"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' "$transcript" | sed 's/.*"file_path"[[:space:]]*:[[:space:]]*"//; s/"$//'
		grep -oE '\*\*\* (Add|Update|Delete) File: [^"\\]+' "$transcript" | sed 's/^.*File: //'
	} 2>/dev/null | tr -s '\\' '/'
)"

# No edits at all -> nothing to nudge about.
if ! printf '%s\n' "$edited_paths" | grep -q '[^[:space:]]'; then
	exit 0
fi

# A code edit exists only if at least one EDITED path is outside .agent-memory/ — writing memory
# files should not, by itself, demand a report.
if ! printf '%s\n' "$edited_paths" | grep -v '\.agent-memory/' | grep -q '[^[:space:]]'; then
	exit 0
fi

# A report was written only if an EDITED path is under .agent-memory/reports/ (mentioning or
# reading a report does not count).
if printf '%s\n' "$edited_paths" | grep -q '\.agent-memory/reports/'; then
	exit 0
fi

reason="You changed code this session but wrote no .agent-memory/reports/ entry. If this was non-trivial, create or update a report now and curate any durable knowledge into memories/rules (save it and announce what you saved), then run node .agent-memory/tools/refresh-manifest.js. If the change was small, very specific, or trivial, you may skip the report — briefly say so and stop."

printf '{"decision":"block","reason":"%s"}\n' "$reason"
exit 0
