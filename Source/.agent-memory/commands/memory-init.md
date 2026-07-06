# /memory-init

Initialize project instruction files after `.agent-memory/` has already been copied into a project.

## Workflow

1. Run the deterministic init helper from the project root:

```sh
node .agent-memory/tools/init-project-memory.js
```

2. Confirm that `AGENTS.md` contains the managed Project Memory section.
3. Confirm that `CLAUDE.md` contains the managed reference section, unless initialized with `--claude none`.

## Options

```sh
node .agent-memory/tools/init-project-memory.js --agents-file AGENTS.md --claude reference
node .agent-memory/tools/init-project-memory.js --agents-file AGENTS.md --claude full
node .agent-memory/tools/init-project-memory.js --agents-file AGENTS.md --claude none
```

The command is idempotent. It updates the managed marker block if it already exists.
