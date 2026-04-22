import {
    connect,
    onToolResult,
    callTool,
    readResource,
    CallToolResult,
    HostContext,
    getHostContext,
    onHostContextChanged,
    applyHostStyles,
    observeBodySize,
    sendChatMessage,
    requestDisplayMode,
    onToolCancelled,
    onResourceTeardown,
} from "../shared/app-bridge";

interface BuilderArgs {
    connectionId: string;
}

interface Column {
    name: string;
    dataType?: string;
}

const args: BuilderArgs = { connectionId: "" };

const connSelect = document.getElementById("conn") as HTMLSelectElement;
const schemaSelect = document.getElementById("schema") as HTMLSelectElement;
const tableSelect = document.getElementById("table") as HTMLSelectElement;
const columnsDiv = document.getElementById("columns") as HTMLDivElement;
const filtersDiv = document.getElementById("filters") as HTMLDivElement;
const sqlPre = document.getElementById("sql") as HTMLPreElement;
const runButton = document.getElementById("run") as HTMLButtonElement;
const askModelButton = document.getElementById("ask-model") as HTMLButtonElement;
const fullscreenButton = document.getElementById("toggle-fullscreen") as HTMLButtonElement;

interface Filter { column: string; op: string; value: string; }
let columns: Column[] = [];
const selectedColumns = new Set<string>();
const filters: Filter[] = [];
let currentDisplayMode: string | undefined;
let fullscreenSupported = true;

function pick<T>(obj: Record<string, unknown> | undefined, ...keys: string[]): T | undefined {
    if (!obj) return undefined;
    for (const k of keys) if (obj[k] !== undefined) return obj[k] as T;
    return undefined;
}

async function readResourceJson<T>(uri: string, fallback: T): Promise<T> {
    try {
        const res = await readResource(uri);
        const text = res.contents?.[0]?.text;
        if (!text) return fallback;
        return JSON.parse(text) as T;
    } catch (err) {
        console.warn(`Could not read ${uri}`, err);
        return fallback;
    }
}

async function loadConnections() {
    const parsed = await readResourceJson<Array<{ id: string; descriptor: { name: string } }>>(
        "mcpdb://connections",
        [],
    );
    connSelect.innerHTML = "<option value=''>Select a connection…</option>";
    for (const c of parsed) {
        const option = document.createElement("option");
        option.value = c.id;
        option.textContent = `${c.descriptor.name} (${c.id})`;
        connSelect.appendChild(option);
    }
    if (args.connectionId) connSelect.value = args.connectionId;
    await loadSchemas();
}

async function loadSchemas() {
    schemaSelect.innerHTML = "";
    if (!connSelect.value) return;
    const parsed = await readResourceJson<Array<{ name: string }>>(
        `mcpdb://connections/${connSelect.value}/schemas`,
        [],
    );
    for (const s of parsed) {
        const opt = document.createElement("option");
        opt.value = s.name;
        opt.textContent = s.name;
        schemaSelect.appendChild(opt);
    }
    await loadTables();
}

async function loadTables() {
    tableSelect.innerHTML = "";
    if (!connSelect.value || !schemaSelect.value) return;
    const parsed = await readResourceJson<{ items?: Array<{ name: string }> }>(
        `mcpdb://connections/${connSelect.value}/schemas/${schemaSelect.value}/tables`,
        {},
    );
    for (const t of parsed.items ?? []) {
        const opt = document.createElement("option");
        opt.value = t.name;
        opt.textContent = t.name;
        tableSelect.appendChild(opt);
    }
    await loadColumns();
}

async function loadColumns() {
    columnsDiv.innerHTML = "";
    selectedColumns.clear();
    columns = [];
    if (!connSelect.value || !schemaSelect.value || !tableSelect.value) return;
    const parsed = await readResourceJson<{ columns?: Array<{ name: string; dataType?: string }> }>(
        `mcpdb://connections/${connSelect.value}/schemas/${schemaSelect.value}/tables/${tableSelect.value}`,
        {},
    );
    columns = parsed.columns ?? [];
    for (const c of columns) {
        const label = document.createElement("label");
        const cb = document.createElement("input");
        cb.type = "checkbox";
        cb.checked = true;
        selectedColumns.add(c.name);
        cb.addEventListener("change", () => {
            if (cb.checked) selectedColumns.add(c.name);
            else selectedColumns.delete(c.name);
            updateSql();
        });
        label.append(cb, ` ${c.name}`);
        columnsDiv.appendChild(label);
    }
    updateSql();
}

function updateSql() {
    const schema = schemaSelect.value;
    const table = tableSelect.value;
    if (!schema || !table) { sqlPre.textContent = ""; return; }
    const cols = selectedColumns.size === 0 ? "*" : Array.from(selectedColumns).join(", ");
    const where = filters.length === 0 ? "" : " WHERE " + filters
        .filter(f => f.column && f.op)
        .map((f, i) => `${f.column} ${f.op} @p${i}`)
        .join(" AND ");
    sqlPre.textContent = `SELECT ${cols}\nFROM ${schema}.${table}${where}\nLIMIT 100;`;
}

function addFilterRow() {
    const row = document.createElement("div");
    row.className = "row";
    const colSel = document.createElement("select");
    for (const c of columns) {
        const opt = document.createElement("option");
        opt.value = c.name;
        opt.textContent = c.name;
        colSel.appendChild(opt);
    }
    const opSel = document.createElement("select");
    for (const op of ["=", "<>", "<", ">", "LIKE"]) {
        const opt = document.createElement("option");
        opt.value = op;
        opt.textContent = op;
        opSel.appendChild(opt);
    }
    const valInput = document.createElement("input");
    const filter: Filter = { column: colSel.value, op: opSel.value, value: "" };
    filters.push(filter);
    colSel.addEventListener("change", () => { filter.column = colSel.value; updateSql(); });
    opSel.addEventListener("change", () => { filter.op = opSel.value; updateSql(); });
    valInput.addEventListener("input", () => { filter.value = valInput.value; updateSql(); });
    row.append(colSel, opSel, valInput);
    filtersDiv.appendChild(row);
    updateSql();
}

function setRunning(running: boolean) {
    runButton.disabled = running;
    runButton.textContent = running ? "Running…" : "Run";
}

function updateFullscreenButton(ctx: HostContext | undefined) {
    currentDisplayMode = ctx?.displayMode;
    if (!fullscreenSupported) {
        fullscreenButton.hidden = true;
        return;
    }
    const available = ctx?.availableDisplayModes;
    if (available && !available.includes("fullscreen")) {
        fullscreenButton.hidden = true;
        return;
    }
    fullscreenButton.hidden = false;
    fullscreenButton.textContent = currentDisplayMode === "fullscreen" ? "Minimize" : "Maximize";
}

connSelect.addEventListener("change", loadSchemas);
schemaSelect.addEventListener("change", loadTables);
tableSelect.addEventListener("change", loadColumns);
document.getElementById("reload")!.addEventListener("click", () => void loadConnections());
document.getElementById("add-filter")!.addEventListener("click", addFilterRow);

runButton.addEventListener("click", async () => {
    const parameters: Record<string, unknown> = {};
    filters.forEach((f, i) => { parameters[`p${i}`] = f.value; });
    setRunning(true);
    try {
        await callTool("db_query", {
            connectionId: connSelect.value,
            sql: sqlPre.textContent,
            parameters,
        });
    } catch (err) {
        console.warn("db_query failed", err);
    } finally {
        setRunning(false);
    }
});

document.getElementById("save")!.addEventListener("click", async () => {
    if (!sqlPre.textContent) return;
    const name = prompt("Script name?");
    if (!name) return;
    try {
        await callTool("scripts_create", {
            name,
            sqlText: sqlPre.textContent,
            destructive: false,
        });
    } catch (err) {
        console.warn("scripts_create failed", err);
    }
});

askModelButton.addEventListener("click", async () => {
    const sql = sqlPre.textContent;
    if (!sql) return;
    try {
        await sendChatMessage("Explain this query and its expected results: \n\n" + sql);
    } catch (err) {
        console.warn("sendChatMessage failed", err);
    }
});

fullscreenButton.addEventListener("click", async () => {
    const target = currentDisplayMode === "fullscreen" ? "inline" : "fullscreen";
    try {
        await requestDisplayMode(target);
    } catch (err) {
        fullscreenSupported = false;
        fullscreenButton.hidden = true;
        console.warn("requestDisplayMode failed", err);
    }
});

onToolResult((result: CallToolResult) => {
    let structured: unknown = result.structuredContent;
    if (structured === undefined) {
        const text = result.content?.[0]?.text;
        if (typeof text === "string" && text.length > 0) {
            try { structured = JSON.parse(text); } catch { /* ignore */ }
        }
    }
    const connId = pick<string>(structured as Record<string, unknown> | undefined, "connectionId", "ConnectionId");
    if (connId) {
        args.connectionId = connId;
        if (connSelect.value !== connId && Array.from(connSelect.options).some(o => o.value === connId)) {
            connSelect.value = connId;
            void loadSchemas();
        }
    }
});

onHostContextChanged((ctx) => {
    applyHostStyles(ctx);
    updateFullscreenButton(ctx);
});

onToolCancelled(() => {
    setRunning(false);
});

onResourceTeardown(() => {
    /* no-op: registered to let the bridge track the subscription */
});

// Apply any host context that was available before subscription.
applyHostStyles(getHostContext());
updateFullscreenButton(getHostContext());

(async () => {
    try {
        await connect();
        observeBodySize();
        await loadConnections();
    } catch (err) {
        console.warn("bridge connect failed", err);
    }
})();
