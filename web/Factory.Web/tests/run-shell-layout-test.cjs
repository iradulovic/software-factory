/* eslint-disable @typescript-eslint/no-require-imports */
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const read = (file) => fs.readFileSync(path.join(root, file), "utf8");
const shell = read("components/shell.tsx");
const overview = read("app/page.tsx");
const styles = read("app/globals.css");

assert.match(styles, /html,body\s*\{\s*height:100%;\s*overflow:hidden;\s*}/, "the document must not own app scrolling");
assert.match(shell, /overflow-y-auto overflow-x-hidden/, "only the central pane may scroll vertically");
assert.match(overview, /return <div className="min-w-0 space-y-5">/, "dashboard content must be allowed to shrink");
assert.match(overview, /<div className="overflow-x-auto"><table className="min-w-\[52rem\]">/, "wide dashboard tables must scroll inside their panels");
assert.match(overview, /xl:grid-cols-\[minmax\(0,1\.6fr\)_minmax\(0,1fr\)\]/, "dashboard columns must not use their content width as a minimum");
