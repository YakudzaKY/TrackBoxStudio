import json
import os
import shutil
import subprocess
import sys
import tempfile
from collections import deque
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
import torch
from iopaint.model_manager import ModelManager
from iopaint.schema import HDStrategy, LDMSampler, InpaintRequest as Config


WHITE_EXPANSION_THRESHOLD = 0.68
WHITE_EXPANSION_RADIUS = 8
LIGHT_REPLACE_START = 0.32
LIGHT_REPLACE_END = 0.78
MASK_EDGE_FEATHER_SIGMA = 3.0
TEMPORAL_FLOW_MAX_DIMENSION = 960
TEMPORAL_FLOW_FB_MAX_ERROR = 1.5
TEMPORAL_MIN_BLEND_RATIO = 0.05
TEMPORAL_MIN_BLEND_PIXELS = 256
TEMPORAL_RELIABLE_ERODE_RADIUS = 1
TEMPORAL_NEIGHBOR_BLEND_MAX = 0.35
TEMPORAL_BLEND_SIGMA = 3.0
TEMPORAL_SIMILARITY_THRESHOLD = 0.18


@dataclass
class TrackRuntime:
    keyframes: list
    cursor: int = 0
    active: dict | None = None


@dataclass
class ProcessedFrameBundle:
    source_frame: np.ndarray
    processed_frame: np.ndarray
    mask: np.ndarray


@dataclass
class TemporalNeighborGuidance:
    warped_processed_frame: np.ndarray
    confidence_map: np.ndarray


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


def process_image_with_lama(image_bgr: np.ndarray, mask: np.ndarray, model_manager: ModelManager, job: dict) -> np.ndarray:
    # IOPaint expects RGB input and returns a BGR image.
    image_rgb = cv2.cvtColor(image_bgr, cv2.COLOR_BGR2RGB)
    config = Config(
        ldm_steps=max(1, int(job.get("ldmSteps", 100))),
        ldm_sampler=LDMSampler.ddim,
        hd_strategy=HDStrategy.CROP,
        hd_strategy_crop_margin=max(0, int(job.get("cropMargin", 128))),
        hd_strategy_crop_trigger_size=max(128, int(job.get("cropTriggerSize", 800))),
        hd_strategy_resize_limit=max(256, int(job.get("resizeLimit", 2048))),
    )
    result = model_manager(image_rgb, mask, config)
    if result.dtype in [np.float64, np.float32]:
        result = np.clip(result, 0, 255).astype(np.uint8)
    return result


def compute_whiteness_map(frame_bgr: np.ndarray) -> np.ndarray:
    lab = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2LAB).astype(np.float32)
    hsv = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2HSV).astype(np.float32)

    lightness = lab[..., 0] / 255.0
    value = hsv[..., 2] / 255.0
    saturation = hsv[..., 1] / 255.0
    whiteness = (0.65 * lightness + 0.35 * value) * (1.0 - saturation)
    return np.clip(whiteness, 0.0, 1.0)


def build_adaptive_inpaint_mask(frame_bgr: np.ndarray, base_mask: np.ndarray) -> np.ndarray:
    adaptive_mask = base_mask.copy()
    whiteness = compute_whiteness_map(frame_bgr)
    white_core = np.where((adaptive_mask > 0) & (whiteness >= WHITE_EXPANSION_THRESHOLD), 255, 0).astype(np.uint8)

    if cv2.countNonZero(white_core) == 0:
        return adaptive_mask

    kernel_size = WHITE_EXPANSION_RADIUS * 2 + 1
    kernel = np.ones((kernel_size, kernel_size), dtype=np.uint8)
    expanded_white = cv2.dilate(white_core, kernel, iterations=1)
    return cv2.bitwise_or(adaptive_mask, expanded_white)


def composite_masked_result(
    base_frame: np.ndarray,
    processed_frame: np.ndarray,
    mask: np.ndarray,
    alpha_reference_frame: np.ndarray | None = None,
) -> np.ndarray:
    alpha_source = base_frame if alpha_reference_frame is None else alpha_reference_frame
    alpha_map = build_light_replacement_alpha(alpha_source, mask)
    alpha_map = alpha_map[..., None]
    source_float = base_frame.astype(np.float32)
    processed_float = processed_frame.astype(np.float32)
    composited = source_float * (1.0 - alpha_map) + processed_float * alpha_map
    return np.clip(composited, 0, 255).astype(np.uint8)


def build_soft_mask(mask: np.ndarray, sigma: float) -> np.ndarray:
    mask_float = mask.astype(np.float32) / 255.0
    if sigma <= 0:
        return mask_float

    softened = cv2.GaussianBlur(mask_float, (0, 0), sigmaX=sigma, sigmaY=sigma)
    return np.clip(softened, 0.0, 1.0)


def build_light_replacement_alpha(source_frame: np.ndarray, mask: np.ndarray) -> np.ndarray:
    if cv2.countNonZero(mask) == 0:
        return np.zeros(mask.shape, dtype=np.float32)

    whiteness = compute_whiteness_map(source_frame)
    feather = build_soft_mask(mask, MASK_EDGE_FEATHER_SIGMA)
    light_weight = np.clip(
        (whiteness - LIGHT_REPLACE_START) / max(0.001, LIGHT_REPLACE_END - LIGHT_REPLACE_START),
        0.0,
        1.0,
    )
    # Smoothstep keeps the transition soft so mid-tones do not snap.
    light_weight = light_weight * light_weight * (3.0 - 2.0 * light_weight)
    alpha = feather * light_weight
    alpha[mask == 0] = 0.0
    return alpha


def get_flow_working_size(width: int, height: int) -> tuple[int, int, float]:
    longest_side = max(width, height)
    if longest_side <= TEMPORAL_FLOW_MAX_DIMENSION:
        return width, height, 1.0

    scale = TEMPORAL_FLOW_MAX_DIMENSION / longest_side
    scaled_width = max(32, int(round(width * scale)))
    scaled_height = max(32, int(round(height * scale)))
    return scaled_width, scaled_height, scale


def resize_flow_to_frame(flow: np.ndarray, width: int, height: int, scale: float) -> np.ndarray:
    if scale >= 1.0 and flow.shape[1] == width and flow.shape[0] == height:
        return flow.astype(np.float32, copy=False)

    resized = cv2.resize(flow, (width, height), interpolation=cv2.INTER_LINEAR)
    resized = resized.astype(np.float32, copy=False)
    resized[..., 0] /= scale
    resized[..., 1] /= scale
    return resized


def build_flow_maps(flow: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    height, width = flow.shape[:2]
    grid_x, grid_y = np.meshgrid(
        np.arange(width, dtype=np.float32),
        np.arange(height, dtype=np.float32),
    )
    return grid_x + flow[..., 0], grid_y + flow[..., 1]


def calculate_bidirectional_flow(previous_frame: np.ndarray, current_frame: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    height, width = current_frame.shape[:2]
    scaled_width, scaled_height, scale = get_flow_working_size(width, height)

    previous_gray = cv2.cvtColor(previous_frame, cv2.COLOR_BGR2GRAY)
    current_gray = cv2.cvtColor(current_frame, cv2.COLOR_BGR2GRAY)
    if scale < 1.0:
        previous_gray = cv2.resize(previous_gray, (scaled_width, scaled_height), interpolation=cv2.INTER_AREA)
        current_gray = cv2.resize(current_gray, (scaled_width, scaled_height), interpolation=cv2.INTER_AREA)

    flow_settings = dict(
        pyr_scale=0.5,
        levels=4,
        winsize=21,
        iterations=5,
        poly_n=7,
        poly_sigma=1.5,
        flags=cv2.OPTFLOW_FARNEBACK_GAUSSIAN,
    )
    backward_flow_small = cv2.calcOpticalFlowFarneback(current_gray, previous_gray, None, **flow_settings)
    forward_flow_small = cv2.calcOpticalFlowFarneback(previous_gray, current_gray, None, **flow_settings)

    backward_flow = resize_flow_to_frame(backward_flow_small, width, height, scale)
    forward_flow = resize_flow_to_frame(forward_flow_small, width, height, scale)
    return backward_flow, forward_flow


def remap_with_flow(
    image: np.ndarray,
    flow: np.ndarray,
    interpolation: int,
    border_mode: int,
    border_value: int | tuple[int, int, int] = 0,
) -> np.ndarray:
    map_x, map_y = build_flow_maps(flow)
    return cv2.remap(image, map_x, map_y, interpolation=interpolation, borderMode=border_mode, borderValue=border_value)


def build_flow_confidence_mask(backward_flow: np.ndarray, forward_flow: np.ndarray) -> np.ndarray:
    map_x, map_y = build_flow_maps(backward_flow)
    sampled_forward = cv2.remap(
        forward_flow,
        map_x,
        map_y,
        interpolation=cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=0,
    )

    height, width = backward_flow.shape[:2]
    inside = (map_x >= 0.0) & (map_x <= width - 1.0) & (map_y >= 0.0) & (map_y <= height - 1.0)
    fb_error = np.linalg.norm(sampled_forward + backward_flow, axis=2)
    return np.where(inside & (fb_error <= TEMPORAL_FLOW_FB_MAX_ERROR), 255, 0).astype(np.uint8)


def build_neighbor_guidance(
    current_bundle: ProcessedFrameBundle,
    neighbor_bundle: ProcessedFrameBundle | None,
) -> TemporalNeighborGuidance | None:
    if neighbor_bundle is None:
        return None

    if cv2.countNonZero(current_bundle.mask) == 0 or cv2.countNonZero(neighbor_bundle.mask) == 0:
        return None

    backward_flow, forward_flow = calculate_bidirectional_flow(
        neighbor_bundle.source_frame,
        current_bundle.source_frame,
    )
    warped_neighbor_mask = remap_with_flow(
        neighbor_bundle.mask,
        backward_flow,
        interpolation=cv2.INTER_NEAREST,
        border_mode=cv2.BORDER_CONSTANT,
        border_value=0,
    )
    overlap_mask = cv2.bitwise_and(current_bundle.mask, warped_neighbor_mask)
    if cv2.countNonZero(overlap_mask) == 0:
        return None

    flow_confidence = build_flow_confidence_mask(backward_flow, forward_flow)
    reliable_mask = cv2.bitwise_and(overlap_mask, flow_confidence)
    if TEMPORAL_RELIABLE_ERODE_RADIUS > 0 and cv2.countNonZero(reliable_mask) > 0:
        kernel_size = TEMPORAL_RELIABLE_ERODE_RADIUS * 2 + 1
        kernel = np.ones((kernel_size, kernel_size), dtype=np.uint8)
        reliable_mask = cv2.erode(reliable_mask, kernel, iterations=1)

    current_area = cv2.countNonZero(current_bundle.mask)
    reliable_area = cv2.countNonZero(reliable_mask)
    if current_area == 0 or reliable_area < TEMPORAL_MIN_BLEND_PIXELS:
        return None

    if reliable_area / current_area < TEMPORAL_MIN_BLEND_RATIO:
        return None

    warped_neighbor_source = remap_with_flow(
        neighbor_bundle.source_frame,
        backward_flow,
        interpolation=cv2.INTER_LINEAR,
        border_mode=cv2.BORDER_REFLECT101,
    )
    warped_neighbor_processed = remap_with_flow(
        neighbor_bundle.processed_frame,
        backward_flow,
        interpolation=cv2.INTER_LINEAR,
        border_mode=cv2.BORDER_REFLECT101,
    )

    source_difference = cv2.absdiff(current_bundle.source_frame, warped_neighbor_source).astype(np.float32)
    source_difference = np.mean(source_difference, axis=2) / 255.0
    similarity = np.clip(1.0 - (source_difference / TEMPORAL_SIMILARITY_THRESHOLD), 0.0, 1.0)

    confidence_map = build_soft_mask(reliable_mask, TEMPORAL_BLEND_SIGMA)
    confidence_map *= similarity
    confidence_map *= TEMPORAL_NEIGHBOR_BLEND_MAX
    confidence_map[current_bundle.mask == 0] = 0.0

    if float(np.max(confidence_map)) <= 0.01:
        return None

    return TemporalNeighborGuidance(
        warped_processed_frame=warped_neighbor_processed,
        confidence_map=confidence_map,
    )


def blend_frames(base_frame: np.ndarray, overlay_frame: np.ndarray, alpha_map: np.ndarray) -> np.ndarray:
    if alpha_map.ndim == 2:
        alpha_map = alpha_map[..., None]

    base_float = base_frame.astype(np.float32)
    overlay_float = overlay_frame.astype(np.float32)
    blended = base_float * (1.0 - alpha_map) + overlay_float * alpha_map
    return np.clip(blended, 0, 255).astype(np.uint8)


def temporally_stabilize_bundle(
    current_bundle: ProcessedFrameBundle,
    previous_bundle: ProcessedFrameBundle | None,
    next_bundle: ProcessedFrameBundle | None,
) -> ProcessedFrameBundle:
    if cv2.countNonZero(current_bundle.mask) == 0:
        return current_bundle

    blended_frame = current_bundle.processed_frame.copy()
    accumulated = current_bundle.processed_frame.astype(np.float32)
    total_weight = np.ones(current_bundle.mask.shape, dtype=np.float32)

    for neighbor_bundle in (previous_bundle, next_bundle):
        guidance = build_neighbor_guidance(current_bundle, neighbor_bundle)
        if guidance is None:
            continue

        accumulated += guidance.warped_processed_frame.astype(np.float32) * guidance.confidence_map[..., None]
        total_weight += guidance.confidence_map

    blended_frame = np.clip(accumulated / total_weight[..., None], 0, 255).astype(np.uint8)
    blended_frame = composite_masked_result(
        current_bundle.processed_frame,
        blended_frame,
        current_bundle.mask,
        alpha_reference_frame=current_bundle.source_frame,
    )
    return ProcessedFrameBundle(
        source_frame=current_bundle.source_frame,
        processed_frame=blended_frame,
        mask=current_bundle.mask,
    )


def process_frame_bundle(
    frame: np.ndarray,
    frame_index: int,
    runtimes: list[TrackRuntime],
    model_manager: ModelManager,
    job: dict,
    mask_padding: int,
) -> ProcessedFrameBundle:
    mask = build_mask(frame.shape, frame_index, runtimes, mask_padding)
    if cv2.countNonZero(mask) == 0:
        return ProcessedFrameBundle(
            source_frame=frame.copy(),
            processed_frame=frame.copy(),
            mask=mask,
        )

    adaptive_mask = build_adaptive_inpaint_mask(frame, mask)
    inpainted_frame = process_image_with_lama(frame, adaptive_mask, model_manager, job)
    processed_frame = composite_masked_result(frame, inpainted_frame, adaptive_mask)
    return ProcessedFrameBundle(
        source_frame=frame.copy(),
        processed_frame=processed_frame,
        mask=adaptive_mask,
    )


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


def process_image(input_path: str, output_path: str, runtimes: list[TrackRuntime], model_manager: ModelManager, job: dict, mask_padding: int) -> None:
    emit_status("Processing image with LaMa (Max quality)...")
    image_bgr = cv2.imread(input_path, cv2.IMREAD_COLOR)
    if image_bgr is None or image_bgr.size == 0:
        raise RuntimeError(f"Failed to read image: {input_path}")

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    result_bundle = process_frame_bundle(image_bgr, 0, runtimes, model_manager, job, mask_padding)
    cv2.imwrite(output_path, result_bundle.processed_frame)
    emit_progress(1.0)


def process_video(input_path: str, output_path: str, runtimes: list[TrackRuntime], model_manager: ModelManager, job: dict, mask_padding: int) -> None:
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

    frame_queue: deque[ProcessedFrameBundle] = deque()
    previous_output_bundle: ProcessedFrameBundle | None = None

    try:
        progress_stride = max(1, total_frames // 200)
        emit_status("Applying LaMa inpainting to video frames (Max quality)...")
        frame_index = 0
        written_frames = 0

        while True:
            ok, frame = capture.read()
            if not ok or frame is None or frame.size == 0:
                break

            frame_queue.append(process_frame_bundle(frame, frame_index, runtimes, model_manager, job, mask_padding))
            frame_index += 1

            while len(frame_queue) >= 2:
                current_bundle = frame_queue[0]
                next_bundle = frame_queue[1]
                output_bundle = temporally_stabilize_bundle(current_bundle, previous_output_bundle, next_bundle)
                writer.write(output_bundle.processed_frame)
                previous_output_bundle = output_bundle
                frame_queue.popleft()
                written_frames += 1
                if written_frames == total_frames or written_frames % progress_stride == 0:
                    emit_progress(written_frames / total_frames)

        while frame_queue:
            current_bundle = frame_queue[0]
            next_bundle = frame_queue[1] if len(frame_queue) > 1 else None
            output_bundle = temporally_stabilize_bundle(current_bundle, previous_output_bundle, next_bundle)
            writer.write(output_bundle.processed_frame)
            previous_output_bundle = output_bundle
            frame_queue.popleft()
            written_frames += 1
            if written_frames == total_frames or written_frames % progress_stride == 0:
                emit_progress(written_frames / total_frames)

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
    quality_preset = str(job.get("qualityPreset", "max")).strip() or "max"

    emit_status(f"Loading LaMa model on {device} ({quality_preset} quality)...")
    model_manager = load_lama_model(device)
    emit_status("LaMa model loaded.")

    runtimes = create_runtime_tracks(tracks)
    suffix = Path(input_path).suffix.lower()
    if suffix in {".mp4", ".avi", ".mov", ".mkv", ".flv", ".wmv", ".webm"}:
        process_video(input_path, output_path, runtimes, model_manager, job, mask_padding)
    else:
        process_image(input_path, output_path, runtimes, model_manager, job, mask_padding)

    emit_status("LaMa processing finished.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"ERROR {exc}", file=sys.stderr, flush=True)
        raise
