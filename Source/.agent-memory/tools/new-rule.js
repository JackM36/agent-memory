const path = require("path");
const fs = require("fs");
const tools = require("./memory-tool-utils");

function main() {
	var id = process.argv[2];
	tools.assertKebabId(id);

	var projectRoot = tools.findProjectRoot(process.cwd());
	var memoryRoot = tools.getMemoryRoot(projectRoot);
	var targetPath = path.join(memoryRoot, "rules", id + ".md");

	if (fs.existsSync(targetPath)) {
		throw new Error("File already exists: " + targetPath);
	}

	var templatePath = path.join(memoryRoot, "templates", "rule.md");
	var template = tools.readText(templatePath);
	var output = tools.applyTemplate(template, {
		id: id,
		date: tools.formatDate(new Date()),
		title: tools.toTitle(id)
	});

	tools.writeText(targetPath, output);
	console.log("Created " + tools.toProjectPath(projectRoot, targetPath));
	console.log("Next: edit the file, then run node .agent-memory/tools/refresh-manifest.js");
}

main();
