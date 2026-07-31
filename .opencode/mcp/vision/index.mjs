import { readFile } from "node:fs/promises";
import path from "node:path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const apiBase = (process.env.VISION_API_BASE || "https://hubway.cc/v1").replace(/\/$/, "");
const apiKey =
  process.env.VISION_API_KEY ||
  process.env.HUBWAY_API_KEY ||
  process.env.OPENAI_API_KEY ||
  "";
const model = process.env.VISION_MODEL || "grok-4.5";

const mimeByExt = {
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".webp": "image/webp",
  ".gif": "image/gif",
  ".bmp": "image/bmp",
};

function guessMime(filePath) {
  return mimeByExt[path.extname(filePath).toLowerCase()] || "image/png";
}

async function toImageUrl(input) {
  const value = String(input || "").trim().replace(/^["']|["']$/g, "");
  if (!value) {
    throw new Error("path is required");
  }

  if (/^https?:\/\//i.test(value)) {
    return { imageUrl: value, source: value };
  }

  if (/^data:image\//i.test(value)) {
    return { imageUrl: value, source: "data-url" };
  }

  const absolute = path.isAbsolute(value) ? value : path.resolve(process.cwd(), value);
  const bytes = await readFile(absolute);
  const mime = guessMime(absolute);
  return {
    imageUrl: `data:${mime};base64,${bytes.toString("base64")}`,
    source: absolute,
  };
}

async function analyzeImage(imageInput, query) {
  if (!apiKey) {
    throw new Error(
      "Missing VISION_API_KEY / HUBWAY_API_KEY / OPENAI_API_KEY. Set one in opencode mcp environment."
    );
  }

  const { imageUrl, source } = await toImageUrl(imageInput);
  const prompt =
    query?.trim() ||
    "Describe this UI screenshot in detail for a desktop app engineer: layout regions, cover/image size and placement, text, colors, spacing, and anything that looks wrong.";

  const response = await fetch(`${apiBase}/chat/completions`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${apiKey}`,
    },
    body: JSON.stringify({
      model,
      temperature: 0.2,
      max_tokens: 1800,
      messages: [
        {
          role: "system",
          content:
            "You are a precise visual QA assistant for desktop UI. Reply in Chinese. Be concrete about layout, sizes, and visual defects.",
        },
        {
          role: "user",
          content: [
            { type: "text", text: prompt },
            { type: "image_url", image_url: { url: imageUrl } },
          ],
        },
      ],
    }),
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Vision API ${response.status}: ${text.slice(0, 800)}`);
  }

  let payload;
  try {
    payload = JSON.parse(text);
  } catch {
    throw new Error(`Vision API returned non-JSON: ${text.slice(0, 400)}`);
  }

  const content = payload?.choices?.[0]?.message?.content;
  if (!content) {
    throw new Error(`Vision API empty response: ${text.slice(0, 400)}`);
  }

  return [
    `source: ${source}`,
    `model: ${model}`,
    "",
    String(content),
  ].join("\n");
}

const server = new McpServer({
  name: "bohemix-vision",
  version: "1.0.0",
});

server.tool(
  "analyze_image",
  "Analyze a local image file or image URL and return a detailed Chinese text description. Use this when the main chat model cannot view images (for example OpenCode Read image failures).",
  {
    path: z
      .string()
      .describe("Absolute local path (e.g. D:\\\\Screenshot\\\\ui.png) or http(s) image URL"),
    query: z
      .string()
      .optional()
      .describe(
        "Optional focus, e.g. 'Check whether the mod cover fills the whole card and describe the glass overlay transition'"
      ),
  },
  async ({ path: imagePath, query }) => {
    try {
      const result = await analyzeImage(imagePath, query);
      return {
        content: [{ type: "text", text: result }],
      };
    } catch (error) {
      return {
        isError: true,
        content: [
          {
            type: "text",
            text: `analyze_image failed: ${error instanceof Error ? error.message : String(error)}`,
          },
        ],
      };
    }
  }
);

const transport = new StdioServerTransport();
await server.connect(transport);
