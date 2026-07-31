import { spawn } from "node:child_process";
import { readdirSync } from "node:fs";
import path from "node:path";

const screenshotDir = "D:\\Screenshot";
const files = readdirSync(screenshotDir)
  .filter((name) => name.toLowerCase().endsWith(".png"))
  .map((name) => ({
    name,
    full: path.join(screenshotDir, name),
    mtime: 0,
  }));

// pick newest by name timestamp-ish fallback: last in sort
files.sort((a, b) => a.name.localeCompare(b.name));
const img = files.at(-1)?.full;
if (!img) {
  console.error("No screenshot found");
  process.exit(1);
}

const child = spawn(process.execPath, ["index.mjs"], {
  cwd: path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1")),
  env: process.env,
  stdio: ["pipe", "pipe", "pipe"],
});

let out = "";
child.stdout.on("data", (d) => {
  out += d.toString();
});
child.stderr.on("data", (d) => process.stderr.write(d));

const send = (msg) => child.stdin.write(`${JSON.stringify(msg)}\n`);

setTimeout(() => {
  send({
    jsonrpc: "2.0",
    id: 1,
    method: "initialize",
    params: {
      protocolVersion: "2024-11-05",
      capabilities: {},
      clientInfo: { name: "smoke", version: "1" },
    },
  });
}, 100);

setTimeout(() => {
  send({ jsonrpc: "2.0", method: "notifications/initialized" });
  send({
    jsonrpc: "2.0",
    id: 2,
    method: "tools/call",
    params: {
      name: "analyze_image",
      arguments: {
        path: img,
        query: "用中文简要描述这张UI截图布局",
      },
    },
  });
}, 400);

setTimeout(() => {
  console.log(out);
  child.kill();
}, 35000);
