import json
import os
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
import torch
from iopaint.model_manager import ModelManager
from iopaint.schema import HDStrategy, LDMSampler, InpaintRequest as Config


@dataclass
class TrackRuntime:
    keyframes: list
    cursor: int = 0
    active: dict | None = None


def emit_status(message: str) -> None:
    print(f"STATUS {message}", flush=True)


def emit_progress(value: float) -> None:
    normalized = max(0.0, min(1.0, value))
    print(f"PROGRESS {normalized:.6f}", flush=True)


def download_lama_model() -> bool:
    emit_status("Downloading LaMa model...")
    result = subprocess.run(
        [sys.executable, "-m", "iopaint", "download", "--model", "lama"],
        capture_output=False,
        text=True,
    )
    return result.returncode == 0


def load_lama_model(device: str) -> ModelManager:
    try:
        return ModelManager(name="lama", device=device)
    except NotImplementedError as exc:
        if "Unsupported model: lama" not in str(exc):
            raise
        if not download_lama_model():
            raise RuntimeError("Failed to download LaMa model.")
        import importlib
        import iopaint.model

        importlib.reload(iopaint.model)
        return ModelManager(name="lama", device=device)


def resolve_device(preference: str) -> str:
    preference = (preference or "cuda-preferred").strip().lower()
    if preference == "cpu":
        return "cpu"
    if preference == "cuda":
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA was requested for LaMa processing, but CUDA is not available.")
        return "cuda"
    if preference in {"cuda-preferred", "auto"}:
        return "cuda" if torch.cuda.is_available() else "cpu"
    raise RuntimeError(f"Unsupported device preference: {preference}")


def create_runtime_tracks(track_documents: list[dict]) -> list[TrackRuntime]:
    runtimes: list[TrackRuntime] = []
    for track in track_documents:
        ordered = sorted(track.get("keyframes", []), key=lambda item: item.get("frame", 0))
        runtimes.append(TrackRuntime(keyframes=ordered))
    return runtimes


def advance_runtime(runtime: TrackRuntime, frame_index: int) -> dict | None:
    while runtime.cursor < len(runtime.keyframes) and runtime.keyframes[runtime.cursor].get("frame", 0) <= frame_index:
        runtime.active = runtime.keyframes[runtime.cursor]
        runtime.cursor += 1
    return runtime.active


def normalize_rect(box: dict, width: int, height: int) -> tuple[int, int, int, int]:
    x = max(0, min(int(box.get("x", 0)), max(0, width - 1)))
    y = max(0, min(int(box.get("y", 0)), max(0, height - 1)))
    w = max(0, min(int(box.get("width", 0)), width - x))
    h = max(0, min(int(box.get("height", 0)), height - y))
    return x, y, w, h


def build_mask(frame_shape: tuple[int, int, int], frame_index: int, runtimes: list[TrackRuntime], mask_padding: int) -> np.ndarray:
    height, width = frame_shape[:2]
    mask = np.zeros((height, width), dtype=np.uint8)

    for runtime in runtimes:
        active = advance_runtime(runtime, frame_index)
        if not active or not active.get("enabled") or not active.get("box"):
            continue

        x, y, w, h = normalize_rect(active["box"], width, height)
        if w <= 0 or h <= 0:
            continue

        if mask_padding > 0:
            x = max(0, x - mask_padding)
            y = max(0, y - mask_padding)
            w = min(width - x, w + mask_padding * 2)
            h = min(height - y, h + mask_padding * 2)

        cv2.rectangle(mask, (x, y), (x + w, y + h), 255, -1)

    return mask


def process_image_with_lama(image: np.ndarray, mask: np.ndarray, model_manager: ModelManager) -> np.ndarray:
    config = Config(
        ldm_steps=50,
        ldm_sampler=LDMSampler.ddim,
        hd_strategy=HDStrategy.CROP,
        hd_strategy_crop_margin=64,
        hd_strategy_crop_trigger_size=800,
        hd_strategy_resize_limit=1600,
    )
    result = model_manager(image, mask, config)
    if result.dtype in [np.float64, np.float32]:
        result = np.clip(result, 0, 255).astype(np.uint8)
    return result


def resolve_ffmpeg() -> str | None:
    return shutil.which("ffmpeg.exe") or shutil.which("ffmpeg")


def move_temp_to_output(temp_video_path: str, output_path: str) -> None:
    if os.path.exists(output_path):
        os.remove(output_path)
    shutil.move(temp_video_path, output_path)


def copy_audio_if_possible(input_path: str, output_path: str, temp_video_path: str) -> None:
    ffmpeg = resolve_ffmpeg()
    if ffmpeg is None:
        move_temp_to_output(temp_video_path, output_path)
        return

    result = subprocess.run(
        [
            ffmpeg,
            "-y",
            "-i",
            temp_video_path,
            "-i",
            input_path,
            "-map",
            "0:v:0",
            "-map",
            "1:a?",
            "-c:v",
            "copy",
            "-c:a",
            "aac",
            "-shortest",
            output_path,
        ],
        capture_output=True,
        text=True,
    )

    if result.returncode == 0 and os.path.exists(output_path):
        os.remove(temp_video_path)
        return

    move_temp_to_output(temp_video_path, output_path)


def process_image(input_path: str, output_path: str, runtimes: list[TrackRuntime], model_manager: ModelManager, mask_padding: int) -> None:
    emit_status("Processing image with LaMa...")
    image = cv2.imread(input_path, cv2.IMREAD_COLOR)
    if image is None or image.size == 0:
        raise RuntimeError(f"Failed to read image: {input_path}")

    mask = build_mask(image.shape, 0, runtimes, mask_padding)
    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)

    if cv2.countNonZero(mask) == 0:
        cv2.imwrite(output_path, image)
        emit_progress(1.0)
        return

    result = process_image_with_lama(image, mask, model_manager)
    cv2.imwrite(output_path, result)
    emit_progress(1.0)


def process_video(input_path: str, output_path: str, runtimes: list[TrackRuntime], model_manager: ModelManager, mask_padding: int) -> None:
    emit_status("Opening video...")
    capture = cv2.VideoCapture(input_path)
    if not capture.isOpened():
        raise RuntimeError(f"Failed to open video: {input_path}")

    total_frames = max(1, int(capture.get(cv2.CAP_PROP_FRAME_COUNT)))
    fps = capture.get(cv2.CAP_PROP_FPS)
    width = int(capture.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT))

    output_directory = os.path.dirname(output_path) or "."
    os.makedirs(output_directory, exist_ok=True)

    temp_directory = tempfile.mkdtemp(prefix="trackbox-lama-")
    temp_video_path = os.path.join(temp_directory, "video-temp.mp4")
    writer = cv2.VideoWriter(
        temp_video_path,
        cv2.VideoWriter_fourcc(*"mp4v"),
        fps if fps > 0 else 30.0,
        (width, height),
    )
    if not writer.isOpened():
        capture.release()
        shutil.rmtree(temp_directory, ignore_errors=True)
        raise RuntimeError(f"Failed to create output video: {temp_video_path}")

    try:
        progress_stride = max(1, total_frames // 200)
        emit_status("Applying LaMa inpainting to video frames...")
        frame_index = 0
        while True:
            ok, frame = capture.read()
            if not ok or frame is None or frame.size == 0:
                break

            mask = build_mask(frame.shape, frame_index, runtimes, mask_padding)
            if cv2.countNonZero(mask) == 0:
                writer.write(frame)
            else:
                result = process_image_with_lama(frame, mask, model_manager)
                writer.write(result)

            frame_index += 1
            if frame_index == total_frames or frame_index % progress_stride == 0:
                emit_progress(frame_index / total_frames)

        writer.release()
        capture.release()
        copy_audio_if_possible(input_path, output_path, temp_video_path)
        shutil.rmtree(temp_directory, ignore_errors=True)
        emit_progress(1.0)
    except Exception:
        writer.release()
        capture.release()
        shutil.rmtree(temp_directory, ignore_errors=True)
        raise


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: lama_inpaint_runner.py <job.json>", file=sys.stderr)
        return 2

    job_path = Path(sys.argv[1])
    with job_path.open("r", encoding="utf-8") as handle:
        job = json.load(handle)

    input_path = job["inputPath"]
    output_path = job["outputPath"]
    tracks = job.get("tracks", [])
    mask_padding = max(0, int(job.get("maskPadding", 0)))
    device = resolve_device(job.get("devicePreference", "cuda-preferred"))

    emit_status(f"Loading LaMa model on {device}...")
    model_manager = load_lama_model(device)
    emit_status("LaMa model loaded.")

    runtimes = create_runtime_tracks(tracks)
    suffix = Path(input_path).suffix.lower()
    if suffix in {".mp4", ".avi", ".mov", ".mkv", ".flv", ".wmv", ".webm"}:
        process_video(input_path, output_path, runtimes, model_manager, mask_padding)
    else:
        process_image(input_path, output_path, runtimes, model_manager, mask_padding)

    emit_status("LaMa processing finished.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"ERROR {exc}", file=sys.stderr, flush=True)
        raise
