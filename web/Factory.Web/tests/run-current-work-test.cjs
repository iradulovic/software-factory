/* eslint-disable @typescript-eslint/no-require-imports */
const { spawnSync } = require("node:child_process");
const { resolve } = require("node:path");
const result = spawnSync(process.execPath, ["./node_modules/tsx/dist/cli.mjs", "--test", "tests/current-work.test.tsx"], {
  env: { ...process.env, NODE_OPTIONS: `--require=${resolve("tests/tsx-preload.cjs")}` }, stdio: "inherit"
});
process.exit(result.status ?? 1);
