#!/bin/sh
set -eu

if ! command -v node >/dev/null 2>&1; then
	echo "Node.js is required but was not found on PATH. Install Node.js, then run this script again." >&2
	exit 1
fi

TARGET=$(osascript <<'APPLESCRIPT'
try
	set selectedFolder to choose folder with prompt "Select the project folder to uninstall Agent Memory from"
	POSIX path of selectedFolder
on error number -128
	return ""
end try
APPLESCRIPT
)

if [ -z "$TARGET" ]; then
	echo "Uninstall cancelled."
	exit 1
fi

MODE=$(osascript <<'APPLESCRIPT'
try
	set selectedItems to choose from list {"keep-data", "delete-all"} with title "Uninstall Agent Memory" with prompt "Choose uninstall mode:" default items {"keep-data"}
	if selectedItems is false then return "__cancelled__"
	return item 1 of selectedItems
on error number -128
	return "__cancelled__"
end try
APPLESCRIPT
)

if [ "$MODE" = "__cancelled__" ]; then
	echo "Uninstall cancelled."
	exit 1
fi

if [ "$MODE" = "delete-all" ]; then
	CONFIRM=$(osascript <<'APPLESCRIPT'
display dialog "Delete all agent memory data? This removes rules, memories, reports, uncertainties, notes, and archive." buttons {"Cancel", "Delete All"} default button "Cancel" with icon caution
button returned of result
APPLESCRIPT
)
	if [ "$CONFIRM" != "Delete All" ]; then
		echo "Uninstall cancelled."
		exit 1
	fi
fi

UNINSTALLER="$TARGET/.agent-memory/tools/uninstall-project-memory.js"
if [ ! -f "$UNINSTALLER" ]; then
	echo "Missing project uninstaller: $UNINSTALLER" >&2
	exit 1
fi

node "$UNINSTALLER" --mode "$MODE"
