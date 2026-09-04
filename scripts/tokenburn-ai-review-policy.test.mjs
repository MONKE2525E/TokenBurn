import test from "node:test";
import assert from "node:assert/strict";
import { configuredReviewModels, reviewExitCode, reviewResult, shouldTryFallback } from "./tokenburn-ai-review-policy.mjs";

test("defaults to the current Gemini model and Claude fallback", () => {
  assert.deepEqual(configuredReviewModels({}), ["gemini-3.7-flash-high", "claude-sonnet-4.6"]);
});

test("migrates the previous default while preserving other explicit models", () => {
  assert.deepEqual(configuredReviewModels({ CLIPROXY_MODEL: "gemini-3.6-flash-high" }), ["gemini-3.7-flash-high", "claude-sonnet-4.6"]);
  assert.deepEqual(configuredReviewModels({ CLIPROXY_MODEL: "gemini-3.6-flash-high", CLIPROXY_FALLBACK_MODEL: "claude-opus-4.6" }), ["gemini-3.7-flash-high", "claude-opus-4.6"]);
  assert.deepEqual(configuredReviewModels({ CLIPROXY_MODEL: "gemini-3.7-flash-high", CLIPROXY_FALLBACK_MODEL: "claude-opus-4.6" }), ["gemini-3.7-flash-high", "claude-opus-4.6"]);
});

test("recognizes a structured quota failure even when OCR exits successfully", () => {
  const result = { code: 0, stdout: JSON.stringify({ status: "failed", error: { message: "429 quota exhausted" } }), stderr: "" };
  assert.equal(reviewResult(result).succeeded, false);
  assert.equal(shouldTryFallback(result), true);
});

test("recognizes provider error codes in structured output", () => {
  const result = { code: 0, stdout: JSON.stringify({ status: "error", error: { code: "RESOURCE_EXHAUSTED" } }), stderr: "" };
  assert.equal(shouldTryFallback(result), true);
});

test("parses fenced structured output without changing plain JSON handling", () => {
  const result = { code: 0, stdout: "```json\n{\"status\":\"failed\",\"message\":\"429 quota exhausted\"}\n```", stderr: "" };
  assert.equal(reviewResult(result).status, "failed");
  assert.equal(shouldTryFallback(result), true);
});

test("recognizes OCR's terminal error after it retries a 429", () => {
  const result = { code: 1, stdout: "", stderr: "Error: llm request failed: context deadline exceeded" };
  assert.equal(shouldTryFallback(result), true);
});

test("does not retry a non-provider review failure", () => {
  const result = { code: 1, stdout: "", stderr: "review rules were invalid" };
  assert.equal(reviewResult(result).succeeded, false);
  assert.equal(shouldTryFallback(result), false);
});

test("passes CI for zero findings and fails it for findings", () => {
  assert.equal(reviewExitCode(0), 0);
  assert.equal(reviewExitCode(1), 1);
  assert.equal(reviewExitCode(3), 1);
});
