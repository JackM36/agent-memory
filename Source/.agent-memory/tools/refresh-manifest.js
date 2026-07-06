const path = require("path");
const tools = require("./memory-tool-utils");

function main() {
	var projectRoot = tools.findProjectRoot(process.cwd());
	var memoryRoot = tools.getMemoryRoot(projectRoot);
	var manifestPath = path.join(memoryRoot, "manifest.md");
	var manifest = tools.readText(manifestPath);

	var rules = readEntries(projectRoot, path.join(memoryRoot, "rules"), "rule");
	var memories = readEntries(projectRoot, path.join(memoryRoot, "memories"), "memory");
	var uncertainties = readEntries(projectRoot, path.join(memoryRoot, "uncertainties"), "uncertainty");
	var reports = readEntries(projectRoot, path.join(memoryRoot, "reports"), "report");

	var alwaysReadRules = rules.filter(function (entry) {
		return entry.frontmatter.always_read === true && entry.frontmatter.status === "active";
	});

	var regularRules = rules.filter(function (entry) {
		return entry.frontmatter.always_read !== true && entry.frontmatter.status === "active";
	});

	var alwaysReadMemories = memories.filter(function (entry) {
		return entry.frontmatter.always_read === true && entry.frontmatter.status === "active";
	});

	var regularMemories = memories.filter(function (entry) {
		return entry.frontmatter.always_read !== true && entry.frontmatter.status === "active";
	});

	var unresolvedUncertainties = uncertainties.filter(function (entry) {
		return entry.frontmatter.status === "unresolved";
	});

	manifest = replaceBlock(manifest, "ALWAYS READ RULE INDEX", formatAlwaysReadRules(alwaysReadRules));
	manifest = replaceBlock(manifest, "RULE INDEX", formatRules(regularRules));
	manifest = replaceBlock(manifest, "ALWAYS READ MEMORY INDEX", formatAlwaysReadMemories(alwaysReadMemories));
	manifest = replaceBlock(manifest, "MEMORY INDEX", formatMemories(regularMemories));
	manifest = replaceBlock(manifest, "UNCERTAINTY INDEX", formatUncertainties(unresolvedUncertainties));
	manifest = replaceBlock(manifest, "REPORT INDEX", formatReports(reports));
	manifest = updateLastRefresh(manifest, tools.formatDate(new Date()));

	tools.writeText(manifestPath, manifest);
	console.log("Refreshed .agent-memory/manifest.md");
}

function readEntries(projectRoot, directoryPath, expectedType) {
	var files = tools.listMarkdownFiles(directoryPath);
	var entries = [];

	for (var i = 0; i < files.length; i++) {
		var filePath = files[i];
		var frontmatter = tools.parseFrontmatter(tools.readText(filePath));

		if (!frontmatter || frontmatter.type !== expectedType) {
			continue;
		}

		entries.push({
			path: tools.toProjectPath(projectRoot, filePath).replace(".agent-memory/", ""),
			frontmatter: frontmatter
		});
	}

	entries.sort(function (left, right) {
		return left.path.localeCompare(right.path);
	});

	return entries;
}

function replaceBlock(content, blockName, replacement) {
	var beginMarker = "<!-- BEGIN " + blockName + " -->";
	var endMarker = "<!-- END " + blockName + " -->";
	var beginIndex = content.indexOf(beginMarker);
	var endIndex = content.indexOf(endMarker);

	if (beginIndex < 0 || endIndex < 0 || endIndex < beginIndex) {
		throw new Error("Manifest is missing marker block: " + blockName);
	}

	var before = content.slice(0, beginIndex + beginMarker.length);
	var after = content.slice(endIndex);
	return before + "\n\n" + replacement.trim() + "\n\n" + after;
}

function updateLastRefresh(content, date) {
	var lines = content.split("\n");

	for (var i = 0; i < lines.length; i++) {
		if (lines[i].startsWith("- Last memory refresh:")) {
			lines[i] = "- Last memory refresh: " + date;
			return lines.join("\n");
		}
	}

	return content;
}

function formatAlwaysReadMemories(entries) {
	if (entries.length === 0) {
		return "No always-read memories yet.";
	}

	return entries.map(function (entry) {
		return formatManifestLine(entry.path, [
			"tags: " + formatArray(entry.frontmatter.tags)
		], entry.frontmatter.summary);
	}).join("\n");
}

function formatAlwaysReadRules(entries) {
	if (entries.length === 0) {
		return "No always-read rules yet.";
	}

	return entries.map(function (entry) {
		return formatManifestLine(entry.path, [
			"priority: " + valueOrPlaceholder(entry.frontmatter.priority),
			"tags: " + formatArray(entry.frontmatter.tags)
		], entry.frontmatter.summary);
	}).join("\n");
}

function formatRules(entries) {
	if (entries.length === 0) {
		return "No rules yet.";
	}

	return entries.map(function (entry) {
		return formatManifestLine(entry.path, [
			"tags: " + formatArray(entry.frontmatter.tags),
			"files: " + formatFiles(entry.frontmatter.files),
			"priority: " + valueOrPlaceholder(entry.frontmatter.priority),
			"updated: " + valueOrPlaceholder(entry.frontmatter.updated)
		], entry.frontmatter.summary);
	}).join("\n");
}

function formatMemories(entries) {
	if (entries.length === 0) {
		return "No memories yet.";
	}

	return entries.map(function (entry) {
		return formatManifestLine(entry.path, [
			"tags: " + formatArray(entry.frontmatter.tags),
			"files: " + formatFiles(entry.frontmatter.files),
			"confidence: " + valueOrPlaceholder(entry.frontmatter.confidence),
			"updated: " + valueOrPlaceholder(entry.frontmatter.updated)
		], entry.frontmatter.summary);
	}).join("\n");
}

function formatUncertainties(entries) {
	if (entries.length === 0) {
		return "No unresolved uncertainties yet.";
	}

	return entries.map(function (entry) {
		return formatManifestLine(entry.path, [
			"tags: " + formatArray(entry.frontmatter.tags),
			"files: " + formatFiles(entry.frontmatter.files),
			"blocking: " + valueOrPlaceholder(entry.frontmatter.blocking)
		], entry.frontmatter.summary);
	}).join("\n");
}

function formatReports(entries) {
	if (entries.length === 0) {
		return "No reports yet.";
	}

	return entries.map(function (entry) {
		return formatManifestLine(entry.path, [
			"tags: " + formatArray(entry.frontmatter.tags),
			"read_when: " + valueOrPlaceholder(entry.frontmatter.read_when)
		], "");
	}).join("\n");
}

function formatManifestLine(entryPath, fields, summary) {
	var line = "- `" + entryPath + "`";

	for (var i = 0; i < fields.length; i++) {
		line += " | " + fields[i];
	}

	if (summary) {
		line += " | " + valueOrPlaceholder(summary);
	}

	return line;
}

function formatArray(value) {
	if (!Array.isArray(value) || value.length === 0) {
		return "[]";
	}

	return "[" + value.join(", ") + "]";
}

function formatFiles(value) {
	if (!Array.isArray(value) || value.length === 0) {
		return "none";
	}

	return value.map(function (file) {
		return "`" + file + "`";
	}).join(", ");
}

function valueOrPlaceholder(value) {
	if (value === undefined || value === null || value === "") {
		return "unknown";
	}

	return String(value);
}

main();
