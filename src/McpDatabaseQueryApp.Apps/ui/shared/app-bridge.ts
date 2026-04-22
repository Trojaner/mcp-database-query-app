export interface CallToolResult {
    content: Array<{ type: string; text?: string; [k: string]: unknown }>;
    structuredContent?: unknown;
    isError?: boolean;
}

export interface ResourceContents {
    contents: Array<{ uri: string; mimeType?: string; text?: string; blob?: string }>;
}

export interface HostContext {
    theme?: "light" | "dark";
    styles?: { variables?: Record<string, string>; css?: { fonts?: string } };
    displayMode?: "inline" | "fullscreen" | "pip";
    availableDisplayModes?: string[];
    containerDimensions?: { height?: number; maxHeight?: number; width?: number; maxWidth?: number };
    locale?: string;
    timeZone?: string;
    platform?: "web" | "desktop" | "mobile";
    safeAreaInsets?: { top: number; right: number; bottom: number; left: number };
    toolInfo?: { id?: number | string; tool?: Record<string, unknown> };
    [k: string]: unknown;
}

type JsonRpcId = number | string;

interface JsonRpcRequest {
    jsonrpc: "2.0";
    id: JsonRpcId;
    method: string;
    params?: unknown;
}

interface JsonRpcResponse {
    jsonrpc: "2.0";
    id: JsonRpcId;
    result?: unknown;
    error?: { code: number; message: string; data?: unknown };
}

interface JsonRpcNotification {
    jsonrpc: "2.0";
    method: string;
    params?: unknown;
}

type Pending = {
    resolve: (v: unknown) => void;
    reject: (e: unknown) => void;
};

let nextId = 1;
const pending = new Map<JsonRpcId, Pending>();
let toolResultCallback: ((result: CallToolResult) => void) | null = null;
let bufferedToolResult: CallToolResult | null = null;
let connected = false;
let connectPromise: Promise<void> | null = null;

let hostContext: HostContext | undefined;
const hostContextListeners: Array<(ctx: HostContext) => void> = [];

let toolInputCallback: ((args: unknown) => void) | null = null;
let bufferedToolInput: unknown = undefined;
let hasBufferedToolInput = false;

let toolInputPartialCallback: ((partial: unknown) => void) | null = null;
let bufferedToolInputPartial: unknown = undefined;
let hasBufferedToolInputPartial = false;

let toolCancelledCallback: (() => void) | null = null;
let bufferedToolCancelled = false;

let resourceTeardownCallback: (() => void) | null = null;
let bufferedResourceTeardown = false;

function post(msg: JsonRpcRequest | JsonRpcNotification): void {
    const target = window.parent && window.parent !== window ? window.parent : null;
    if (!target) throw new Error("no parent frame");
    target.postMessage(msg, "*");
}

function request<T>(method: string, params?: unknown, timeoutMs = 15000): Promise<T> {
    const id = nextId++;
    return new Promise<T>((resolve, reject) => {
        const timer = setTimeout(() => {
            if (pending.delete(id)) reject(new Error(`request ${method} timed out`));
        }, timeoutMs);
        pending.set(id, {
            resolve: (v) => { clearTimeout(timer); resolve(v as T); },
            reject: (e) => { clearTimeout(timer); reject(e); },
        });
        try {
            post({ jsonrpc: "2.0", id, method, params });
        } catch (e) {
            pending.delete(id);
            clearTimeout(timer);
            reject(e);
        }
    });
}

function notify(method: string, params?: unknown): void {
    post({ jsonrpc: "2.0", method, params });
}

function isJsonRpcMessage(data: unknown): data is JsonRpcResponse | JsonRpcNotification {
    return typeof data === "object" && data !== null && (data as { jsonrpc?: unknown }).jsonrpc === "2.0";
}

function fireHostContextListeners(): void {
    if (!hostContext) return;
    const ctx = hostContext;
    for (const cb of hostContextListeners) {
        try { cb(ctx); } catch { /* swallow */ }
    }
}

function mergeHostContext(partial: unknown): void {
    if (!partial || typeof partial !== "object") return;
    hostContext = { ...(hostContext ?? {}), ...(partial as HostContext) };
}

function handleMessage(ev: MessageEvent): void {
    const data = ev.data;
    if (!isJsonRpcMessage(data)) return;

    if ("id" in data && (data as JsonRpcResponse).id !== undefined && !("method" in data)) {
        const resp = data as JsonRpcResponse;
        const p = pending.get(resp.id);
        if (!p) return;
        pending.delete(resp.id);
        if (resp.error) p.reject(new Error(resp.error.message));
        else p.resolve(resp.result);
        return;
    }

    if ("method" in data) {
        const note = data as JsonRpcNotification;
        switch (note.method) {
            case "ui/notifications/tool-result": {
                const result = note.params as CallToolResult | undefined;
                if (!result) return;
                if (toolResultCallback) toolResultCallback(result);
                else bufferedToolResult = result;
                return;
            }
            case "ui/notifications/host-context-changed": {
                mergeHostContext(note.params);
                fireHostContextListeners();
                return;
            }
            case "ui/notifications/tool-input": {
                if (toolInputCallback) toolInputCallback(note.params);
                else { bufferedToolInput = note.params; hasBufferedToolInput = true; }
                return;
            }
            case "ui/notifications/tool-input-partial": {
                if (toolInputPartialCallback) toolInputPartialCallback(note.params);
                else { bufferedToolInputPartial = note.params; hasBufferedToolInputPartial = true; }
                return;
            }
            case "ui/notifications/tool-cancelled": {
                if (toolCancelledCallback) toolCancelledCallback();
                else bufferedToolCancelled = true;
                return;
            }
            case "ui/resource-teardown": {
                if (resourceTeardownCallback) resourceTeardownCallback();
                else bufferedResourceTeardown = true;
                return;
            }
        }
    }
}

window.addEventListener("message", handleMessage);

export function connect(timeoutMs = 5000): Promise<void> {
    if (connectPromise) return connectPromise;
    connectPromise = (async () => {
        if (!window.parent || window.parent === window) {
            throw new Error("not embedded in a host frame");
        }
        const response = await request<{ hostContext?: HostContext } | undefined>("ui/initialize", {
            protocolVersion: "2026-01-26",
            appInfo: { name: "mcp-database-query-app-ui", version: "0.1.0" },
            appCapabilities: {
                availableDisplayModes: ["inline", "fullscreen"],
            },
        }, timeoutMs);
        if (response && typeof response === "object" && (response as { hostContext?: HostContext }).hostContext) {
            hostContext = (response as { hostContext?: HostContext }).hostContext;
        } else {
            hostContext = hostContext ?? {};
        }
        notify("ui/notifications/initialized", {});
        connected = true;
        fireHostContextListeners();
    })();
    return connectPromise;
}

export function onToolResult(cb: (result: CallToolResult) => void): void {
    toolResultCallback = cb;
    if (bufferedToolResult) {
        const r = bufferedToolResult;
        bufferedToolResult = null;
        cb(r);
    }
}

export function callTool(name: string, args: Record<string, unknown>): Promise<CallToolResult> {
    return request<CallToolResult>("tools/call", { name, arguments: args });
}

export function readResource(uri: string): Promise<ResourceContents> {
    return request<ResourceContents>("resources/read", { uri });
}

export function isConnected(): boolean {
    return connected;
}

export function getHostContext(): HostContext | undefined {
    return hostContext;
}

export function onHostContextChanged(cb: (ctx: HostContext) => void): void {
    hostContextListeners.push(cb);
    if (hostContext) {
        try { cb(hostContext); } catch { /* swallow */ }
    }
}

export function applyHostStyles(ctx: HostContext | undefined): void {
    if (!ctx) return;
    const vars = ctx.styles?.variables;
    if (vars) {
        for (const [name, value] of Object.entries(vars)) {
            document.documentElement.style.setProperty(name, value);
        }
    }
    if (ctx.theme) {
        document.documentElement.dataset.theme = ctx.theme;
    }
    const fonts = ctx.styles?.css?.fonts;
    if (fonts) {
        const id = "mcp-database-query-app-host-fonts";
        if (!document.getElementById(id)) {
            const style = document.createElement("style");
            style.id = id;
            style.textContent = fonts;
            document.head.appendChild(style);
        }
    }
}

let sizeDebounceTimer: ReturnType<typeof setTimeout> | null = null;
let pendingSize: { width: number; height: number } | null = null;

export function notifySizeChanged(width: number, height: number): void {
    pendingSize = { width, height };
    if (sizeDebounceTimer !== null) return;
    sizeDebounceTimer = setTimeout(() => {
        sizeDebounceTimer = null;
        if (pendingSize) {
            const size = pendingSize;
            pendingSize = null;
            try {
                notify("ui/notifications/size-changed", { width: size.width, height: size.height });
            } catch { /* swallow */ }
        }
    }, 100);
}

export function observeBodySize(element?: HTMLElement): void {
    const el = element ?? document.body;
    const emit = () => {
        const root = document.documentElement;
        const width = Math.max(root.scrollWidth, el.scrollWidth);
        const height = Math.max(root.scrollHeight, el.scrollHeight);
        notifySizeChanged(width, height);
    };
    emit();
    if (typeof ResizeObserver !== "undefined") {
        const ro = new ResizeObserver(() => emit());
        ro.observe(el);
        ro.observe(document.documentElement);
        const mo = new MutationObserver(() => emit());
        mo.observe(el, { childList: true, subtree: true, attributes: true, characterData: true });
    } else {
        window.addEventListener("resize", emit);
    }
}

export function openLink(url: string): Promise<void> {
    return request<void>("ui/open-link", { url });
}

export function sendChatMessage(text: string): Promise<void> {
    return request<void>("ui/message", {
        role: "user",
        content: [{ type: "text", text }],
    });
}

export function requestDisplayMode(mode: "inline" | "fullscreen" | "pip"): Promise<void> {
    return request<void>("ui/request-display-mode", { mode });
}

export function updateModelContext(structuredContent: Record<string, unknown>): Promise<void> {
    return request<void>("ui/update-model-context", { structuredContent });
}

export function onToolInput(cb: (args: unknown) => void): void {
    toolInputCallback = cb;
    if (hasBufferedToolInput) {
        const v = bufferedToolInput;
        hasBufferedToolInput = false;
        bufferedToolInput = undefined;
        cb(v);
    }
}

export function onToolInputPartial(cb: (partial: unknown) => void): void {
    toolInputPartialCallback = cb;
    if (hasBufferedToolInputPartial) {
        const v = bufferedToolInputPartial;
        hasBufferedToolInputPartial = false;
        bufferedToolInputPartial = undefined;
        cb(v);
    }
}

export function onToolCancelled(cb: () => void): void {
    toolCancelledCallback = cb;
    if (bufferedToolCancelled) {
        bufferedToolCancelled = false;
        cb();
    }
}

export function onResourceTeardown(cb: () => void): void {
    resourceTeardownCallback = cb;
    if (bufferedResourceTeardown) {
        bufferedResourceTeardown = false;
        cb();
    }
}
