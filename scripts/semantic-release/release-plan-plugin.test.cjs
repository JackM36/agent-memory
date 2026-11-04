const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const plugin = require("./release-plan-plugin.cjs");
const config = require("../../.releaserc.json");

test("writes the planned version and tag", () => {
    const outputPath = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "release-plan-")), "plan.json");
    plugin.verifyRelease({ outputPath }, { nextRelease: { type: "minor", version: "0.2.0", gitTag: "v0.2.0" } });

    assert.deepEqual(JSON.parse(fs.readFileSync(outputPath, "utf8")), { type: "minor", version: "0.2.0", tag: "v0.2.0" });
});

test("releases from main with v-prefixed tags", () => {
    assert.deepEqual(config.branches, ["main"]);
    assert.equal(config.tagFormat, "v${version}");
});

test("only user-visible change types release", () => {
    const [, { releaseRules }] = config.plugins.find((p) => Array.isArray(p) && p[0] === "@semantic-release/commit-analyzer");
    const release = (type) => releaseRules.find((r) => r.type === type)?.release;

    assert.equal(releaseRules.find((r) => r.breaking).release, "major");
    assert.equal(release("feat"), "minor");
    assert.equal(release("fix"), "patch");
    for (const type of ["chore", "ci", "docs", "refactor", "style", "test"]) {
        assert.equal(release(type), false, type);
    }
});

test("the plan plugin runs before GitHub publishing and the binaries are attached", () => {
    const names = config.plugins.map((p) => (Array.isArray(p) ? p[0] : p));
    assert.ok(names.indexOf("./scripts/semantic-release/release-plan-plugin.cjs") < names.indexOf("@semantic-release/github"));

    const [, { assets }] = config.plugins.find((p) => Array.isArray(p) && p[0] === "@semantic-release/github");
    assert.deepEqual(assets.map((a) => a.path), ["dist/*.zip", "dist/*.tar.gz", "dist/SHA256SUMS"]);
});
