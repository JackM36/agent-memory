const path = require("path");
const fs = require("fs");
const tools = require("./memory-tool-utils");

function main() {
	var shortName = process.argv[2];
	tools.assertKebabId(shortName);

	var now = new Date();
	var date = tools.formatDate(now);
	var id = tools.formatDateTime(now) + "_" + shortName;
	var projectRoot = tools.findProjectRoot(process.cwd());
	var memoryRoot = tools.getMemoryRoot(projectRoot);
	var targetPath = path.join(memoryRoot, "reports", id + ".md");

	if (fs.existsSync(targetPath)) {
		throw new Error("File already exists: " + targetPath);
	}

	var templatePath = path.join(memoryRoot, "templates", "report.md");
	var template = tools.readText(templatePath);
	var output = tools.applyTemplate(template, {
		id: id,
		date: date,
		task: shortName.split("-").join(" "),
		title: tools.toTitle(shortName)
	});

	tools.writeText(targetPath, output);
	console.log("Created " + tools.toProjectPath(projectRoot, targetPath));
	console.log("Next: edit the report, then run node .agent-memory/tools/refresh-manifest.js");
}

main();
