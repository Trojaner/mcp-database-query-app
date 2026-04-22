import { build } from "esbuild";
import { readFile, writeFile, mkdir } from "node:fs/promises";
import { existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const outDir = resolve(here, "../ui-dist");
if (!existsSync(outDir)) {
    await mkdir(outDir, { recursive: true });
}

async function bundleApp(name) {
    const entry = resolve(here, name, "app.ts");
    const shell = await readFile(resolve(here, name, "index.html"), "utf8");
    const result = await build({
        entryPoints: [entry],
        bundle: true,
        minify: true,
        format: "iife",
        target: ["es2022"],
        write: false,
        logLevel: "silent",
    });
    const js = result.outputFiles[0].text;
    const html = shell.replace("<!-- INLINE_SCRIPT -->", `<script>${js}</script>`);
    const outPath = join(outDir, `${name}.html`);
    await writeFile(outPath, html, "utf8");
    console.log(`[McpDatabaseQueryApp.Apps] built ${outPath}`);
}

await bundleApp("results");
await bundleApp("builder");
await bundleApp("chart");
await bundleApp("schema-viewer");
