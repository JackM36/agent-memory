const childProcess = require("child_process");
const fs = require("fs");
const path = require("path");
const readline = require("readline");
const installer = require("./install");

const knownAgents = ["claude", "codex", "gemini", "opencode"];

async function main() {
	var options = parseArgs(process.argv.slice(2));

	if (!options.target) {
		options.target = await prompt("Target project folder: ");
	}

	if (!options.agentsValue) {
		options.agentsValue = await promptAgents();
	}

	if (!options.claude) {
		options.claude = await promptClaudeMode();
	}

	var agents = parseAgents(options.agentsValue);
	var hasHookableAgent = agents.indexOf("claude") >= 0 || agents.indexOf("codex") >= 0;
	if (options.hooks && !options.hooksFlagPassed && hasHookableAgent) {
		options.hooks = await promptHooks();
	}

	var installOptions = {
		agents: agents,
		claude: options.claude,
		force: options.force,
		hooks: options.hooks,
		source: path.resolve(__dirname, "..", "Source"),
		target: path.resolve(options.target)
	};

	installer.install(installOptions);
	validateTarget(installOptions.target);
}

function parseArgs(args) {
	var options = {
		agentsValue: null,
		claude: null,
		force: false,
		hooks: true,
		hooksFlagPassed: false,
		target: null
	};

	for (var i = 0; i < args.length; i++) {
		var arg = args[i];

		if (arg === "--target") {
			options.target = readOptionValue(args, ++i, arg);
			continue;
		}

		if (arg === "--agents") {
			options.agentsValue = readOptionValue(args, ++i, arg);
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
			options.hooksFlagPassed = true;
			continue;
		}

		if (arg === "--hooks") {
			options.hooks = true;
			options.hooksFlagPassed = true;
			continue;
		}

		if (arg === "--help" || arg === "-h") {
			printHelp();
			process.exit(0);
		}

		throw new Error("Unknown option: " + arg);
	}

	if (options.claude && ["none", "reference", "full"].indexOf(options.claude) < 0) {
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

function printHelp() {
	console.log("Usage: node tools/install-interactive.js [--target path] [--agents none|all|claude,codex,gemini,opencode] [--claude reference|full|none] [--no-hooks] [--force]");
}

async function promptAgents() {
	console.log("Select integrations:");
	console.log("1. Claude");
	console.log("2. Codex");
	console.log("3. Gemini");
	console.log("4. OpenCode");
	console.log("5. All");
	console.log("6. None");

	var answer = await prompt("Enter comma-separated choices [6]: ");
	if (!answer.trim()) {
		return "none";
	}

	var selected = answer.split(",").map(function (choice) {
		return choice.trim();
	}).filter(Boolean);

	if (selected.indexOf("5") >= 0 || selected.indexOf("all") >= 0) {
		return "all";
	}

	if (selected.indexOf("6") >= 0 || selected.indexOf("none") >= 0) {
		return "none";
	}

	var agents = [];
	var choiceMap = {
		"1": "claude",
		"2": "codex",
		"3": "gemini",
		"4": "opencode"
	};

	for (var i = 0; i < selected.length; i++) {
		var agent = choiceMap[selected[i]] || selected[i].toLowerCase();
		if (knownAgents.indexOf(agent) < 0) {
			throw new Error("Unknown integration choice: " + selected[i]);
		}

		if (agents.indexOf(agent) < 0) {
			agents.push(agent);
		}
	}

	return agents.length > 0 ? agents.join(",") : "none";
}

async function promptClaudeMode() {
	console.log("CLAUDE.md behavior:");
	console.log("1. reference - add a short reference to AGENTS.md");
	console.log("2. full - add the full Project Memory section");
	console.log("3. none - do not change CLAUDE.md");

	var answer = await prompt("Choose [1]: ");
	if (!answer.trim() || answer.trim() === "1") {
		return "reference";
	}

	if (answer.trim() === "2") {
		return "full";
	}

	if (answer.trim() === "3") {
		return "none";
	}

	if (["reference", "full", "none"].indexOf(answer.trim()) >= 0) {
		return answer.trim();
	}

	throw new Error("Unknown CLAUDE.md choice: " + answer);
}

async function promptHooks() {
	console.log("Install the Stop-hook enforcement for Claude/Codex?");
	console.log("It nudges the agent to write a report when a session changes code but records none.");

	var answer = await prompt("Install hooks? [Y/n]: ");
	var trimmed = answer.trim().toLowerCase();
	return trimmed !== "n" && trimmed !== "no";
}

function prompt(question) {
	var rl = readline.createInterface({
		input: process.stdin,
		output: process.stdout
	});

	return new Promise(function (resolve) {
		rl.question(question, function (answer) {
			rl.close();
			resolve(answer);
		});
	});
}

function parseAgents(value) {
	if (value === "none") {
		return [];
	}

	if (value === "all") {
		return knownAgents.slice();
	}

	var agents = value.split(",").map(function (agent) {
		return agent.trim().toLowerCase();
	}).filter(Boolean);

	for (var i = 0; i < agents.length; i++) {
		if (knownAgents.indexOf(agents[i]) < 0) {
			throw new Error("Unknown integration: " + agents[i]);
		}
	}

	return agents;
}

function validateTarget(targetRoot) {
	var validatorPath = path.join(targetRoot, ".agent-memory", "tools", "validate-memory.js");
	if (!fs.existsSync(validatorPath)) {
		throw new Error("Cannot validate. Missing validator: " + validatorPath);
	}

	console.log("Validating installed memory scaffold...");
	childProcess.execFileSync(process.execPath, [validatorPath], {
		cwd: targetRoot,
		stdio: "inherit"
	});
}

if (require.main === module) {
	main().catch(function (error) {
		console.error(error.message);
		process.exit(1);
	});
}

module.exports = {
	parseAgents: parseAgents,
	parseArgs: parseArgs,
	validateTarget: validateTarget
};
