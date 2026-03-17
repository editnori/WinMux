#!/usr/bin/env bun
import fs from "node:fs";
import fsp from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { spawnSync } from "node:child_process";
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

function runProfile(featureId, args) {
  const outputDir = path.join(os.tmpdir(), "winmux-perf-suite", featureId.replace(/[^a-z0-9_-]+/gi, "-"));
  fs.mkdirSync(outputDir, { recursive: true });
  runProcess(["bun", "tools/native-debugger.mjs", "profile-action", ...args, "--output-dir", outputDir]);
  return JSON.parse(fs.readFileSync(path.join(outputDir, "profile-action.json"), "utf8"));
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

function getActiveProject(state) {
  return (state.projects || []).find((project) => project.id === state.projectId) || null;
}

function getActiveThread(state) {
  return (state.threads || []).find((thread) => thread.id === state.activeThreadId) || null;
}

function deriveContext(state) {
  const activeProject = getActiveProject(state);
  const activeThread = getActiveThread(state);
  const altProject = (state.projects || []).find((project) => project.id !== state.projectId) || null;
  const altThread = activeProject?.threads?.find((thread) => thread.id !== state.activeThreadId) || null;

  return {
    activeProjectId: activeProject?.id || state.projectId || null,
    activeProjectName: activeProject?.name || null,
    activeThreadId: activeThread?.id || state.activeThreadId || null,
    altProjectName: altProject?.name || null,
    activeView: state.activeView || "terminal",
    activeThreadName: activeThread?.name || null,
    activeTabId: state.activeTabId || activeThread?.selectedTabId || null,
    activeLayout: activeThread?.layout || null,
    autoFitPaneContentLocked: activeThread?.autoFitPaneContentLocked ?? null,
    paneOpen: Boolean(state.paneOpen),
    inspectorOpen: Boolean(state.inspectorOpen),
    altThreadName: altThread?.name || null,
    selectedDiffPath: state.selectedDiffPath || activeThread?.selectedDiffPath || null,
    zoomedPaneId: activeThread?.zoomedPaneId || null,
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

function measureFeature(feature, context) {
  switch (feature.runner) {
    case "togglePane":
      return runProfile(feature.id, ["--request", "{\"action\":\"togglePane\"}", "--settle-ms", "0"]);
    case "toggleInspector":
      return runProfile(feature.id, ["--request", "{\"action\":\"toggleInspector\"}", "--settle-ms", "0"]);
    case "showSettings":
      return runProfile(feature.id, ["--request", "{\"action\":\"showSettings\"}", "--settle-ms", "0"]);
    case "showTerminal":
      return runProfile(feature.id, ["--request", "{\"action\":\"showTerminal\"}", "--settle-ms", "0"]);
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
      return runProfile(feature.id, ["--request", "{\"action\":\"setPaneSplit\",\"value\":\"primary:0.62;secondary:0.48\"}", "--settle-ms", "0"]);
    case "fitPanes":
      return runProfile(feature.id, ["--request", "{\"action\":\"fitPanes\"}", "--settle-ms", "0"]);
    case "fitVisiblePanes":
      return runProfile(feature.id, ["--request", "{\"action\":\"fitVisiblePanes\"}", "--settle-ms", "0"]);
    case "toggleFitPanesLock":
      return runProfile(feature.id, ["--request", "{\"action\":\"toggleFitPanesLock\"}", "--settle-ms", "0"]);
    case "toggleFitVisiblePanesLock":
      return runProfile(feature.id, ["--request", "{\"action\":\"toggleFitVisiblePanesLock\"}", "--settle-ms", "0"]);
    case "newTerminalTab":
      return runProfile(feature.id, ["--request", "{\"action\":\"newTab\"}", "--settle-ms", "0"]);
    case "newBrowserPane":
      return runProfile(feature.id, ["--request", "{\"action\":\"newBrowserPane\"}", "--settle-ms", "0"]);
    case "newEditorPane":
      return runProfile(feature.id, ["--request", "{\"action\":\"newEditorPane\",\"value\":\"MainPage.xaml.cs\"}", "--settle-ms", "0"]);
    case "selectDiffFile":
      if (!context.selectedDiffPath) {
        return { skipped: true, reason: "No selected diff path is available in the current workspace." };
      }
      return runProfile(feature.id, ["--request", JSON.stringify({ action: "selectDiffFile", value: context.selectedDiffPath }), "--settle-ms", "0"]);
    case "autosaveWrite": {
      const snapshot = runPerfSnapshot();
      return {
        perfSnapshot: snapshot,
        response: { durationMs: snapshot.lastDurationsMs?.["workspace.save"] ?? null },
        actionProfile: { totalMs: snapshot.lastDurationsMs?.["workspace.save"] ?? null },
      };
    }
    case "saveSession":
      return runProfile(feature.id, ["--request", "{\"action\":\"saveSession\"}", "--settle-ms", "0"]);
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

async function restoreBaseline(baselineState) {
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
  const baselineState = runNativeState();
  const baselineContext = deriveContext(baselineState);
  const requestedIds = new Set(args.feature ? String(args.feature).split(",").filter(Boolean) : []);
  const includeMutating = isEnabled(args["include-mutating"]);

  const results = [];
  for (const feature of catalog.features) {
    if (requestedIds.size > 0 && !requestedIds.has(feature.id)) {
      continue;
    }

    if (feature.measurementMode !== "automated") {
      results.push({ id: feature.id, name: feature.name, status: "skipped", reason: `measurementMode=${feature.measurementMode}` });
      continue;
    }

    if (feature.mutation === "mutating" && !includeMutating) {
      results.push({ id: feature.id, name: feature.name, status: "skipped", reason: "mutating feature; rerun with --include-mutating" });
      continue;
    }

    const measurement = measureFeature(feature, baselineContext);
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

    await restoreBaseline(baselineState);
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
