#!/usr/bin/env bash
# Shared Stop-hook backstop for the .agent-memory system. Used by BOTH Claude Code and Codex CLI
# (their Stop-hook contracts are identical: JSON on stdin with transcript_path + stop_hook_active,
# and a {"decision":"block","reason":"..."} response blocks the stop and re-prompts once).
#
# If the session changed code but wrote no report to .agent-memory/reports/, nudge ONCE to write a
# report (or explicitly skip if the change was trivial). The stop_hook_active guard prevents an
# infinite block loop, so the agent gets exactly one reminder.

input="$(cat)"

get_field() {
	# $1 = field name; extracts a JSON string value from the stdin payload.
	printf '%s' "$input" | grep -o "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | head -n1 | sed 's/.*:[[:space:]]*"//; s/"$//'
}

stop_active="$(printf '%s' "$input" | grep -o '"stop_hook_active"[[:space:]]*:[[:space:]]*true' | head -n1)"
if [ -n "$stop_active" ]; then
	# Already nudged once this stop cycle — let the agent's decision stand.
	exit 0
fi

transcript="$(get_field transcript_path)"
if [ -z "$transcript" ]; then
	exit 0
fi
# Normalize Windows path (C:\\Users\\... -> C:/Users/...) so bash can open it.
transcript="$(printf '%s' "$transcript" | tr -s '\\' '/')"
if [ ! -f "$transcript" ]; then
	exit 0
fi

# Did the session edit any project file (language-agnostic)? An edit "counts" only if its target
# path is OUTSIDE .agent-memory/ — writing memory files should not, by itself, demand a report.
#   Claude: tool_use Edit/Write/MultiEdit -> "file_path" values.
#   Codex:  apply_patch -> target paths on "*** Add/Update/Delete File:" or "+++ b/..." lines.
edited_paths="$(
	{
		grep -oE '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' "$transcript" | sed 's/.*:[[:space:]]*"//; s/"$//'
		grep -oE '\*\*\* (Add|Update|Delete) File: .*' "$transcript" | sed 's/^.*File: //'
		grep -oE '^\+\+\+ b/.*' "$transcript" | sed 's#^\+\+\+ b/##'
	} 2>/dev/null | tr -s '\\' '/'
)"

# Only nudge if the transcript actually shows an edit/patch tool was used at all.
if ! grep -Eq '"name"[[:space:]]*:[[:space:]]*"(Edit|Write|MultiEdit)"|apply_patch' "$transcript"; then
	exit 0
fi

# A code edit exists if at least one edited path is outside .agent-memory/.
if ! printf '%s\n' "$edited_paths" | grep -q '[^[:space:]]'; then
	exit 0
fi
if ! printf '%s\n' "$edited_paths" | grep -vq '\.agent-memory/'; then
	# Every edited path was under .agent-memory/ (e.g. only memory files touched) — no nudge.
	exit 0
fi

# Was a report written this session? Any transcript mention of an .agent-memory/reports/ path.
if grep -Eq '\.agent-memory[\\/]reports[\\/]' "$transcript"; then
	exit 0
fi

reason="You changed code this session but wrote no .agent-memory/reports/ entry. If this was non-trivial, create or update a report now and curate any durable knowledge into memories/rules (save it and announce what you saved), then update the manifest. If the change was small, very specific, or trivial, you may skip the report — briefly say so and stop."

printf '{"decision":"block","reason":"%s"}\n' "$reason"
exit 0
