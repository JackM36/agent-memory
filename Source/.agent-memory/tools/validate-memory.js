const fs = require("fs");
const path = require("path");
const tools = require("./memory-tool-utils");

function main() {
	var projectRoot = tools.findProjectRoot(process.cwd());
	var memoryRoot = tools.getMemoryRoot(projectRoot);
	var errors = [];

	validateRequiredPaths(projectRoot, memoryRoot, errors);
	validateManifest(projectRoot, memoryRoot, errors);
	validateFrontmatter(projectRoot, memoryRoot, errors);

	if (errors.length > 0) {
		console.error("Agent memory validation failed:");
		for (var i = 0; i < errors.length; i++) {
			console.error("- " + errors[i]);
		}

		process.exit(1);
	}

	console.log("Agent memory validation passed.");
}

function validateRequiredPaths(projectRoot, memoryRoot, errors) {
	var requiredMemoryPaths = [
		"README.md",
		"manifest.md",
		"protocol.md",
		"commands.md",
		"config.md",
		"agents/README.md",
		"agents/memory-scout.md",
		"agents/memory-maintainer.md",
		"commands/README.md",
		"commands/memory-init.md",
		"commands/memory-add.md",
		"commands/memory-query.md",
		"commands/memory-file-history.md",
		"commands/memory-refresh.md",
		"commands/memory-maintain.md",
		"commands/memory-prune.md",
		"commands/memory-resolve.md",
		"commands/memory-scout.md",
		"rules/README.md",
		"memories/README.md",
		"uncertainties/README.md",
		"reports/README.md",
		"notes/README.md",
		"notes/pending.md",
		"archive/README.md",
		"templates/README.md",
		"templates/memory.md",
		"templates/rule.md",
		"templates/uncertainty.md",
		"templates/report.md",
		"templates/pending-note.md",
		"templates/memory-scout-result.md",
		"templates/memory-maintenance-handoff.md",
		"templates/memory-maintenance-result.md",
		"templates/agents-section.md",
		"templates/manifest-rule-entry.md",
		"templates/manifest-memory-entry.md",
		"templates/manifest-uncertainty-entry.md",
		"templates/manifest-report-entry.md",
		"tools/memory-tool-utils.js",
		"tools/validate-memory.js",
		"tools/init-project-memory.js",
		"tools/uninstall-project-memory.js",
		"tools/uninstall-project-memory-interactive.js",
		"tools/new-rule.js",
		"tools/new-memory.js",
		"tools/new-uncertainty.js",
		"tools/new-report.js",
		"tools/refresh-manifest.js"
	];

	for (var i = 0; i < requiredMemoryPaths.length; i++) {
		var requiredPath = path.join(memoryRoot, requiredMemoryPaths[i]);
		if (!fs.existsSync(requiredPath)) {
			errors.push("Missing required path: .agent-memory/" + requiredMemoryPaths[i]);
		}
	}

	var integrationGroups = [
		[
		".claude/commands/memory-add.md",
		".claude/commands/memory-init.md",
		".claude/commands/memory-query.md",
		".claude/commands/memory-file-history.md",
		".claude/commands/memory-refresh.md",
		".claude/commands/memory-maintain.md",
		".claude/commands/memory-prune.md",
		".claude/commands/memory-resolve.md",
		".claude/commands/memory-scout.md",
		".claude/agents/memory-scout.md",
		".claude/agents/memory-maintainer.md",
		".claude/skills/agent-memory/SKILL.md"
		],
		[
		".opencode/command/memory-add.md",
		".opencode/command/memory-init.md",
		".opencode/command/memory-query.md",
		".opencode/command/memory-file-history.md",
		".opencode/command/memory-refresh.md",
		".opencode/command/memory-maintain.md",
		".opencode/command/memory-prune.md",
		".opencode/command/memory-resolve.md",
		".opencode/command/memory-scout.md",
		".opencode/agent/memory-scout.md",
		".opencode/agent/memory-maintainer.md",
		".opencode/skills/agent-memory/SKILL.md"
		],
		[
		".agents/skills/agent-memory/SKILL.md",
		".codex/agents/memory-scout.toml",
		".codex/agents/memory-maintainer.toml"
		],
		[
		".gemini/commands/memory-add.toml",
		".gemini/commands/memory-init.toml",
		".gemini/commands/memory-query.toml",
		".gemini/commands/memory-file-history.toml",
		".gemini/commands/memory-refresh.toml",
		".gemini/commands/memory-maintain.toml",
		".gemini/commands/memory-prune.toml",
		".gemini/commands/memory-resolve.toml",
		".gemini/commands/memory-scout.toml",
		".gemini/agents/memory-scout.md",
		".gemini/agents/memory-maintainer.md"
		]
	];

	for (var j = 0; j < integrationGroups.length; j++) {
		validateIntegrationGroup(projectRoot, integrationGroups[j], errors);
	}
}

function validateIntegrationGroup(projectRoot, requiredPaths, errors) {
	var groupIsPresent = false;

	for (var i = 0; i < requiredPaths.length; i++) {
		if (fs.existsSync(path.join(projectRoot, requiredPaths[i]))) {
			groupIsPresent = true;
			break;
		}
	}

	if (!groupIsPresent) {
		return;
	}

	for (var j = 0; j < requiredPaths.length; j++) {
		var requiredPath = path.join(projectRoot, requiredPaths[j]);
		if (!fs.existsSync(requiredPath)) {
			errors.push("Incomplete native integration; missing path: " + requiredPaths[j]);
		}
	}
}

function validateManifest(projectRoot, memoryRoot, errors) {
	var manifestPath = path.join(memoryRoot, "manifest.md");
	if (!fs.existsSync(manifestPath)) {
		return;
	}

	var manifest = tools.readText(manifestPath);
	var markerNames = [
		"ALWAYS READ RULE INDEX",
		"RULE INDEX",
		"ALWAYS READ MEMORY INDEX",
		"MEMORY INDEX",
		"UNCERTAINTY INDEX",
		"REPORT INDEX"
	];

	for (var i = 0; i < markerNames.length; i++) {
		var markerName = markerNames[i];
		if (manifest.indexOf("<!-- BEGIN " + markerName + " -->") < 0 || manifest.indexOf("<!-- END " + markerName + " -->") < 0) {
			errors.push("Manifest is missing managed marker block: " + markerName);
		}
	}

	if (manifest.indexOf("Reports are not default context") < 0) {
		errors.push("Manifest must state that reports are not default context.");
	}

	validateManifestPaths(projectRoot, manifest, errors);
}

function validateManifestPaths(projectRoot, manifest, errors) {
	var pathPattern = /`([^`]+\.md)`/g;
	var match = pathPattern.exec(manifest);

	while (match) {
		var manifestPath = match[1];

		if (manifestPath.indexOf("{{") >= 0 || manifestPath.indexOf("path/to/") >= 0 || manifestPath.indexOf("path/or/") >= 0) {
			match = pathPattern.exec(manifest);
			continue;
		}

		var filePath = path.join(projectRoot, ".agent-memory", manifestPath);
		if (!fs.existsSync(filePath)) {
			errors.push("Manifest indexes missing file: " + manifestPath);
		}

		if (manifestPath.startsWith("archive/")) {
			errors.push("Manifest must not index archived files as active entries: " + manifestPath);
		}

		match = pathPattern.exec(manifest);
	}
}

function validateFrontmatter(projectRoot, memoryRoot, errors) {
	validateDirectory(projectRoot, path.join(memoryRoot, "rules"), "rule", ["id", "type", "tags", "files", "summary", "always_read", "priority", "updated", "status"], errors);
	validateDirectory(projectRoot, path.join(memoryRoot, "memories"), "memory", ["id", "type", "tags", "files", "summary", "updated", "last_verified", "verified_by", "confidence", "status"], errors);
	validateDirectory(projectRoot, path.join(memoryRoot, "uncertainties"), "uncertainty", ["id", "type", "tags", "files", "summary", "blocking", "created", "status"], errors);
	validateDirectory(projectRoot, path.join(memoryRoot, "reports"), "report", ["id", "type", "date", "agent", "task", "tags", "files_changed", "memory_updates", "uncertainties_created", "uncertainties_resolved", "read_when"], errors);
}

function validateDirectory(projectRoot, directoryPath, expectedType, requiredFields, errors) {
	var files = tools.listMarkdownFiles(directoryPath);

	for (var i = 0; i < files.length; i++) {
		var filePath = files[i];
		var projectPath = tools.toProjectPath(projectRoot, filePath);
		var frontmatter = tools.parseFrontmatter(tools.readText(filePath));

		if (!frontmatter) {
			errors.push(projectPath + " is missing YAML frontmatter.");
			continue;
		}

		validateRequiredFields(projectPath, frontmatter, requiredFields, errors);

		if (frontmatter.type !== expectedType) {
			errors.push(projectPath + " has type `" + frontmatter.type + "`; expected `" + expectedType + "`.");
		}

		validateAllowedValues(projectPath, frontmatter, expectedType, errors);
	}
}

function validateRequiredFields(projectPath, frontmatter, requiredFields, errors) {
	for (var i = 0; i < requiredFields.length; i++) {
		var field = requiredFields[i];
		if (frontmatter[field] === undefined || frontmatter[field] === null || frontmatter[field] === "") {
			errors.push(projectPath + " is missing required frontmatter field: " + field);
		}
	}
}

function validateAllowedValues(projectPath, frontmatter, expectedType, errors) {
	if (expectedType === "rule") {
		if (typeof frontmatter.always_read !== "boolean") {
			errors.push(projectPath + " field `always_read` must be true or false.");
		}

		validateOneOf(projectPath, "priority", frontmatter.priority, ["high", "medium", "low"], errors);
		validateOneOf(projectPath, "status", frontmatter.status, ["active", "stale", "superseded", "archived"], errors);
	}

	if (expectedType === "memory") {
		validateOneOf(projectPath, "confidence", frontmatter.confidence, ["high", "medium", "low"], errors);
		validateOneOf(projectPath, "verified_by", frontmatter.verified_by, ["code", "user", "tests", "repeated-use", "report", "unverified"], errors);
		validateOneOf(projectPath, "status", frontmatter.status, ["active", "stale", "superseded", "archived"], errors);
	}

	if (expectedType === "uncertainty") {
		if (typeof frontmatter.blocking !== "boolean") {
			errors.push(projectPath + " field `blocking` must be true or false.");
		}

		validateOneOf(projectPath, "status", frontmatter.status, ["unresolved", "resolved", "archived"], errors);
	}

	if (expectedType === "report" && frontmatter.read_when === "") {
		errors.push(projectPath + " must explain when the report should be read.");
	}
}

function validateOneOf(projectPath, field, value, allowedValues, errors) {
	if (allowedValues.indexOf(value) < 0) {
		errors.push(projectPath + " field `" + field + "` must be one of: " + allowedValues.join(", "));
	}
}

main();
