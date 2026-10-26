import { parseTokenString } from "./format.js";

// Pricing per million tokens (USD)
const PRICING: Record<string, { input: number; cached: number; output: number }> = {
  "claude-opus-4.6":   { input: 5,  cached: 0.50, output: 25 },
  "claude-opus-4.5":   { input: 5,  cached: 0.50, output: 25 },
  "claude-sonnet-4.6": { input: 3,  cached: 0.30, output: 15 },
  "claude-sonnet-4.5": { input: 3,  cached: 0.30, output: 15 },
  "claude-sonnet-4":   { input: 3,  cached: 0.30, output: 15 },
  "claude-haiku-4.5":  { input: 1,  cached: 0.10, output: 5  },
  "gpt-5.4":           { input: 2.5, cached: 0.63, output: 10 },
  "gpt-5.2":           { input: 2.5, cached: 0.63, output: 10 },
  "gpt-5.1":           { input: 2.5, cached: 0.63, output: 10 },
};

export interface PriceEstimate {
  inputCost: number;
  cachedCost: number;
  outputCost: number;
  totalCost: number;
  formatted: string;
}

export interface PriceInput {
  model: string;
  inputTokens: string;
  outputTokens: string;
  cachedTokens: string;
  /** Sub-agent input tokens (independent of main agent input). */
  subAgentInputTokens?: string;
  /** Sub-agent output tokens (independent of main agent output). */
  subAgentOutputTokens?: string;
  /** Sub-agent cached input tokens. */
  subAgentCachedTokens?: string;
}

function formatPrice(totalCost: number): string {
  return totalCost < 0.01 ? "<$0.01" : `$${totalCost.toFixed(2)}`;
}

/** Estimate the cost of a single (main + optional sub-agent) trial. */
export function estimatePrice(args: PriceInput): PriceEstimate {
  const pricing = PRICING[args.model] || PRICING["claude-sonnet-4.5"];

  const cost = (input: string, output: string, cached: string): {
    inputCost: number; cachedCost: number; outputCost: number;
  } => {
    const inTok = parseTokenString(input) / 1_000_000;
    const outTok = parseTokenString(output) / 1_000_000;
    const cacheTok = parseTokenString(cached) / 1_000_000;
    // Uncached input is treated as cache writes (2× base rate, 1-hour cache).
    const uncachedIn = Math.max(0, inTok - cacheTok);
    return {
      inputCost: uncachedIn * pricing.input * 2,
      cachedCost: cacheTok * pricing.cached,
      outputCost: outTok * pricing.output,
    };
  };

  const main = cost(args.inputTokens, args.outputTokens, args.cachedTokens);
  const sub = cost(
    args.subAgentInputTokens || "0",
    args.subAgentOutputTokens || "0",
    args.subAgentCachedTokens || "0",
  );

  const inputCost = main.inputCost + sub.inputCost;
  const cachedCost = main.cachedCost + sub.cachedCost;
  const outputCost = main.outputCost + sub.outputCost;
  const totalCost = inputCost + cachedCost + outputCost;

  return {
    inputCost,
    cachedCost,
    outputCost,
    totalCost,
    formatted: formatPrice(totalCost),
  };
}

export function formatTokens(s: string): string {
  if (!s) return "—";
  return s;
}
