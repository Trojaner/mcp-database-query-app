import { Chart, registerables } from "chart.js";
import "chartjs-adapter-date-fns";
import {
    connect,
    onToolResult,
    CallToolResult,
    HostContext,
    getHostContext,
    onHostContextChanged,
    applyHostStyles,
    observeBodySize,
    requestDisplayMode,
    onResourceTeardown,
} from "../shared/app-bridge";

Chart.register(...registerables);

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface Column {
    name: string;
    dataType?: string;
}

type ChartKind = "bar" | "line" | "area" | "scatter" | "timeseries" | "combo" | "pie" | "doughnut";
const CHART_KINDS: ChartKind[] = ["bar", "line", "area", "scatter", "timeseries", "combo", "pie", "doughnut"];

interface SeriesSpecHint {
    column: string;
    label?: string;
    type?: "line" | "bar";
    axis?: string;
    dashed?: boolean;
    fill?: boolean;
    color?: string;
}

interface ChartHints {
    chartType?: string;
    xAxis?: string;
    yAxis?: string;
    yAxes?: string[];
    series?: SeriesSpecHint[];
    stacked?: boolean;
    forecastColumn?: string;
    lowerBoundColumn?: string;
    upperBoundColumn?: string;
}

interface QueryOutput {
    columns: Column[];
    rows: unknown[][];
}

// Per-series configuration, keyed by column name. Persisted across re-renders
// so toggling a control (or the theme) doesn't reset the user's choices.
interface SeriesState {
    include: boolean;
    type: "line" | "bar";
    axis: "y" | "y1";
    dashed: boolean;
    fill: boolean;
    color?: string;
}

let output: QueryOutput | undefined;
let pendingHints: ChartHints = {};
const seriesState = new Map<string, SeriesState>();

// ---------------------------------------------------------------------------
// Normalization (accepts both camelCase and PascalCase payloads)
// ---------------------------------------------------------------------------

function pick<T>(obj: Record<string, unknown> | undefined, ...keys: string[]): T | undefined {
    if (!obj) return undefined;
    for (const k of keys) if (obj[k] !== undefined) return obj[k] as T;
    return undefined;
}

function asStringArray(value: unknown): string[] | undefined {
    if (!Array.isArray(value)) return undefined;
    return value.filter((v) => typeof v === "string") as string[];
}

function normalizeSeriesHints(value: unknown): SeriesSpecHint[] | undefined {
    if (!Array.isArray(value)) return undefined;
    const out: SeriesSpecHint[] = [];
    for (const item of value) {
        if (!item || typeof item !== "object") continue;
        const o = item as Record<string, unknown>;
        const column = pick<string>(o, "column", "Column");
        if (!column) continue;
        out.push({
            column,
            label: pick<string>(o, "label", "Label"),
            type: normalizeSeriesType(pick<string>(o, "type", "Type")),
            axis: pick<string>(o, "axis", "Axis"),
            dashed: pick<boolean>(o, "dashed", "Dashed") ?? false,
            fill: pick<boolean>(o, "fill", "Fill") ?? false,
            color: pick<string>(o, "color", "Color"),
        });
    }
    return out.length > 0 ? out : undefined;
}

function normalizeSeriesType(value: string | undefined): "line" | "bar" | undefined {
    if (!value) return undefined;
    const t = value.toLowerCase();
    return t === "bar" ? "bar" : t === "line" ? "line" : undefined;
}

function normalizeAxis(value: string | undefined): "y" | "y1" {
    if (!value) return "y";
    const a = value.toLowerCase();
    return a === "right" || a === "y1" || a === "secondary" ? "y1" : "y";
}

function normalizeQueryOutput(raw: unknown): QueryOutput | undefined {
    if (!raw || typeof raw !== "object") return undefined;
    const o = raw as Record<string, unknown>;
    const columnsRaw = pick<unknown[]>(o, "columns", "Columns");
    const rowsRaw = pick<unknown[][]>(o, "rows", "Rows");
    if (!Array.isArray(columnsRaw) || !Array.isArray(rowsRaw)) return undefined;
    const columns: Column[] = columnsRaw.map((c) => {
        if (typeof c === "string") return { name: c };
        const cc = c as Record<string, unknown>;
        return {
            name: pick<string>(cc, "name", "Name") ?? "",
            dataType: pick<string>(cc, "dataType", "DataType"),
        };
    });
    pendingHints = {
        chartType: pick<string>(o, "chartType", "ChartType"),
        xAxis: pick<string>(o, "xAxis", "XAxis"),
        yAxis: pick<string>(o, "yAxis", "YAxis"),
        yAxes: asStringArray(pick(o, "yAxes", "YAxes")),
        series: normalizeSeriesHints(pick(o, "series", "Series")),
        stacked: pick<boolean>(o, "stacked", "Stacked") ?? false,
        forecastColumn: pick<string>(o, "forecastColumn", "ForecastColumn"),
        lowerBoundColumn: pick<string>(o, "lowerBoundColumn", "LowerBoundColumn"),
        upperBoundColumn: pick<string>(o, "upperBoundColumn", "UpperBoundColumn"),
    };
    return { columns, rows: rowsRaw as unknown[][] };
}

function extractStructured(result: CallToolResult): unknown {
    if (result.structuredContent !== undefined) return result.structuredContent;
    const text = result.content?.[0]?.text;
    if (typeof text === "string" && text.length > 0) {
        try { return JSON.parse(text); } catch { return undefined; }
    }
    return undefined;
}

// ---------------------------------------------------------------------------
// Element references
// ---------------------------------------------------------------------------

const chartTypeSelect = document.getElementById("chartType") as HTMLSelectElement;
const xAxisSelect = document.getElementById("xAxis") as HTMLSelectElement;
const stackedCheck = document.getElementById("stacked") as HTMLInputElement;
const stackedWrap = document.getElementById("stackedWrap") as HTMLLabelElement;
const seriesPanel = document.getElementById("seriesPanel") as HTMLDivElement;
const forecastColSelect = document.getElementById("forecastCol") as HTMLSelectElement;
const lowerColSelect = document.getElementById("lowerCol") as HTMLSelectElement;
const upperColSelect = document.getElementById("upperCol") as HTMLSelectElement;
const advancedDiv = document.getElementById("advanced") as HTMLDivElement;
const toggleAdvancedBtn = document.getElementById("toggle-advanced") as HTMLButtonElement;
const canvas = document.getElementById("chart") as HTMLCanvasElement;
const emptyDiv = document.getElementById("empty") as HTMLDivElement;
const toggleDisplayBtn = document.getElementById("toggle-display") as HTMLButtonElement;

let currentChart: Chart | null = null;
let currentDisplayMode: "inline" | "fullscreen" | "pip" = "inline";

// ---------------------------------------------------------------------------
// Value inspection helpers
// ---------------------------------------------------------------------------

function isNumeric(value: unknown): boolean {
    if (typeof value === "number") return Number.isFinite(value);
    if (typeof value === "string") return value.trim() !== "" && !isNaN(Number(value));
    return false;
}

function toNumber(value: unknown): number | null {
    if (typeof value === "number") return Number.isFinite(value) ? value : null;
    if (typeof value === "string") {
        const t = value.trim();
        if (t === "" || isNaN(Number(t))) return null;
        return Number(t);
    }
    return null;
}

function parseDate(value: unknown): number | null {
    if (value === null || value === undefined) return null;
    if (value instanceof Date) return value.getTime();
    if (typeof value === "number") return value;
    if (typeof value === "string") {
        const trimmed = value.trim();
        if (trimmed === "") return null;
        const n = Date.parse(trimmed);
        return isNaN(n) ? null : n;
    }
    return null;
}

function isTruthyFlag(value: unknown): boolean {
    if (value === true) return true;
    if (typeof value === "number") return value > 0;
    if (typeof value === "string") {
        const t = value.trim().toLowerCase();
        return t === "true" || t === "1" || t === "yes" || t === "y" || t === "t";
    }
    return false;
}

function columnIndex(name: string): number {
    if (!output) return -1;
    return output.columns.findIndex((c) => c.name === name);
}

function isNumericColumn(index: number): boolean {
    if (!output || index < 0) return false;
    const sample = output.rows.find((r) => r[index] !== null && r[index] !== undefined);
    return sample ? isNumeric(sample[index]) : false;
}

function isDateColumn(index: number): boolean {
    if (!output || index < 0) return false;
    const sample = output.rows.find((r) => r[index] !== null && r[index] !== undefined);
    if (!sample) return false;
    const v = sample[index];
    if (isNumeric(v)) return false; // prefer treating plain numbers as numeric
    return parseDate(v) !== null;
}

// ---------------------------------------------------------------------------
// Theme / palette
// ---------------------------------------------------------------------------

const lightPalette = [
    "#4e79a7", "#f28e2b", "#e15759", "#76b7b2", "#59a14f",
    "#edc948", "#b07aa1", "#ff9da7", "#9c755f", "#bab0ac",
];
const darkPalette = [
    "#7ab0df", "#ffb062", "#ff8a8c", "#a8e0dc", "#8fd483",
    "#ffe580", "#dcacd1", "#ffc7d0", "#c9a68b", "#dcd3cf",
];

function readCssVar(name: string, fallback: string): string {
    const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return v.length > 0 ? v : fallback;
}

function activePalette(): string[] {
    return getHostContext()?.theme === "dark" ? darkPalette : lightPalette;
}

// Convert #rgb / #rrggbb (or pass through rgba/named) to an rgba() with alpha.
function withAlpha(color: string, alpha: number): string {
    let hex = color.trim();
    if (hex[0] !== "#") return color;
    hex = hex.slice(1);
    if (hex.length === 3) hex = hex.split("").map((c) => c + c).join("");
    if (hex.length !== 6) return color;
    const r = parseInt(hex.slice(0, 2), 16);
    const g = parseInt(hex.slice(2, 4), 16);
    const b = parseInt(hex.slice(4, 6), 16);
    return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

function applyChartDefaults(ctx: HostContext | undefined): void {
    const isDark = ctx?.theme === "dark";
    Chart.defaults.color = readCssVar("--color-text-primary", isDark ? "#eaeaea" : "#333");
    Chart.defaults.borderColor = readCssVar("--color-border-secondary", isDark ? "#ffffff22" : "#0000001a");
    const fontFamily = readCssVar("--font-sans", "system-ui, sans-serif");
    if (fontFamily) {
        Chart.defaults.font = { ...(Chart.defaults.font ?? {}), family: fontFamily };
    }
}

// ---------------------------------------------------------------------------
// Control population + series state
// ---------------------------------------------------------------------------

function fillColumnSelect(select: HTMLSelectElement, columns: Column[], includeNone: boolean, selected?: string): void {
    select.innerHTML = "";
    if (includeNone) {
        const none = document.createElement("option");
        none.value = "";
        none.textContent = "(none)";
        select.appendChild(none);
    }
    for (const col of columns) {
        const opt = document.createElement("option");
        opt.value = col.name;
        opt.textContent = col.name;
        select.appendChild(opt);
    }
    if (selected !== undefined) select.value = selected;
}

function currentKind(): ChartKind {
    const v = chartTypeSelect.value as ChartKind;
    return CHART_KINDS.indexOf(v) >= 0 ? v : "bar";
}

function isPolarKind(kind: ChartKind): boolean {
    return kind === "pie" || kind === "doughnut";
}

// Initialize selects + per-series state from a fresh tool result.
function initFromHints(columns: Column[], rows: unknown[][], hints: ChartHints): void {
    // Chart type
    if (hints.chartType) {
        const t = hints.chartType.toLowerCase();
        if (CHART_KINDS.indexOf(t as ChartKind) >= 0) chartTypeSelect.value = t;
    }
    const kind = currentKind();

    // X axis: hint, else first date column for timeseries, else first column.
    let xName = hints.xAxis && columnIndex(hints.xAxis) >= 0 ? hints.xAxis : undefined;
    if (!xName) {
        if (kind === "timeseries") {
            const dateIdx = columns.findIndex((_, i) => isDateColumn(i));
            xName = columns[dateIdx >= 0 ? dateIdx : 0]?.name;
        } else {
            xName = columns[0]?.name;
        }
    }
    fillColumnSelect(xAxisSelect, columns, false, xName);

    // Determine the default included Y columns and their per-series config.
    seriesState.clear();
    const palette = activePalette();
    let colorCursor = 0;
    const ensure = (name: string): SeriesState => {
        let s = seriesState.get(name);
        if (!s) {
            s = {
                include: false,
                type: kind === "bar" ? "bar" : "line",
                axis: "y",
                dashed: false,
                fill: kind === "area",
                color: palette[colorCursor++ % palette.length],
            };
            seriesState.set(name, s);
        }
        return s;
    };
    for (const c of columns) ensure(c.name);

    const xIdx = columnIndex(xAxisSelect.value);
    if (hints.series && hints.series.length > 0) {
        for (const sp of hints.series) {
            if (columnIndex(sp.column) < 0) continue;
            const s = ensure(sp.column);
            s.include = true;
            if (sp.type) s.type = sp.type;
            s.axis = normalizeAxis(sp.axis);
            s.dashed = !!sp.dashed;
            s.fill = sp.fill || kind === "area";
            if (sp.color) s.color = sp.color;
        }
    } else {
        let picks = hints.yAxes && hints.yAxes.length > 0
            ? hints.yAxes
            : hints.yAxis
                ? [hints.yAxis]
                : [];
        picks = picks.filter((n) => columnIndex(n) >= 0);
        if (picks.length === 0) {
            const firstNumeric = columns.findIndex((_, i) => i !== xIdx && isNumericColumn(i));
            if (firstNumeric >= 0) picks = [columns[firstNumeric]!.name];
        }
        for (const n of picks) ensure(n).include = true;
    }

    // Stacked + forecast/band selects
    stackedCheck.checked = !!hints.stacked;
    fillColumnSelect(forecastColSelect, columns, true, hints.forecastColumn && columnIndex(hints.forecastColumn) >= 0 ? hints.forecastColumn : "");
    fillColumnSelect(lowerColSelect, columns, true, hints.lowerBoundColumn && columnIndex(hints.lowerBoundColumn) >= 0 ? hints.lowerBoundColumn : "");
    fillColumnSelect(upperColSelect, columns, true, hints.upperBoundColumn && columnIndex(hints.upperBoundColumn) >= 0 ? hints.upperBoundColumn : "");

    buildSeriesPanel();
    updateControlVisibility();
}

// Rebuild the per-series control rows (respects the current X column + kind).
function buildSeriesPanel(): void {
    if (!output) return;
    const kind = currentKind();
    const xName = xAxisSelect.value;
    const palette = activePalette();
    seriesPanel.innerHTML = "";

    output.columns.forEach((col) => {
        if (col.name === xName) return; // the X column can't also be a series
        const state = seriesState.get(col.name);
        if (!state) return;

        const row = document.createElement("div");
        row.className = "series-row" + (state.include ? "" : " disabled");

        const nameLabel = document.createElement("label");
        nameLabel.className = "series-name";
        const include = document.createElement("input");
        include.type = "checkbox";
        include.checked = state.include;
        include.addEventListener("change", () => {
            state.include = include.checked;
            row.classList.toggle("disabled", !state.include);
            renderChart();
        });
        const swatch = document.createElement("span");
        swatch.className = "series-swatch";
        swatch.style.background = state.color ?? palette[0]!;
        nameLabel.appendChild(include);
        nameLabel.appendChild(swatch);
        nameLabel.appendChild(document.createTextNode(col.name));
        row.appendChild(nameLabel);

        const opts = document.createElement("div");
        opts.className = "series-opts";

        // Per-series render type (combo only)
        if (kind === "combo") {
            opts.appendChild(makeSelectControl("Type", ["line", "bar"], state.type, (v) => {
                state.type = v as "line" | "bar";
                renderChart();
            }));
        }
        // Axis assignment (cartesian only)
        if (!isPolarKind(kind)) {
            opts.appendChild(makeSelectControl("Axis", [["Left", "y"], ["Right", "y1"]], state.axis, (v) => {
                state.axis = v as "y" | "y1";
                renderChart();
            }));
        }
        // Dashed + fill (line-like series only)
        const lineLike = kind === "line" || kind === "area" || kind === "timeseries" || kind === "combo";
        if (lineLike) {
            opts.appendChild(makeCheckControl("Dashed", state.dashed, (v) => { state.dashed = v; renderChart(); }));
            opts.appendChild(makeCheckControl("Fill", state.fill, (v) => { state.fill = v; renderChart(); }));
        }

        row.appendChild(opts);
        seriesPanel.appendChild(row);
    });
}

type Options = string[] | Array<[string, string]>;

function makeSelectControl(labelText: string, options: Options, value: string, onChange: (v: string) => void): HTMLLabelElement {
    const label = document.createElement("label");
    label.textContent = labelText + " ";
    const select = document.createElement("select");
    for (const opt of options) {
        const [text, val] = Array.isArray(opt) ? opt : [opt, opt];
        const o = document.createElement("option");
        o.value = val;
        o.textContent = text.charAt(0).toUpperCase() + text.slice(1);
        select.appendChild(o);
    }
    select.value = value;
    select.addEventListener("change", () => onChange(select.value));
    label.appendChild(select);
    return label;
}

function makeCheckControl(labelText: string, checked: boolean, onChange: (v: boolean) => void): HTMLLabelElement {
    const label = document.createElement("label");
    label.className = "chk";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.checked = checked;
    input.addEventListener("change", () => onChange(input.checked));
    label.appendChild(input);
    label.appendChild(document.createTextNode(" " + labelText));
    return label;
}

// Show/hide the top-level controls that only apply to certain chart kinds.
function updateControlVisibility(): void {
    const kind = currentKind();
    stackedWrap.hidden = !(kind === "bar" || kind === "area" || kind === "combo");
}

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

interface ResolvedSeries {
    name: string;
    index: number;
    state: SeriesState;
}

function resolvedSeries(): ResolvedSeries[] {
    if (!output) return [];
    const xName = xAxisSelect.value;
    const out: ResolvedSeries[] = [];
    for (const col of output.columns) {
        if (col.name === xName) continue;
        const state = seriesState.get(col.name);
        if (state?.include) out.push({ name: col.name, index: columnIndex(col.name), state });
    }
    return out;
}

function forecastFlags(): boolean[] | null {
    if (!output) return null;
    const idx = columnIndex(forecastColSelect.value);
    if (idx < 0) return null;
    return output.rows.map((r) => isTruthyFlag(r[idx]));
}

const DASH: number[] = [6, 4];

function renderChart(): void {
    const ctx = getHostContext();
    applyChartDefaults(ctx);
    const palette = activePalette();
    const textColor = readCssVar("--color-text-primary", ctx?.theme === "dark" ? "#eaeaea" : "#333");
    const gridColor = readCssVar("--color-border-secondary", ctx?.theme === "dark" ? "#ffffff22" : "#0000001a");

    if (currentChart) { currentChart.destroy(); currentChart = null; }

    const series = resolvedSeries();
    if (!output || output.rows.length === 0 || series.length === 0) {
        emptyDiv.hidden = false;
        canvas.style.display = "none";
        return;
    }
    emptyDiv.hidden = true;
    canvas.style.display = "";

    const kind = currentKind();
    const xIndex = columnIndex(xAxisSelect.value);

    if (isPolarKind(kind)) {
        renderPolar(kind as "pie" | "doughnut", series[0]!, xIndex, palette, textColor);
        return;
    }

    const flags = forecastFlags();
    const usesRightAxis = series.some((s) => s.state.axis === "y1");
    const stacked = stackedCheck.checked && (kind === "bar" || kind === "area" || kind === "combo");

    // Base Chart.js type: combo/bar => bar, scatter => scatter, everything else => line.
    const baseType: "bar" | "line" | "scatter" = kind === "bar" ? "bar" : kind === "combo" ? "bar" : kind === "scatter" ? "scatter" : "line";
    const pointMode = kind === "timeseries" || kind === "scatter";

    const datasets: any[] = [];

    // Confidence band (drawn first so it sits behind the series). Bound to the
    // primary series' axis; supported for line-like and time charts.
    if (!pointMode || kind === "timeseries") {
        pushBand(datasets, series[0]!, xIndex, pointMode, palette[0]!);
    }

    series.forEach((s, i) => {
        const color = s.state.color ?? palette[i % palette.length]!;
        const seriesType = kind === "combo" ? s.state.type : kind === "bar" ? "bar" : kind === "scatter" ? "scatter" : "line";
        const isLine = seriesType === "line";
        const fill = isLine && (s.state.fill || kind === "area");

        const dataset: any = {
            label: s.name,
            borderColor: color,
            backgroundColor: seriesType === "bar" ? withAlpha(color, 0.75) : (fill ? withAlpha(color, 0.25) : color),
            borderWidth: 2,
            yAxisID: s.state.axis,
        };
        if (kind === "combo") dataset.type = seriesType;

        if (pointMode) {
            const pts: { x: number; y: number }[] = [];
            const pflags: boolean[] = [];
            for (let r = 0; r < output!.rows.length; r++) {
                const row = output!.rows[r]!;
                const xv = kind === "timeseries" ? parseDate(row[xIndex]) : toNumber(row[xIndex]);
                const yv = toNumber(row[s.index]);
                if (xv === null || yv === null) continue;
                pts.push({ x: xv, y: yv });
                pflags.push(flags ? flags[r]! : false);
            }
            if (kind === "timeseries") {
                const order = pts.map((_, idx) => idx).sort((a, b) => pts[a]!.x - pts[b]!.x);
                dataset.data = order.map((idx) => pts[idx]!);
                applyLineStyling(dataset, s.state, isLine, order.map((idx) => pflags[idx]!), color);
            } else {
                dataset.data = pts;
                dataset.showLine = false;
                dataset.pointRadius = 3;
            }
        } else {
            dataset.data = output!.rows.map((row) => toNumber(row[s.index]));
            if (isLine) {
                applyLineStyling(dataset, s.state, true, flags, color);
            } else {
                // bar: dim forecast bars via per-point colors
                if (flags) {
                    dataset.backgroundColor = flags.map((f) => withAlpha(color, f ? 0.4 : 0.75));
                }
            }
        }
        datasets.push(dataset);
    });

    const labels = pointMode ? undefined : output.rows.map((r) => String(r[xIndex] ?? ""));

    const xScale: any = kind === "timeseries"
        ? { type: "time", stacked, ticks: { color: textColor }, grid: { color: gridColor } }
        : kind === "scatter"
            ? { type: "linear", ticks: { color: textColor }, grid: { color: gridColor } }
            : { stacked, ticks: { color: textColor }, grid: { color: gridColor } };

    const scales: any = {
        x: xScale,
        y: { type: "linear", position: "left", stacked, ticks: { color: textColor }, grid: { color: gridColor } },
    };
    if (usesRightAxis) {
        scales.y1 = { type: "linear", position: "right", ticks: { color: textColor }, grid: { drawOnChartArea: false, color: gridColor } };
    }

    const realCount = series.length;
    currentChart = new Chart(canvas, {
        type: baseType,
        data: { labels, datasets },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { mode: "index", intersect: false },
            plugins: {
                legend: {
                    display: realCount > 1,
                    labels: {
                        color: textColor,
                        filter: (item: any) => typeof item.text !== "string" || item.text.indexOf("__band") !== 0,
                    },
                },
                tooltip: {
                    filter: (item: any) => typeof item.dataset?.label !== "string" || item.dataset.label.indexOf("__band") !== 0,
                },
            },
            scales,
        },
    });
}

function applyLineStyling(dataset: any, state: SeriesState, isLine: boolean, flags: boolean[] | null, color: string): void {
    if (!isLine) return;
    dataset.pointRadius = 2;
    dataset.tension = 0;
    dataset.fill = state.fill ? "origin" : false;
    if (state.dashed) {
        dataset.borderDash = DASH;
    } else if (flags) {
        // Partially dashed line: solid over history, dashed once the forecast
        // flag turns on (the segment *entering* a forecast point is dashed).
        dataset.segment = {
            borderDash: (segCtx: any) => (flags[segCtx.p1DataIndex] ? DASH : undefined),
            borderColor: (segCtx: any) => (flags[segCtx.p1DataIndex] ? withAlpha(color, 0.85) : color),
        };
    }
}

// Adds the lower/upper confidence band datasets (upper fills down to lower).
function pushBand(datasets: any[], primary: ResolvedSeries, xIndex: number, pointMode: boolean, color: string): void {
    const lowIdx = columnIndex(lowerColSelect.value);
    const highIdx = columnIndex(upperColSelect.value);
    if (lowIdx < 0 || highIdx < 0 || !output) return;

    const bandFill = withAlpha(color, 0.15);
    const common = {
        borderColor: "transparent",
        pointRadius: 0,
        borderWidth: 0,
        yAxisID: primary.state.axis,
        order: 99,
    };

    if (pointMode) {
        const rows = output.rows
            .map((row) => ({ x: parseDate(row[xIndex]), lo: toNumber(row[lowIdx]), hi: toNumber(row[highIdx]) }))
            .filter((p) => p.x !== null) as { x: number; lo: number | null; hi: number | null }[];
        rows.sort((a, b) => a.x - b.x);
        datasets.push({ ...common, label: "__band_lower", data: rows.map((p) => ({ x: p.x, y: p.lo })), backgroundColor: bandFill, fill: false });
        datasets.push({ ...common, label: "__band_upper", data: rows.map((p) => ({ x: p.x, y: p.hi })), backgroundColor: bandFill, fill: "-1" });
    } else {
        datasets.push({ ...common, label: "__band_lower", data: output.rows.map((r) => toNumber(r[lowIdx])), backgroundColor: bandFill, fill: false });
        datasets.push({ ...common, label: "__band_upper", data: output.rows.map((r) => toNumber(r[highIdx])), backgroundColor: bandFill, fill: "-1" });
    }
}

function renderPolar(kind: "pie" | "doughnut", primary: ResolvedSeries, xIndex: number, palette: string[], textColor: string): void {
    const labels = output!.rows.map((r) => String(r[xIndex] ?? ""));
    const values = output!.rows.map((r) => toNumber(r[primary.index]) ?? 0);
    const colors = labels.map((_, i) => palette[i % palette.length]!);
    currentChart = new Chart(canvas, {
        type: kind,
        data: {
            labels,
            datasets: [{ label: primary.name, data: values, backgroundColor: colors, borderColor: withAlpha("#000000", 0), borderWidth: 1 }],
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: { legend: { display: true, position: "right", labels: { color: textColor } } },
        },
    });
}

// ---------------------------------------------------------------------------
// Display-mode toggle + wiring
// ---------------------------------------------------------------------------

function updateDisplayModeUi(ctx: HostContext | undefined): void {
    const available = ctx?.availableDisplayModes;
    const supportsFullscreen = !available || available.includes("fullscreen");
    toggleDisplayBtn.hidden = !supportsFullscreen;
    const mode = ctx?.displayMode;
    if (mode === "inline" || mode === "fullscreen" || mode === "pip") {
        const prev = currentDisplayMode;
        currentDisplayMode = mode;
        toggleDisplayBtn.textContent = mode === "fullscreen" ? "Minimize" : "Maximize";
        if (prev !== mode && currentChart) {
            setTimeout(() => { currentChart?.resize(); }, 50);
        }
    } else {
        toggleDisplayBtn.textContent = currentDisplayMode === "fullscreen" ? "Minimize" : "Maximize";
    }
}

toggleDisplayBtn.addEventListener("click", () => {
    const next: "inline" | "fullscreen" = currentDisplayMode === "fullscreen" ? "inline" : "fullscreen";
    requestDisplayMode(next).catch((err) => console.warn("requestDisplayMode failed", err));
});

toggleAdvancedBtn.addEventListener("click", () => {
    const show = advancedDiv.hidden;
    advancedDiv.hidden = !show;
    toggleAdvancedBtn.setAttribute("aria-expanded", show ? "true" : "false");
    toggleAdvancedBtn.textContent = show ? "Series & axes ▴" : "Series & axes ▾";
});

chartTypeSelect.addEventListener("change", () => {
    // Re-derive per-series fill/type defaults that depend on the chart kind.
    const kind = currentKind();
    for (const s of seriesState.values()) {
        if (kind === "area") s.fill = true;
    }
    buildSeriesPanel();
    updateControlVisibility();
    renderChart();
});
xAxisSelect.addEventListener("change", () => { buildSeriesPanel(); renderChart(); });
stackedCheck.addEventListener("change", renderChart);
forecastColSelect.addEventListener("change", renderChart);
lowerColSelect.addEventListener("change", renderChart);
upperColSelect.addEventListener("change", renderChart);

onHostContextChanged((ctx) => {
    applyHostStyles(ctx);
    updateDisplayModeUi(ctx);
    renderChart();
});

onResourceTeardown(() => {
    if (currentChart) { currentChart.destroy(); currentChart = null; }
});

onToolResult((result) => {
    const normalized = normalizeQueryOutput(extractStructured(result));
    if (!normalized) return;
    output = normalized;
    if (output.columns.length > 0) {
        initFromHints(output.columns, output.rows, pendingHints);
    }
    renderChart();
});

connect()
    .then(() => {
        observeBodySize();
        const ctx = getHostContext();
        applyHostStyles(ctx);
        updateDisplayModeUi(ctx);
        renderChart();
    })
    .catch(() => {
        renderChart();
    });
