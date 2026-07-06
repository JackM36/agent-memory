const fs = require("fs");
const path = require("path");

function findProjectRoot(startDirectory) {
	var currentDirectory = path.resolve(startDirectory || process.cwd());

	while (true) {
		if (fs.existsSync(path.join(currentDirectory, ".agent-memory"))) {
			return currentDirectory;
		}

		var parentDirectory = path.dirname(currentDirectory);
		if (parentDirectory === currentDirectory) {
			throw new Error("Could not find .agent-memory in this directory or any parent directory.");
		}

		currentDirectory = parentDirectory;
	}
}

function getMemoryRoot(projectRoot) {
	return path.join(projectRoot, ".agent-memory");
}

function readText(filePath) {
	return fs.readFileSync(filePath, "utf8");
}

function writeText(filePath, content) {
	fs.mkdirSync(path.dirname(filePath), { recursive: true });
	fs.writeFileSync(filePath, normalizeLineEndings(content), "utf8");
}

function normalizeLineEndings(content) {
	return content.replace(/\r\n/g, "\n").replace(/\r/g, "\n");
}

function formatDate(date) {
	return date.getFullYear() + "-" + pad(date.getMonth() + 1) + "-" + pad(date.getDate());
}

function formatDateTime(date) {
	return formatDate(date) + "_" + pad(date.getHours()) + pad(date.getMinutes());
}

function pad(value) {
	return String(value).padStart(2, "0");
}

function assertKebabId(id) {
	if (!id || !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(id)) {
		throw new Error("Expected a lowercase kebab-case id, for example: architecture-overview");
	}
}

function toTitle(id) {
	return id
		.split("-")
		.filter(Boolean)
		.map(function (part) {
			return part.charAt(0).toUpperCase() + part.slice(1);
		})
		.join(" ");
}

function applyTemplate(template, values) {
	var output = template;
	var keys = Object.keys(values);

	for (var i = 0; i < keys.length; i++) {
		var token = "{{" + keys[i] + "}}";
		output = output.split(token).join(values[keys[i]]);
	}

	return output;
}

function parseFrontmatter(content) {
	var normalizedContent = normalizeLineEndings(content);

	if (!normalizedContent.startsWith("---\n")) {
		return null;
	}

	var endIndex = normalizedContent.indexOf("\n---", 4);
	if (endIndex < 0) {
		return null;
	}

	var rawFrontmatter = normalizedContent.slice(4, endIndex).trim();
	var lines = rawFrontmatter.split("\n");
	var frontmatter = {};
	var currentListKey = null;

	for (var i = 0; i < lines.length; i++) {
		var line = lines[i];
		var trimmedLine = line.trim();

		if (!trimmedLine) {
			continue;
		}

		if (currentListKey && trimmedLine.startsWith("- ")) {
			frontmatter[currentListKey].push(parseScalar(trimmedLine.slice(2).trim()));
			continue;
		}

		currentListKey = null;
		var separatorIndex = line.indexOf(":");
		if (separatorIndex < 0) {
			continue;
		}

		var key = line.slice(0, separatorIndex).trim();
		var rawValue = line.slice(separatorIndex + 1).trim();

		if (!rawValue) {
			frontmatter[key] = [];
			currentListKey = key;
			continue;
		}

		frontmatter[key] = parseScalar(rawValue);
	}

	return frontmatter;
}

function parseScalar(rawValue) {
	if (rawValue === "true") {
		return true;
	}

	if (rawValue === "false") {
		return false;
	}

	if (rawValue === "[]") {
		return [];
	}

	if (rawValue.startsWith("[") && rawValue.endsWith("]")) {
		var innerValue = rawValue.slice(1, -1).trim();
		if (!innerValue) {
			return [];
		}

		return innerValue.split(",").map(function (part) {
			return part.trim().replace(/^['"]|['"]$/g, "");
		});
	}

	return rawValue.replace(/^['"]|['"]$/g, "");
}

function listMarkdownFiles(directoryPath) {
	if (!fs.existsSync(directoryPath)) {
		return [];
	}

	var entries = fs.readdirSync(directoryPath, { withFileTypes: true });
	var files = [];

	for (var i = 0; i < entries.length; i++) {
		var entry = entries[i];
		var fullPath = path.join(directoryPath, entry.name);

		if (entry.isDirectory()) {
			files = files.concat(listMarkdownFiles(fullPath));
			continue;
		}

		if (entry.isFile() && entry.name.endsWith(".md") && entry.name !== "README.md") {
			files.push(fullPath);
		}
	}

	return files;
}

function toProjectPath(projectRoot, filePath) {
	return normalizeLineEndings(path.relative(projectRoot, filePath)).replace(/\\/g, "/");
}

module.exports = {
	applyTemplate: applyTemplate,
	assertKebabId: assertKebabId,
	findProjectRoot: findProjectRoot,
	formatDate: formatDate,
	formatDateTime: formatDateTime,
	getMemoryRoot: getMemoryRoot,
	listMarkdownFiles: listMarkdownFiles,
	normalizeLineEndings: normalizeLineEndings,
	parseFrontmatter: parseFrontmatter,
	readText: readText,
	toProjectPath: toProjectPath,
	toTitle: toTitle,
	writeText: writeText
};
