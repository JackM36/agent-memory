#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
INSTALLER="$SCRIPT_DIR/tools/install-interactive.js"

if ! command -v node >/dev/null 2>&1; then
	echo "Node.js is required but was not found on PATH. Install Node.js, then run this script again." >&2
	exit 1
fi

TARGET=$(osascript <<'APPLESCRIPT'
try
	set selectedFolder to choose folder with prompt "Select the target project folder"
	POSIX path of selectedFolder
on error number -128
	return ""
end try
APPLESCRIPT
)

if [ -z "$TARGET" ]; then
	echo "Install cancelled."
	exit 1
fi

AGENTS=$(osascript <<'APPLESCRIPT'
try
	set selectedItems to choose from list {"claude", "codex", "gemini", "opencode"} with title "Agent Memory Integrations" with prompt "Choose native integrations to install:" with multiple selections allowed
	if selectedItems is false then return "__cancelled__"
	set AppleScript's text item delimiters to ","
	set selectedText to selectedItems as text
	set AppleScript's text item delimiters to ""
	if selectedText is "" then return "none"
	return selectedText
on error number -128
	return "__cancelled__"
end try
APPLESCRIPT
)

if [ "$AGENTS" = "__cancelled__" ]; then
	echo "Install cancelled."
	exit 1
fi

CLAUDE=$(osascript <<'APPLESCRIPT'
try
	set selectedItems to choose from list {"reference", "full", "none"} with title "CLAUDE.md Setup" with prompt "Choose CLAUDE.md behavior:" default items {"reference"}
	if selectedItems is false then return "__cancelled__"
	return item 1 of selectedItems
on error number -128
	return "__cancelled__"
end try
APPLESCRIPT
)

if [ "$CLAUDE" = "__cancelled__" ]; then
	echo "Install cancelled."
	exit 1
fi

HOOKS_ARG=""
case ",$AGENTS," in
	*,claude,* | *,codex,*)
		HOOKS_CHOICE=$(osascript <<'APPLESCRIPT'
try
	display dialog "Install the Stop-hook enforcement for Claude/Codex? It nudges the agent to write a report when a session changes code but records none." with title "Agent Memory Hooks" buttons {"No", "Yes"} default button "Yes"
	return button returned of result
on error number -128
	return "__cancelled__"
end try
APPLESCRIPT
)
		if [ "$HOOKS_CHOICE" = "__cancelled__" ]; then
			echo "Install cancelled."
			exit 1
		fi
		if [ "$HOOKS_CHOICE" = "No" ]; then
			HOOKS_ARG="--no-hooks"
		else
			HOOKS_ARG="--hooks"
		fi
		;;
esac

node "$INSTALLER" --target "$TARGET" --agents "$AGENTS" --claude "$CLAUDE" $HOOKS_ARG
