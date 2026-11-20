import asyncio

from dynwinrt import RoApartment, has_package_identity
from winapp_bindings import (
    AIFeatureReadyResultState,
    AIFeatureReadyState,
    LanguageModel,
    LanguageModelResponseStatus,
    TextRewriter,
    TextRewriteTone,
)


TEXT = "WinApp CLI makes it easier to use Windows APIs from Python."


async def rewrite(text: str) -> str:
    if not has_package_identity():
        raise RuntimeError("Run this example with winapp-py run --identity")
    with RoApartment():
        state = LanguageModel.get_ready_state()
        if state not in (AIFeatureReadyState.Ready, AIFeatureReadyState.NotReady):
            raise RuntimeError(f"Windows AI language model is not ready: {state.name}")
        if state == AIFeatureReadyState.NotReady:
            ready = await LanguageModel.ensure_ready_async()
            if ready.status != AIFeatureReadyResultState.Success:
                raise RuntimeError(f"Language model preparation failed: {ready.status.name}")

        with await LanguageModel.create_async() as model:
            rewriter = TextRewriter.create_instance(model)
            try:
                result = await rewriter.rewrite_async(text, TextRewriteTone.Formal)
                if result.status != LanguageModelResponseStatus.Complete:
                    raise RuntimeError(f"Text rewriting failed: {result.status.name}")
                if not result.text.strip():
                    raise RuntimeError("Windows AI returned empty rewritten text")
                return result.text
            finally:
                del rewriter


def main() -> None:
    print(asyncio.run(rewrite(TEXT)))


if __name__ == "__main__":
    main()
