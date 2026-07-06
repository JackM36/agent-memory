const fs = require("fs");
const path = require("path");
const initProjectMemory = require("./init-project-memory");
const hookConfig = require("./hook-config");
const tools = require("./memory-tool-utils");

const systemPaths = [
	"README.md",
	"manifest.md",
	"protocol.md",
	"commands.md",
	"config.md",
	"agents",
	"commands",
	"hooks",
	"templates",
	"tools"
];

const dataPaths = [
	"rules",
	"memories",
	"uncertainties",
	"reports",
	"notes",
	"archive"
];

const wrapperPaths = [
	".claude/commands/memory-add.md",
	".claude/commands/memory-init.md",
	".claude/commands/memory-query.md",
	".claude/commands/memory-file-history.md",
	".claude/commands/memory-refresh.md",
	".claude/commands/memory-prune.md",
	".claude/commands/memory-resolve.md",
	".claude/commands/memory-scout.md",
	".claude/agents/memory-scout.md",
	".claude/skills/agent-memory",
	".opencode/command/memory-add.md",
	".opencode/command/memory-init.md",
	".opencode/command/memory-query.md",
	".opencode/command/memory-file-history.md",
	".opencode/command/memory-refresh.md",
	".opencode/command/memory-prune.md",
	".opencode/command/memory-resolve.md",
	".opencode/command/memory-scout.md",
	".opencode/agent/memory-scout.md",
	".opencode/skills/agent-memory",
	".agents/skills/agent-memory",
	".codex/agents/memory-scout.toml",
	".gemini/commands/memory-add.toml",
	".gemini/commands/memory-init.toml",
	".gemini/commands/memory-query.toml",
	".gemini/commands/memory-file-history.toml",
	".gemini/commands/memory-refresh.toml",
	".gemini/commands/memory-prune.toml",
	".gemini/commands/memory-resolve.toml",
	".gemini/commands/memory-scout.toml",
	".gemini/agents/memory-scout.md"
];

function main() {
	var options = parseArgs(process.argv.slice(2));
	var projectRoot = options.target ? path.resolve(options.target) : tools.findProjectRoot(process.cwd());
	uninstallProjectMemory(projectRoot, options.mode);
}

function parseArgs(args) {
	var options = {
		mode: "keep-data",
		target: null
	};

	for (var i = 0; i < args.length; i++) {
		var arg = args[i];

		if (arg === "--mode") {
			options.mode = readOptionValue(args, ++i, arg);
			continue;
		}

		if (arg === "--target") {
			options.target = readOptionValue(args, ++i, arg);
			continue;
		}

		if (arg === "--help" || arg === "-h") {
			printHelp();
			process.exit(0);
		}

		throw new Error("Unknown option: " + arg);
	}

	if (["keep-data", "delete-all"].indexOf(options.mode) < 0) {
		throw new Error("--mode must be one of: keep-data, delete-all");
	}

	return options;
}

function readOptionValue(args, index, optionName) {
	if (index >= args.length || args[index].startsWith("--")) {
		throw new Error("Missing value for " + optionName);
	}

	return args[index];
}

function printHelp() {
	console.log("Usage: node .agent-memory/tools/uninstall-project-memory.js [--mode keep-data|delete-all] [--target path]");
}

function uninstallProjectMemory(projectRoot, mode) {
	removeMarkedSection(path.join(projectRoot, "AGENTS.md"));
	removeMarkedSection(path.join(projectRoot, "CLAUDE.md"));
	removeWrappers(projectRoot);

	var hookMessages = hookConfig.uninstallHooks(projectRoot);
	for (var h = 0; h < hookMessages.length; h++) {
		console.log(hookMessages[h]);
	}

	if (mode === "delete-all") {
		removePath(path.join(projectRoot, ".agent-memory"));
		console.log("Deleted all agent memory files from " + projectRoot);
		return;
	}

	removeSystemFiles(projectRoot);
	console.log("Removed agent memory system files from " + projectRoot);
	console.log("Kept project memory data folders: " + dataPaths.join(", "));
}

function removeMarkedSection(filePath) {
	if (!fs.existsSync(filePath)) {
		return;
	}

	var content = fs.readFileSync(filePath, "utf8").replace(/\r\n/g, "\n").replace(/\r/g, "\n");
	var beginIndex = content.indexOf(initProjectMemory.beginMarker);
	var endIndex = content.indexOf(initProjectMemory.endMarker);

	if (beginIndex < 0 || endIndex < beginIndex) {
		return;
	}

	var before = content.slice(0, beginIndex).trimEnd();
	var after = content.slice(endIndex + initProjectMemory.endMarker.length).trimStart();
	var updated = [before, after].filter(Boolean).join("\n\n");
	fs.writeFileSync(filePath, updated ? updated + "\n" : "", "utf8");
}

function removeWrappers(projectRoot) {
	for (var i = 0; i < wrapperPaths.length; i++) {
		var targetPath = path.join(projectRoot, wrapperPaths[i]);
		removePath(targetPath);
		removeEmptyParents(projectRoot, path.dirname(targetPath));
	}
}

function removeSystemFiles(projectRoot) {
	var memoryRoot = path.join(projectRoot, ".agent-memory");
	for (var i = 0; i < systemPaths.length; i++) {
		removePath(path.join(memoryRoot, systemPaths[i]));
	}

	for (var j = 0; j < dataPaths.length; j++) {
		var dataPath = path.join(memoryRoot, dataPaths[j]);
		if (!fs.existsSync(dataPath)) {
			fs.mkdirSync(dataPath, { recursive: true });
		}
	}
}

function removePath(targetPath) {
	if (!fs.existsSync(targetPath)) {
		return;
	}

	var stat = fs.lstatSync(targetPath);
	if (stat.isDirectory()) {
		fs.rmSync(targetPath, { recursive: true, force: true });
		return;
	}

	fs.unlinkSync(targetPath);
}

function removeEmptyParents(projectRoot, directoryPath) {
	var currentDirectory = directoryPath;
	while (currentDirectory.indexOf(projectRoot) === 0 && currentDirectory !== projectRoot) {
		if (!fs.existsSync(currentDirectory)) {
			currentDirectory = path.dirname(currentDirectory);
			continue;
		}

		if (fs.readdirSync(currentDirectory).length > 0) {
			return;
		}

		fs.rmdirSync(currentDirectory);
		currentDirectory = path.dirname(currentDirectory);
	}
}

if (require.main === module) {
	main();
}

module.exports = {
	parseArgs: parseArgs,
	uninstallProjectMemory: uninstallProjectMemory
};
