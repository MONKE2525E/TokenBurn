#!/usr/bin/env node

// Security model:
// - pull_request_target is used only to post review comments.
// - The PR head is fetched by exact SHA and materialized in a detached,
//   quarantined worktree. Nothing from that worktree is executed or installed.
// - Review rules are read from the trusted base commit, never the PR head.
// - OCR gets an isolated HOME and no MCP or shell-tool configuration.
// - PR text, comments, diffs, and files are data for the reviewer, never commands.

import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const ownerRepo = required("GITHUB_REPOSITORY").split("/");
const owner = ownerRepo[0];
const repo = ownerRepo[1];
const token = required("GITHUB_TOKEN");
const api = process.env.GITHUB_API_URL || "https://api.github.com";
const ruleDoc = ".github/tokenburn-review-rules.md";
const ruleConfig = ".github/tokenburn-ocr-rules.json";
const stateMarker = "<!-- tokenburn-ai-review:v1";
const shaPattern = /^[0-9a-f]{40}$/i;

function required(name) {
  const value = process.env[name];
  if (!value) throw new Error(`missing required environment variable ${name}`);
  return value;
}

function redact(value) {
  const apiKey = process.env.CLIPROXY_API_KEY || "";
  const githubToken = process.env.GITHUB_TOKEN || "";
  let out = String(value || "");
  if (apiKey) out = out.split(apiKey).join("REDACTED");
  if (githubToken) out = out.split(githubToken).join("REDACTED");
  return out.replace(/(Bearer\s+)[A-Za-z0-9._\-+/=]+/gi, "$1REDACTED");
}

function sanitizeStateValue(value) {
  return String(value || "").split("-->").join("").replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, "").slice(0, 200);
}

async function github(pathname, init = {}) {
  const response = await fetch(pathname.startsWith("http") ? pathname : `${api}${pathname}`, {
    ...init,
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: "application/vnd.github+json",
      "X-GitHub-Api-Version": "2022-11-28",
      ...(init.body ? { "Content-Type": "application/json" } : {}),
      ...(init.headers || {}),
    },
  });
  if (!response.ok) throw new Error(`GitHub API ${response.status}: ${(await response.text()).slice(0, 400)}`);
  return response.status === 204 ? null : response.json();
}

function run(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { shell: false, ...options });
    let stdout = "";
    let stderr = "";
    child.stdout?.on("data", (data) => { stdout += data; });
    child.stderr?.on("data", (data) => { stderr += data; });
    child.on("error", reject);
    child.on("close", (code) => resolve({ code, stdout, stderr }));
  });
}

async function git(args, options = {}) {
  const result = await run("git", args, { ...options, env: { ...process.env, GIT_LFS_SKIP_SMUDGE: "1" } });
  if (result.code !== 0) throw new Error(`git ${args[0]} failed: ${result.stderr.slice(0, 600)}`);
  return result;
}

function event() {
  return JSON.parse(readFileSync(required("GITHUB_EVENT_PATH"), "utf8"));
}

async function context() {
  const current = event();
  if (process.env.GITHUB_EVENT_NAME === "pull_request_target") {
    return { pr: current.pull_request, mode: "normal", force: false, trusted: true };
  }
  if (process.env.GITHUB_EVENT_NAME !== "issue_comment" || !current.issue?.pull_request) return null;
  const firstLine = String(current.comment?.body || "").split("\n", 1)[0].trim();
  const match = /^\/tokenburn-review(?:\s|$)(.*)$/i.exec(firstLine);
  if (!match) return null;
  const author = current.comment?.user?.login;
  if (!author) return null;
  let permission;
  try {
    permission = await github(`/repos/${owner}/${repo}/collaborators/${encodeURIComponent(author)}/permission`);
  } catch {
    return null;
  }
  if (!permission || !["admin", "write", "maintain"].includes(permission.permission)) return null;
  const pr = await github(`/repos/${owner}/${repo}/pulls/${current.issue.number}`);
  const flags = match[1].toLowerCase();
  return { pr, mode: flags.includes("security") ? "security" : "normal", force: flags.includes("full"), trusted: false };
}

async function existingStateComment(number) {
  for (let page = 1; page <= 10; page += 1) {
    const comments = await github(`/repos/${owner}/${repo}/issues/${number}/comments?per_page=100&page=${page}`);
    const found = comments.find((comment) => comment.body?.includes(stateMarker));
    if (found || comments.length < 100) return found || null;
  }
  return null;
}

function parseState(comment) {
  if (!comment) return null;
  const start = comment.body.indexOf(stateMarker);
  const end = comment.body.indexOf("-->", start);
  if (start < 0 || end < 0) return null;
  try { return JSON.parse(comment.body.slice(start + stateMarker.length, end).trim()); } catch { return null; }
}

async function updateState(number, existing, text, state) {
  const body = `${text}\n\n${stateMarker} ${JSON.stringify(state)} -->`;
  const request = existing
    ? [`/repos/${owner}/${repo}/issues/comments/${existing.id}`, { method: "PATCH", body: JSON.stringify({ body }) }]
    : [`/repos/${owner}/${repo}/issues/${number}/comments`, { method: "POST", body: JSON.stringify({ body }) }];
  try { return await github(request[0], request[1]); } catch (error) { console.error(`progress comment failed: ${error.message}`); return existing; }
}

function summary(stage, details = {}) {
  if (stage === "preparing") return `Preparing TokenBurn AI review (${details.mode} mode)...`;
  if (stage === "reviewing") return `Reviewing ${details.sha?.slice(0, 7) || "the current commit"} with \`${details.model}\`...`;
  if (stage === "complete") return `TokenBurn AI review complete. ${details.findings} finding${details.findings === 1 ? "" : "s"}.`;
  if (stage === "skipped") return "TokenBurn AI review skipped: no review gateway is configured.";
  return `TokenBurn AI review failed (${details.reason || "review_failed"}).`;
}

async function trustedFile(baseSha, relativePath) {
  const result = await git(["show", `${baseSha}:${relativePath}`]);
  return result.stdout;
}

async function reviewInQuarantine(pr, model, home) {
  const quarantine = mkdtempSync(path.join(tmpdir(), "tokenburn-pr-"));
  rmSync(quarantine, { recursive: true, force: true });
  try {
    await git(["-c", "core.hooksPath=/dev/null", "worktree", "add", "--detach", quarantine, pr.head.sha]);
    for (const file of [ruleConfig, ruleDoc]) {
      const destination = path.join(quarantine, file);
      mkdirSync(path.dirname(destination), { recursive: true });
      writeFileSync(destination, await trustedFile(pr.base.sha, file));
    }
    const environment = {
      PATH: process.env.PATH,
      HOME: home,
      OCR_LLM_URL: required("CLIPROXY_URL"),
      OCR_LLM_TOKEN: required("CLIPROXY_API_KEY"),
      OCR_LLM_MODEL: model,
      OCR_USE_ANTHROPIC: "false",
    };
    const args = ["review", "--from", pr.base.sha, "--to", pr.head.sha, "--format", "json", "--model", model, "--audience", "agent", "--rule", ruleConfig, "--background", ruleDoc, "--concurrency", "2", "--timeout", "10", "--max-git-procs", "2"];
    const preview = await run("ocr", ["review", "--from", pr.base.sha, "--to", pr.head.sha, "--preview"], { cwd: quarantine, env: environment });
    if (preview.code !== 0) return { code: 1, stderr: "review preview failed", previewFailed: true };
    return await run("ocr", args, { cwd: quarantine, env: environment });
  } finally {
    try { await git(["worktree", "remove", "--force", quarantine]); } catch { rmSync(quarantine, { recursive: true, force: true }); }
  }
}

function jsonFromOutput(output) {
  const trimmed = output.trim();
  const fenced = trimmed.match(/```(?:json)?\s*([\s\S]*?)\s*```/i)?.[1];
  for (const candidate of [trimmed, fenced]) {
    if (!candidate) continue;
    try { return JSON.parse(candidate); } catch { }
  }
  const start = trimmed.search(/[\[{]/);
  const end = Math.max(trimmed.lastIndexOf("}"), trimmed.lastIndexOf("]"));
  if (start >= 0 && end > start) {
    try { return JSON.parse(trimmed.slice(start, end + 1)); } catch { }
  }
  return null;
}

function findings(output) {
  const data = jsonFromOutput(output);
  const list = Array.isArray(data) ? data : data?.comments || data?.findings || data?.issues || data?.results || [];
  return list.filter((item) => item && typeof item === "object").map((item) => ({
    file: item.file || item.path || item.filename,
    line: item.line || item.line_number || item.start_line,
    severity: item.severity || item.level || "info",
    message: item.message || item.description || item.body || item.content || "",
  })).filter((item) => item.message);
}

async function postFindings(number, pr, items) {
  const positioned = items.filter((item) => item.file && Number.isInteger(Number(item.line)) && Number(item.line) > 0);
  const unpositioned = items.filter((item) => !positioned.includes(item));
  if (positioned.length) {
    try {
      await github(`/repos/${owner}/${repo}/pulls/${number}/reviews`, { method: "POST", body: JSON.stringify({ commit_id: pr.head.sha, event: "COMMENT", comments: positioned.map((item) => ({ path: item.file, line: Number(item.line), side: "RIGHT", body: `**[${item.severity}]** ${item.message}` })) }) });
    } catch (error) {
      console.error(`inline comments failed: ${error.message}`);
      unpositioned.push(...positioned);
    }
  }
  if (unpositioned.length) {
    await github(`/repos/${owner}/${repo}/issues/${number}/comments`, { method: "POST", body: JSON.stringify({ body: ["**TokenBurn AI review findings**", "", ...unpositioned.map((item) => `- \`${item.file || "unknown"}:${item.line || "?"}\` [${item.severity}] ${item.message}`)].join("\n") }) });
  }
}

async function main() {
  const selected = await context();
  if (!selected) return;
  const { pr, mode, force, trusted } = selected;
  if (trusted && pr.draft) return;
  if (!shaPattern.test(pr.base.sha) || !shaPattern.test(pr.head.sha)) throw new Error("invalid base or head SHA");
  const number = pr.number;
  const existing = await existingStateComment(number);
  const previous = parseState(existing);
  if (!force && previous?.completed && previous.headSha === pr.head.sha && previous.mode === mode) return;
  const models = [...new Set([process.env.CLIPROXY_MODEL || "gemini-3.6-flash-high", process.env.CLIPROXY_FALLBACK_MODEL || "claude-sonnet-4.6"])].filter(Boolean);
  const baseState = { ...(previous || {}), prNumber: number, headSha: pr.head.sha, mode, models, completed: false };
  if (!String(process.env.CLIPROXY_API_KEY || "").trim() || !String(process.env.CLIPROXY_URL || "").trim()) {
    await updateState(number, existing, summary("skipped"), { ...baseState, status: "skipped", reason: "provider_not_configured" });
    return;
  }
  let stateComment = await updateState(number, existing, summary("preparing", { mode }), { ...baseState, status: "preparing" });
  const home = mkdtempSync(path.join(tmpdir(), "tokenburn-ocr-home-"));
  try {
    await git(["fetch", "--no-tags", "--no-recurse-submodules", "origin", pr.base.sha, pr.head.sha]);
    let result;
    for (const model of models) {
      stateComment = await updateState(number, stateComment, summary("reviewing", { sha: pr.head.sha, model }), { ...baseState, model, status: "reviewing" });
      result = await reviewInQuarantine(pr, model, home);
      if (result.code === 0) break;
      const output = `${result.stderr}\n${result.stdout}`;
      if (!/quota|rate[ -]?limit|429|model.{0,40}(?:not found|unavailable)/i.test(output)) break;
    }
    if (!result || result.code !== 0) {
      const reason = result?.previewFailed ? "preview_failed" : "review_failed";
      const raw = `${result?.stderr || ""}\n${result?.stdout || ""}`;
      const detail = redact(raw).replace(/\s+/g, " ").trim().slice(0, 400);
      console.error(`tokenburn review failed (${reason}): ${detail}`);
      await updateState(number, stateComment, summary("failed", { reason }), { ...baseState, status: "failed", reason, detail: sanitizeStateValue(detail) });
      process.exitCode = 1;
      return;
    }
    const resultFindings = findings(result.stdout);
    await postFindings(number, pr, resultFindings);
    await updateState(number, stateComment, summary("complete", { findings: resultFindings.length }), { ...baseState, status: "completed", findings: resultFindings.length, completed: true });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
}

main().catch((error) => { console.error(error.stack || error.message); process.exitCode = 1; });
