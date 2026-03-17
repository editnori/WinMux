#!/usr/bin/env bun
import fs from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { parse as parseYaml } from "yaml";

const cwd = process.cwd();
const defaultRequestTimeoutMs = Number(process.env.NATIVE_AUTOMATION_FETCH_TIMEOUT_MS || "20000");
let nativeAutomationScriptWindowsPathPromise = null;

function fail(message, extra) {
  const error = new Error(message);
  if (extra) {
    error.extra = extra;
  }
  throw error;
}

function parseArgs(argv) {
  const result = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (!arg.startsWith("--")) {
      result._.push(arg);
      continue;
    }

    const key = arg.slice(2);
    const next = argv[i + 1];
    if (next && !next.startsWith("--")) {
      result[key] = next;
      i++;
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

async function getWindowsPath(localPath) {
  const proc = Bun.spawn({
    cmd: ["wslpath", "-w", localPath],
    stdout: "pipe",
    stderr: "pipe",
    cwd,
    env: process.env,
  });
  const [stdout, stderr, exitCode] = await Promise.all([
    new Response(proc.stdout).text(),
    new Response(proc.stderr).text(),
    proc.exited,
  ]);
  if (exitCode !== 0) {
    fail(`Could not resolve Windows path for ${localPath}`, { stderr, stdout, exitCode });
  }

  return stdout.trim();
}

async function getNativeAutomationScriptWindowsPath() {
  if (!nativeAutomationScriptWindowsPathPromise) {
    nativeAutomationScriptWindowsPathPromise = getWindowsPath(path.resolve(cwd, "scripts", "run-native-automation.ps1"));
  }

  return nativeAutomationScriptWindowsPathPromise;
}

async function runNativeTool(tool, forwardArgs = [], options = {}) {
  const scriptPath = await getNativeAutomationScriptWindowsPath();
  const timeoutMs = Number(options.timeoutMs || defaultRequestTimeoutMs);
  const proc = Bun.spawn({
    cmd: [
      "powershell.exe",
      "-NoProfile",
      "-ExecutionPolicy",
      "Bypass",
      "-File",
      scriptPath,
      tool,
      ...forwardArgs.filter((value) => value != null).map((value) => String(value)),
    ],
    stdout: "pipe",
    stderr: "pipe",
    cwd,
    env: process.env,
  });
  const timeout = setTimeout(() => proc.kill(), timeoutMs);

  try {
    const [stdout, stderr, exitCode] = await Promise.all([
      new Response(proc.stdout).text(),
      new Response(proc.stderr).text(),
      proc.exited,
    ]);
    if (exitCode !== 0) {
      fail(`native automation tool '${tool}' failed`, {
        kind: "automation-tool-failed",
        tool,
        exitCode,
        stdout,
        stderr,
      });
    }

    const text = stdout.trim();
    if (!text) {
      return null;
    }

    try {
      return JSON.parse(text);
    } catch (error) {
      fail(`Could not parse JSON output from native automation tool '${tool}'`, {
        tool,
        stdout,
        stderr,
        error: error.message,
      });
    }
  } finally {
    clearTimeout(timeout);
  }
}

async function safeJsonOrText(response) {
  try {
    return await response.json();
  } catch {
    return await response.text();
  }
}

function nowStamp() {
  return new Date().toISOString().replace(/[:.]/g, "-");
}

async function ensureDir(dirPath) {
  await fs.mkdir(dirPath, { recursive: true });
  return dirPath;
}

async function defaultArtifactDir(prefix) {
  return ensureDir(path.resolve(cwd, "tmp", "automation-debugger", `${prefix}-${nowStamp()}`));
}

function parseJsonArg(value, fallback = null) {
  if (value == null) {
    return fallback;
  }

  try {
    return JSON.parse(value);
  } catch (error) {
    fail(`Could not parse JSON argument: ${value}`, { error: error.message });
  }
}

async function writeArtifactJson(outputDir, name, data) {
  if (!outputDir) {
    return null;
  }

  const fullPath = path.resolve(outputDir, name);
  await fs.writeFile(fullPath, JSON.stringify(data, null, 2));
  return fullPath;
}

async function loadStructuredFile(filePath) {
  const resolved = path.resolve(cwd, filePath);
  const text = await fs.readFile(resolved, "utf8");
  if (resolved.endsWith(".yaml") || resolved.endsWith(".yml")) {
    return { resolved, data: parseYaml(text) };
  }

  return { resolved, data: JSON.parse(text) };
}

function interpolateValue(value, variables) {
  if (typeof value === "string") {
    return value.replace(/\$\{([^}]+)\}/g, (_, name) => {
      if (Object.prototype.hasOwnProperty.call(variables, name)) {
        return String(variables[name] ?? "");
      }

      return "";
    });
  }

  if (Array.isArray(value)) {
    return value.map((item) => interpolateValue(item, variables));
  }

  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.entries(value).map(([key, entryValue]) => [key, interpolateValue(entryValue, variables)]),
    );
  }

  return value;
}

function unwrapStatePayload(value) {
  if (value && typeof value === "object") {
    if ("state" in value && value.state && typeof value.state === "object") {
      return value.state;
    }
    if ("beforeState" in value && value.beforeState && typeof value.beforeState === "object") {
      return value.beforeState;
    }
    if ("afterState" in value && value.afterState && typeof value.afterState === "object") {
      return value.afterState;
    }
  }

  return value;
}

async function captureScreenshot(outputDir, annotated = false, filename = null) {
  const targetDir = outputDir || (await defaultArtifactDir("inspect"));
  const resolvedName = filename || (annotated ? "annotated.png" : "screenshot.png");
  const targetPath = path.resolve(targetDir, resolvedName);
  return runNativeTool("screenshot", [targetPath, String(annotated)]);
}

function flatten(value, prefix = "", out = new Map()) {
  if (value === null || typeof value !== "object") {
    out.set(prefix || "$", value);
    return out;
  }

  if (Array.isArray(value)) {
    if (value.length === 0) {
      out.set(prefix || "$", []);
      return out;
    }

    value.forEach((item, index) => flatten(item, `${prefix}[${index}]`, out));
    return out;
  }

  const keys = Object.keys(value);
  if (keys.length === 0) {
    out.set(prefix || "$", {});
    return out;
  }

  for (const key of keys) {
    const nextPrefix = prefix ? `${prefix}.${key}` : key;
    flatten(value[key], nextPrefix, out);
  }

  return out;
}

function diffObjects(before, after) {
  const left = flatten(before);
  const right = flatten(after);
  const allKeys = new Set([...left.keys(), ...right.keys()]);
  const changes = [];

  for (const key of [...allKeys].sort()) {
    const beforeValue = left.has(key) ? left.get(key) : undefined;
    const afterValue = right.has(key) ? right.get(key) : undefined;
    if (JSON.stringify(beforeValue) === JSON.stringify(afterValue)) {
      continue;
    }

    changes.push({
      path: key,
      before: beforeValue,
      after: afterValue,
    });
  }

  return changes;
}

function tailEvents(eventsResponse, sinceSequence = 0, maxCount = 40) {
  const events = (eventsResponse?.events || []).filter((event) => event.sequence >= sinceSequence);
  if (events.length <= maxCount) {
    return events;
  }

  return events.slice(-maxCount);
}

async function getState() {
  return runNativeTool("state");
}

async function getUiTree() {
  return runNativeTool("ui-tree");
}

async function getUiRefs() {
  return runNativeTool("ui-refs");
}

async function getEvents() {
  return runNativeTool("events");
}

async function clearEvents() {
  return runNativeTool("events-clear");
}

async function getPerfSnapshot() {
  return runNativeTool("perf-snapshot");
}

async function getDoctor() {
  return runNativeTool("doctor");
}

async function getTerminalState(tabId = "") {
  return runNativeTool("terminal-state", [tabId]);
}

async function getBrowserState(paneId = "") {
  return runNativeTool("browser-state", [paneId]);
}

async function getDiffState(paneId = "", maxLines = 0) {
  return runNativeTool("diff-state", [paneId, String(maxLines)]);
}

async function getEditorState(paneId = "", maxChars = 0, maxFiles = 0) {
  return runNativeTool("editor-state", [paneId, String(maxChars), String(maxFiles)]);
}

async function runAction(request) {
  return runNativeTool("action", [JSON.stringify(request)]);
}

async function runUiAction(request) {
  return runNativeTool("ui-action", [JSON.stringify(request)]);
}

function getActiveThread(state) {
  return (state?.threads || []).find((thread) => thread.id === state?.activeThreadId) || null;
}

function getActiveTab(state) {
  const thread = getActiveThread(state);
  return thread?.tabs?.find((tab) => tab.id === state?.activeTabId) || null;
}

function getProjectByName(state, name) {
  return (state?.projects || []).find((project) => project.name === name) || null;
}

function getThreadByName(state, name) {
  return (state?.threads || []).find((thread) => thread.name === name) || null;
}

function resolveSemanticAction(descriptor, state) {
  if (!descriptor || typeof descriptor !== "object") {
    fail("semanticAction must be an object");
  }

  const name = descriptor.semanticAction || descriptor.action || descriptor.name;
  switch (name) {
    case "selectThreadByName": {
      const thread = getThreadByName(state, descriptor.value);
      if (!thread) {
        fail(`No thread named '${descriptor.value}'`);
      }

      return { kind: "action", request: { action: "selectThread", threadId: thread.id } };
    }
    case "selectProjectByName": {
      const project = getProjectByName(state, descriptor.value);
      if (!project) {
        fail(`No project named '${descriptor.value}'`);
      }

      return { kind: "action", request: { action: "selectProject", projectId: project.id } };
    }
    case "openDiffFile":
      return { kind: "action", request: { action: "selectDiffFile", value: descriptor.value } };
    case "openEditorFile":
      return { kind: "action", request: { action: "newEditorPane", value: descriptor.value } };
    case "toggleInspector":
      return { kind: "action", request: { action: "toggleInspector" } };
    case "setLayout":
      return { kind: "action", request: { action: "setLayout", value: descriptor.value } };
    default:
      fail(`Unknown semantic action '${name}'`);
  }
}

async function buildConditionSnapshot(condition) {
  const snapshot = { state: await getState() };
  const activeTab = getActiveTab(snapshot.state);
  if ("rendererReady" in condition || "terminalStarted" in condition || "terminalExited" in condition) {
    snapshot.terminal = await getTerminalState(
      condition.tabId || (activeTab?.kind === "terminal" ? snapshot.state.activeTabId : ""),
    );
  }
  if ("browserInitialized" in condition || "browserUri" in condition) {
    snapshot.browser = await getBrowserState(condition.paneId || "");
  }
  if ("diffPath" in condition || "hasDiff" in condition) {
    snapshot.diff = await getDiffState(condition.paneId || "", condition.maxLines || 0);
  }
  if ("editorSelectedPath" in condition || "editorDirty" in condition) {
    snapshot.editor = await getEditorState(condition.paneId || "", condition.maxChars || 0, condition.maxFiles || 0);
  }
  return snapshot;
}

function activeTerminal(snapshot) {
  return snapshot?.terminal?.tabs?.find((tab) => tab.tabId === (snapshot.state?.activeTabId || snapshot?.terminal?.selectedTabId)) || snapshot?.terminal?.tabs?.[0] || null;
}

function activeBrowser(snapshot) {
  return snapshot?.browser?.panes?.find((pane) => pane.paneId === (snapshot.state?.activeTabId || snapshot?.browser?.selectedPaneId)) || snapshot?.browser?.panes?.[0] || null;
}

function activeDiff(snapshot) {
  return snapshot?.diff?.panes?.find((pane) => pane.paneId === (snapshot.state?.activeTabId || snapshot?.diff?.selectedPaneId)) || snapshot?.diff?.panes?.[0] || null;
}

function activeEditor(snapshot) {
  return snapshot?.editor?.panes?.find((pane) => pane.paneId === (snapshot.state?.activeTabId || snapshot?.editor?.selectedPaneId)) || snapshot?.editor?.panes?.[0] || null;
}

function matchesCondition(snapshot, condition) {
  const state = snapshot.state;
  const thread = getActiveThread(state);
  const tab = getActiveTab(state);
  const terminal = activeTerminal(snapshot);
  const browser = activeBrowser(snapshot);
  const diff = activeDiff(snapshot);
  const editor = activeEditor(snapshot);

  const checks = {
    activeThreadId: () => state.activeThreadId === condition.activeThreadId,
    activeTabId: () => state.activeTabId === condition.activeTabId,
    projectId: () => state.projectId === condition.projectId,
    projectName: () => state.projectName === condition.projectName,
    activeView: () => state.activeView === condition.activeView,
    paneOpen: () => state.paneOpen === condition.paneOpen,
    inspectorOpen: () => state.inspectorOpen === condition.inspectorOpen,
    selectedDiffPath: () => state.selectedDiffPath === condition.selectedDiffPath,
    threadName: () => thread?.name === condition.threadName,
    paneCount: () => thread?.paneCount === condition.paneCount,
    tabCount: () => thread?.tabCount === condition.tabCount,
    activeTabKind: () => tab?.kind === condition.activeTabKind,
    rendererReady: () => terminal?.rendererReady === condition.rendererReady,
    terminalStarted: () => terminal?.started === condition.terminalStarted,
    terminalExited: () => terminal?.exited === condition.terminalExited,
    browserInitialized: () => browser?.initialized === condition.browserInitialized,
    browserUri: () => browser?.uri === condition.browserUri,
    diffPath: () => diff?.path === condition.diffPath,
    hasDiff: () => diff?.hasDiff === condition.hasDiff,
    editorSelectedPath: () => editor?.selectedPath === condition.editorSelectedPath,
    editorDirty: () => editor?.dirty === condition.editorDirty,
  };
  const passiveKeys = new Set(["tabId", "paneId", "maxLines", "maxChars", "maxFiles"]);

  return Object.keys(condition).every((key) => {
    if (!(key in checks)) {
      return passiveKeys.has(key);
    }

    return checks[key]();
  });
}

function validateCondition(condition) {
  const supported = new Set([
    "activeThreadId",
    "activeTabId",
    "projectId",
    "projectName",
    "activeView",
    "paneOpen",
    "inspectorOpen",
    "selectedDiffPath",
    "threadName",
    "paneCount",
    "tabCount",
    "activeTabKind",
    "rendererReady",
    "terminalStarted",
    "terminalExited",
    "browserInitialized",
    "browserUri",
    "diffPath",
    "hasDiff",
    "editorSelectedPath",
    "editorDirty",
    "tabId",
    "paneId",
    "maxLines",
    "maxChars",
    "maxFiles",
  ]);

  const invalidKeys = Object.keys(condition || {}).filter((key) => !supported.has(key));
  if (invalidKeys.length > 0) {
    fail(`Unsupported wait condition key(s): ${invalidKeys.join(", ")}`, {
      kind: "invalid-wait-condition",
      condition,
    });
  }
}

function classifyTimeout(condition) {
  if (!condition || typeof condition !== "object") {
    return "action-timeout";
  }

  if ("rendererReady" in condition || "terminalStarted" in condition || "terminalExited" in condition) {
    return "renderer-init-timeout";
  }

  if ("browserInitialized" in condition || "browserUri" in condition) {
    return "browser-pane-init-timeout";
  }

  if ("diffPath" in condition || "hasDiff" in condition || "selectedDiffPath" in condition) {
    return "git-refresh-timeout";
  }

  return "action-timeout";
}

async function waitForCondition(condition, options = {}) {
  validateCondition(condition);
  const timeoutMs = Number(options.timeoutMs || options.timeout || 10000);
  const intervalMs = Number(options.intervalMs || options.interval || 100);
  const started = Date.now();
  let lastSnapshot = null;

  while (Date.now() - started <= timeoutMs) {
    lastSnapshot = await buildConditionSnapshot(condition);
    if (matchesCondition(lastSnapshot, condition)) {
      return {
        ok: true,
        elapsedMs: Date.now() - started,
        condition,
        snapshot: lastSnapshot,
      };
    }

    await Bun.sleep(intervalMs);
  }

  return {
    ok: false,
    elapsedMs: Date.now() - started,
    timeoutKind: classifyTimeout(condition),
    condition,
    snapshot: lastSnapshot,
  };
}

async function captureInspect(options = {}) {
  const eventTailCount = Number(options.eventTailCount || options.events || 40);
  const includeUiTree = !(!isEnabled(options.includeUiTree, true) || isEnabled(options["no-ui-tree"]));
  const outputDir = options.outputDir || options["output-dir"] || (await defaultArtifactDir("inspect"));
  await ensureDir(outputDir);

  const [state, perf, eventsResponse] = await Promise.all([
    getState(),
    getPerfSnapshot().catch(() => null),
    getEvents(),
  ]);
  const activeTab = getActiveTab(state);
  let uiTree = null;
  let uiTreeError = null;
  let uiRefs = [];
  if (includeUiTree) {
    try {
      uiTree = await getUiTree();
      uiRefs = uiTree?.interactiveNodes || [];
    } catch (error) {
      uiTreeError = {
        message: error.message,
        extra: error.extra || null,
      };
      uiRefs = await getUiRefs().catch(() => []);
    }
  } else {
    uiRefs = await getUiRefs().catch(() => []);
  }
  const [terminal, browser, diff, editor] = await Promise.all([
    getTerminalState(activeTab?.kind === "terminal" ? activeTab.id : "").catch(() => null),
    getBrowserState("").catch(() => null),
    getDiffState("", Number(options.maxLines || 0)).catch(() => null),
    getEditorState("", Number(options.maxChars || 0), Number(options.maxFiles || 0)).catch(() => null),
  ]);

  const screenshot = !isEnabled(options.screenshot, true) || isEnabled(options["no-screenshot"])
    ? null
    : await captureScreenshot(outputDir, false, "screenshot.png");
  const annotatedScreenshot = !isEnabled(options.annotated, true) || isEnabled(options["no-annotated"])
    ? null
    : await captureScreenshot(outputDir, true, "annotated.png");

  const result = {
    ok: true,
    timestamp: new Date().toISOString(),
    state,
    perf,
    eventTail: tailEvents(eventsResponse, 0, eventTailCount),
    uiTree: uiTree
      ? {
          windowTitle: uiTree.windowTitle,
          activeView: uiTree.activeView,
          root: uiTree.root,
          interactiveNodes: uiTree.interactiveNodes,
        }
      : null,
    uiTreeError,
    uiRefs,
    terminal,
    browser,
    diff,
    editor,
    screenshot,
    annotatedScreenshot,
    outputDir,
  };

  await writeArtifactJson(outputDir, "inspect.json", result);
  return result;
}

async function profileAction(args) {
  const wantsCapture = isEnabled(args.capture) || isEnabled(args.annotated);
  const outputDir = args["output-dir"] || args.outputDir || (wantsCapture ? await defaultArtifactDir("profile-action") : null);
  if (outputDir) {
    await ensureDir(outputDir);
  }

  const beforeState = await getState();
  const beforePerf = await getPerfSnapshot().catch(() => null);
  const beforeEvents = await getEvents();
  const waitCondition = parseJsonArg(args.wait, null);

  let requestKind = args.ui ? "ui" : "action";
  requestKind = isEnabled(args.ui) ? "ui" : "action";
  let request = parseJsonArg(args.request, null);
  if (!request && args["request-file"]) {
    request = (await loadStructuredFile(args["request-file"])).data;
  }
  if (!request && args.semantic) {
    const semantic = resolveSemanticAction({ name: args.semantic, value: args.value }, beforeState);
    requestKind = semantic.kind === "uiAction" ? "ui" : "action";
    request = semantic.request;
  }
  if (!request) {
    fail("profile-action requires --request '<json>' or --semantic <name> --value <value>");
  }

  const beforeScreenshot = wantsCapture ? await captureScreenshot(outputDir, false, "before.png") : null;
  const beforeAnnotatedScreenshot = isEnabled(args.annotated)
    ? await captureScreenshot(outputDir, true, "before-annotated.png")
    : null;
  const started = performance.now();
  const response = requestKind === "ui"
    ? await runUiAction(request)
    : await runAction(request);
  let waitResult = null;
  if (waitCondition) {
    waitResult = await waitForCondition(waitCondition, {
      timeoutMs: args.timeout ? Number(args.timeout) : 10000,
      intervalMs: args.interval ? Number(args.interval) : 100,
    });
  } else {
    const settleMs = Number(args["settle-ms"] ?? args.settleMs ?? 150);
    if (settleMs > 0) {
      await Bun.sleep(settleMs);
    }
  }

  const [afterState, afterPerf, afterEvents] = await Promise.all([
    getState(),
    getPerfSnapshot().catch(() => null),
    getEvents(),
  ]);

  const screenshot = wantsCapture ? await captureScreenshot(outputDir, false, "after.png") : null;
  const annotatedScreenshot = isEnabled(args.annotated)
    ? await captureScreenshot(outputDir, true, "after-annotated.png")
    : null;
  const result = {
    ok: response.ok !== false && (!waitResult || waitResult.ok),
    requestKind,
    request,
    response,
    totalElapsedMs: Math.round((performance.now() - started) * 100) / 100,
    wait: waitResult,
    actionProfile: afterPerf?.lastAction && afterPerf.lastAction.correlationId === response.correlationId
      ? afterPerf.lastAction
      : afterPerf?.lastAction || null,
    beforeState,
    afterState,
    stateDiff: diffObjects(beforeState, afterState),
    eventDiff: tailEvents(afterEvents, beforeEvents.nextSequence || 0, Number(args.events || 60)),
    beforePerf,
    afterPerf,
    beforeScreenshot,
    beforeAnnotatedScreenshot,
    screenshot,
    annotatedScreenshot,
    outputDir,
  };

  if (outputDir) {
    await writeArtifactJson(outputDir, "profile-action.json", result);
  }

  return result;
}

function summarizeNumbers(values) {
  if (!values.length) {
    return { count: 0, min: 0, max: 0, avg: 0, median: 0, p95: 0 };
  }

  const sorted = [...values].sort((a, b) => a - b);
  const avg = values.reduce((sum, value) => sum + value, 0) / values.length;
  const pick = (p) => sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(sorted.length * p) - 1))];
  return {
    count: values.length,
    min: sorted[0],
    max: sorted[sorted.length - 1],
    avg: Math.round(avg * 10) / 10,
    median: pick(0.5),
    p95: pick(0.95),
  };
}

async function runCommandStep(command) {
  const proc = Bun.spawn({
    cmd: ["zsh", "-lc", command],
    stdout: "pipe",
    stderr: "pipe",
    cwd,
    env: process.env,
  });
  const [stdout, stderr] = await Promise.all([
    new Response(proc.stdout).text(),
    new Response(proc.stderr).text(),
  ]);
  const exitCode = await proc.exited;
  return { exitCode, stdout, stderr, ok: exitCode === 0 };
}

function getValueAtPath(source, propertyPath) {
  return propertyPath.split(".").reduce((value, key) => {
    if (value == null) {
      return undefined;
    }
    if (key.includes("[") && key.endsWith("]")) {
      const [left, indexPart] = key.split("[");
      const index = Number(indexPart.slice(0, -1));
      return value[left]?.[index];
    }
    return value[key];
  }, source);
}

function assertCondition(step, context) {
  const actual = getValueAtPath(context, step.path);
  if ("equals" in step) {
    return { ok: JSON.stringify(actual) === JSON.stringify(step.equals), actual };
  }
  if ("contains" in step) {
    return { ok: String(actual ?? "").includes(step.contains), actual };
  }
  if ("exists" in step) {
    return { ok: step.exists ? actual !== undefined && actual !== null : actual == null, actual };
  }
  if ("gt" in step) {
    return { ok: Number(actual) > Number(step.gt), actual };
  }
  if ("lt" in step) {
    return { ok: Number(actual) < Number(step.lt), actual };
  }
  fail(`Unsupported assert step: ${JSON.stringify(step)}`);
}

async function loadScenario(filePath) {
  const { resolved, data } = await loadStructuredFile(filePath);
  return { resolved, scenario: data };
}

async function executeScenarioStep(step, stepDir) {
  switch (step.type) {
    case "action":
      return profileAction({
        request: JSON.stringify(step.request),
        wait: step.wait ? JSON.stringify(step.wait) : null,
        capture: step.capture,
        annotated: step.annotated,
        events: step.events,
        outputDir: stepDir,
      });
    case "uiAction":
      return profileAction({
        ui: true,
        request: JSON.stringify(step.request),
        wait: step.wait ? JSON.stringify(step.wait) : null,
        capture: step.capture,
        annotated: step.annotated,
        events: step.events,
        outputDir: stepDir,
      });
    case "semanticAction": {
      const state = await getState();
      const semantic = resolveSemanticAction(step, state);
      return profileAction({
        ui: semantic.kind === "uiAction",
        request: JSON.stringify(semantic.request),
        wait: step.wait ? JSON.stringify(step.wait) : null,
        capture: step.capture,
        annotated: step.annotated,
        events: step.events,
        outputDir: stepDir,
      });
    }
    case "wait":
      return waitForCondition(step.condition, {
        timeoutMs: step.timeoutMs,
        intervalMs: step.intervalMs,
      });
    case "inspect":
      return captureInspect({
        outputDir: stepDir,
        events: step.events,
        includeUiTree: step.includeUiTree,
        screenshot: step.screenshot,
        annotated: step.annotated,
      });
    case "assert": {
      const snapshot = {
        state: await getState(),
        perf: await getPerfSnapshot().catch(() => null),
      };
      const result = assertCondition(step, snapshot);
      if (!result.ok) {
        fail(`Assertion failed for ${step.path}`, result);
      }
      return result;
    }
    case "sleep":
      await Bun.sleep(Number(step.ms || 250));
      return { ok: true, sleptMs: Number(step.ms || 250) };
    case "command": {
      const result = await runCommandStep(step.command);
      if (!result.ok && step.failOnNonZero !== false) {
        fail(`Command step failed: ${step.command}`, result);
      }
      return result;
    }
    default:
      fail(`Unknown scenario step type '${step.type}'`);
  }
}

async function runScenarioFile(filePath, options = {}) {
  const { resolved, scenario: rawScenario } = await loadScenario(filePath);
  const variableOverrides = parseJsonArg(options.vars, {});
  const scenarioVariables = {
    ...process.env,
    ...(rawScenario.vars || {}),
    ...variableOverrides,
  };
  const scenario = interpolateValue(rawScenario, scenarioVariables);
  const outputDir = options.outputDir || options["output-dir"] || (await defaultArtifactDir(path.basename(filePath, path.extname(filePath))));
  await ensureDir(outputDir);

  const context = {
    scenarioPath: resolved,
    outputDir,
    results: [],
    variables: scenarioVariables,
  };

  const defaults = scenario.defaults || {};
  const steps = (scenario.steps || []).map((step) => ({ ...defaults, ...step }));
  for (let index = 0; index < steps.length; index++) {
    const step = steps[index];
    const stepName = step.name || `${step.type || "step"}-${index + 1}`;
    const stepDir = path.resolve(outputDir, `${String(index + 1).padStart(2, "0")}-${stepName.replace(/[^a-z0-9_-]+/gi, "-")}`);
    await ensureDir(stepDir);
    const started = performance.now();
    try {
      const retries = Math.max(0, Number(step.retries || 0));
      const retryDelayMs = Number(step.retryDelayMs || 250);
      let result = null;
      let attempt = 0;
      while (attempt <= retries) {
        try {
          attempt++;
          result = await executeScenarioStep(step, stepDir);
          if (result?.ok === false && step.allowFailure !== true) {
            fail(`Scenario step '${stepName}' returned ok=false`, result);
          }
          break;
        } catch (error) {
          if (attempt > retries) {
            throw error;
          }

          await Bun.sleep(retryDelayMs);
        }
      }

      const stepResult = {
        name: stepName,
        type: step.type,
        ok: result?.ok !== false,
        attempts: attempt,
        elapsedMs: Math.round((performance.now() - started) * 100) / 100,
        result,
      };
      context.results.push(stepResult);
      await writeArtifactJson(stepDir, "step-result.json", stepResult);
    } catch (error) {
      const failureInspect = step.screenshotOnFailure !== false
        ? await captureInspect({ outputDir: stepDir, events: step.events || 60 })
        : null;
      const stepResult = {
        name: stepName,
        type: step.type,
        ok: false,
        elapsedMs: Math.round((performance.now() - started) * 100) / 100,
        error: error.message,
        extra: error.extra || null,
        inspect: failureInspect,
      };
      context.results.push(stepResult);
      await writeArtifactJson(stepDir, "step-result.json", stepResult);
      return {
        ok: false,
        scenarioPath: resolved,
        outputDir,
        steps: context.results,
      };
    }
  }

  const output = {
    ok: true,
    scenarioPath: resolved,
    outputDir,
    steps: context.results,
  };
  await writeArtifactJson(outputDir, "scenario-result.json", output);
  return output;
}

async function runBenchmark(filePath, options = {}) {
  const iterations = Number(options.iterations || 3);
  const outputDir = options.outputDir || options["output-dir"] || (await defaultArtifactDir(`benchmark-${path.basename(filePath, path.extname(filePath))}`));
  await ensureDir(outputDir);

  const runs = [];
  for (let index = 0; index < iterations; index++) {
    const runDir = path.resolve(outputDir, `run-${String(index + 1).padStart(2, "0")}`);
    const result = await runScenarioFile(filePath, {
      outputDir: runDir,
      vars: options.vars,
    });
    runs.push(result);
    if (!result.ok && !isEnabled(options.continueOnFailure)) {
      break;
    }
  }

  const totals = runs.map((run) => run.steps.reduce((sum, step) => sum + (step.elapsedMs || 0), 0));
  const stepNames = [...new Set(runs.flatMap((run) => run.steps.map((step) => step.name)))];
  const steps = stepNames.map((name) => ({
    name,
    timings: summarizeNumbers(
      runs
        .map((run) => run.steps.find((step) => step.name === name)?.elapsedMs)
        .filter((value) => typeof value === "number"),
    ),
  }));

  const output = {
    ok: runs.every((run) => run.ok),
    scenarioPath: path.resolve(cwd, filePath),
    outputDir,
    iterations: runs.length,
    totalTimings: summarizeNumbers(totals),
    steps,
    runs,
  };
  await writeArtifactJson(outputDir, "benchmark-result.json", output);
  return output;
}

async function watchUi(options = {}) {
  const durationMs = Number(options.duration || options.durationMs || 30000);
  const intervalMs = Number(options.interval || options.intervalMs || 500);
  const eventTailCount = Number(options.events || 20);
  const started = Date.now();
  let previousState = await getState();
  let previousEvents = await getEvents();
  let previousUiTree = isEnabled(options.includeUiTree) ? await getUiTree() : null;
  const observations = [];

  while (Date.now() - started <= durationMs) {
    await Bun.sleep(intervalMs);
    const [state, events, uiTree] = await Promise.all([
      getState(),
      getEvents(),
      isEnabled(options.includeUiTree) ? getUiTree() : Promise.resolve(null),
    ]);
    const stateChanges = diffObjects(previousState, state);
    const eventDiff = tailEvents(events, previousEvents.nextSequence || 0, eventTailCount);
    const uiTreeChanges = uiTree && previousUiTree
      ? diffObjects(previousUiTree.interactiveNodes || [], uiTree.interactiveNodes || [])
      : [];
    if (stateChanges.length > 0 || eventDiff.length > 0 || uiTreeChanges.length > 0) {
      const observation = {
        timestamp: new Date().toISOString(),
        stateChanges,
        eventDiff,
        uiTreeChanges,
      };
      observations.push(observation);
      console.log(JSON.stringify(observation, null, 2));
    }
    previousState = state;
    previousEvents = events;
    previousUiTree = uiTree;
  }

  return { ok: true, observations };
}

async function crashReport(options = {}) {
  const outputDir = options.outputDir || options["output-dir"] || (await defaultArtifactDir("crash-report"));
  await ensureDir(outputDir);
  const doctor = await getDoctor();
  const inspect = doctor.uiResponsive === false
    ? null
    : await captureInspect({
        outputDir,
        events: options.events || 80,
        includeUiTree: !isEnabled(options.includeUiTree, true) ? false : true,
        screenshot: isEnabled(options.capture, true),
        annotated: isEnabled(options.annotated),
      });
  const report = {
    ok: true,
    timestamp: new Date().toISOString(),
    doctor,
    inspect,
    outputDir,
  };
  await writeArtifactJson(outputDir, "crash-report.json", report);
  return report;
}

async function run() {
  const args = parseArgs(process.argv.slice(2));
  const [command] = args._;

  switch (command) {
    case "perf-snapshot":
      return getPerfSnapshot();
    case "doctor":
      return getDoctor();
    case "inspect":
      return captureInspect(args);
    case "wait": {
      const condition = parseJsonArg(args.condition, null);
      if (!condition) {
        fail("wait requires --condition '<json>'");
      }
      return waitForCondition(condition, args);
    }
    case "profile-action":
      return profileAction(args);
    case "click-and-capture": {
      const request = {
        action: args.action || "click",
        automationId: args["automation-id"],
        refLabel: args["ref-label"],
        elementId: args["element-id"],
        name: args.name,
        text: args.text,
        value: args.value,
      };
      return profileAction({
        ui: true,
        request: JSON.stringify(request),
        wait: args.wait,
        capture: true,
        annotated: args.annotated !== "false",
        events: args.events || 60,
        outputDir: args["output-dir"],
      });
    }
    case "state-diff": {
      const before = args.before
        ? unwrapStatePayload(JSON.parse(await fs.readFile(path.resolve(cwd, args.before), "utf8")))
        : await getState();
      const after = args.after
        ? unwrapStatePayload(JSON.parse(await fs.readFile(path.resolve(cwd, args.after), "utf8")))
        : await getState();
      return { ok: true, changes: diffObjects(before, after) };
    }
    case "scenario":
      if (!args._[1]) {
        fail("scenario requires a file path");
      }
      return runScenarioFile(args._[1], args);
    case "benchmark":
      if (!args._[1]) {
        fail("benchmark requires a file path");
      }
      return runBenchmark(args._[1], args);
    case "ui-watch":
      return watchUi(args);
    case "crash-report":
      return crashReport(args);
    default:
      fail(`Unknown command '${command}'. Expected one of: perf-snapshot, doctor, inspect, wait, profile-action, click-and-capture, state-diff, scenario, benchmark, ui-watch, crash-report`);
  }
}

run()
  .then((result) => {
    if (result !== undefined) {
      console.log(JSON.stringify(result, null, 2));
    }
    if (result?.ok === false) {
      process.exit(1);
    }
  })
  .catch((error) => {
    console.error(JSON.stringify({
      ok: false,
      error: error.message,
      extra: error.extra || null,
    }, null, 2));
    process.exit(1);
  });
