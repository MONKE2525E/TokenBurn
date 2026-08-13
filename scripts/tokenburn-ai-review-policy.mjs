const DEFAULT_PRIMARY_MODEL = "gemini-3.7-flash-high";
const DEFAULT_FALLBACK_MODEL = "claude-sonnet-4.6";
const LEGACY_PRIMARY_MODEL = "gemini-3.6-flash-high";

const RETRYABLE_FAILURE = /(?:quota|rate[ _-]?limit|too[ _-]?many[ _-]?requests|resource[ _-]?exhausted|429|overloaded|capacity|context deadline exceeded|deadline exceeded|model.{0,60}(?:not found|unavailable|disabled|unsupported)|(?:provider|llm).{0,60}(?:failed|error|unavailable))/i;
const FAILED_STATUS = new Set(["error", "failed", "failure"]);

function jsonFromOutput(output) {
  const trimmed = String(output || "").trim();
  const fenced = trimmed.match(/```(?:json)?\s*([\s\S]*?)\s*```/i)?.[1];
  for (const candidate of [fenced, trimmed]) {
    if (!candidate) continue;
    try { return JSON.parse(candidate); } catch { }
  }
  const start = trimmed.search(/[\[{]/);
  const end = Math.max(trimmed.lastIndexOf("}"), trimmed.lastIndexOf("]"));
  if (start >= 0 && end > start) {
    try { return JSON.parse(trimmed.slice(start, end + 1)); } catch { }
  }
  return null;
}

function textValue(value) {
  if (value == null) return "";
  if (typeof value === "string") return value;
  try { return JSON.stringify(value); } catch { return String(value); }
}

function statusOf(data) {
  const status = String(data?.status || data?.manifest?.terminal_state || "").toLowerCase();
  return status || null;
}

function outputForClassification(result, data) {
  return [
    result?.stderr,
    result?.stdout,
    data?.message,
    data?.error,
    data?.error?.message,
    data?.error?.type,
    data?.details,
  ].map(textValue).filter(Boolean).join(" ");
}

export function configuredReviewModels(environment = process.env) {
  const primary = String(environment.CLIPROXY_MODEL || DEFAULT_PRIMARY_MODEL).trim();
  const fallback = String(environment.CLIPROXY_FALLBACK_MODEL || DEFAULT_FALLBACK_MODEL).trim();
  const migratedPrimary = primary === LEGACY_PRIMARY_MODEL ? DEFAULT_PRIMARY_MODEL : primary;
  return [...new Set([migratedPrimary, fallback].filter(Boolean))];
}

export function reviewResult(result) {
  const data = jsonFromOutput(result?.stdout);
  const status = statusOf(data);
  const failed = result?.code !== 0 || FAILED_STATUS.has(status);
  const classification = outputForClassification(result, data);
  return {
    data,
    status,
    succeeded: !failed,
    retryable: failed && RETRYABLE_FAILURE.test(classification),
  };
}

export function shouldTryFallback(result) {
  return reviewResult(result).retryable;
}

export function reviewExitCode(findingCount) {
  return Number(findingCount) > 0 ? 1 : 0;
}
