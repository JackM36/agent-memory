const fs = require("fs");
const path = require("path");
const tools = require("./memory-tool-utils");

const beginMarker = "<!-- BEGIN AGENT MEMORY -->";
const endMarker = "<!-- END AGENT MEMORY -->";

function main() {
	var options = parseArgs(process.argv.slice(2));
	var projectRoot = options.target ? path.resolve(options.target) : tools.findProjectRoot(process.cwd());
	var result = initializeProject(projectRoot, options);

	for (var i = 0; i < result.messages.length; i++) {
		console.log(result.messages[i]);
	}
}

function parseArgs(args) {
	var options = {
		agentsFile: "AGENTS.md",
		claude: "reference",
		target: null
	};

	for (var i = 0; i < args.length; i++) {
		var arg = args[i];

		if (arg === "--agents-file") {
			options.agentsFile = readOptionValue(args, ++i, arg);
			continue;
		}

		if (arg === "--claude") {
			options.claude = readOptionValue(args, ++i, arg);
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

function printHelp() {
	console.log("Usage: node .agent-memory/tools/init-project-memory.js [--agents-file AGENTS.md] [--claude reference|full|none] [--target path]");
}

function initializeProject(projectRoot, options) {
	var effectiveOptions = options || {};
	var memoryRoot = path.join(projectRoot, ".agent-memory");
	var templatePath = path.join(memoryRoot, "templates", "agents-section.md");

	if (!fs.existsSync(templatePath)) {
		throw new Error("Missing memory section template: " + templatePath);
	}

	var messages = [];
	var section = wrapSection(tools.readText(templatePath));
	var agentsFile = effectiveOptions.agentsFile || "AGENTS.md";
	var agentsPath = path.join(projectRoot, agentsFile);
	var agentsAction = upsertSection(agentsPath, section);
	messages.push(formatAction(agentsAction, agentsFile));

	var claudeMode = effectiveOptions.claude || "reference";
	if (claudeMode !== "none") {
		var claudePath = path.join(projectRoot, "CLAUDE.md");
		var claudeSection = claudeMode === "full" ? section : wrapSection("## Project Memory\n\nFollow the Project Memory section in `" + agentsFile.replace(/\\/g, "/") + "`.");
		var claudeAction = upsertSection(claudePath, claudeSection);
		messages.push(formatAction(claudeAction, "CLAUDE.md"));
	}

	return { messages: messages };
}

function wrapSection(section) {
	return beginMarker + "\n" + tools.normalizeLineEndings(section).trim() + "\n" + endMarker;
}

function upsertSection(filePath, section) {
	var content = "";
	var existed = fs.existsSync(filePath);

	if (existed) {
		content = tools.readText(filePath);
	}

	var normalizedContent = tools.normalizeLineEndings(content).trimEnd();
	var beginIndex = normalizedContent.indexOf(beginMarker);
	var endIndex = normalizedContent.indexOf(endMarker);

	if (beginIndex >= 0 && endIndex > beginIndex) {
		var before = normalizedContent.slice(0, beginIndex).trimEnd();
		var after = normalizedContent.slice(endIndex + endMarker.length).trimStart();
		var updated = joinSections([before, section, after]);
		tools.writeText(filePath, updated + "\n");
		return "updated";
	}

	var created = joinSections([normalizedContent, section]);
	tools.writeText(filePath, created + "\n");
	return existed ? "appended" : "created";
}

function joinSections(sections) {
	var output = [];

	for (var i = 0; i < sections.length; i++) {
		if (sections[i]) {
			output.push(sections[i]);
		}
	}

	return output.join("\n\n");
}

function formatAction(action, fileName) {
	return action.charAt(0).toUpperCase() + action.slice(1) + " " + fileName;
}

if (require.main === module) {
	main();
}

module.exports = {
	beginMarker: beginMarker,
	endMarker: endMarker,
	initializeProject: initializeProject,
	parseArgs: parseArgs,
	upsertSection: upsertSection,
	wrapSection: wrapSection
};
