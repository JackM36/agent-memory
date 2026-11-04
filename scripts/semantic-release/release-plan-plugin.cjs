const fs = require("node:fs");

// semantic-release plugin: records the next release so the workflow can build artifacts for that version
// before the real run tags and publishes it. Runs in verifyRelease, which the dry run also executes.
module.exports = {
    verifyRelease(pluginConfig, context) {
        const outputPath = pluginConfig.outputPath || "release-plan.json";
        const plan = {
            type: context.nextRelease.type,
            version: context.nextRelease.version,
            tag: context.nextRelease.gitTag,
        };

        fs.writeFileSync(outputPath, `${JSON.stringify(plan, null, 2)}\n`, "utf8");
    },
};
