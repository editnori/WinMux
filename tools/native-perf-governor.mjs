#!/usr/bin/env bun
import fs from "node:fs";
import fsp from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import os from "node:os";

const cwd = process.cwd();
const catalogPath = path.resolve(cwd, "perf", "feature-catalog.json");
const nativeAutomationScriptPath = path.resolve(cwd, "scripts", "run-native-automation.ps1");
let cachedWindowsNativeAutomationScriptPath = null;

function fail(message, extra) {
  const error = new Error(message);
  if (extra) {
    error.extra = extra;
  }
  throw error;
}

function parseArgs(argv) {
  const result = { _: [] };
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    if (!arg.startsWith("--")) {
      result._.push(arg);
      continue;
    }

    const key = arg.slice(2);
    const next = argv[index + 1];
    if (next && !next.startsWith("--")) {
      result[key] = next;
      index++;
      continue;
    }

    result[key] = true;
  }

  return result;
}

function isEnabled(value, defaultValue = false) {
  if (value == null) {
    return defaultValue;
  }

  if (typeof value === "boolean") {
    return value;
  }

  return value !== "false";
}

async function loadCatalog() {
  return JSON.parse(await fsp.readFile(catalogPath, "utf8"));
}

function runProcess(cmd, options = {}) {
  const result = spawnSync(cmd[0], cmd.slice(1), {
    cwd,
    encoding: "utf8",
    maxBuffer: 20 * 1024 * 1024,
    ...options,
  });
  if (result.status !== 0) {
    fail(`Command failed: ${cmd.join(" ")}`, {
      status: result.status,
      stdout: result.stdout,
      stderr: result.stderr,
    });
  }

  return result.stdout.trim();
}

function runNativeState() {
  return runNativeAutomationRoute("state");
}

function runPerfSnapshot() {
  const stdout = runProcess(["bun", "tools/native-debugger.mjs", "perf-snapshot"]);
  return JSON.parse(stdout);
}

function runDebugger(command, args = []) {
  const stdout = runProcess(["bun", "tools/native-debugger.mjs", command, ...args]);
  return stdout ? JSON.parse(stdout) : null;
}

function runProfile(featureId, args) {
  const outputDir = path.join(os.tmpdir(), "winmux-perf-suite", featureId.replace(/[^a-z0-9_-]+/gi, "-"));
  fs.mkdirSync(outputDir, { recursive: true });
  runProcess(["bun", "tools/native-debugger.mjs", "profile-action", ...args, "--no-events", "--output-dir", outputDir]);
  return JSON.parse(fs.readFileSync(path.join(outputDir, "profile-action.json"), "utf8"));
}

function runActionProfile(featureId, request, options = {}) {
  const args = ["--request", JSON.stringify(request)];
  if (options.ui) {
    args.unshift("--ui");
  }
  if (options.wait) {
    args.push("--wait", JSON.stringify(options.wait));
  }
  if (options.settleMs != null) {
    args.push("--settle-ms", String(options.settleMs));
  }
  return runProfile(featureId, args);
}

function waitForConditionSync(condition, options = {}) {
  const args = ["--condition", JSON.stringify(condition)];
  if (options.timeoutMs != null) {
    args.push("--timeout", String(options.timeoutMs));
  }
  if (options.intervalMs != null) {
    args.push("--interval", String(options.intervalMs));
  }
  return runDebugger("wait", args);
}

function parseActionCases() {
  const source = fs.readFileSync(path.resolve(cwd, "MainPage.xaml.cs"), "utf8");
  const startIndex = source.indexOf("switch (request.Action?.Trim().ToLowerInvariant())");
  const endIndex = source.indexOf("default:", startIndex);
  const chunk = source.slice(startIndex, endIndex);
  const matches = [...chunk.matchAll(/case \"([^\"]+)\":/g)].map((match) => match[1]);
  return [...new Set(matches)];
}

function resolveWindowsNativeAutomationScriptPath() {
  if (cachedWindowsNativeAutomationScriptPath) {
    return cachedWindowsNativeAutomationScriptPath;
  }

  cachedWindowsNativeAutomationScriptPath = runProcess(["wslpath", "-w", nativeAutomationScriptPath]);
  return cachedWindowsNativeAutomationScriptPath;
}

function runNativeAutomationRoute(route, payload) {
  const cmd = [
    "powershell.exe",
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    resolveWindowsNativeAutomationScriptPath(),
    route,
  ];
  if (payload != null) {
    cmd.push(typeof payload === "string" ? payload : JSON.stringify(payload));
  }

  const stdout = runProcess(cmd);
  return stdout ? JSON.parse(stdout) : null;
}

function runNativeAction(request) {
  const response = runNativeAutomationRoute("action", request);
  if (response && response.ok === false) {
    fail(`Native action failed: ${request.action}`, response);
  }

  return response;
}

function runBrowserState(paneId = "") {
  return runNativeAutomationRoute("browser-state", { paneId });
}

function runTerminalState(tabId = "") {
  return runNativeAutomationRoute("terminal-state", { tabId });
}

function runDiffState(paneId = "", maxLines = 0) {
  return runNativeAutomationRoute("diff-state", { paneId, maxLines });
}

function runEditorState(paneId = "", maxChars = 0, maxFiles = 0) {
  return runNativeAutomationRoute("editor-state", { paneId, maxChars, maxFiles });
}

function runNativeToolTiming(route, payload = null, extra = {}) {
  const started = performance.now();
  const response = runNativeAutomationRoute(route, payload);
  const durationMs = Math.round((performance.now() - started) * 100) / 100;
  return {
    response: { durationMs },
    actionProfile: {
      totalMs: durationMs,
      firstRenderCompleteMs: extra.firstRenderMs ?? 0,
      asyncBackgroundMs: extra.asyncMs ?? 0,
    },
    route,
    routeResponse: response,
  };
}

function runCommandTiming(cmd, options = {}) {
  const started = performance.now();
  const stdout = runProcess(cmd, options);
  const durationMs = Math.round((performance.now() - started) * 100) / 100;
  return {
    response: { durationMs },
    actionProfile: {
      totalMs: durationMs,
      firstRenderCompleteMs: 0,
      asyncBackgroundMs: 0,
    },
    stdout,
  };
}

function getActiveProject(state) {
  return (state.projects || []).find((project) => project.id === state.projectId) || null;
}

function getActiveThread(state) {
  return (state.threads || []).find((thread) => thread.id === state.activeThreadId) || null;
}

function getActiveTab(state) {
  return getActiveThread(state)?.tabs?.find((tab) => tab.id === state.activeTabId) || null;
}

function getPrimaryBrowserSnapshot(paneId = "") {
  const state = runBrowserState(paneId);
  const selectedPaneId = state?.selectedPaneId || paneId || null;
  return state?.panes?.find((pane) => pane.paneId === selectedPaneId) || state?.panes?.[0] || null;
}

function getPrimaryTerminalSnapshot(tabId = "") {
  const state = runTerminalState(tabId);
  const selectedTabId = state?.selectedTabId || tabId || null;
  return state?.tabs?.find((tab) => tab.tabId === selectedTabId) || state?.tabs?.[0] || null;
}

function getThreadById(state, threadId) {
  return (state?.threads || []).find((thread) => thread.id === threadId) || null;
}

function getBrowserPaneForThread(browserState, threadId) {
  return (browserState?.panes || []).find((pane) => pane.threadId === threadId) || null;
}

function pickAlternateTheme(theme) {
  return String(theme || "").toLowerCase() === "light" ? "dark" : "light";
}

function pickAlternateProfile(shellProfileId) {
  for (const candidate of ["wsl", "powershell", "cmd"]) {
    if (!stringEquals(candidate, shellProfileId)) {
      return candidate;
    }
  }
  return "wsl";
}

function stringEquals(left, right) {
  return String(left || "").toLowerCase() === String(right || "").toLowerCase();
}

function deriveContext(state) {
  const activeProject = getActiveProject(state);
  const activeThread = getActiveThread(state);
  const activeTab = getActiveTab(state);
  const altProject = (state.projects || []).find((project) => project.id !== state.projectId) || null;
  const altThread = activeProject?.threads?.find((thread) => thread.id !== state.activeThreadId) || null;

  return {
    activeProjectId: activeProject?.id || state.projectId || null,
    activeProjectName: activeProject?.name || null,
    activeProjectPath: activeProject?.rootPath || state.projectPath || null,
    activeThreadId: activeThread?.id || state.activeThreadId || null,
    altProjectName: altProject?.name || null,
    activeView: state.activeView || "terminal",
    theme: state.theme || null,
    shellProfileId: state.shellProfileId || null,
    worktreePath: state.worktreePath || activeThread?.worktreePath || null,
    activeThreadName: activeThread?.name || null,
    activeTabId: state.activeTabId || activeThread?.selectedTabId || null,
    activeTabKind: activeTab?.kind || null,
    activeTabTitle: activeTab?.title || null,
    activeLayout: activeThread?.layout || null,
    autoFitPaneContentLocked: activeThread?.autoFitPaneContentLocked ?? null,
    paneOpen: Boolean(state.paneOpen),
    inspectorOpen: Boolean(state.inspectorOpen),
    inspectorSection: state.inspectorSection || null,
    notesScope: state.notesScope || null,
    altThreadName: altThread?.name || null,
    alternateWorktreePath: activeProject?.rootPath && !stringEquals(activeProject.rootPath, state.worktreePath || activeThread?.worktreePath)
      ? activeProject.rootPath
      : activeProject?.threads?.find((thread) => thread.id !== activeThread?.id && !stringEquals(thread.worktreePath, state.worktreePath || activeThread?.worktreePath))?.worktreePath || null,
    selectedDiffPath: state.selectedDiffPath || activeThread?.selectedDiffPath || null,
    diffReviewSource: state.diffReviewSource || activeThread?.diffReviewSource || null,
    selectedCheckpointId: state.selectedCheckpointId || activeThread?.selectedCheckpointId || null,
    checkpointCount: state.checkpointCount ?? activeThread?.checkpointCount ?? null,
    noteCount: activeThread?.noteCount ?? null,
    selectedNoteId: activeThread?.selectedNoteId || null,
    zoomedPaneId: activeThread?.zoomedPaneId || null,
    browserCredentialCount: state.browserCredentialCount ?? null,
  };
}

function evaluateBudget(feature, profile, extra = {}) {
  const budget = feature.budget || {};
  const metrics = {
    responseMs: profile.response?.durationMs ?? profile.actionProfile?.totalMs ?? extra.responseMs ?? null,
    firstRenderMs: profile.actionProfile?.firstRenderCompleteMs ?? extra.firstRenderMs ?? null,
    asyncMs: profile.actionProfile?.asyncBackgroundMs ?? extra.asyncMs ?? null,
  };
  const failures = [];
  for (const key of ["responseMs", "firstRenderMs", "asyncMs"]) {
    if (budget[key] == null || metrics[key] == null) {
      continue;
    }

    if (metrics[key] > budget[key]) {
      failures.push(`${key} ${metrics[key].toFixed(1)}ms > ${budget[key]}ms`);
    }
  }

  return {
    metrics,
    pass: failures.length === 0,
    failures,
  };
}

function runBrowserEval(paneId = "", script = "document.title") {
  return runNativeAutomationRoute("browser-eval", { paneId, script });
}

function buildPerfMarker(prefix = "WINMUX_PERF") {
  return `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function normalizeCredentialOrigin(rawUrl) {
  const url = new URL(rawUrl);
  return url.origin.toLowerCase();
}

function buildCredentialId(rawUrl, username = "") {
  const source = `${normalizeCredentialOrigin(rawUrl)}\n${username || ""}`;
  return createHash("sha256").update(source, "utf8").digest("hex").slice(0, 24);
}

function createCredentialCsv(entries) {
  const fixtureDir = path.join(os.tmpdir(), "winmux-perf-fixtures");
  fs.mkdirSync(fixtureDir, { recursive: true });
  const filePath = path.join(fixtureDir, `browser-credentials-${buildPerfMarker("csv")}.csv`);
  const escape = (value) => `"${String(value ?? "").replaceAll('"', '""')}"`;
  const lines = ["name,url,username,password,note"];
  for (const entry of entries) {
    lines.push([
      escape(entry.name),
      escape(entry.url),
      escape(entry.username),
      escape(entry.password),
      escape(entry.note),
    ].join(","));
  }
  fs.writeFileSync(filePath, `${lines.join("\n")}\n`, "utf8");
  return filePath;
}

function toWindowsPath(localPath) {
  return runProcess(["wslpath", "-w", localPath]);
}

function measurementFromDuration(durationMs, extra = {}) {
  return {
    response: { durationMs },
    actionProfile: {
      totalMs: durationMs,
      firstRenderCompleteMs: extra.firstRenderMs ?? durationMs,
      asyncBackgroundMs: extra.asyncMs ?? 0,
    },
    ...extra,
  };
}

function measurementFromProfileDuration(profile, key, fallback = null) {
  const durationMs = profile?.afterPerf?.lastDurationsMs?.[key] ?? fallback;
  if (durationMs == null) {
    return null;
  }

  return measurementFromDuration(durationMs, { profile, perfKey: key });
}

async function ensureSelectedTab(tabId) {
  if (!tabId) {
    return runNativeState();
  }

  const state = runNativeState();
  if (state.activeTabId === tabId) {
    return state;
  }

  runNativeAction({ action: "selectTab", tabId });
  return waitForState(`activeTabId=${tabId}`, (candidate) => candidate.activeTabId === tabId, 5000);
}

async function ensureActiveTerminalTab() {
  const state = runNativeState();
  const thread = getActiveThread(state);
  const terminalTab = thread?.tabs?.find((tab) => stringEquals(tab.kind, "terminal"));
  if (!terminalTab?.id) {
    return { skipped: true, reason: "No terminal tab is available on the active thread." };
  }

  const nextState = await ensureSelectedTab(terminalTab.id);
  return { state: nextState, thread: getActiveThread(nextState), tab: terminalTab };
}

async function ensureActiveBrowserTab() {
  const state = runNativeState();
  const thread = getActiveThread(state);
  const browserTab = thread?.tabs?.find((tab) => stringEquals(tab.kind, "browser"));
  if (!browserTab?.id) {
    return { skipped: true, reason: "No browser pane is available on the active thread." };
  }

  const nextState = await ensureSelectedTab(browserTab.id);
  const browserState = runBrowserState("");
  return {
    state: nextState,
    thread: getActiveThread(nextState),
    tab: browserTab,
    browser: getBrowserPaneForThread(browserState, nextState.activeThreadId) || getPrimaryBrowserSnapshot(""),
  };
}

async function ensureThreadNotes(minCount = 1) {
  let state = runNativeState();
  let thread = getActiveThread(state);
  if (!thread?.id) {
    return { skipped: true, reason: "No active thread is available for note profiling." };
  }

  while ((thread.notes || []).length < minCount) {
    const nextCount = (thread.notes || []).length + 1;
    runNativeAction({
      action: "addThreadNote",
      threadId: thread.id,
      title: `Perf Note ${nextCount}`,
      value: `Perf note ${buildPerfMarker("note")}`,
    });
    state = await waitForState(`noteCount=${nextCount}`, (candidate) => (getActiveThread(candidate)?.noteCount || 0) >= nextCount, 5000);
    thread = getActiveThread(state);
  }

  return { state, thread };
}

async function ensureArchivedThreadNote() {
  const ensured = await ensureThreadNotes(1);
  if (ensured.skipped) {
    return ensured;
  }

  let state = ensured.state;
  let thread = ensured.thread;
  let note = (thread.notes || []).find((item) => item.archived) || (thread.notes || [])[0];
  if (!note) {
    return { skipped: true, reason: "Could not resolve a note for archive profiling." };
  }

  if (!note.archived) {
    runNativeAction({ action: "archiveThreadNote", threadId: thread.id, noteId: note.id });
    await sleep(120);
    state = runNativeState();
    thread = getActiveThread(state);
    note = (thread.notes || []).find((item) => item.id === note.id) || note;
  }

  return { state, thread, note };
}

async function ensureCheckpointExists() {
  let state = runNativeState();
  let thread = getActiveThread(state);
  if (!thread?.id) {
    return { skipped: true, reason: "No active thread is available for checkpoint profiling." };
  }

  if ((thread.checkpointCount || 0) > 0) {
    return { state, thread };
  }

  const checkpointName = `Perf ${buildPerfMarker("checkpoint")}`;
  runNativeAction({ action: "captureCheckpoint", value: checkpointName });
  state = await waitForState("checkpointCount>=1", (candidate) => (getActiveThread(candidate)?.checkpointCount || 0) >= 1, 10000);
  thread = getActiveThread(state);
  return { state, thread };
}

async function measureFeature(feature, context) {
  switch (feature.runner) {
    case "togglePane":
      return runActionProfile(feature.id, { action: "togglePane" }, { settleMs: 0 });
    case "toggleInspector":
      return runActionProfile(feature.id, { action: "toggleInspector" }, { settleMs: 0 });
    case "showSettings":
      return runActionProfile(feature.id, { action: "showSettings" }, { settleMs: 0 });
    case "showTerminal":
      return runActionProfile(feature.id, { action: "showTerminal" }, { settleMs: 0 });
    case "projectSwitch":
      if (!context.altProjectName) {
        return { skipped: true, reason: "No alternate project is available in the current workspace." };
      }
      return runProfile(feature.id, ["--semantic", "selectProjectByName", "--value", context.altProjectName, "--wait", JSON.stringify({ projectName: context.altProjectName }), "--settle-ms", "0"]);
    case "threadSwitch":
      if (!context.altThreadName) {
        return { skipped: true, reason: "No alternate thread is available in the active project." };
      }
      return runProfile(feature.id, ["--semantic", "selectThreadByName", "--value", context.altThreadName, "--wait", JSON.stringify({ threadName: context.altThreadName }), "--settle-ms", "0"]);
    case "layoutDual":
      return runProfile(feature.id, ["--semantic", "setLayout", "--value", "dual", "--settle-ms", "0"]);
    case "layoutQuad":
      return runProfile(feature.id, ["--semantic", "setLayout", "--value", "quad", "--settle-ms", "0"]);
    case "setPaneSplit":
      return runActionProfile(feature.id, { action: "setPaneSplit", value: "primary:0.62;secondary:0.48" }, { settleMs: 0 });
    case "fitPanes":
      return runActionProfile(feature.id, { action: "fitPanes" }, { settleMs: 0 });
    case "fitVisiblePanes":
      return runActionProfile(feature.id, { action: "fitVisiblePanes" }, { settleMs: 0 });
    case "toggleFitPanesLock":
      return runActionProfile(feature.id, { action: "toggleFitPanesLock" }, { settleMs: 0 });
    case "toggleFitVisiblePanesLock":
      return runActionProfile(feature.id, { action: "toggleFitVisiblePanesLock" }, { settleMs: 0 });
    case "themeSwitch": {
      const nextTheme = pickAlternateTheme(context.theme);
      return runActionProfile(feature.id, { action: "setTheme", value: nextTheme }, { wait: { theme: nextTheme }, settleMs: 0 });
    }
    case "profileSwitch": {
      const nextProfile = pickAlternateProfile(context.shellProfileId);
      return runActionProfile(feature.id, { action: "setProfile", value: nextProfile }, { wait: { shellProfileId: nextProfile }, settleMs: 0 });
    }
    case "paneZoom": {
      if (!context.activeTabId) {
        return { skipped: true, reason: "No active pane is available to zoom." };
      }
      const nextZoomedPaneId = stringEquals(context.zoomedPaneId, context.activeTabId) ? null : context.activeTabId;
      return runActionProfile(feature.id, { action: "togglePaneZoom", tabId: context.activeTabId }, { wait: { zoomedPaneId: nextZoomedPaneId }, settleMs: 0 });
    }
    case "paneFocus": {
      if (!context.activeTabId) {
        return { skipped: true, reason: "No active pane is available to focus." };
      }
      const nextZoomedPaneId = stringEquals(context.zoomedPaneId, context.activeTabId) ? null : context.activeTabId;
      return runActionProfile(feature.id, { action: "focusPane", tabId: context.activeTabId }, { wait: { zoomedPaneId: nextZoomedPaneId }, settleMs: 0 });
    }
    case "threadWorktreeSwitch": {
      if (!context.activeThreadId || !context.alternateWorktreePath) {
        return { skipped: true, reason: "No alternate worktree path is available for the active thread." };
      }
      return runActionProfile(
        feature.id,
        { action: "setThreadWorktree", threadId: context.activeThreadId, value: context.alternateWorktreePath },
        { wait: { worktreePath: context.alternateWorktreePath }, settleMs: 0 },
      );
    }
    case "newTerminalTab":
      return runActionProfile(feature.id, { action: "newTab" }, { settleMs: 0 });
    case "newBrowserPane":
      return runActionProfile(feature.id, { action: "newBrowserPane" }, { settleMs: 0 });
    case "newEditorPane":
      return runActionProfile(feature.id, { action: "newEditorPane", value: "MainPage.xaml.cs" }, { settleMs: 0 });
    case "selectTab": {
      const state = runNativeState();
      const thread = getActiveThread(state);
      const targetTab = thread?.tabs?.find((tab) => tab.id !== state.activeTabId);
      if (!targetTab) {
        return { skipped: true, reason: "No alternate tab is available on the active thread." };
      }
      return runActionProfile(feature.id, { action: "selectTab", tabId: targetTab.id }, { wait: { activeTabId: targetTab.id }, settleMs: 0 });
    }
    case "closeTab": {
      const state = runNativeState();
      const thread = getActiveThread(state);
      const baselineTabCount = thread?.tabCount ?? thread?.tabs?.length ?? 0;
      const paneLimit = thread?.paneLimit ?? 0;
      if (paneLimit > 0 && baselineTabCount >= paneLimit) {
        return { skipped: true, reason: "Active thread is already at its pane limit; closing a temp tab would spill into overflow behavior." };
      }
      runNativeAction({ action: "newTab" });
      const waitResult = waitForConditionSync({ tabCount: baselineTabCount + 1 }, { timeoutMs: 10000, intervalMs: 100 });
      if (!waitResult?.ok) {
        return { skipped: true, reason: "Temporary tab did not appear for close-tab profiling." };
      }
      const targetTabId = waitResult.snapshot?.state?.activeTabId;
      if (!targetTabId) {
        return { skipped: true, reason: "Could not resolve the temporary tab id for close-tab profiling." };
      }
      return runActionProfile(feature.id, { action: "closeTab", tabId: targetTabId }, { wait: { tabCount: baselineTabCount }, settleMs: 0 });
    }
    case "renamePane": {
      const state = runNativeState();
      const activeTab = getActiveTab(state);
      if (!activeTab?.id) {
        return { skipped: true, reason: "No active tab is available to rename." };
      }
      const originalTitle = activeTab.title || "Pane";
      const tempTitle = `${originalTitle} Perf`;
      const profile = runActionProfile(feature.id, { action: "renamePane", tabId: activeTab.id, value: tempTitle }, { wait: { activeTabTitle: tempTitle }, settleMs: 0 });
      runNativeAction({ action: "renamePane", tabId: activeTab.id, value: originalTitle });
      return profile;
    }
    case "selectDiffFile":
      if (!context.selectedDiffPath) {
        return { skipped: true, reason: "No selected diff path is available in the current workspace." };
      }
      return runActionProfile(feature.id, { action: "selectDiffFile", value: context.selectedDiffPath }, { settleMs: 0 });
    case "captureCheckpoint": {
      const state = runNativeState();
      const thread = getActiveThread(state);
      if (!thread?.id || !state.selectedDiffPath) {
        return { skipped: true, reason: "No active diff selection is available for checkpoint capture." };
      }
      const nextCheckpointCount = (thread.checkpointCount || 0) + 1;
      return runActionProfile(
        feature.id,
        { action: "captureCheckpoint", value: `Perf ${buildPerfMarker("checkpoint")}` },
        { wait: { checkpointCount: nextCheckpointCount }, settleMs: 0 },
      );
    }
    case "selectReviewSource": {
      const ensured = await ensureCheckpointExists();
      if (ensured.skipped) {
        return ensured;
      }
      const currentSource = ensured.state.diffReviewSource || "live";
      const nextSource = !stringEquals(currentSource, "baseline") ? "baseline" : "live";
      return runActionProfile(
        feature.id,
        { action: "selectReviewSource", value: nextSource },
        {
          wait: { diffReviewSource: nextSource },
          settleMs: 0,
        },
      );
    }
    case "addThreadNote": {
      const state = runNativeState();
      const thread = getActiveThread(state);
      if (!thread?.id) {
        return { skipped: true, reason: "No active thread is available for note creation." };
      }
      const nextNoteCount = (thread.noteCount || 0) + 1;
      return runActionProfile(
        feature.id,
        {
          action: "addThreadNote",
          threadId: thread.id,
          title: `Perf Note ${nextNoteCount}`,
          value: `Perf note ${buildPerfMarker("note")}`,
        },
        { wait: { noteCount: nextNoteCount }, settleMs: 0 },
      );
    }
    case "updateThreadNote": {
      const ensured = await ensureThreadNotes(1);
      if (ensured.skipped) {
        return ensured;
      }
      const note = (ensured.thread.notes || [])[0];
      if (!note?.id) {
        return { skipped: true, reason: "Could not resolve a note to update." };
      }
      return runActionProfile(
        feature.id,
        {
          action: "updateThreadNote",
          threadId: ensured.thread.id,
          noteId: note.id,
          title: `${note.title || "Perf Note"} Updated`,
          value: `Updated ${buildPerfMarker("note")}`,
        },
        { wait: { selectedNoteId: note.id }, settleMs: 0 },
      );
    }
    case "archiveThreadNote": {
      const ensured = await ensureThreadNotes(1);
      if (ensured.skipped) {
        return ensured;
      }
      const note = (ensured.thread.notes || []).find((item) => !item.archived) || (ensured.thread.notes || [])[0];
      if (!note?.id) {
        return { skipped: true, reason: "Could not resolve a note to archive." };
      }
      return runActionProfile(
        feature.id,
        { action: "archiveThreadNote", threadId: ensured.thread.id, noteId: note.id },
        { settleMs: 0 },
      );
    }
    case "restoreThreadNote": {
      const ensured = await ensureArchivedThreadNote();
      if (ensured.skipped) {
        return ensured;
      }
      if (!ensured.note?.id) {
        return { skipped: true, reason: "Could not resolve an archived note to restore." };
      }
      return runActionProfile(
        feature.id,
        { action: "restoreThreadNote", threadId: ensured.thread.id, noteId: ensured.note.id },
        { settleMs: 0 },
      );
    }
    case "selectThreadNote": {
      const ensured = await ensureThreadNotes(2);
      if (ensured.skipped) {
        return ensured;
      }
      const targetNote = (ensured.thread.notes || []).find((item) => item.id !== ensured.thread.selectedNoteId) || (ensured.thread.notes || [])[0];
      if (!targetNote?.id) {
        return { skipped: true, reason: "No alternate note is available for selection profiling." };
      }
      return runActionProfile(
        feature.id,
        { action: "selectThreadNote", threadId: ensured.thread.id, noteId: targetNote.id },
        { wait: { selectedNoteId: targetNote.id }, settleMs: 0 },
      );
    }
    case "openThreadNotes": {
      const state = runNativeState();
      const thread = getActiveThread(state);
      if (!thread?.id) {
        return { skipped: true, reason: "No active thread is available for notes inspector profiling." };
      }
      return runActionProfile(
        feature.id,
        { action: "showThreadNotes", threadId: thread.id },
        { wait: { inspectorOpen: true, inspectorSection: "notes", notesScope: "thread" }, settleMs: 0 },
      );
    }
    case "openProjectNotes": {
      const state = runNativeState();
      const thread = getActiveThread(state);
      if (!thread?.id) {
        return { skipped: true, reason: "No active thread is available for project notes profiling." };
      }
      return runActionProfile(
        feature.id,
        { action: "showProjectNotes", threadId: thread.id },
        { wait: { inspectorOpen: true, inspectorSection: "notes", notesScope: "project" }, settleMs: 0 },
      );
    }
    case "browserNavigate": {
      const browser = getPrimaryBrowserSnapshot();
      if (!browser?.paneId) {
        return { skipped: true, reason: "No browser pane is available in the current workspace." };
      }
      return runActionProfile(feature.id, { action: "navigateBrowser", value: "https://example.com/" }, { wait: { browserUri: "https://example.com/" }, settleMs: 0 });
    }
    case "browserNewTab": {
      const browser = getPrimaryBrowserSnapshot();
      if (!browser?.paneId) {
        return { skipped: true, reason: "No browser pane is available in the current workspace." };
      }
      return runActionProfile(feature.id, { action: "newBrowserTab", value: "https://example.com/" }, { wait: { browserTabCount: (browser.tabCount || 0) + 1 }, settleMs: 0 });
    }
    case "browserSelectTab": {
      let browser = getPrimaryBrowserSnapshot();
      if (!browser?.paneId) {
        return { skipped: true, reason: "No browser pane is available in the current workspace." };
      }
      if ((browser.tabCount || 0) < 2) {
        runNativeAction({ action: "newBrowserTab", value: "https://example.com/" });
        const waitResult = waitForConditionSync({ browserTabCount: 2 }, { timeoutMs: 10000, intervalMs: 100 });
        if (!waitResult?.ok) {
          return { skipped: true, reason: "Could not create a second browser tab for selection profiling." };
        }
        browser = getPrimaryBrowserSnapshot();
      }
      const targetTab = browser.tabs?.find((tab) => tab.id !== browser.selectedTabId);
      if (!targetTab?.id) {
        return { skipped: true, reason: "No alternate browser tab is available for selection profiling." };
      }
      return runActionProfile(feature.id, { action: "selectBrowserTab", value: targetTab.id }, { wait: { selectedBrowserTabId: targetTab.id }, settleMs: 0 });
    }
    case "browserCloseTab": {
      let browser = getPrimaryBrowserSnapshot();
      if (!browser?.paneId) {
        return { skipped: true, reason: "No browser pane is available in the current workspace." };
      }
      if ((browser.tabCount || 0) < 2) {
        runNativeAction({ action: "newBrowserTab", value: "https://example.com/" });
        const waitResult = waitForConditionSync({ browserTabCount: 2 }, { timeoutMs: 10000, intervalMs: 100 });
        if (!waitResult?.ok) {
          return { skipped: true, reason: "Could not create a second browser tab for close profiling." };
        }
        browser = getPrimaryBrowserSnapshot();
      }
      const targetTab = browser.tabs?.find((tab) => tab.id !== browser.selectedTabId) || browser.tabs?.[browser.tabs.length - 1];
      if (!targetTab?.id) {
        return { skipped: true, reason: "No browser tab is available for close profiling." };
      }
      return runActionProfile(feature.id, { action: "closeBrowserTab", value: targetTab.id }, { wait: { browserTabCount: Math.max(0, (browser.tabCount || 0) - 1) }, settleMs: 0 });
    }
    case "browserCredentialImport": {
      const state = runNativeState();
      const currentCount = state.browserCredentialCount;
      if (currentCount == null) {
        return { skipped: true, reason: "Browser credential count is not available on this build." };
      }
      const url = `https://perf-import-${Date.now().toString(36)}.example.invalid/login`;
      const username = "perf-user";
      const csvPath = createCredentialCsv([{
        name: "Perf Import",
        url,
        username,
        password: "perf-pass",
        note: buildPerfMarker("vault"),
      }]);
      const profile = runActionProfile(
        feature.id,
        { action: "importBrowserPasswordsCsv", value: toWindowsPath(csvPath) },
        { wait: { browserCredentialCount: currentCount + 1 }, settleMs: 0 },
      );
      runNativeAction({ action: "deleteBrowserCredential", value: buildCredentialId(url, username) });
      return profile;
    }
    case "browserCredentialDelete": {
      const state = runNativeState();
      const currentCount = state.browserCredentialCount;
      if (currentCount == null) {
        return { skipped: true, reason: "Browser credential count is not available on this build." };
      }
      const url = `https://perf-delete-${Date.now().toString(36)}.example.invalid/login`;
      const username = "perf-delete";
      const csvPath = createCredentialCsv([{
        name: "Perf Delete",
        url,
        username,
        password: "perf-pass",
        note: buildPerfMarker("vault"),
      }]);
      runNativeAction({ action: "importBrowserPasswordsCsv", value: toWindowsPath(csvPath) });
      const importedState = await waitForState(`browserCredentialCount=${currentCount + 1}`, (candidate) => candidate.browserCredentialCount === currentCount + 1, 5000);
      return runActionProfile(
        feature.id,
        { action: "deleteBrowserCredential", value: buildCredentialId(url, username) },
        { wait: { browserCredentialCount: importedState.browserCredentialCount - 1 }, settleMs: 0 },
      );
    }
    case "browserCredentialClear": {
      const state = runNativeState();
      if (!isEnabled(process.env.WINMUX_PERF_ALLOW_VAULT_CLEAR)) {
        return { skipped: true, reason: "Vault clear is gated; rerun with WINMUX_PERF_ALLOW_VAULT_CLEAR=1 on an isolated credential store." };
      }
      if ((state.browserCredentialCount || 0) === 0) {
        return { skipped: true, reason: "Browser credential vault is already empty." };
      }
      return runActionProfile(feature.id, { action: "clearBrowserCredentials" }, { wait: { browserCredentialCount: 0 }, settleMs: 0 });
    }
    case "browserAutofillSuccess":
    case "browserAutofillBlocked":
    case "browserAutofillFailed": {
      const ensured = await ensureActiveBrowserTab();
      if (ensured.skipped) {
        return ensured;
      }
      const targetUrl = "https://example.com/";
      if (!stringEquals(ensured.browser?.uri, targetUrl)) {
        runNativeAction({ action: "navigateBrowser", value: targetUrl });
        await waitForBrowserState(`browser uri=${targetUrl}`, (candidate) => stringEquals(getBrowserPaneForThread(candidate, ensured.thread.id)?.uri, targetUrl), 10000);
      }
      const readyBrowser = getBrowserPaneForThread(runBrowserState(""), ensured.thread.id);
      if (stringEquals(readyBrowser?.credentialAutofillOutcome, "no-match") || String(readyBrowser?.credentialAutofillStatus || "").includes("none exactly match")) {
        return { skipped: true, reason: "No matching WinMux browser credential is available for example.com autofill profiling." };
      }

      const fixtureScript = feature.runner === "browserAutofillBlocked"
        ? "document.body.innerHTML = `<form><input type=\"text\" name=\"user\" /><input type=\"password\" autocomplete=\"new-password\" /><button>Submit</button></form>`; true;"
        : feature.runner === "browserAutofillFailed"
          ? "document.body.innerHTML = `<form><input type=\"text\" name=\"user\" value=\"prefilled-user\" /><input type=\"password\" value=\"prefilled-pass\" /><button>Submit</button></form>`; true;"
          : "document.body.innerHTML = `<form><input type=\"text\" name=\"user\" /><input type=\"password\" /><button>Submit</button></form>`; true;";
      runBrowserEval(readyBrowser?.paneId || "", fixtureScript);
      const expectedOutcome = feature.runner === "browserAutofillBlocked"
        ? "blocked"
        : feature.runner === "browserAutofillFailed"
          ? "failed"
          : "autofilled";
      return runActionProfile(
        feature.id,
        { action: "autofillBrowser" },
        { wait: { browserCredentialOutcome: expectedOutcome }, settleMs: 0 },
      );
    }
    case "terminalInputLatency": {
      const ensured = await ensureActiveTerminalTab();
      if (ensured.skipped) {
        return ensured;
      }
      const marker = buildPerfMarker("terminal");
      return runActionProfile(
        feature.id,
        { action: "input", value: `echo ${marker}\r` },
        { wait: { terminalVisibleTextContains: marker, tabId: ensured.tab.id }, settleMs: 0 },
      );
    }
    case "browserStartPageThemeRefresh": {
      const ensured = await ensureActiveBrowserTab();
      if (ensured.skipped) {
        return ensured;
      }
      if (!stringEquals(ensured.browser?.uri || "winmux://start", "winmux://start")) {
        runNativeAction({ action: "navigateBrowser", value: "" });
        await waitForBrowserState("browser start page", (candidate) => stringEquals(getBrowserPaneForThread(candidate, ensured.thread.id)?.uri || "winmux://start", "winmux://start"), 10000);
      }
      const nextTheme = pickAlternateTheme(context.theme);
      const profile = runActionProfile(feature.id, { action: "setTheme", value: nextTheme }, { wait: { theme: nextTheme }, settleMs: 0 });
      return measurementFromProfileDuration(profile, "browser.start-page.theme-refresh", profile.response?.durationMs ?? profile.actionProfile?.totalMs);
    }
    case "uiTreeRoute":
      return runNativeToolTiming("ui-tree");
    case "browserStateRoute":
      return runNativeToolTiming("browser-state", { paneId: "" });
    case "diffStateRoute":
      return runNativeToolTiming("diff-state", { paneId: "", maxLines: 80 });
    case "editorStateRoute":
      return runNativeToolTiming("editor-state", { paneId: "", maxChars: 4096, maxFiles: 64 });
    case "doctorRoute":
      return runNativeToolTiming("doctor");
    case "renderTraceRoute":
      return runNativeToolTiming("render-trace", { frames: 4, captureScreenshots: false, annotated: false });
    case "screenshotRoute":
      return runNativeToolTiming("screenshot", { path: "", annotated: false });
    case "autosaveWrite": {
      const snapshot = runPerfSnapshot();
      return {
        perfSnapshot: snapshot,
        response: { durationMs: snapshot.lastDurationsMs?.["workspace.save"] ?? null },
        actionProfile: { totalMs: snapshot.lastDurationsMs?.["workspace.save"] ?? null },
      };
    }
    case "saveSession":
      return runActionProfile(feature.id, { action: "saveSession" }, { settleMs: 0 });
    default:
      return { skipped: true, reason: `No runner is implemented for ${feature.runner || feature.id}.` };
  }
}

function getActiveThreadFromState(state) {
  return (state.threads || []).find((thread) => thread.id === state.activeThreadId) || null;
}

function getViewAction(view) {
  switch ((view || "terminal").toLowerCase()) {
    case "settings":
      return "showSettings";
    default:
      return "showTerminal";
  }
}

async function sleep(ms) {
  await new Promise((resolve) => setTimeout(resolve, ms));
}

async function waitForState(description, predicate, timeoutMs = 3000) {
  const startedAt = Date.now();
  while (Date.now() - startedAt <= timeoutMs) {
    const state = runNativeState();
    if (predicate(state)) {
      return state;
    }

    await sleep(80);
  }

  fail(`Timed out waiting for ${description}.`);
}

async function waitForBrowserState(description, predicate, timeoutMs = 5000) {
  const startedAt = Date.now();
  while (Date.now() - startedAt <= timeoutMs) {
    const browserState = runBrowserState("");
    if (predicate(browserState)) {
      return browserState;
    }

    await sleep(80);
  }

  fail(`Timed out waiting for ${description}.`);
}

function captureBaseline() {
  return {
    state: runNativeState(),
    browser: runBrowserState(""),
  };
}

async function restoreActiveThreadNotes(baselineThread) {
  if (!baselineThread?.id) {
    return;
  }

  let state = runNativeState();
  let thread = getThreadById(state, baselineThread.id);
  if (!thread) {
    return;
  }

  const baselineNotesById = new Map((baselineThread.notes || []).map((note) => [note.id, note]));
  for (const note of thread.notes || []) {
    if (baselineNotesById.has(note.id)) {
      continue;
    }

    runNativeAction({ action: "deleteThreadNote", threadId: baselineThread.id, noteId: note.id });
    state = await waitForState(`note ${note.id} to be removed`, (candidate) => {
      const candidateThread = getThreadById(candidate, baselineThread.id);
      return !(candidateThread?.notes || []).some((item) => item.id === note.id);
    }, 5000);
    thread = getThreadById(state, baselineThread.id);
  }

  for (const baselineNote of baselineThread.notes || []) {
    const currentNote = (thread?.notes || []).find((note) => note.id === baselineNote.id);
    if (!currentNote) {
      continue;
    }

    if (!stringEquals(currentNote.title, baselineNote.title) || !stringEquals(currentNote.text, baselineNote.text)) {
      runNativeAction({
        action: "updateThreadNote",
        threadId: baselineThread.id,
        noteId: baselineNote.id,
        title: baselineNote.title,
        value: baselineNote.text,
      });
    }

    if (Boolean(currentNote.archived) !== Boolean(baselineNote.archived)) {
      runNativeAction({
        action: baselineNote.archived ? "archiveThreadNote" : "restoreThreadNote",
        threadId: baselineThread.id,
        noteId: baselineNote.id,
      });
    }
  }

  state = runNativeState();
  thread = getThreadById(state, baselineThread.id);
  if (baselineThread.selectedNoteId && thread?.selectedNoteId !== baselineThread.selectedNoteId) {
    runNativeAction({ action: "selectThreadNote", threadId: baselineThread.id, noteId: baselineThread.selectedNoteId });
    await waitForState(`selectedNoteId=${baselineThread.selectedNoteId}`, (candidate) => getThreadById(candidate, baselineThread.id)?.selectedNoteId === baselineThread.selectedNoteId, 5000);
  }
}

async function restoreThreadBrowserTabs(baselineBrowserState, baselineThreadId) {
  const baselinePane = getBrowserPaneForThread(baselineBrowserState, baselineThreadId);
  if (!baselinePane) {
    return;
  }

  let browserState = runBrowserState("");
  let currentPane = getBrowserPaneForThread(browserState, baselineThreadId);
  if (!currentPane) {
    return;
  }

  while ((currentPane.tabCount || 0) > (baselinePane.tabCount || 0)) {
    const target = currentPane.tabs?.find((tab) => tab.id !== currentPane.selectedTabId) || currentPane.tabs?.[currentPane.tabs.length - 1];
    if (!target?.id) {
      break;
    }

    runNativeAction({ action: "closeBrowserTab", value: target.id });
    browserState = await waitForBrowserState(`browser tab ${target.id} to close`, (candidate) => {
      const pane = getBrowserPaneForThread(candidate, baselineThreadId);
      return !(pane?.tabs || []).some((tab) => tab.id === target.id);
    }, 5000);
    currentPane = getBrowserPaneForThread(browserState, baselineThreadId);
  }

  while ((currentPane?.tabCount || 0) < (baselinePane.tabCount || 0)) {
    const baselineTab = baselinePane.tabs?.[currentPane?.tabCount || 0];
    runNativeAction({ action: "newBrowserTab", value: baselineTab?.uri || "" });
    browserState = await waitForBrowserState(`browser tab count=${baselinePane.tabCount}`, (candidate) => {
      const pane = getBrowserPaneForThread(candidate, baselineThreadId);
      return (pane?.tabCount || 0) >= (baselinePane.tabCount || 0);
    }, 10000);
    currentPane = getBrowserPaneForThread(browserState, baselineThreadId);
  }

  currentPane = getBrowserPaneForThread(runBrowserState(""), baselineThreadId);
  const baselineTabs = baselinePane.tabs || [];
  const currentTabs = currentPane?.tabs || [];
  for (let index = 0; index < Math.min(baselineTabs.length, currentTabs.length); index++) {
    const baselineTab = baselineTabs[index];
    const currentTab = currentTabs[index];
    if (!baselineTab || !currentTab) {
      continue;
    }

    if (currentPane.selectedTabId !== currentTab.id) {
      runNativeAction({ action: "selectBrowserTab", value: currentTab.id });
      browserState = await waitForBrowserState(`selectedBrowserTabId=${currentTab.id}`, (candidate) => getBrowserPaneForThread(candidate, baselineThreadId)?.selectedTabId === currentTab.id, 5000);
      currentPane = getBrowserPaneForThread(browserState, baselineThreadId);
    }

    if (!stringEquals(currentTab.uri, baselineTab.uri)) {
      runNativeAction({ action: "navigateBrowser", value: baselineTab.uri || "" });
      browserState = await waitForBrowserState(`browser tab ${currentTab.id} uri=${baselineTab.uri || "winmux://start"}`, (candidate) => {
        const pane = getBrowserPaneForThread(candidate, baselineThreadId);
        const tab = (pane?.tabs || [])[index];
        return stringEquals(tab?.uri || "", baselineTab.uri || "winmux://start");
      }, 10000);
      currentPane = getBrowserPaneForThread(browserState, baselineThreadId);
    }
  }

  const baselineSelectedIndex = Math.max(0, baselineTabs.findIndex((tab) => tab.id === baselinePane.selectedTabId));
  const selectedTab = (getBrowserPaneForThread(runBrowserState(""), baselineThreadId)?.tabs || [])[baselineSelectedIndex];
  if (selectedTab?.id) {
    runNativeAction({ action: "selectBrowserTab", value: selectedTab.id });
    await waitForBrowserState(`selectedBrowserTabId=${selectedTab.id}`, (candidate) => getBrowserPaneForThread(candidate, baselineThreadId)?.selectedTabId === selectedTab.id, 5000);
  }
}

async function restoreBaseline(baselineSnapshot) {
  const baselineState = baselineSnapshot.state;
  const baselineBrowserState = baselineSnapshot.browser;
  const baseline = deriveContext(baselineState);
  const baselineThread = getActiveThreadFromState(baselineState);
  let state = runNativeState();

  if ((state.activeView || "terminal") !== baseline.activeView) {
    runNativeAction({ action: getViewAction(baseline.activeView) });
    state = await waitForState(`activeView=${baseline.activeView}`, (candidate) => (candidate.activeView || "terminal") === baseline.activeView);
  }

  if (Boolean(state.paneOpen) !== Boolean(baseline.paneOpen)) {
    runNativeAction({ action: "togglePane" });
    state = await waitForState(`paneOpen=${baseline.paneOpen}`, (candidate) => Boolean(candidate.paneOpen) === Boolean(baseline.paneOpen));
  }

  if (Boolean(state.inspectorOpen) !== Boolean(baseline.inspectorOpen)) {
    runNativeAction({ action: "toggleInspector" });
    state = await waitForState(`inspectorOpen=${baseline.inspectorOpen}`, (candidate) => Boolean(candidate.inspectorOpen) === Boolean(baseline.inspectorOpen));
  }

  if (baseline.activeProjectId && state.projectId !== baseline.activeProjectId) {
    runNativeAction({ action: "selectProject", projectId: baseline.activeProjectId });
    state = await waitForState(`projectId=${baseline.activeProjectId}`, (candidate) => candidate.projectId === baseline.activeProjectId, 5000);
  }

  if (baseline.activeThreadId && state.activeThreadId !== baseline.activeThreadId) {
    runNativeAction({ action: "selectThread", threadId: baseline.activeThreadId });
    state = await waitForState(`activeThreadId=${baseline.activeThreadId}`, (candidate) => candidate.activeThreadId === baseline.activeThreadId, 5000);
  }

  if (baseline.theme && !stringEquals(state.theme, baseline.theme)) {
    runNativeAction({ action: "setTheme", value: baseline.theme });
    state = await waitForState(`theme=${baseline.theme}`, (candidate) => stringEquals(candidate.theme, baseline.theme), 5000);
  }

  if (baseline.shellProfileId && !stringEquals(state.shellProfileId, baseline.shellProfileId)) {
    runNativeAction({ action: "setProfile", value: baseline.shellProfileId });
    state = await waitForState(`shellProfileId=${baseline.shellProfileId}`, (candidate) => stringEquals(candidate.shellProfileId, baseline.shellProfileId), 5000);
  }

  let activeThread = getActiveThreadFromState(state);
  const baselinePaneIds = new Set((baselineThread?.panes || []).map((pane) => pane.id));
  const extraPaneIds = (activeThread?.panes || [])
    .map((pane) => pane.id)
    .filter((paneId) => !baselinePaneIds.has(paneId));
  for (const paneId of extraPaneIds) {
    runNativeAction({ action: "closeTab", tabId: paneId });
    state = await waitForState(`pane ${paneId} to close`, (candidate) => {
      const candidateThread = getActiveThreadFromState(candidate);
      return !(candidateThread?.panes || []).some((pane) => pane.id === paneId);
    }, 5000);
    activeThread = getActiveThreadFromState(state);
  }

  if (baseline.worktreePath && !stringEquals(state.worktreePath, baseline.worktreePath)) {
    runNativeAction({ action: "setThreadWorktree", threadId: baseline.activeThreadId, value: baseline.worktreePath });
    state = await waitForState(`worktreePath=${baseline.worktreePath}`, (candidate) => stringEquals(candidate.worktreePath, baseline.worktreePath), 10000);
    activeThread = getActiveThreadFromState(state);
  }

  if ((baselineThread?.checkpointCount || 0) === 0 &&
    (((activeThread?.checkpointCount || 0) !== 0) ||
      !stringEquals(state.diffReviewSource, baseline.diffReviewSource) ||
      !stringEquals(state.selectedCheckpointId, baseline.selectedCheckpointId))) {
    runNativeAction({ action: "setThreadWorktree", threadId: baseline.activeThreadId, value: baseline.worktreePath || baseline.activeProjectPath || "" });
    state = await waitForState(`checkpointCount=${baselineThread?.checkpointCount || 0}`, (candidate) => {
      const candidateThread = getThreadById(candidate, baseline.activeThreadId);
      return stringEquals(candidate.worktreePath, baseline.worktreePath || baseline.activeProjectPath || "") &&
        (candidateThread?.checkpointCount || 0) === (baselineThread?.checkpointCount || 0) &&
        stringEquals(candidate.diffReviewSource, baseline.diffReviewSource || "live");
    }, 10000);
    activeThread = getActiveThreadFromState(state);
  }

  if (baseline.activeLayout && activeThread?.layout !== baseline.activeLayout) {
    runNativeAction({ action: "setLayout", threadId: baseline.activeThreadId, value: baseline.activeLayout });
    state = await waitForState(`layout=${baseline.activeLayout}`, (candidate) => getActiveThreadFromState(candidate)?.layout === baseline.activeLayout);
    activeThread = getActiveThreadFromState(state);
  }

  if (baseline.autoFitPaneContentLocked != null && activeThread?.autoFitPaneContentLocked !== baseline.autoFitPaneContentLocked) {
    runNativeAction({ action: "toggleFitVisiblePanesLock", threadId: baseline.activeThreadId });
    state = await waitForState(`autoFitPaneContentLocked=${baseline.autoFitPaneContentLocked}`, (candidate) => getActiveThreadFromState(candidate)?.autoFitPaneContentLocked === baseline.autoFitPaneContentLocked);
    activeThread = getActiveThreadFromState(state);
  }

  if (baseline.selectedDiffPath && !stringEquals(state.selectedDiffPath, baseline.selectedDiffPath)) {
    runNativeAction({ action: "selectDiffFile", value: baseline.selectedDiffPath });
    state = await waitForState(`selectedDiffPath=${baseline.selectedDiffPath}`, (candidate) => stringEquals(candidate.selectedDiffPath, baseline.selectedDiffPath), 10000);
  }

  await restoreActiveThreadNotes(baselineThread);
  await restoreThreadBrowserTabs(baselineBrowserState, baseline.activeThreadId);

  if (baseline.activeTabId && state.activeTabId !== baseline.activeTabId) {
    runNativeAction({ action: "selectTab", tabId: baseline.activeTabId });
    await waitForState(`activeTabId=${baseline.activeTabId}`, (candidate) => candidate.activeTabId === baseline.activeTabId);
  }
}

async function listFeatures() {
  const catalog = await loadCatalog();
  const summary = catalog.features.reduce((acc, feature) => {
    acc.total++;
    acc[feature.measurementMode] = (acc[feature.measurementMode] || 0) + 1;
    return acc;
  }, { total: 0 });
  console.log(JSON.stringify({
    catalogPath,
    totalFeatures: summary.total,
    automated: summary.automated || 0,
    fixtureRequired: summary.fixture_required || 0,
    manual: summary.manual || 0,
  }, null, 2));
}

async function checkCatalog() {
  const catalog = await loadCatalog();
  const coveredActions = new Set(catalog.features.flatMap((feature) => feature.automationActions || []));
  const actionCases = parseActionCases();
  const missing = actionCases.filter((action) => !coveredActions.has(action));
  const output = {
    ok: missing.length === 0,
    actionCount: actionCases.length,
    coveredActionCount: actionCases.length - missing.length,
    missingActions: missing,
  };
  console.log(JSON.stringify(output, null, 2));
  if (!output.ok) {
    process.exit(1);
  }
}

async function runSuite(args) {
  const catalog = await loadCatalog();
  const baselineSnapshot = captureBaseline();
  const baselineContext = deriveContext(baselineSnapshot.state);
  const requestedIds = new Set(args.feature ? String(args.feature).split(",").filter(Boolean) : []);
  const includeMutating = isEnabled(args["include-mutating"]);
  const includeFixtureRequired = isEnabled(args["include-fixture-required"]);
  const includeManual = isEnabled(args["include-manual"]);

  const results = [];
  for (const feature of catalog.features) {
    if (requestedIds.size > 0 && !requestedIds.has(feature.id)) {
      continue;
    }

    if (feature.measurementMode === "manual" && !includeManual) {
      results.push({ id: feature.id, name: feature.name, status: "skipped", reason: "measurementMode=manual" });
      continue;
    }

    if (feature.measurementMode === "fixture_required" && !includeFixtureRequired) {
      results.push({ id: feature.id, name: feature.name, status: "skipped", reason: "measurementMode=fixture_required" });
      continue;
    }

    if (!["automated", "fixture_required", "manual"].includes(feature.measurementMode)) {
      results.push({ id: feature.id, name: feature.name, status: "skipped", reason: `measurementMode=${feature.measurementMode}` });
      continue;
    }

    if (["mutating", "destructive"].includes(feature.mutation) && !includeMutating) {
      results.push({ id: feature.id, name: feature.name, status: "skipped", reason: "mutating feature; rerun with --include-mutating" });
      continue;
    }

    const measurement = await measureFeature(feature, baselineContext);
    if (measurement?.skipped) {
      results.push({ id: feature.id, name: feature.name, status: "skipped", reason: measurement.reason });
      continue;
    }

    const budget = evaluateBudget(feature, measurement);
    results.push({
      id: feature.id,
      name: feature.name,
      status: budget.pass ? "pass" : "fail",
      budget: feature.budget,
      metrics: budget.metrics,
      failures: budget.failures,
    });

    await restoreBaseline(baselineSnapshot);
  }

  const output = {
    ok: results.every((result) => result.status !== "fail"),
    context: baselineContext,
    results,
  };
  console.log(JSON.stringify(output, null, 2));
  if (!output.ok) {
    process.exit(1);
  }
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const command = args._[0] || "list";
  switch (command) {
    case "list":
      await listFeatures();
      break;
    case "check":
      await checkCatalog();
      break;
    case "run":
      await runSuite(args);
      break;
    default:
      fail(`Unknown command '${command}'. Expected list, check, or run.`);
  }
}

main().catch((error) => {
  console.error(JSON.stringify({
    ok: false,
    error: error.message,
    extra: error.extra || null,
  }, null, 2));
  process.exit(1);
});
