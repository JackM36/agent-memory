const fs = require("fs");
const path = require("path");
const tools = require("./memory-tool-utils");

// Identity marker: any Stop hook whose command references this script is "ours" — used for both
// idempotent install and precise uninstall, since JSON files can't carry comment markers.
const scriptMarker = "report-nudge.sh";
const scriptRelativePath = ".agent-memory/hooks/report-nudge.sh";
const fragmentsRelativeDir = ".agent-memory/hooks/fragments";

// Which config file and fragment each agent integration wires up.
const targets = {
	claude: { file: ".claude/settings.local.json", fragment: "claude-settings.hooks.json" },
	codex: { file: ".codex/hooks.json", fragment: "codex-hooks.json" }
};

function readJson(filePath) {
	// Returns the parsed object, or null if the file does not exist. Throws on invalid JSON.
	if (!fs.existsSync(filePath)) {
		return null;
	}

	var raw = tools.normalizeLineEndings(fs.readFileSync(filePath, "utf8")).trim();
	if (!raw) {
		return {};
	}

	return JSON.parse(raw);
}

function writeJson(filePath, value) {
	tools.writeText(filePath, JSON.stringify(value, null, 2) + "\n");
}

function stopGroupIsOurs(group) {
	if (!group || !Array.isArray(group.hooks)) {
		return false;
	}

	for (var i = 0; i < group.hooks.length; i++) {
		var command = group.hooks[i] && group.hooks[i].command;
		if (typeof command === "string" && command.indexOf(scriptMarker) >= 0) {
			return true;
		}
	}

	return false;
}

function makeScriptExecutable(projectRoot) {
	var scriptPath = path.join(projectRoot, scriptRelativePath);
	try {
		fs.chmodSync(scriptPath, 0o755);
	} catch (error) {
		// Best-effort; Windows and read-only filesystems can ignore this.
	}
}

function installHooks(projectRoot, agents) {
	var messages = [];
	var agentList = agents || [];

	for (var i = 0; i < agentList.length; i++) {
		var target = targets[agentList[i]];
		if (!target) {
			continue;
		}

		var filePath = path.join(projectRoot, target.file);
		var fragmentPath = path.join(projectRoot, fragmentsRelativeDir, target.fragment);

		if (!fs.existsSync(fragmentPath)) {
			messages.push("Skipped " + target.file + " (missing fragment " + target.fragment + ").");
			continue;
		}

		var existing;
		try {
			existing = readJson(filePath);
		} catch (error) {
			messages.push("Skipped " + target.file + " (existing file is not valid JSON; left untouched).");
			continue;
		}

		var config = existing || {};
		if (config.hooks !== undefined && (typeof config.hooks !== "object" || Array.isArray(config.hooks))) {
			messages.push("Skipped " + target.file + " (unexpected 'hooks' shape; left untouched).");
			continue;
		}
		config.hooks = config.hooks || {};

		if (config.hooks.Stop !== undefined && !Array.isArray(config.hooks.Stop)) {
			messages.push("Skipped " + target.file + " (unexpected 'hooks.Stop' shape; left untouched).");
			continue;
		}
		config.hooks.Stop = config.hooks.Stop || [];

		var alreadyPresent = false;
		for (var s = 0; s < config.hooks.Stop.length; s++) {
			if (stopGroupIsOurs(config.hooks.Stop[s])) {
				alreadyPresent = true;
				break;
			}
		}

		if (alreadyPresent) {
			messages.push(target.file + ": Stop hook already present.");
			makeScriptExecutable(projectRoot);
			continue;
		}

		var fragment = JSON.parse(tools.normalizeLineEndings(fs.readFileSync(fragmentPath, "utf8")));
		var group = fragment.hooks.Stop[0];
		config.hooks.Stop.push(group);
		writeJson(filePath, config);
		makeScriptExecutable(projectRoot);
		messages.push(existing ? target.file + ": added Stop hook." : target.file + ": created with Stop hook.");
	}

	return messages;
}

function uninstallHooks(projectRoot) {
	var messages = [];
	var keys = Object.keys(targets);

	for (var i = 0; i < keys.length; i++) {
		var target = targets[keys[i]];
		var filePath = path.join(projectRoot, target.file);

		if (!fs.existsSync(filePath)) {
			continue;
		}

		var config;
		try {
			config = readJson(filePath);
		} catch (error) {
			messages.push("Skipped " + target.file + " (not valid JSON; left untouched).");
			continue;
		}

		if (!config || !config.hooks || !Array.isArray(config.hooks.Stop)) {
			continue;
		}

		var kept = [];
		var removed = false;
		for (var s = 0; s < config.hooks.Stop.length; s++) {
			if (stopGroupIsOurs(config.hooks.Stop[s])) {
				removed = true;
				continue;
			}
			kept.push(config.hooks.Stop[s]);
		}

		if (!removed) {
			continue;
		}

		config.hooks.Stop = kept;
		if (config.hooks.Stop.length === 0) {
			delete config.hooks.Stop;
		}
		if (Object.keys(config.hooks).length === 0) {
			delete config.hooks;
		}

		if (Object.keys(config).length === 0) {
			fs.unlinkSync(filePath);
			messages.push(target.file + ": removed Stop hook and deleted now-empty file.");
			continue;
		}

		writeJson(filePath, config);
		messages.push(target.file + ": removed Stop hook.");
	}

	return messages;
}

module.exports = {
	installHooks: installHooks,
	uninstallHooks: uninstallHooks,
	scriptMarker: scriptMarker
};
