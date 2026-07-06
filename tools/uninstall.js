const childProcess = require("child_process");
const fs = require("fs");
const path = require("path");

function main() {
	var options = parseArgs(process.argv.slice(2));
	uninstall(options);
}

function parseArgs(args) {
	var options = {
		mode: "keep-data",
		target: null
	};

	for (var i = 0; i < args.length; i++) {
		var arg = args[i];

		if (arg === "--target") {
			options.target = readOptionValue(args, ++i, arg);
			continue;
		}

		if (arg === "--mode") {
			options.mode = readOptionValue(args, ++i, arg);
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
	console.log("Usage: node tools/uninstall.js --target path [--mode keep-data|delete-all]");
}

function uninstall(options) {
	var targetRoot = path.resolve(options.target);
	var projectUninstaller = path.join(targetRoot, ".agent-memory", "tools", "uninstall-project-memory.js");

	if (!fs.existsSync(projectUninstaller)) {
		throw new Error("Missing project uninstaller: " + projectUninstaller);
	}

	childProcess.execFileSync(process.execPath, [projectUninstaller, "--mode", options.mode], {
		cwd: targetRoot,
		stdio: "inherit"
	});
}

if (require.main === module) {
	main();
}

module.exports = {
	parseArgs: parseArgs,
	uninstall: uninstall
};
