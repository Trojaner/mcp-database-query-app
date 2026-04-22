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
}

let output: QueryOutput | undefined;

function pick<T>(obj: Record<string, unknown> | undefined, ...keys: string[]): T | undefined {
    if (!obj) return undefined;
    for (const k of keys) if (obj[k] !== undefined) return obj[k] as T;
    return undefined;
}

interface ChartHints {
    chartType?: string;
    xAxis?: string;
    yAxis?: string;
}

let pendingHints: ChartHints = {};

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
            name: (pick<string>(cc, "name", "Name") ?? ""),
            dataType: pick<string>(cc, "dataType", "DataType"),
        };
    });
    pendingHints = {
        chartType: pick<string>(o, "chartType", "ChartType"),
        xAxis: pick<string>(o, "xAxis", "XAxis"),
        yAxis: pick<string>(o, "yAxis", "YAxis"),
    };
    return {
        columns,
        rows: rowsRaw as unknown[][],
        rowCount: (pick<number>(o, "rowCount", "RowCount") ?? rowsRaw.length),
        truncated: (pick<boolean>(o, "truncated", "Truncated") ?? false),
        executionMs: (pick<number>(o, "executionMs", "ExecutionMs") ?? 0),
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

const chartTypeSelect = document.getElementById("chartType") as HTMLSelectElement;
const xAxisSelect = document.getElementById("xAxis") as HTMLSelectElement;
const yAxisSelect = document.getElementById("yAxis") as HTMLSelectElement;
const canvas = document.getElementById("chart") as HTMLCanvasElement;
const emptyDiv = document.getElementById("empty") as HTMLDivElement;
const toggleDisplayBtn = document.getElementById("toggle-display") as HTMLButtonElement;

let currentChart: Chart | null = null;
let currentDisplayMode: "inline" | "fullscreen" | "pip" = "inline";

function populateAxisSelects(columns: Column[]) {
    xAxisSelect.innerHTML = "";
    yAxisSelect.innerHTML = "";
    for (const col of columns) {
        const xOpt = document.createElement("option");
        xOpt.value = col.name;
        xOpt.textContent = col.name;
        xAxisSelect.appendChild(xOpt);

        const yOpt = document.createElement("option");
        yOpt.value = col.name;
        yOpt.textContent = col.name;
        yAxisSelect.appendChild(yOpt);
    }
    if (columns.length > 1) {
        yAxisSelect.selectedIndex = 1;
    }
}

function findFirstNumericColumnIndex(columns: Column[], rows: unknown[][], skipIndex: number): number {
    for (let i = 0; i < columns.length; i++) {
        if (i === skipIndex) continue;
        const sample = rows.find(r => r[i] !== null && r[i] !== undefined);
        if (sample && isNumeric(sample[i])) return i;
    }
    return -1;
}

function applyHintsAndDefaults(columns: Column[], rows: unknown[][], hints: ChartHints) {
    const validChartTypes = ["bar", "line", "timeseries", "pie", "doughnut"];
    if (hints.chartType) {
        const t = hints.chartType.toLowerCase();
        if (validChartTypes.indexOf(t) !== -1) chartTypeSelect.value = t;
    }

    const names = columns.map(c => c.name);
    const xIdx = hints.xAxis ? names.indexOf(hints.xAxis) : -1;
    if (xIdx >= 0) xAxisSelect.value = names[xIdx]!;

    const yIdx = hints.yAxis ? names.indexOf(hints.yAxis) : -1;
    if (yIdx >= 0) {
        yAxisSelect.value = names[yIdx]!;
    } else if (!hints.yAxis) {
        const currentX = xAxisSelect.selectedIndex;
        const numericIdx = findFirstNumericColumnIndex(columns, rows, currentX);
        if (numericIdx >= 0) yAxisSelect.value = names[numericIdx]!;
    }
}

function getColumnIndex(name: string): number {
    if (!output) return -1;
    return output.columns.findIndex(c => c.name === name);
}

function isNumeric(value: unknown): boolean {
    if (typeof value === "number") return true;
    if (typeof value === "string") return value.trim() !== "" && !isNaN(Number(value));
    return false;
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

function applyChartDefaults(ctx: HostContext | undefined): void {
    const isDark = ctx?.theme === "dark";
    Chart.defaults.color = readCssVar("--color-text-primary", isDark ? "#eaeaea" : "#333");
    Chart.defaults.borderColor = readCssVar("--color-border-secondary", isDark ? "#ffffff22" : "#0000001a");
    const fontFamily = readCssVar("--font-sans", "system-ui, sans-serif");
    if (fontFamily) {
        Chart.defaults.font = { ...(Chart.defaults.font ?? {}), family: fontFamily };
    }
}

function renderChart() {
    const ctx = getHostContext();
    const isDark = ctx?.theme === "dark";
    const palette = isDark ? darkPalette : lightPalette;

    applyChartDefaults(ctx);

    if (!output || output.rows.length === 0) {
        emptyDiv.hidden = false;
        canvas.style.display = "none";
        if (currentChart) {
            currentChart.destroy();
            currentChart = null;
        }
        return;
    }

    emptyDiv.hidden = true;
    canvas.style.display = "";

    const xIndex = getColumnIndex(xAxisSelect.value);
    const yIndex = getColumnIndex(yAxisSelect.value);
    if (xIndex < 0 || yIndex < 0) return;

    const selected = chartTypeSelect.value as "bar" | "line" | "timeseries" | "pie" | "doughnut";
    const isTimeseries = selected === "timeseries";
    const chartJsType: "bar" | "line" | "pie" | "doughnut" = isTimeseries ? "line" : selected;
    const isPolar = chartJsType === "pie" || chartJsType === "doughnut";

    const textColor = readCssVar("--color-text-primary", isDark ? "#eaeaea" : "#333");
    const gridColor = readCssVar("--color-border-secondary", isDark ? "#ffffff22" : "#0000001a");

    if (currentChart) {
        currentChart.destroy();
    }

    let chartData: import("chart.js").ChartData;
    if (isTimeseries) {
        const points: { x: number; y: number }[] = [];
        for (const r of output.rows) {
            const t = parseDate(r[xIndex]);
            const v = r[yIndex];
            if (t === null || !isNumeric(v)) continue;
            points.push({ x: t, y: Number(v) });
        }
        points.sort((a, b) => a.x - b.x);
        chartData = {
            datasets: [{
                label: yAxisSelect.value,
                data: points,
                backgroundColor: palette[0]!,
                borderColor: palette[0]!,
                borderWidth: 2,
                fill: false,
                pointRadius: 2,
            }],
        };
    } else {
        const labels = output.rows.map(r => String(r[xIndex] ?? ""));
        const values = output.rows.map(r => {
            const v = r[yIndex];
            return isNumeric(v) ? Number(v) : 0;
        });
        const colors = labels.map((_, i) => palette[i % palette.length]!);
        chartData = {
            labels,
            datasets: [{
                label: yAxisSelect.value,
                data: values,
                backgroundColor: isPolar ? colors : palette[0]!,
                borderColor: isPolar ? "#0000" : palette[0]!,
                borderWidth: isPolar ? 1 : 2,
                fill: chartJsType === "line" ? false : undefined,
            }],
        };
    }

    const linearScales = {
        x: isTimeseries
            ? { type: "time" as const, ticks: { color: textColor }, grid: { color: gridColor } }
            : { ticks: { color: textColor }, grid: { color: gridColor } },
        y: { ticks: { color: textColor }, grid: { color: gridColor } },
    };

    currentChart = new Chart(canvas, {
        type: chartJsType,
        data: chartData,
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: {
                    display: isPolar,
                    labels: { color: textColor },
                },
            },
            scales: isPolar ? {} : linearScales,
        },
    });
}

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
    requestDisplayMode(next).catch((err) => {
        console.warn("requestDisplayMode failed", err);
    });
});

chartTypeSelect.addEventListener("change", renderChart);
xAxisSelect.addEventListener("change", renderChart);
yAxisSelect.addEventListener("change", renderChart);

onHostContextChanged((ctx) => {
    applyHostStyles(ctx);
    updateDisplayModeUi(ctx);
    renderChart();
});

onResourceTeardown(() => {
    if (currentChart) {
        currentChart.destroy();
        currentChart = null;
    }
});

onToolResult((result) => {
    const normalized = normalizeQueryOutput(extractStructured(result));
    if (!normalized) return;
    output = normalized;
    if (output.columns.length > 0) {
        populateAxisSelects(output.columns);
        applyHintsAndDefaults(output.columns, output.rows, pendingHints);
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
