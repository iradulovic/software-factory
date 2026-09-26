/* eslint-disable @typescript-eslint/no-require-imports */
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const read = (file) => fs.readFileSync(path.join(root, file), "utf8");
const shell = read("components/shell.tsx");
const overview = read("app/page.tsx");
const attention = read("components/attention-queue.tsx");
const styles = read("app/globals.css");

assert.match(styles, /html,body\s*\{\s*height:100%;\s*overflow:hidden;\s*}/, "the document must not own app scrolling");
assert.match(shell, /overflow-y-auto overflow-x-hidden/, "only the central pane may scroll vertically");
assert.match(overview, /return <div className="min-w-0 space-y-5">/, "dashboard content must be allowed to shrink");
assert.doesNotMatch(overview, /Agent status|<th>Active task<\/th>/, "overview must not render the agent status table");
assert.match(shell, /postPause\(`\/api\/agents\/\$\{agent\}\/(pause|resume)`\)/, "navbar controls must use the agent pause/resume API");
assert.match(shell, /disabled=\{a\.state === "Unavailable" \|\| agentPause\.isPending\}/, "unavailable agents must not expose an active pause control");
assert.match(shell, /<NudgePill \/>/, "nudges must be available from the navbar on every page");
assert.match(shell, /aria-label=\{hasUnread \? `View nudges, \$\{unreadCount\} unread` : "View nudges"\}/, "the nudge trigger must announce its unread count");
assert.doesNotMatch(overview, /NudgeInbox/, "overview must not render the nudge inbox after moving it to the navbar");
assert.match(styles, /@media \(prefers-reduced-motion: reduce\) \{ \.nudge-attention-dot \{ animation:none; \} \}/, "nudge attention must stop under reduced motion");
assert.match(attention, /panel min-w-0/, "attention panel must be allowed to shrink");
assert.match(attention, /lg:grid-cols-2/, "attention cards must adapt to wider layouts");
