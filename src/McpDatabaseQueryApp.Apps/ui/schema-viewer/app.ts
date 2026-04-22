import dagre from "dagre";
import {
    connect,
    onToolResult,
    callTool,
    CallToolResult,
    HostContext,
    getHostContext,
    onHostContextChanged,
    applyHostStyles,
    observeBodySize,
    requestDisplayMode,
    onToolInput,
    onResourceTeardown,
} from "../shared/app-bridge";

interface ColumnInfo {
    name: string;
    dataType?: string;
    isPrimaryKey?: boolean;
    isForeignKey?: boolean;
}

interface TableInfo {
    schema?: string;
    name: string;
    columns: ColumnInfo[];
}

interface ForeignKey {
    fromTable: string;
    fromColumn: string;
    toTable: string;
    toColumn: string;
}

interface SchemaOutput {
    tables: TableInfo[];
    foreignKeys: ForeignKey[];
}

const container = document.getElementById("container") as HTMLDivElement;
const svgOverlay = document.getElementById("svg-overlay") as unknown as SVGSVGElement;
const emptyDiv = document.getElementById("empty") as HTMLDivElement;
const detailsPanel = document.getElementById("details-panel") as HTMLElement;
const toggleFullscreenBtn = document.getElementById("toggle-fullscreen") as HTMLButtonElement;

const TABLE_WIDTH = 200;
const ROW_HEIGHT = 22;
const HEADER_HEIGHT = 30;
const PADDING = 40;

let currentData: SchemaOutput | undefined;
let currentToolInput: Record<string, unknown> | undefined;
let currentDisplayMode: "inline" | "fullscreen" | "pip" = "inline";
let selectedTableKey: string | null = null;

function tableKey(t: TableInfo): string {
    return t.schema ? `${t.schema}.${t.name}` : t.name;
}

function getStrokeColor(): string {
    const v = getComputedStyle(document.documentElement)
        .getPropertyValue("--color-border-primary")
        .trim();
    return v || "#8886";
}

function renderTable(table: TableInfo, x: number, y: number): HTMLDivElement {
    const box = document.createElement("div");
    box.className = "table-box";
    box.style.left = `${x}px`;
    box.style.top = `${y}px`;
    box.style.width = `${TABLE_WIDTH}px`;
    const key = tableKey(table);
    box.dataset.table = key;

    if (key === selectedTableKey) box.classList.add("selected");

    box.addEventListener("click", () => {
        onTableClick(table);
    });

    const header = document.createElement("div");
    header.className = "table-header";
    header.textContent = key;
    box.appendChild(header);

    for (const col of table.columns) {
        const row = document.createElement("div");
        row.className = "table-col";
        row.dataset.column = col.name;

        const pk = document.createElement("span");
        pk.className = "col-pk";
        pk.textContent = col.isPrimaryKey ? "PK" : col.isForeignKey ? "FK" : "";

        const name = document.createElement("span");
        name.className = "col-name";
        name.textContent = col.name;

        const type = document.createElement("span");
        type.className = "col-type";
        type.textContent = col.dataType ?? "";

        row.append(pk, name, type);
        box.appendChild(row);
    }

    container.appendChild(box);
    return box;
}

function computeLayout(tables: TableInfo[], foreignKeys: ForeignKey[]): Map<string, { x: number; y: number }> {
    const g = new dagre.graphlib.Graph();
    g.setGraph({ rankdir: "LR", nodesep: PADDING, ranksep: PADDING * 2 });
    g.setDefaultEdgeLabel(() => ({}));

    for (const t of tables) {
        const h = HEADER_HEIGHT + t.columns.length * ROW_HEIGHT;
        g.setNode(tableKey(t), { width: TABLE_WIDTH, height: h });
    }

    for (const fk of foreignKeys) {
        if (g.hasNode(fk.fromTable) && g.hasNode(fk.toTable)) {
            g.setEdge(fk.fromTable, fk.toTable);
        }
    }

    dagre.layout(g);

    const positions = new Map<string, { x: number; y: number }>();
    for (const nodeId of g.nodes()) {
        const node = g.node(nodeId);
        if (node) {
            positions.set(nodeId, {
                x: node.x - node.width / 2 + PADDING,
                y: node.y - node.height / 2 + PADDING,
            });
        }
    }
    return positions;
}

function getColumnAnchor(
    tableEl: HTMLDivElement,
    columnName: string,
    side: "left" | "right",
): { x: number; y: number } {
    const colEl = tableEl.querySelector(`[data-column="${columnName}"]`) as HTMLElement | null;
    const boxRect = tableEl.getBoundingClientRect();
    const colRect = colEl?.getBoundingClientRect();
    const left = parseFloat(tableEl.style.left);
    const top = parseFloat(tableEl.style.top);
    const midY = colRect
        ? top + (colRect.top - boxRect.top) + colRect.height / 2
        : top + HEADER_HEIGHT / 2;
    const x = side === "right" ? left + TABLE_WIDTH : left;
    return { x, y: midY };
}

function drawRelationships(
    foreignKeys: ForeignKey[],
    tableEls: Map<string, HTMLDivElement>,
) {
    let maxX = 0;
    let maxY = 0;
    for (const el of tableEls.values()) {
        const r = parseFloat(el.style.left) + el.offsetWidth;
        const b = parseFloat(el.style.top) + el.offsetHeight;
        if (r > maxX) maxX = r;
        if (b > maxY) maxY = b;
    }
    svgOverlay.setAttribute("width", String(maxX + PADDING * 2));
    svgOverlay.setAttribute("height", String(maxY + PADDING * 2));
    svgOverlay.innerHTML = "";

    const stroke = getStrokeColor();
    const ns = "http://www.w3.org/2000/svg";
    for (const fk of foreignKeys) {
        const fromEl = tableEls.get(fk.fromTable);
        const toEl = tableEls.get(fk.toTable);
        if (!fromEl || !toEl) continue;

        const fromX = parseFloat(fromEl.style.left);
        const toX = parseFloat(toEl.style.left);
        const fromRight = fromX > toX ? "left" : "right";
        const toSide = fromX > toX ? "right" : "left";

        const start = getColumnAnchor(fromEl, fk.fromColumn, fromRight);
        const end = getColumnAnchor(toEl, fk.toColumn, toSide);

        const dx = Math.abs(end.x - start.x) * 0.5;
        const d = `M ${start.x} ${start.y} C ${start.x + (fromRight === "right" ? dx : -dx)} ${start.y}, ${end.x + (toSide === "right" ? dx : -dx)} ${end.y}, ${end.x} ${end.y}`;

        const path = document.createElementNS(ns, "path");
        path.setAttribute("d", d);
        path.setAttribute("fill", "none");
        path.setAttribute("stroke", stroke);
        path.setAttribute("stroke-width", "1.5");
        svgOverlay.appendChild(path);

        const circle = document.createElementNS(ns, "circle");
        circle.setAttribute("cx", String(end.x));
        circle.setAttribute("cy", String(end.y));
        circle.setAttribute("r", "3");
        circle.setAttribute("fill", stroke);
        svgOverlay.appendChild(circle);
    }
}

function render(data: SchemaOutput | undefined) {
    // Clear previously rendered table nodes (keep svg overlay element).
    const boxes = container.querySelectorAll(".table-box");
    for (const b of Array.from(boxes)) b.remove();

    if (!data || data.tables.length === 0) {
        emptyDiv.hidden = false;
        container.style.display = "none";
        svgOverlay.innerHTML = "";
        return;
    }

    emptyDiv.hidden = true;
    container.style.display = "";

    const positions = computeLayout(data.tables, data.foreignKeys);
    const tableEls = new Map<string, HTMLDivElement>();

    for (const table of data.tables) {
        const key = tableKey(table);
        const pos = positions.get(key) ?? { x: 0, y: 0 };
        const el = renderTable(table, pos.x, pos.y);
        tableEls.set(key, el);
    }

    requestAnimationFrame(() => {
        drawRelationships(data.foreignKeys, tableEls);
    });
}

function pick<T>(obj: Record<string, unknown> | undefined, ...keys: string[]): T | undefined {
    if (!obj) return undefined;
    for (const k of keys) if (obj[k] !== undefined) return obj[k] as T;
    return undefined;
}

function normalizeSchemaOutput(raw: unknown): SchemaOutput | undefined {
    if (!raw || typeof raw !== "object") return undefined;
    const o = raw as Record<string, unknown>;
    const tablesRaw = pick<unknown[]>(o, "tables", "Tables");
    const fksRaw = pick<unknown[]>(o, "foreignKeys", "ForeignKeys") ?? [];
    if (!Array.isArray(tablesRaw)) return undefined;
    const tables: TableInfo[] = tablesRaw.map((t) => {
        const tt = t as Record<string, unknown>;
        const colsRaw = (pick<unknown[]>(tt, "columns", "Columns") ?? []) as unknown[];
        const columns: ColumnInfo[] = colsRaw.map((c) => {
            const cc = c as Record<string, unknown>;
            return {
                name: pick<string>(cc, "name", "Name") ?? "",
                dataType: pick<string>(cc, "dataType", "DataType"),
                isPrimaryKey: pick<boolean>(cc, "isPrimaryKey", "IsPrimaryKey"),
                isForeignKey: pick<boolean>(cc, "isForeignKey", "IsForeignKey"),
            };
        });
        return {
            schema: pick<string>(tt, "schema", "Schema"),
            name: pick<string>(tt, "name", "Name") ?? "",
            columns,
        };
    });
    const foreignKeys: ForeignKey[] = (fksRaw as unknown[]).map((f) => {
        const ff = f as Record<string, unknown>;
        return {
            fromTable: pick<string>(ff, "fromTable", "FromTable") ?? "",
            fromColumn: pick<string>(ff, "fromColumn", "FromColumn") ?? "",
            toTable: pick<string>(ff, "toTable", "ToTable") ?? "",
            toColumn: pick<string>(ff, "toColumn", "ToColumn") ?? "",
        };
    });
    return { tables, foreignKeys };
}

function extractStructured(result: CallToolResult): unknown {
    if (result.structuredContent !== undefined) return result.structuredContent;
    const text = result.content?.[0]?.text;
    if (typeof text === "string" && text.length > 0) {
        try { return JSON.parse(text); } catch { return undefined; }
    }
    return undefined;
}

// ---- Details panel (lazy db_table_describe) ----

function closeDetailsPanel(): void {
    detailsPanel.classList.remove("open");
    detailsPanel.innerHTML = "";
    if (selectedTableKey) {
        const prev = container.querySelector(`.table-box[data-table="${CSS.escape(selectedTableKey)}"]`);
        prev?.classList.remove("selected");
        selectedTableKey = null;
    }
}

function openDetailsPanel(title: string): void {
    detailsPanel.innerHTML = "";
    const close = document.createElement("button");
    close.id = "details-close";
    close.textContent = "×";
    close.title = "Close";
    close.type = "button";
    close.addEventListener("click", closeDetailsPanel);
    detailsPanel.appendChild(close);

    const h = document.createElement("h2");
    h.textContent = title;
    detailsPanel.appendChild(h);

    const loading = document.createElement("div");
    loading.id = "details-loading";
    loading.textContent = "Loading details...";
    detailsPanel.appendChild(loading);

    detailsPanel.classList.add("open");
}

function renderDetails(result: CallToolResult): void {
    const loading = detailsPanel.querySelector("#details-loading");
    loading?.remove();

    if (result.isError) {
        const err = document.createElement("div");
        err.id = "details-error";
        err.textContent = "Failed to load table details.";
        detailsPanel.appendChild(err);
        return;
    }

    const data = extractStructured(result) as Record<string, unknown> | undefined;
    if (!data) {
        const err = document.createElement("div");
        err.id = "details-error";
        err.textContent = "No details returned.";
        detailsPanel.appendChild(err);
        return;
    }

    const columns = (pick<unknown[]>(data, "columns", "Columns") ?? []) as Record<string, unknown>[];
    const keys = (pick<unknown[]>(data, "keys", "Keys", "primaryKeys", "PrimaryKeys") ?? []) as Record<string, unknown>[];
    const indexes = (pick<unknown[]>(data, "indexes", "Indexes") ?? []) as Record<string, unknown>[];

    if (columns.length > 0) {
        const h = document.createElement("h3");
        h.textContent = "Columns";
        detailsPanel.appendChild(h);
        const table = document.createElement("table");
        table.innerHTML = "<thead><tr><th>Name</th><th>Type</th><th>Null</th></tr></thead>";
        const tbody = document.createElement("tbody");
        for (const c of columns) {
            const tr = document.createElement("tr");
            const name = pick<string>(c, "name", "Name") ?? "";
            const type = pick<string>(c, "dataType", "DataType", "type", "Type") ?? "";
            const nullable = pick<boolean>(c, "isNullable", "IsNullable", "nullable", "Nullable");
            tr.innerHTML = `<td></td><td></td><td></td>`;
            (tr.children[0] as HTMLElement).textContent = name;
            (tr.children[1] as HTMLElement).textContent = type;
            (tr.children[2] as HTMLElement).textContent = nullable === undefined ? "" : nullable ? "YES" : "NO";
            tbody.appendChild(tr);
        }
        table.appendChild(tbody);
        detailsPanel.appendChild(table);
    }

    if (keys.length > 0) {
        const h = document.createElement("h3");
        h.textContent = "Keys";
        detailsPanel.appendChild(h);
        const ul = document.createElement("ul");
        for (const k of keys) {
            const li = document.createElement("li");
            const name = pick<string>(k, "name", "Name") ?? "";
            const type = pick<string>(k, "type", "Type", "keyType", "KeyType") ?? "";
            const cols = (pick<unknown[]>(k, "columns", "Columns") ?? []).join(", ");
            li.textContent = `${type ? type + ": " : ""}${name}${cols ? " (" + cols + ")" : ""}`;
            ul.appendChild(li);
        }
        detailsPanel.appendChild(ul);
    }

    if (indexes.length > 0) {
        const h = document.createElement("h3");
        h.textContent = "Indexes";
        detailsPanel.appendChild(h);
        const ul = document.createElement("ul");
        for (const i of indexes) {
            const li = document.createElement("li");
            const name = pick<string>(i, "name", "Name") ?? "";
            const unique = pick<boolean>(i, "isUnique", "IsUnique", "unique", "Unique");
            const cols = (pick<unknown[]>(i, "columns", "Columns") ?? []).join(", ");
            li.textContent = `${unique ? "UNIQUE " : ""}${name}${cols ? " (" + cols + ")" : ""}`;
            ul.appendChild(li);
        }
        detailsPanel.appendChild(ul);
    }

    if (columns.length === 0 && keys.length === 0 && indexes.length === 0) {
        const note = document.createElement("div");
        note.id = "details-error";
        note.textContent = "No additional details available.";
        detailsPanel.appendChild(note);
    }
}

function getConnectionId(): string | undefined {
    const fromInput = currentToolInput
        ? (pick<string>(currentToolInput, "connectionId", "ConnectionId"))
        : undefined;
    if (fromInput) return fromInput;
    const ctx = getHostContext();
    const toolArgs = ctx?.toolInfo?.tool as Record<string, unknown> | undefined;
    if (toolArgs) {
        const v = pick<string>(toolArgs, "connectionId", "ConnectionId");
        if (v) return v;
    }
    return undefined;
}

function onTableClick(table: TableInfo): void {
    const connectionId = getConnectionId();
    if (!connectionId) return; // no-op when connectionId is not available

    const key = tableKey(table);

    if (selectedTableKey) {
        const prev = container.querySelector(`.table-box[data-table="${CSS.escape(selectedTableKey)}"]`);
        prev?.classList.remove("selected");
    }
    selectedTableKey = key;
    const cur = container.querySelector(`.table-box[data-table="${CSS.escape(key)}"]`);
    cur?.classList.add("selected");

    openDetailsPanel(key);

    const args: Record<string, unknown> = {
        connectionId,
        table: table.name,
    };
    if (table.schema) args.schema = table.schema;

    callTool("db_table_describe", args)
        .then((r) => {
            if (selectedTableKey !== key) return; // user moved on
            renderDetails(r);
        })
        .catch(() => {
            if (selectedTableKey !== key) return;
            const loading = detailsPanel.querySelector("#details-loading");
            loading?.remove();
            const err = document.createElement("div");
            err.id = "details-error";
            err.textContent = "Failed to load table details.";
            detailsPanel.appendChild(err);
        });
}

// ---- Fullscreen toggle ----

function updateFullscreenButton(): void {
    if (currentDisplayMode === "fullscreen") {
        toggleFullscreenBtn.textContent = "Minimize";
        toggleFullscreenBtn.title = "Return to inline";
    } else {
        toggleFullscreenBtn.textContent = "Maximize";
        toggleFullscreenBtn.title = "Expand to fullscreen";
    }
}

toggleFullscreenBtn.addEventListener("click", () => {
    const next = currentDisplayMode === "fullscreen" ? "inline" : "fullscreen";
    requestDisplayMode(next).catch(() => { /* host may reject; ignore */ });
});

// ---- Host context wiring ----

onHostContextChanged((ctx: HostContext) => {
    applyHostStyles(ctx);
    if (ctx.displayMode) {
        currentDisplayMode = ctx.displayMode;
        updateFullscreenButton();
    }
    const toolArgs = ctx.toolInfo?.tool as Record<string, unknown> | undefined;
    if (toolArgs) currentToolInput = toolArgs;
    render(currentData);
});

onToolInput((args: unknown) => {
    if (args && typeof args === "object") {
        currentToolInput = args as Record<string, unknown>;
    }
});

onResourceTeardown(() => {
    /* host is tearing down this resource view; nothing to clean up */
});

onToolResult((result) => {
    const normalized = normalizeSchemaOutput(extractStructured(result));
    currentData = normalized;
    render(currentData);
});

// Apply any host context already present before connect resolves.
const initialCtx = getHostContext();
if (initialCtx) {
    applyHostStyles(initialCtx);
    if (initialCtx.displayMode) currentDisplayMode = initialCtx.displayMode;
    const toolArgs = initialCtx.toolInfo?.tool as Record<string, unknown> | undefined;
    if (toolArgs) currentToolInput = toolArgs;
    updateFullscreenButton();
} else {
    updateFullscreenButton();
}

connect()
    .then(() => {
        observeBodySize();
    })
    .catch(() => {
        render(undefined);
    });
