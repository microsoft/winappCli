// A stand-in for @github/copilot-sdk/extension. It records what the extension declares and every prompt it
// sends to the hosting agent, so tests can open the canvas, call tools, and check prompts without a CLI.
export const host = {
    config: null,
    sends: [],
    logs: [],
    sendError: null, // set to an Error to make session.send fail
};
globalThis.__prBoardHost = host;

export class CanvasError extends Error {
    constructor(code, message) {
        super(message);
        this.code = code;
    }
}

export function createCanvas(decl) {
    return decl;
}

export async function joinSession(config) {
    host.config = config;
    return {
        sessionId: "test-session",
        workspacePath: undefined,
        async send({ prompt }) {
            if (host.sendError) throw host.sendError;
            host.sends.push({ prompt, at: Date.now() });
        },
        async sendAndWait(o) { return this.send(o); },
        log(message, opts) { host.logs.push({ message, ...opts }); },
        on() { return () => {}; },
        rpc: { management: { list: async () => ({ sessions: [] }) } },
    };
}
