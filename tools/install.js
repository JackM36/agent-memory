const fs = require("fs");
const path = require("path");
const initProjectMemory = require("../Source/.agent-memory/tools/init-project-memory");
const hookConfig = require("../Source/.agent-memory/tools/hook-config");

const repoRoot = path.resolve(__dirname, "..");
const defaultSourceRoot = path.join(repoRoot, "Source");

const agentGroups = {
	claude: [".claude"],
	codex: [".agents", ".codex"],
	gemini: [".gemini"],
	opencode: [".opencode"]
};

function main() {
	var options = parseArgs(process.argv.slice(2));
	install(options);
}

function parseArgs(args) {
	var options = {
		agents: [],
		claude: "reference",
		force: false,
		hooks: true,
		source: defaultSourceRoot,
		target: null
	};

	for (var i = 0; i < args.length; i++) {
		var arg = args[i];

		if (arg === "--target") {
			options.target = readOptionValue(args, ++i, arg);
			continue;
		}

		if (arg === "--source") {
			options.source = readOptionValue(args, ++i, arg);
			continue;
		}

		if (arg === "--agents") {
			options.agents = parseAgents(readOptionValue(args, ++i, arg));
			continue;
		}

		if (arg === "--claude") {
			options.claude = readOptionValue(args, ++i, arg);
			continue;
		}

		if (arg === "--force") {
			options.force = true;
			continue;
		}

		if (arg === "--no-hooks") {
			options.hooks = false;
			continue;
		}

		if (arg === "--hooks") {
			options.hooks = true;
			continue;
		}

		if (arg === "--help" || arg === "-h") {
			printHelp();
			process.exit(0);
		}

		throw new Error("Unknown option: " + arg);
	}

	if (!options.target) {
		throw new Error("Missing required --target path.");
	}

	if (["none", "reference", "full"].indexOf(options.claude) < 0) {
		throw new Error("--claude must be one of: none, reference, full");
	}

	return options;
}

function readOptionValue(args, index, optionName) {
	if (index >= args.length || args[index].startsWith("--")) {
		throw new Error("Missing value for " + optionName);
	}

	return args[index];
}

function parseAgents(value) {
	if (value === "none") {
		return [];
	}

	if (value === "all") {
		return Object.keys(agentGroups);
	}

	var agents = value.split(",").map(function (agent) {
		return agent.trim();
	}).filter(Boolean);

	for (var i = 0; i < agents.length; i++) {
		if (!agentGroups[agents[i]]) {
			throw new Error("Unknown agent integration: " + agents[i]);
		}
	}

	return agents;
}

function printHelp() {
	console.log("Usage: node tools/install.js --target path [--agents none|all|claude,codex,gemini,opencode] [--claude reference|full|none] [--no-hooks] [--force]");
}

function install(options) {
	var sourceRoot = path.resolve(options.source);
	var targetRoot = path.resolve(options.target);

	if (!fs.existsSync(sourceRoot)) {
		throw new Error("Source does not exist: " + sourceRoot);
	}

	if (!fs.existsSync(targetRoot)) {
		throw new Error("Target does not exist: " + targetRoot);
	}

	var installPaths = [".agent-memory"].concat(getIntegrationPaths(options.agents));

	for (var i = 0; i < installPaths.length; i++) {
		copyPath(sourceRoot, targetRoot, installPaths[i], options.force);
	}

	var initResult = initProjectMemory.initializeProject(targetRoot, { claude: options.claude });

	console.log("Installed agent memory into " + targetRoot);
	console.log("Agent integrations: " + (options.agents.length > 0 ? options.agents.join(", ") : "none"));
	for (var j = 0; j < initResult.messages.length; j++) {
		console.log(initResult.messages[j]);
	}

	if (options.hooks) {
		var hookMessages = hookConfig.installHooks(targetRoot, options.agents);
		for (var k = 0; k < hookMessages.length; k++) {
			console.log(hookMessages[k]);
		}
	}
	console.log("Next: run node .agent-memory/tools/validate-memory.js from the target project.");
	if (options.agents.indexOf("opencode") >= 0) {
		console.log("OpenCode: quit and restart OpenCode so new command, agent, and skill files load.");
	}
}

function getIntegrationPaths(agents) {
	var paths = [];

	for (var i = 0; i < agents.length; i++) {
		var groupPaths = agentGroups[agents[i]];
		for (var j = 0; j < groupPaths.length; j++) {
			if (paths.indexOf(groupPaths[j]) < 0) {
				paths.push(groupPaths[j]);
			}
		}
	}

	return paths;
}

function copyPath(sourceRoot, targetRoot, relativePath, force) {
	var sourcePath = path.join(sourceRoot, relativePath);
	var targetPath = path.join(targetRoot, relativePath);

	if (!fs.existsSync(sourcePath)) {
		throw new Error("Missing source path: " + sourcePath);
	}

	var stat = fs.statSync(sourcePath);
	if (stat.isDirectory()) {
		var entries = fs.readdirSync(sourcePath);
		for (var i = 0; i < entries.length; i++) {
			copyPath(sourceRoot, targetRoot, path.join(relativePath, entries[i]), force);
		}
		return;
	}

	if (!stat.isFile()) {
		return;
	}

	var sourceContent = fs.readFileSync(sourcePath);
	if (fs.existsSync(targetPath) && !force) {
		var targetContent = fs.readFileSync(targetPath);
		if (!sourceContent.equals(targetContent)) {
			throw new Error("Target file already exists. Use --force to overwrite: " + targetPath);
		}
	}

	fs.mkdirSync(path.dirname(targetPath), { recursive: true });
	fs.writeFileSync(targetPath, sourceContent);
}

if (require.main === module) {
	main();
}

module.exports = {
	install: install,
	parseArgs: parseArgs
};
