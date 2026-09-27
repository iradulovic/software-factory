/* eslint-disable @typescript-eslint/no-require-imports */
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const read = (file) => fs.readFileSync(path.join(root, file), "utf8");
const shell = read("components/shell.tsx");
const layout = read("app/layout.tsx");
const overview = read("app/page.tsx");
const attention = read("components/attention-queue.tsx");
const styles = read("app/globals.css");

assert.match(layout, /import \{ cookies \} from "next\/headers"/, "the server layout must read the sidebar preference before rendering");
assert.match(layout, /const sidebarState = cookieStore\.get\("sidebar_state"\)\?\.value/, "the saved desktop sidebar cookie must be read during initial rendering");
assert.match(layout, /const defaultSidebarOpen = sidebarState !== "false"/, "only a saved false value should collapse the desktop sidebar");
assert.match(layout, /<Shell defaultSidebarOpen=\{defaultSidebarOpen\}>/, "the server-rendered sidebar preference must reach the shell");
assert.match(shell, /<SidebarProvider defaultOpen=\{defaultSidebarOpen\}/, "the saved preference must initialize the sidebar provider");
assert.match(styles, /html,body\s*\{\s*height:100%;\s*overflow:hidden;\s*}/, "the document must not own app scrolling");
assert.match(shell, /overflow-y-auto overflow-x-hidden/, "only the central pane may scroll vertically");
assert.match(overview, /return <div className="min-w-0 space-y-5">/, "dashboard content must be allowed to shrink");
assert.doesNotMatch(overview, /Agent status|<th>Active task<\/th>/, "overview must not render the agent status table");
assert.match(shell, /<DispatchPausePill \/>/, "global dispatch control must be available from the navbar");
assert.match(shell, /postPause\(globalPaused \? "\/api\/control\/resume" : "\/api\/control\/pause"\)/, "global control must use the pause/resume API");
assert.doesNotMatch(overview, /Pause reason|Reason \(optional\)|Pause dispatch/, "overview must not capture or render a global pause reason/control");
assert.match(shell, /postPause\(`\/api\/agents\/\$\{agent\}\/(pause|resume)`\)/, "navbar controls must use the agent pause/resume API");
assert.match(shell, /disabled=\{a\.state === "Unavailable" \|\| agentPause\.isPending\}/, "unavailable agents must not expose an active pause control");
assert.match(shell, /<NudgePill \/>/, "nudges must be available from the navbar on every page");
assert.match(shell, /aria-label=\{hasUnread \? `View nudges, \$\{unreadCount\} unread` : "View nudges"\}/, "the nudge trigger must announce its unread count");
assert.doesNotMatch(overview, /NudgeInbox/, "overview must not render the nudge inbox after moving it to the navbar");
assert.match(styles, /@media \(prefers-reduced-motion: reduce\) \{ \.nudge-attention-dot \{ animation:none; \} \}/, "nudge attention must stop under reduced motion");
assert.match(attention, /panel min-w-0/, "attention panel must be allowed to shrink");
assert.match(attention, /lg:grid-cols-2/, "attention cards must adapt to wider layouts");
