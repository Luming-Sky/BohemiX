import { createHash } from "node:crypto";

const AFDIAN_SUCCESS_CODE = 200;
const MAX_REQUEST_BYTES = 8 * 1024;
const MAX_AFDIAN_PAGES = 20;
const JSON_HEADERS = { "content-type": "application/json; charset=utf-8" };

export default {
  async fetch(request, env) {
    if (request.method === "OPTIONS") {
      return new Response(null, {
        status: 204,
        headers: { allow: "POST, OPTIONS" }
      });
    }

    if (request.method !== "POST") {
      return jsonResponse(405, { error: "method_not_allowed" }, { allow: "POST, OPTIONS" });
    }

    try {
      if (new URL(request.url).pathname === "/verify") {
        return await verifyAccess(request, env);
      }

      const submission = await readSubmission(request);
      const plans = parseEligiblePlans(env.ECHO_CAVE_ELIGIBLE_PLANS_JSON);
      if (!env.AFDIAN_TOKEN || !env.AFDIAN_USER_ID || plans.length === 0 || !env.ECHO_CAVE_DB) {
        console.error("Echo Cave worker is missing required bindings or configuration.");
        return jsonResponse(503, { error: "service_unavailable" });
      }

      const sponsor = await findEligibleSponsor(
        submission.afdianSupporterIdentifier,
        plans,
        env);
      if (sponsor === null) {
        return jsonResponse(403, { error: "afdian_plan_required" });
      }

      await env.ECHO_CAVE_DB.prepare(
        `INSERT INTO echo_messages (
          supporter_identifier, supporter_name, plan_name, subject, message,
          contact, app_version, language
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)`
      ).bind(
        submission.afdianSupporterIdentifier,
        sponsor.name,
        sponsor.planName,
        submission.subject,
        submission.message,
        submission.contact,
        submission.appVersion,
        submission.language
      ).run();

      return jsonResponse(201, { accepted: true });
    } catch (error) {
      if (error instanceof ClientInputError) {
        return jsonResponse(400, { error: error.code });
      }

      console.error("Echo Cave request failed", error);
      return jsonResponse(502, { error: "verification_unavailable" });
    }
  }
};

async function readSubmission(request) {
  const payload = await readJsonPayload(request);
  return {
    subject: requiredText(payload?.subject, 3, 80, "invalid_subject"),
    message: requiredText(payload?.message, 10, 2000, "invalid_message"),
    contact: optionalText(payload?.contact, 120, "invalid_contact"),
    appVersion: requiredText(payload?.appVersion, 1, 80, "invalid_app_version"),
    language: requiredText(payload?.language, 2, 16, "invalid_language"),
    afdianSupporterIdentifier: requiredText(
      payload?.afdianSupporterIdentifier,
      1,
      120,
      "invalid_afdian_supporter"
    )
  };
}

async function verifyAccess(request, env) {
  const payload = await readJsonPayload(request);
  const identifier = requiredText(
    payload?.afdianSupporterIdentifier,
    1,
    120,
    "invalid_afdian_supporter"
  );
  const plans = parseEligiblePlans(env.ECHO_CAVE_ELIGIBLE_PLANS_JSON);
  if (!env.AFDIAN_TOKEN || !env.AFDIAN_USER_ID || plans.length === 0) {
    console.error("Echo Cave worker is missing required Afdian configuration.");
    return jsonResponse(503, { error: "service_unavailable" });
  }

  const sponsor = await findSponsor(identifier, env);
  const isEligible = sponsor !== null
    && plans.some((plan) => plan.toLocaleLowerCase() === sponsor.planName.toLocaleLowerCase());
  return jsonResponse(200, {
    isConfigured: true,
    isEligible,
    sponsorName: sponsor?.name ?? "",
    planName: sponsor?.planName ?? ""
  });
}

async function readJsonPayload(request) {
  const contentLength = Number(request.headers.get("content-length") ?? 0);
  if (Number.isFinite(contentLength) && contentLength > MAX_REQUEST_BYTES) {
    throw new ClientInputError("request_too_large");
  }

  let payload;
  try {
    const body = await request.text();
    if (body.length > MAX_REQUEST_BYTES) {
      throw new ClientInputError("request_too_large");
    }
    payload = JSON.parse(body);
  } catch (error) {
    if (error instanceof ClientInputError) {
      throw error;
    }
    throw new ClientInputError("invalid_json");
  }

  return payload;
}

async function findEligibleSponsor(identifier, eligiblePlans, env) {
  const sponsor = await findSponsor(identifier, env);
  if (sponsor === null) {
    return null;
  }

  if (!eligiblePlans.some((plan) => plan.toLocaleLowerCase() === sponsor.planName.toLocaleLowerCase())) {
    return null;
  }

  return sponsor;
}

async function findSponsor(identifier, env) {
  const records = await loadAfdianSponsors(env);
  const normalizedIdentifier = identifier.toLocaleLowerCase();
  const idMatches = records.filter((record) =>
    record.userId?.toLocaleLowerCase() === normalizedIdentifier
  );
  const candidates = idMatches.length > 0
    ? idMatches
    : records.filter((record) => record.name?.toLocaleLowerCase() === normalizedIdentifier);

  if (candidates.length !== 1) {
    return null;
  }

  return candidates[0];
}

async function loadAfdianSponsors(env) {
  const sponsors = new Map();
  let totalPages = MAX_AFDIAN_PAGES;
  const endpoint = env.AFDIAN_API_ENDPOINT || "https://afdian.com/api/open/query-sponsor";

  for (let page = 1; page <= totalPages; page += 1) {
    const parameters = JSON.stringify({ page });
    const timestamp = Math.floor(Date.now() / 1000);
    const signatureInput = `${env.AFDIAN_TOKEN}params${parameters}ts${timestamp}user_id${env.AFDIAN_USER_ID}`;
    const response = await fetch(endpoint, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        user_id: env.AFDIAN_USER_ID,
        ts: timestamp,
        params: parameters,
        sign: createHash("md5").update(signatureInput, "utf8").digest("hex")
      })
    });
    if (!response.ok) {
      throw new Error(`Afdian responded with HTTP ${response.status}`);
    }

    const payload = await response.json();
    if (payload?.ec !== AFDIAN_SUCCESS_CODE || !payload.data) {
      throw new Error("Afdian returned an invalid sponsor response");
    }

    const data = payload.data;
    totalPages = Math.min(MAX_AFDIAN_PAGES, Math.max(1, Number(data.total_page) || 1));
    const records = Array.isArray(data.list) ? data.list : [];
    for (const record of records) {
      const userId = trimText(record?.user?.user_id, 120);
      const name = trimText(record?.user?.name, 80);
      const planName = trimText(record?.current_plan?.name, 120);
      if (!name || !planName) {
        continue;
      }

      const key = userId || name.toLocaleLowerCase();
      sponsors.set(key, { userId, name, planName });
    }

    if (page >= totalPages || records.length === 0) {
      break;
    }
  }

  return [...sponsors.values()];
}

function parseEligiblePlans(value) {
  try {
    const parsed = JSON.parse(value ?? "[]");
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed
      .filter((plan) => typeof plan === "string")
      .map((plan) => plan.trim())
      .filter(Boolean);
  } catch {
    return [];
  }
}

function requiredText(value, minimumLength, maximumLength, errorCode) {
  if (typeof value !== "string") {
    throw new ClientInputError(errorCode);
  }

  const normalized = value.trim();
  if (normalized.length < minimumLength || normalized.length > maximumLength) {
    throw new ClientInputError(errorCode);
  }

  return normalized;
}

function optionalText(value, maximumLength, errorCode) {
  if (value === undefined || value === null || value === "") {
    return "";
  }

  if (typeof value !== "string") {
    throw new ClientInputError(errorCode);
  }

  const normalized = value.trim();
  if (normalized.length > maximumLength) {
    throw new ClientInputError(errorCode);
  }

  return normalized;
}

function trimText(value, maximumLength) {
  return typeof value === "string" ? value.trim().slice(0, maximumLength) : "";
}

function jsonResponse(status, body, headers = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { ...JSON_HEADERS, ...headers }
  });
}

class ClientInputError extends Error {
  constructor(code) {
    super(code);
    this.code = code;
  }
}
