const readline = require("readline");
const uninstaller = require("./uninstall-project-memory");
const tools = require("./memory-tool-utils");

async function main() {
	var projectRoot = tools.findProjectRoot(process.cwd());
	var mode = await promptMode();
	uninstaller.uninstallProjectMemory(projectRoot, mode);
}

async function promptMode() {
	console.log("What do you want to uninstall?");
	console.log("1. Remove memory system but keep project memory data (recommended)");
	console.log("2. Delete everything, including rules, memories, reports, uncertainties, notes, and archive");

	var answer = await prompt("Choose [1]: ");
	var trimmedAnswer = answer.trim();
	if (!trimmedAnswer || trimmedAnswer === "1" || trimmedAnswer === "keep-data") {
		return "keep-data";
	}

	if (trimmedAnswer === "2" || trimmedAnswer === "delete-all") {
		var confirmation = await prompt("Type DELETE to confirm deleting all agent memory data: ");
		if (confirmation !== "DELETE") {
			throw new Error("Delete-all uninstall cancelled.");
		}

		return "delete-all";
	}

	throw new Error("Unknown uninstall choice: " + answer);
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

if (require.main === module) {
	main().catch(function (error) {
		console.error(error.message);
		process.exit(1);
	});
}

module.exports = {
	promptMode: promptMode
};
