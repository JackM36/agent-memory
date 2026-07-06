const path = require("path");
const tools = require("./memory-tool-utils");

function main() {
	var id = process.argv[2];
	tools.assertKebabId(id);

	var projectRoot = tools.findProjectRoot(process.cwd());
	var memoryRoot = tools.getMemoryRoot(projectRoot);
	var targetPath = path.join(memoryRoot, "memories", id + ".md");

	createFromTemplate(memoryRoot, targetPath, "memory.md", {
		id: id,
		date: tools.formatDate(new Date()),
		title: tools.toTitle(id)
	});
}

function createFromTemplate(memoryRoot, targetPath, templateName, values) {
	var templatePath = path.join(memoryRoot, "templates", templateName);

	if (require("fs").existsSync(targetPath)) {
		throw new Error("File already exists: " + targetPath);
	}

	var template = tools.readText(templatePath);
	var output = tools.applyTemplate(template, values);
	tools.writeText(targetPath, output);
	console.log("Created " + tools.toProjectPath(path.dirname(memoryRoot), targetPath));
	console.log("Next: edit the file, then run node .agent-memory/tools/refresh-manifest.js");
}

main();
