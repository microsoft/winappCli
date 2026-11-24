import argparse
import asyncio
from pathlib import Path

from dynwinrt import RoApartment, has_package_identity
from winapp_bindings.microsoft.graphics.imaging import ImageBuffer
from winapp_bindings.microsoft.windows.ai import AIFeatureReadyState
from winapp_bindings.microsoft.windows.ai.imaging import TextRecognizer
from winapp_bindings.windows.graphics.imaging import (
    BitmapAlphaMode,
    BitmapDecoder,
    BitmapPixelFormat,
)
from winapp_bindings.windows.storage.streams import DataWriter, InMemoryRandomAccessStream


async def recognize(path: Path) -> str:
    if not has_package_identity():
        raise RuntimeError("Run this example with winapp-py run --identity")
    image = await asyncio.to_thread(path.read_bytes)
    with RoApartment():
        state = TextRecognizer.get_ready_state()
        if state != AIFeatureReadyState.Ready:
            raise RuntimeError(f"Windows AI OCR model is not ready: {state.name}")

        with InMemoryRandomAccessStream() as stream:
            with DataWriter(stream) as writer:
                writer.write_bytes(image)
                await writer.store_async()
                writer.detach_stream()
            stream.seek(0)

            decoder = await BitmapDecoder.create_async(stream)
            bitmap = await decoder.get_software_bitmap_async(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore
            )
            with bitmap, ImageBuffer.create_for_software_bitmap(bitmap) as buffer:
                with await TextRecognizer.create_async() as recognizer:
                    result = await recognizer.recognize_text_from_image_async(buffer)

                    lines: list[str] = []
                    for line in result.lines:
                        if line is None:
                            raise RuntimeError("Windows AI OCR returned a null line")
                        lines.append(line.text)
                    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description="Recognize image text with Windows AI")
    parser.add_argument("image", type=Path)
    args = parser.parse_args()
    print(asyncio.run(recognize(args.image)))


if __name__ == "__main__":
    main()
