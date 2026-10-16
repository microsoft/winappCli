import { CopilotClient, type ModelInfo } from "@github/copilot-sdk";
import { resolveCopilotCliPath } from "./copilot-cli.js";

export interface AvailableModel {
  id: string;
  name: string;
}

export async function discoverAvailableModels(): Promise<AvailableModel[]> {
  // Same native-binary path as trial sessions; the SDK's default resolution
  // fails with the pinned CLI ("Copilot CLI not found at …\@github\index.js").
  const cliPath = resolveCopilotCliPath();
  const client = new CopilotClient({
    logLevel: "error",
    ...(cliPath ? { env: { ...process.env, COPILOT_CLI_PATH: cliPath } as Record<string, string> } : {}),
  });

  try {
    await client.start();
    const models = await client.listModels();

    return models
      .filter((model: ModelInfo) => model.policy?.state !== "disabled")
      .map((model: ModelInfo) => ({ id: model.id, name: model.name }));
  } finally {
    await client.stop();
  }
}
