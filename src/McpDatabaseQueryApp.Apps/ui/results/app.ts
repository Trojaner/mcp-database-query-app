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
    updateModelContext,
    onToolInput,
    onToolInputPartial,
    onToolCancelled,
    onResourceTeardown,
} from "../shared/app-bridge";

interface Column {
    name: string;
    dataType?: string;
}

interface QueryOutput {
    columns: Column[];
    rows: unknown[][];
    rowCount: number;
    truncated: boolean;
    executionMs: number;
    resultSetId?: string | null;
    connectionId?: string | null;
}

type SortState = { index: number; descending: boolean } | null;

const empty = document.getElementById("empty") as HTMLDivElement;
const head = document.getElementById("head") as HTMLTableRowElement;
const body = document.getElementById("body") as HTMLTableSectionElement;
const count = document.getElementById("count") as HTMLSpanElement;
const search = document.getElementById("search") as HTMLInputElement;
const exportBtn = document.getElementById("export") as HTMLButtonElement;
const toggleDisplayBtn = document.getElementById("toggle-display") as HTMLButtonElement;
const banner = document.getElementById("banner") as HTMLDivElement;
const bannerLabel = document.getElementById("banner-label") as HTMLSpanElement;
const bannerSql = document.getElementById("banner-sql") as HTMLSpanElement;
const bannerSpinner = document.getElementById("banner-spinner") as HTMLSpanElement;

let output: QueryOutput | undefined;
let sort: SortState = null;
let currentDisplayMode: "inline" | "fullscreen" | "pip" = "inline";
let displayModeSupported = true;
let bannerHideTimer: ReturnType<typeof setTimeout> | null = null;

function pick<T>(obj: Record<string, unknown>, ...keys: string[]): T | undefined {
    for (const k of keys) {
        if (obj[k] !== undefined) return obj[k] as T;
    }
    return undefined;
}

function normalizeQueryOutput(raw: unknown): QueryOutput | undefined {
    if (!raw || typeof raw !== "object") return undefined;
    const o = raw as Record<string, unknown>;
    const columnsRaw = pick<unknown[]>(o, "columns", "Columns");
    const rowsRaw = pick<unknown[][]>(o, "rows", "Rows");
    if (!Array.isArray(columnsRaw) || !Array.isArray(rowsRaw)) return undefined;
    const columns: Column[] = columnsRaw.map((c) => {
        const cc = c as Record<string, unknown>;
        return {
            name: (pick<string>(cc, "name", "Name") ?? ""),
            dataType: pick<string>(cc, "dataType", "DataType"),
        };
    });
    return {
        columns,
        rows: rowsRaw as unknown[][],
        rowCount: (pick<number>(o, "rowCount", "RowCount") ?? rowsRaw.length),
        truncated: (pick<boolean>(o, "truncated", "Truncated") ?? false),
        executionMs: (pick<number>(o, "executionMs", "ExecutionMs") ?? 0),
        resultSetId: pick<string>(o, "resultSetId", "ResultSetId") ?? null,
        connectionId: pick<string>(o, "connectionId", "ConnectionId") ?? null,
    };
}

function extractStructured(result: CallToolResult): unknown {
    if (result.structuredContent !== undefined) return result.structuredContent;
    const text = result.content?.[0]?.text;
    if (typeof text === "string" && text.length > 0) {
        try { return JSON.parse(text); } catch { return undefined; }
    }
    return undefined;
}

function render(data: QueryOutput | undefined) {
    head.innerHTML = "";
    body.innerHTML = "";
    if (!data || data.rows.length === 0) {
        empty.hidden = false;
        count.textContent = data ? `${data.rowCount} rows · ${data.executionMs} ms` : "no data";
        return;
    }

    empty.hidden = true;

    for (let i = 0; i < data.columns.length; i++) {
        const th = document.createElement("th");
        th.textContent = data.columns[i]!.name;
        th.addEventListener("click", () => {
            if (sort?.index === i) {
                sort.descending = !sort.descending;
            } else {
                sort = { index: i, descending: false };
            }
            render(data);
        });
        if (sort?.index === i) {
            th.classList.add("sorted");
            if (sort.descending) th.classList.add("desc");
        }
        head.appendChild(th);
    }

    const sorted = sort
        ? [...data.rows].sort((a, b) => {
            const av = a[sort!.index];
            const bv = b[sort!.index];
            const cmp = av === null || av === undefined
                ? (bv === null || bv === undefined ? 0 : -1)
                : (bv === null || bv === undefined ? 1 : String(av).localeCompare(String(bv), undefined, { numeric: true }));
            return sort!.descending ? -cmp : cmp;
        })
        : data.rows;

    for (const row of sorted) {
        const tr = document.createElement("tr");
        for (const cell of row) {
            const td = document.createElement("td");
            td.textContent = cell === null || cell === undefined ? "NULL" : String(cell);
            tr.appendChild(td);
        }
        body.appendChild(tr);
    }

    count.textContent = `${data.rowCount} rows${data.truncated ? " · truncated" : ""} · ${data.executionMs} ms`;
    applyFilter();
}

function applyFilter() {
    const q = search.value.trim().toLowerCase();
    for (const tr of Array.from(body.children) as HTMLTableRowElement[]) {
        const text = tr.textContent?.toLowerCase() ?? "";
        tr.classList.toggle("hidden", q.length > 0 && !text.includes(q));
    }
}

function showBanner(label: string, sql: string | undefined, spinning: boolean) {
    if (bannerHideTimer) {
        clearTimeout(bannerHideTimer);
        bannerHideTimer = null;
    }
    bannerLabel.textContent = label;
    bannerSql.textContent = sql ?? "";
    bannerSpinner.hidden = !spinning;
    banner.hidden = false;
}

function hideBanner(delayMs = 0) {
    if (bannerHideTimer) {
        clearTimeout(bannerHideTimer);
        bannerHideTimer = null;
    }
    if (delayMs > 0) {
        bannerHideTimer = setTimeout(() => {
            banner.hidden = true;
            bannerHideTimer = null;
        }, delayMs);
    } else {
        banner.hidden = true;
    }
}

function extractSql(args: unknown): string | undefined {
    if (!args || typeof args !== "object") return undefined;
    const a = args as Record<string, unknown>;
    const sql = a.sql ?? a.Sql ?? a.query ?? a.Query;
    return typeof sql === "string" ? sql : undefined;
}

function updateToggleButton() {
    if (!displayModeSupported) {
        toggleDisplayBtn.hidden = true;
        return;
    }
    toggleDisplayBtn.hidden = false;
    toggleDisplayBtn.textContent = currentDisplayMode === "fullscreen" ? "Minimize" : "Maximize";
}

toggleDisplayBtn.addEventListener("click", async () => {
    const target: "inline" | "fullscreen" =
        currentDisplayMode === "fullscreen" ? "inline" : "fullscreen";
    try {
        await requestDisplayMode(target);
        currentDisplayMode = target;
        updateToggleButton();
    } catch {
        displayModeSupported = false;
        updateToggleButton();
    }
});

search.addEventListener("input", applyFilter);

exportBtn.addEventListener("click", async () => {
    if (!output) return;
    try {
        const res = await callTool("ui_results_export_csv", {
            columns: output.columns.map(c => c.name),
            rows: output.rows,
        });
        let csv: string | undefined;
        const sc = res.structuredContent as Record<string, unknown> | undefined;
        if (sc) csv = (sc.csv as string | undefined) ?? (sc.Csv as string | undefined);
        if (!csv) csv = res.content?.[0]?.text;
        if (typeof csv === "string" && csv.length > 0) {
            const blob = new Blob([csv], { type: "text/csv" });
            const url = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = url;
            a.download = "mcp-database-query-app-results.csv";
            a.click();
            URL.revokeObjectURL(url);
        }
    } catch (err) {
        console.warn("CSV export failed", err);
    }
});

onToolResult((result) => {
    const structured = extractStructured(result);
    const normalized = normalizeQueryOutput(structured);
    if (normalized) {
        output = normalized;
        render(output);
        hideBanner();
        if (normalized.resultSetId) {
            updateModelContext({
                lastQuery: {
                    connectionId: normalized.connectionId ?? null,
                    resultSetId: normalized.resultSetId,
                    columns: normalized.columns.map(c => c.name),
                    rowCount: normalized.rowCount,
                    truncated: normalized.truncated,
                },
            }).catch(() => { /* fire-and-forget */ });
        }
    }
});

onToolInputPartial((partial) => {
    const sql = extractSql(partial);
    showBanner("running", sql, true);
});

onToolInput((args) => {
    const sql = extractSql(args);
    showBanner("running", sql, true);
});

onToolCancelled(() => {
    showBanner("cancelled", bannerSql.textContent || undefined, false);
    hideBanner(1500);
});

onResourceTeardown(() => {
    // no-op; acknowledge subscription for host bookkeeping
});

function applyContext(ctx: HostContext | undefined) {
    applyHostStyles(ctx);
    if (ctx?.displayMode === "inline" || ctx?.displayMode === "fullscreen" || ctx?.displayMode === "pip") {
        currentDisplayMode = ctx.displayMode;
    }
    const available = ctx?.availableDisplayModes;
    if (Array.isArray(available) && available.length > 0) {
        const hasFs = available.indexOf("fullscreen") !== -1;
        const hasInline = available.indexOf("inline") !== -1;
        displayModeSupported = hasFs && hasInline;
    }
    updateToggleButton();
    if (output) render(output);
}

onHostContextChanged((ctx) => {
    applyContext(ctx);
});

connect()
    .then(() => {
        applyContext(getHostContext());
        observeBodySize();
    })
    .catch(() => {
        if (!output) render(undefined);
    });
