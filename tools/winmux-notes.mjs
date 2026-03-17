#!/usr/bin/env bun

const port = Number(process.env.NATIVE_TERMINAL_AUTOMATION_PORT || 9331);
const baseUrl = `http://127.0.0.1:${port}`;

const [command = "help", ...rest] = process.argv.slice(2);
const args = parseArgs(rest);

const actions = new Set(["add", "update", "delete", "select", "open"]);
const listAliases = new Set(["list", "pull"]);
const showAliases = new Set(["show", "get"]);

try {
  if (command === "help" || command === "--help" || command === "-h") {
    print(helpText());
    process.exit(0);
  }

  if (listAliases.has(command)) {
    if (args.noteId) {
      const output = await showNote(args);
      print(output);
      process.exit(0);
    }

    const output = await listNotes(args);
    print(output);
    process.exit(0);
  }

  if (showAliases.has(command)) {
    const output = await showNote(args);
    print(output);
    process.exit(0);
  }

  if (!actions.has(command)) {
    throw usageError(`Unknown command '${command}'.`);
  }

  const output = await mutateNotes(command, args);
  print(output);
} catch (error) {
  print({
    ok: false,
    error: error instanceof Error ? error.message : String(error),
  });
  process.exit(1);
}

function helpText() {
  return {
    ok: true,
    usage: [
      "bun run native:notes -- list [--scope thread|project] [--thread-id <id>] [--project-id <id>]",
      "bun run native:notes -- show --note-id <id> [--thread-id <id>] [--project-id <id>]",
      "bun run native:notes -- add [--thread-id <id>] [--tab-id <id>] [--title <title>] [--text <text>]",
      "bun run native:notes -- update --note-id <id> [--thread-id <id>] [--tab-id <id>] [--title <title>] [--text <text>]",
      "bun run native:notes -- delete --note-id <id> [--thread-id <id>]",
      "bun run native:notes -- select --note-id <id> [--thread-id <id>]",
      "bun run native:notes -- open [--scope thread|project] [--thread-id <id>]",
    ],
    notes: [
      "Defaults to the active thread when no ids are provided.",
      "Use --scope project to read the full note index for the active project.",
      "Use --tab-id on add/update to attach a note to a specific pane.",
      "The tool prints concise JSON so agents can parse it directly.",
    ],
  };
}

async function listNotes(rawArgs) {
  const state = await getState();
  const scope = normalizeScope(rawArgs.scope);
  const project = resolveProject(state, rawArgs.projectId);

  if (scope === "project") {
    return {
      ok: true,
      action: "list",
      scope,
      project: summarizeProject(project),
      notes: (project.notes ?? []).map(normalizeNote),
    };
  }

  const thread = resolveThread(state, project, rawArgs.threadId);
  return {
    ok: true,
    action: "list",
    scope,
    project: summarizeProject(project),
    thread: summarizeThread(thread),
    notes: (thread.notes ?? []).map(normalizeNote),
  };
}

async function showNote(rawArgs) {
  const noteId = required(rawArgs.noteId, "--note-id is required for show.");
  const state = await getState();
  const project = resolveProject(state, rawArgs.projectId, { optional: true });
  const thread = resolveThread(state, project, rawArgs.threadId, { optional: true });
  const note = findNoteById(state, noteId, { projectId: project?.id, threadId: thread?.id });

  return {
    ok: true,
    action: "show",
    note: normalizeNote(note),
  };
}

async function mutateNotes(commandName, rawArgs) {
  const payload = buildActionPayload(commandName, rawArgs);
  const response = await postAction(payload);
  const state = response?.state ?? await getState();
  const project = resolveProject(state, payload.projectId, { optional: true });
  const thread = resolveThread(state, project, payload.threadId, { optional: true });
  const noteId = payload.noteId || thread?.selectedNoteId || null;
  const note = noteId ? findNoteById(state, noteId, { projectId: project?.id, threadId: thread?.id, optional: true }) : null;

  return {
    ok: response?.ok === true,
    action: payload.action,
    thread: thread ? summarizeThread(thread) : null,
    project: project ? summarizeProject(project) : null,
    note: note ? normalizeNote(note) : noteId ? { id: noteId } : null,
    state: summarizeState(state),
  };
}

function buildActionPayload(commandName, rawArgs) {
  const base = {
    projectId: rawArgs.projectId || undefined,
    threadId: rawArgs.threadId || undefined,
    tabId: rawArgs.tabId || undefined,
    noteId: rawArgs.noteId || undefined,
    title: rawArgs.title || undefined,
    value: rawArgs.text ?? rawArgs.value ?? undefined,
  };

  switch (commandName) {
    case "add":
      return { ...base, action: "addThreadNote" };
    case "update":
      required(base.noteId, "--note-id is required for update.");
      return { ...base, action: "updateThreadNote" };
    case "delete":
      required(base.noteId, "--note-id is required for delete.");
      return { ...base, action: "deleteThreadNote" };
    case "select":
      required(base.noteId, "--note-id is required for select.");
      return { ...base, action: "selectThreadNote" };
    case "open":
      return {
        ...base,
        action: normalizeScope(rawArgs.scope) === "project" ? "showProjectNotes" : "showThreadNotes",
      };
    default:
      throw usageError(`Unsupported command '${commandName}'.`);
  }
}

async function getState() {
  return requestJson("/state");
}

async function postAction(payload) {
  return requestJson("/action", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(payload),
  });
}

async function requestJson(path, init = {}) {
  let response;
  try {
    response = await fetch(`${baseUrl}${path}`, init);
  } catch (error) {
    throw new Error(
      `WinMux automation is not reachable at ${baseUrl}. Launch 'bun run webview2:start' first.`,
    );
  }

  const bodyText = await response.text();
  const body = parseJsonSafely(bodyText);
  if (!response.ok) {
    const message = body?.message || body?.error || bodyText || `HTTP ${response.status}`;
    throw new Error(message);
  }

  return body;
}

function parseArgs(argv) {
  const parsed = {};
  for (let index = 0; index < argv.length; index++) {
    const token = argv[index];
    if (!token.startsWith("--")) {
      continue;
    }

    const withoutPrefix = token.slice(2);
    const equalsIndex = withoutPrefix.indexOf("=");
    if (equalsIndex >= 0) {
      parsed[toCamelKey(withoutPrefix.slice(0, equalsIndex))] = withoutPrefix.slice(equalsIndex + 1);
      continue;
    }

    const next = argv[index + 1];
    if (!next || next.startsWith("--")) {
      parsed[toCamelKey(withoutPrefix)] = true;
      continue;
    }

    parsed[toCamelKey(withoutPrefix)] = next;
    index++;
  }

  return parsed;
}

function toCamelKey(value) {
  return value.replace(/-([a-z])/g, (_, character) => character.toUpperCase());
}

function normalizeScope(value) {
  return String(value || "thread").trim().toLowerCase() === "project" ? "project" : "thread";
}

function resolveProject(state, projectId, options = {}) {
  const projects = state?.projects ?? [];
  const project = projectId
    ? projects.find((candidate) => candidate.id === projectId)
    : projects.find((candidate) => candidate.id === state.projectId) ?? projects[0];

  if (!project && !options.optional) {
    throw new Error(projectId ? `Project '${projectId}' was not found.` : "No active project was found.");
  }

  return project || null;
}

function resolveThread(state, project, threadId, options = {}) {
  const threads = project?.threads ?? state?.threads ?? [];
  const thread = threadId
    ? threads.find((candidate) => candidate.id === threadId)
    : threads.find((candidate) => candidate.id === state.activeThreadId)
      ?? threads.find((candidate) => candidate.id === project?.selectedThreadId)
      ?? threads[0];

  if (!thread && !options.optional) {
    throw new Error(threadId ? `Thread '${threadId}' was not found.` : "No active thread was found.");
  }

  return thread || null;
}

function findNoteById(state, noteId, filters = {}) {
  const matches = [];
  for (const project of state?.projects ?? []) {
    if (filters.projectId && project.id !== filters.projectId) {
      continue;
    }

    for (const note of project.notes ?? []) {
      if (note.id !== noteId) {
        continue;
      }

      if (filters.threadId && note.threadId !== filters.threadId) {
        continue;
      }

      matches.push(note);
    }
  }

  if (matches.length === 0) {
    if (filters.optional) {
      return null;
    }

    throw new Error(`Note '${noteId}' was not found.`);
  }

  return matches[0];
}

function summarizeProject(project) {
  if (!project) {
    return null;
  }

  return {
    id: project.id,
    name: project.name,
    noteCount: project.notes?.length ?? 0,
  };
}

function summarizeThread(thread) {
  if (!thread) {
    return null;
  }

  return {
    id: thread.id,
    name: thread.name,
    selectedNoteId: thread.selectedNoteId ?? null,
    selectedTabId: thread.selectedTabId ?? thread.selectedPaneId ?? null,
    noteCount: thread.notes?.length ?? 0,
  };
}

function summarizeState(state) {
  return {
    projectId: state?.projectId ?? null,
    projectName: state?.projectName ?? null,
    activeThreadId: state?.activeThreadId ?? null,
    activeTabId: state?.activeTabId ?? null,
    theme: state?.theme ?? null,
  };
}

function normalizeNote(note) {
  return note
    ? {
        id: note.id,
        title: note.title,
        text: note.text,
        preview: note.preview,
        projectId: note.projectId,
        projectName: note.projectName,
        threadId: note.threadId,
        threadName: note.threadName,
        paneId: note.paneId,
        paneTitle: note.paneTitle,
        selected: note.selected,
        updatedAt: note.updatedAt,
      }
    : null;
}

function required(value, message) {
  if (value === undefined || value === null || value === "") {
    throw usageError(message);
  }

  return value;
}

function usageError(message) {
  return new Error(message);
}

function parseJsonSafely(value) {
  if (!value) {
    return null;
  }

  try {
    return JSON.parse(value);
  } catch {
    return null;
  }
}

function print(value) {
  console.log(JSON.stringify(value, null, 2));
}
