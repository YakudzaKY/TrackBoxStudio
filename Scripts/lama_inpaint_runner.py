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


def resolve_coverage_config_path() -> Path:
    override = os.environ.get("TRACKBOX_COVERAGE_CONFIG_PATH", "").strip()
    if override:
        return Path(override)

    return Path(__file__).resolve().parent.parent / "Data" / "lama-coverage-config.json"


def merge_coverage_config(defaults: dict[str, int | float]) -> dict[str, int | float]:
    config_path = resolve_coverage_config_path()
    merged = defaults.copy()
    if not config_path.exists():
        return merged

    try:
        with config_path.open("r", encoding="utf-8") as handle:
            loaded = json.load(handle)
    except (OSError, ValueError, TypeError):
        return merged

    if not isinstance(loaded, dict):
        return merged

    for key, default_value in defaults.items():
        if key not in loaded:
            continue

        raw_value = loaded[key]
        try:
            if isinstance(default_value, int) and not isinstance(default_value, bool):
                merged[key] = int(raw_value)
            else:
                merged[key] = float(raw_value)
        except (TypeError, ValueError):
            merged[key] = default_value

    return merged


DEFAULT_COVERAGE_CONFIG = {
    # Lower = dimmer white-ish pixels start contributing to the stable mask.
    "mask_min_whiteness": 0.43,
    # Lower = darker but still bright watermark pixels are allowed into the stable mask.
    "mask_min_luminance": 0.50,
    # Lower = more frames are treated as outliers when the crop changes too much.
    "stable_frame_delta_threshold": 0.12,
    # If too few frames survive the delta filter, keep at least this fraction of the calmest ones.
    "stable_frame_keep_ratio": 0.45,
    # Lower = a pixel only needs to appear in fewer stable frames to enter the final mask.
    "stable_mask_presence_ratio": 0.35,
    # Morphological close to connect tiny gaps inside the stable mask.
    "mask_close_radius": 2,
    # Expand the final stable mask by this many pixels before inpaint.
    "mask_expand_radius": 6,
    # Remove very tiny islands before and after expansion.
    "mask_min_component_area": 24,
    # 0 disables temporal cross-frame blend; 1 enables forward blend from segment start.
    "temporal_blend_enabled": 1,
    # Blend weight at segment start (0..1).
    "temporal_blend_edge_strength": 0.26,
    # Exponent for start-to-end decay curve (>= 0.1).
    "temporal_blend_falloff_power": 1.35,
}

COVERAGE_CONFIG = merge_coverage_config(DEFAULT_COVERAGE_CONFIG)

MASK_MIN_WHITENESS = COVERAGE_CONFIG["mask_min_whiteness"]
MASK_MIN_LUMINANCE = COVERAGE_CONFIG["mask_min_luminance"]
STABLE_FRAME_DELTA_THRESHOLD = COVERAGE_CONFIG["stable_frame_delta_threshold"]
STABLE_FRAME_KEEP_RATIO = COVERAGE_CONFIG["stable_frame_keep_ratio"]
STABLE_MASK_PRESENCE_RATIO = COVERAGE_CONFIG["stable_mask_presence_ratio"]
MASK_CLOSE_RADIUS = COVERAGE_CONFIG["mask_close_radius"]
MASK_EXPAND_RADIUS = COVERAGE_CONFIG["mask_expand_radius"]
MASK_MIN_COMPONENT_AREA = COVERAGE_CONFIG["mask_min_component_area"]
TEMPORAL_BLEND_ENABLED = int(COVERAGE_CONFIG["temporal_blend_enabled"]) > 0
TEMPORAL_BLEND_EDGE_STRENGTH = COVERAGE_CONFIG["temporal_blend_edge_strength"]
TEMPORAL_BLEND_FALLOFF_POWER = COVERAGE_CONFIG["temporal_blend_falloff_power"]

VIDEO_EXTENSIONS = {".mp4", ".avi", ".mov", ".mkv", ".flv", ".wmv", ".webm"}
DISCORD_SAFE_VIDEO_CRF = 14
DISCORD_SAFE_VIDEO_PRESET = "slow"
DISCORD_SAFE_AUDIO_BITRATE = "192k"


@dataclass
class BoxSegment:
    track_id: str
    start_frame: int
    end_frame: int
    rect: tuple[int, int, int, int]


@dataclass
class SegmentMaskPlan:
    start_frame: int
    end_frame: int
    rect: tuple[int, int, int, int]
    local_mask: np.ndarray


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


def normalize_rect(box: dict, width: int, height: int) -> tuple[int, int, int, int]:
    x = max(0, min(int(box.get("x", 0)), max(0, width - 1)))
    y = max(0, min(int(box.get("y", 0)), max(0, height - 1)))
    w = max(0, min(int(box.get("width", 0)), width - x))
    h = max(0, min(int(box.get("height", 0)), height - y))
    return x, y, w, h


def expand_rect(rect: tuple[int, int, int, int], padding: int, width: int, height: int) -> tuple[int, int, int, int]:
    x, y, w, h = rect
    if padding <= 0:
        return x, y, w, h

    left = max(0, x - padding)
    top = max(0, y - padding)
    right = min(width, x + w + padding)
    bottom = min(height, y + h + padding)
    return left, top, max(0, right - left), max(0, bottom - top)


def merge_adjacent_segments(segments: list[BoxSegment]) -> list[BoxSegment]:
    if not segments:
        return []

    ordered = sorted(segments, key=lambda segment: (segment.track_id, segment.start_frame, segment.rect))
    merged = [ordered[0]]

    for segment in ordered[1:]:
        previous = merged[-1]
        if (
            previous.track_id == segment.track_id
            and previous.rect == segment.rect
            and segment.start_frame <= previous.end_frame + 1
        ):
            previous.end_frame = max(previous.end_frame, segment.end_frame)
            continue

        merged.append(segment)

    return merged


def extract_box_segments(
    tracks: list[dict],
    frame_count: int,
    width: int,
    height: int,
    mask_padding: int,
) -> list[BoxSegment]:
    segments: list[BoxSegment] = []

    for track in tracks:
        ordered_keyframes = sorted(track.get("keyframes", []), key=lambda item: item.get("frame", 0))
        for index, keyframe in enumerate(ordered_keyframes):
            if not keyframe.get("enabled") or not keyframe.get("box"):
                continue

            start_frame = max(0, min(int(keyframe.get("frame", 0)), max(0, frame_count - 1)))
            next_frame = frame_count
            if index < len(ordered_keyframes) - 1:
                next_frame = max(0, min(int(ordered_keyframes[index + 1].get("frame", frame_count)), frame_count))

            end_frame = min(frame_count - 1, max(start_frame, next_frame - 1))
            rect = normalize_rect(keyframe["box"], width, height)
            rect = expand_rect(rect, mask_padding, width, height)
            if rect[2] <= 0 or rect[3] <= 0:
                continue

            segments.append(
                BoxSegment(
                    track_id=str(track.get("id", "")),
                    start_frame=start_frame,
                    end_frame=end_frame,
                    rect=rect,
                )
            )

    return merge_adjacent_segments(segments)


def process_image_with_lama(image_bgr: np.ndarray, mask: np.ndarray, model_manager: ModelManager, job: dict) -> np.ndarray:
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
    if result.dtype in {np.float64, np.float32}:
        result = np.clip(result, 0, 255).astype(np.uint8)
    return result


def compute_whiteness_map(frame_bgr: np.ndarray) -> np.ndarray:
    lab = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2LAB).astype(np.float32)
    hsv = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2HSV).astype(np.float32)
    lightness = lab[..., 0] / 255.0
    value = hsv[..., 2] / 255.0
    saturation = hsv[..., 1] / 255.0
    whiteness = (0.65 * lightness + 0.35 * value) * (1.0 - saturation)
    whiteness = cv2.GaussianBlur(whiteness, (0, 0), sigmaX=1.0, sigmaY=1.0)
    return np.clip(whiteness, 0.0, 1.0)


def compute_luminance_map(frame_bgr: np.ndarray) -> np.ndarray:
    lab = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2LAB).astype(np.float32)
    luminance = lab[..., 0] / 255.0
    luminance = cv2.GaussianBlur(luminance, (0, 0), sigmaX=1.0, sigmaY=1.0)
    return np.clip(luminance, 0.0, 1.0)


def build_candidate_mask(crop_bgr: np.ndarray) -> np.ndarray:
    whiteness = compute_whiteness_map(crop_bgr)
    luminance = compute_luminance_map(crop_bgr)
    candidate = (whiteness >= MASK_MIN_WHITENESS) & (luminance >= MASK_MIN_LUMINANCE)
    return np.where(candidate, 255, 0).astype(np.uint8)


def remove_small_components(mask: np.ndarray, min_area: int) -> np.ndarray:
    if min_area <= 1 or cv2.countNonZero(mask) == 0:
        return mask

    component_count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
    filtered = np.zeros_like(mask)
    for component_index in range(1, component_count):
        if stats[component_index, cv2.CC_STAT_AREA] >= min_area:
            filtered[labels == component_index] = 255
    return filtered


def refine_local_mask(mask: np.ndarray) -> np.ndarray:
    refined = remove_small_components(mask.astype(np.uint8, copy=False), MASK_MIN_COMPONENT_AREA)
    if MASK_CLOSE_RADIUS > 0 and cv2.countNonZero(refined) > 0:
        kernel_size = MASK_CLOSE_RADIUS * 2 + 1
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (kernel_size, kernel_size))
        refined = cv2.morphologyEx(refined, cv2.MORPH_CLOSE, kernel)
    refined = remove_small_components(refined, MASK_MIN_COMPONENT_AREA)
    if MASK_EXPAND_RADIUS > 0 and cv2.countNonZero(refined) > 0:
        kernel_size = MASK_EXPAND_RADIUS * 2 + 1
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (kernel_size, kernel_size))
        refined = cv2.dilate(refined, kernel, iterations=1)
    return remove_small_components(refined, MASK_MIN_COMPONENT_AREA)


def temp_array_path(directory: str, frame_index: int) -> str:
    return os.path.join(directory, f"{frame_index:08d}.npy")


def save_temp_array(directory: str, frame_index: int, array: np.ndarray) -> None:
    np.save(temp_array_path(directory, frame_index), array, allow_pickle=False)


def load_temp_array(directory: str, frame_index: int) -> np.ndarray:
    return np.load(temp_array_path(directory, frame_index), allow_pickle=False)


def build_segment_stable_local_mask(segment: BoxSegment, source_directory: str) -> np.ndarray:
    x, y, w, h = segment.rect
    candidate_masks: list[np.ndarray] = []
    luminance_crops: list[np.ndarray] = []

    for frame_index in range(segment.start_frame, segment.end_frame + 1):
        frame = load_temp_array(source_directory, frame_index).astype(np.uint8, copy=False)
        crop = frame[y : y + h, x : x + w]
        if crop.size == 0:
            continue
        candidate_masks.append(build_candidate_mask(crop))
        luminance_crops.append(compute_luminance_map(crop))

    if not candidate_masks:
        return np.zeros((h, w), dtype=np.uint8)

    if len(candidate_masks) == 1:
        return refine_local_mask(candidate_masks[0])

    luminance_stack = np.stack(luminance_crops, axis=0).astype(np.float32, copy=False)
    median_luminance = np.median(luminance_stack, axis=0)
    frame_deltas = np.mean(np.abs(luminance_stack - median_luminance[None, ...]), axis=(1, 2))

    keep_count = max(1, min(len(candidate_masks), int(np.ceil(len(candidate_masks) * STABLE_FRAME_KEEP_RATIO))))
    stable_indices = np.flatnonzero(frame_deltas <= STABLE_FRAME_DELTA_THRESHOLD)
    if stable_indices.size < keep_count:
        stable_indices = np.argsort(frame_deltas)[:keep_count]

    candidate_stack = np.stack(candidate_masks, axis=0) > 0
    stable_candidate_stack = candidate_stack[stable_indices]
    presence_map = np.mean(stable_candidate_stack.astype(np.float32), axis=0)
    stable_mask = np.where(presence_map >= STABLE_MASK_PRESENCE_RATIO, 255, 0).astype(np.uint8)

    if cv2.countNonZero(stable_mask) == 0:
        best_frame_index = int(np.argmin(frame_deltas))
        stable_mask = candidate_masks[best_frame_index]

    return refine_local_mask(stable_mask)


def build_segment_mask_plans(segments: list[BoxSegment], source_directory: str) -> list[SegmentMaskPlan]:
    plans: list[SegmentMaskPlan] = []
    total_segments = max(1, len(segments))

    emit_status("Building stable watermark masks for enabled box ranges...")
    for segment_index, segment in enumerate(segments, start=1):
        local_mask = build_segment_stable_local_mask(segment, source_directory)
        if cv2.countNonZero(local_mask) == 0:
            emit_status(
                f"Segment {segment_index}/{total_segments}: no stable watermark mask found, skipping range "
                f"{segment.start_frame}-{segment.end_frame}."
            )
        else:
            plans.append(
                SegmentMaskPlan(
                    start_frame=segment.start_frame,
                    end_frame=segment.end_frame,
                    rect=segment.rect,
                    local_mask=local_mask,
                )
            )
        emit_progress(0.25 + 0.30 * (segment_index / total_segments))

    return plans


def build_plans_by_frame(frame_count: int, plans: list[SegmentMaskPlan]) -> list[list[SegmentMaskPlan]]:
    plans_by_frame: list[list[SegmentMaskPlan]] = [[] for _ in range(frame_count)]
    for plan in plans:
        for frame_index in range(plan.start_frame, plan.end_frame + 1):
            plans_by_frame[frame_index].append(plan)
    return plans_by_frame


def compose_frame_mask(frame_shape: tuple[int, int, int], plans: list[SegmentMaskPlan]) -> np.ndarray:
    height, width = frame_shape[:2]
    mask = np.zeros((height, width), dtype=np.uint8)
    for plan in plans:
        x, y, w, h = plan.rect
        if w <= 0 or h <= 0:
            continue
        target = mask[y : y + h, x : x + w]
        np.maximum(target, plan.local_mask, out=target)
    return mask


def render_mask_overlay(frame_bgr: np.ndarray, mask: np.ndarray) -> np.ndarray:
    if mask.size == 0 or cv2.countNonZero(mask) == 0:
        return frame_bgr.copy()

    alpha_map = (mask.astype(np.float32) / 255.0) * 0.42
    if np.max(alpha_map) <= 0.0:
        return frame_bgr.copy()

    alpha_map = alpha_map[..., None]
    tint = np.zeros_like(frame_bgr)
    tint[..., 0] = 48
    tint[..., 1] = 220
    tint[..., 2] = 96

    blended = frame_bgr.astype(np.float32) * (1.0 - alpha_map) + tint.astype(np.float32) * alpha_map
    result = np.clip(blended, 0.0, 255.0).astype(np.uint8)

    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if contours:
        cv2.drawContours(result, contours, -1, (96, 255, 160), 1, lineType=cv2.LINE_AA)

    return result


def blend_frame_region_with_anchor(
    frame_bgr: np.ndarray,
    anchor_bgr: np.ndarray,
    plan: SegmentMaskPlan,
    blend_alpha: float,
) -> np.ndarray:
    if blend_alpha <= 0.0:
        return frame_bgr

    x, y, w, h = plan.rect
    if w <= 0 or h <= 0:
        return frame_bgr

    local_mask = plan.local_mask
    if local_mask.size == 0 or cv2.countNonZero(local_mask) == 0:
        return frame_bgr

    frame_crop = frame_bgr[y : y + h, x : x + w]
    anchor_crop = anchor_bgr[y : y + h, x : x + w]
    if frame_crop.shape != anchor_crop.shape or frame_crop.shape[:2] != local_mask.shape[:2]:
        return frame_bgr

    alpha_map = (local_mask.astype(np.float32) / 255.0) * float(blend_alpha)
    if np.max(alpha_map) <= 0.0:
        return frame_bgr

    alpha_map = alpha_map[..., None]
    blended_crop = frame_crop.astype(np.float32) * (1.0 - alpha_map) + anchor_crop.astype(np.float32) * alpha_map
    frame_crop[...] = np.clip(blended_crop, 0.0, 255.0).astype(np.uint8)
    return frame_bgr


def compute_temporal_blend_alpha(frame_index: int, start_frame: int, end_frame: int) -> float:
    span = max(1, end_frame - start_frame + 1)
    if span <= 1:
        return max(0.0, min(1.0, float(TEMPORAL_BLEND_EDGE_STRENGTH)))

    progress_from_start = max(0.0, min(1.0, (frame_index - start_frame) / max(1, span - 1)))
    decay = (1.0 - progress_from_start) ** max(0.1, float(TEMPORAL_BLEND_FALLOFF_POWER))
    return max(0.0, min(1.0, float(TEMPORAL_BLEND_EDGE_STRENGTH) * decay))


def apply_temporal_segment_blend(
    frame_count: int,
    source_directory: str,
    processed_directory: str,
    segment_plans: list[SegmentMaskPlan],
) -> None:
    if not TEMPORAL_BLEND_ENABLED or not segment_plans:
        return

    emit_status("Applying start-anchored temporal blend for inpainted ranges...")
    total_plans = max(1, len(segment_plans))
    progress_stride = max(1, total_plans // 60)

    for plan_index, plan in enumerate(segment_plans, start=1):
        start = max(0, plan.start_frame)
        end = min(frame_count - 1, plan.end_frame)
        if start > end:
            continue

        if start <= 0:
            continue

        carry_frame = load_temp_array(processed_directory, start - 1).astype(np.uint8, copy=False)
        for frame_index in range(start, end + 1):
            blend_alpha = compute_temporal_blend_alpha(frame_index, start, end)
            if blend_alpha <= 0.0:
                carry_frame = load_temp_array(processed_directory, frame_index).astype(np.uint8, copy=False)
                continue

            frame = load_temp_array(processed_directory, frame_index).astype(np.uint8, copy=False)
            frame = blend_frame_region_with_anchor(frame, carry_frame, plan, blend_alpha)
            save_temp_array(processed_directory, frame_index, frame)
            carry_frame = frame

        if plan_index == total_plans or plan_index % progress_stride == 0:
            emit_progress(0.86 + 0.09 * (plan_index / total_plans))


def resolve_ffmpeg() -> str | None:
    return shutil.which("ffmpeg.exe") or shutil.which("ffmpeg")


def move_temp_to_output(temp_video_path: str, output_path: str) -> None:
    if os.path.exists(output_path):
        os.remove(output_path)
    shutil.move(temp_video_path, output_path)


def encode_processed_video_with_ffmpeg(
    ffmpeg: str,
    input_path: str,
    output_path: str,
    processed_directory: str,
    frame_count: int,
    fps: float,
    width: int,
    height: int,
    preserve_audio: bool,
) -> None:
    command = [
        ffmpeg,
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
        "-f",
        "rawvideo",
        "-vcodec",
        "rawvideo",
        "-pix_fmt",
        "bgr24",
        "-s:v",
        f"{width}x{height}",
        "-r",
        f"{fps:.6f}",
        "-i",
        "-",
    ]

    if preserve_audio:
        command.extend(["-i", input_path])

    command.extend(
        [
            "-map",
            "0:v:0",
        ]
    )

    if preserve_audio:
        command.extend(["-map", "1:a?"])

    if width % 2 != 0 or height % 2 != 0:
        emit_status("Padding output to even dimensions for H.264 / yuv420p compatibility.")
        command.extend(["-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2"])

    command.extend(
        [
            "-c:v",
            "libx264",
            "-preset",
            DISCORD_SAFE_VIDEO_PRESET,
            "-crf",
            str(DISCORD_SAFE_VIDEO_CRF),
            "-profile:v",
            "high",
            "-pix_fmt",
            "yuv420p",
            "-movflags",
            "+faststart",
        ]
    )

    if preserve_audio:
        command.extend(
            [
                "-c:a",
                "aac",
                "-b:a",
                DISCORD_SAFE_AUDIO_BITRATE,
                "-shortest",
            ]
        )
    else:
        command.append("-an")

    command.append(output_path)

    progress_stride = max(1, frame_count // 200)
    process = subprocess.Popen(
        command,
        stdin=subprocess.PIPE,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
    )

    try:
        if process.stdin is None:
            raise RuntimeError("ffmpeg encoder stdin was not available.")

        for frame_index in range(frame_count):
            output_frame = load_temp_array(processed_directory, frame_index).astype(np.uint8, copy=False)
            if output_frame.shape[1] != width or output_frame.shape[0] != height:
                raise RuntimeError(
                    f"Processed frame {frame_index} has unexpected size "
                    f"{output_frame.shape[1]}x{output_frame.shape[0]} (expected {width}x{height})."
                )

            process.stdin.write(np.ascontiguousarray(output_frame).tobytes())
            if frame_index == frame_count - 1 or (frame_index + 1) % progress_stride == 0:
                emit_progress(0.95 + 0.05 * ((frame_index + 1) / frame_count))

        process.stdin.close()
        stderr_output = process.stderr.read().decode("utf-8", errors="replace") if process.stderr is not None else ""
        return_code = process.wait()
    except BrokenPipeError as exc:
        if process.stdin is not None and not process.stdin.closed:
            process.stdin.close()
        stderr_output = process.stderr.read().decode("utf-8", errors="replace") if process.stderr is not None else ""
        process.wait()
        details = stderr_output.strip() or "ffmpeg closed the input pipe unexpectedly."
        raise RuntimeError(f"ffmpeg video encode failed.{os.linesep}{details}") from exc
    except Exception:
        process.kill()
        process.wait()
        raise

    if return_code != 0 or not os.path.exists(output_path):
        details = stderr_output.strip() or "ffmpeg exited without producing an output file."
        raise RuntimeError(f"ffmpeg video encode failed.{os.linesep}{details}")


def copy_audio_if_possible(input_path: str, output_path: str, temp_video_path: str) -> None:
    ffmpeg = resolve_ffmpeg()
    if ffmpeg is None:
        emit_status("ffmpeg was not found. Saving processed video without audio.")
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

    emit_status("ffmpeg audio copy failed. Saving processed video without audio.")
    move_temp_to_output(temp_video_path, output_path)


def process_image(input_path: str, output_path: str, tracks: list[dict], model_manager: ModelManager | None, job: dict, mask_padding: int) -> None:
    render_mask_only = bool(job.get("renderMaskOnly", False))
    emit_status("Rendering stable watermark mask preview..." if render_mask_only else "Processing image with stable watermark mask...")
    image_bgr = cv2.imread(input_path, cv2.IMREAD_COLOR)
    if image_bgr is None or image_bgr.size == 0:
        raise RuntimeError(f"Failed to read image: {input_path}")

    segments = extract_box_segments(tracks, 1, image_bgr.shape[1], image_bgr.shape[0], mask_padding)
    mask = np.zeros(image_bgr.shape[:2], dtype=np.uint8)
    temp_source_directory = save_single_frame_temp_directory(image_bgr)

    try:
        for segment in segments:
            local_mask = build_segment_stable_local_mask(segment, temp_source_directory)
            if cv2.countNonZero(local_mask) == 0:
                continue
            x, y, w, h = segment.rect
            target = mask[y : y + h, x : x + w]
            np.maximum(target, local_mask, out=target)
    finally:
        shutil.rmtree(temp_source_directory, ignore_errors=True)

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    if cv2.countNonZero(mask) == 0:
        cv2.imwrite(output_path, image_bgr)
        emit_progress(1.0)
        return

    if not render_mask_only and model_manager is None:
        raise RuntimeError("LaMa model was not initialized for inpaint processing.")

    result_frame = render_mask_overlay(image_bgr, mask) if render_mask_only else process_image_with_lama(image_bgr, mask, model_manager, job)
    cv2.imwrite(output_path, result_frame)
    emit_progress(1.0)


def save_single_frame_temp_directory(frame: np.ndarray) -> str:
    temp_directory = tempfile.mkdtemp(prefix="trackbox-image-mask-")
    save_temp_array(temp_directory, 0, frame.astype(np.uint8, copy=False))
    return temp_directory


def process_video(input_path: str, output_path: str, tracks: list[dict], model_manager: ModelManager | None, job: dict, mask_padding: int) -> None:
    render_mask_only = bool(job.get("renderMaskOnly", False))
    preserve_audio = bool(job.get("preserveAudio", True))
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
    source_directory = os.path.join(temp_directory, "source-frames")
    processed_directory = os.path.join(temp_directory, "processed-frames")
    temp_video_path = os.path.join(temp_directory, "video-temp.mp4")
    discord_safe_output_path = os.path.join(temp_directory, "video-discord-safe.mp4")
    os.makedirs(source_directory, exist_ok=True)
    os.makedirs(processed_directory, exist_ok=True)

    writer: cv2.VideoWriter | None = None

    try:
        emit_status("Collecting source frames...")
        frame_index = 0
        while True:
            ok, frame = capture.read()
            if not ok or frame is None or frame.size == 0:
                break

            save_temp_array(source_directory, frame_index, frame.astype(np.uint8, copy=False))
            frame_index += 1
            emit_progress(0.25 * (frame_index / max(1, total_frames)))

        capture.release()
        capture = None

        frame_count = frame_index
        if frame_count == 0:
            raise RuntimeError(f"Failed to read video frames: {input_path}")

        segments = extract_box_segments(tracks, frame_count, width, height, mask_padding)
        if not segments:
            emit_status("No enabled box ranges found. Copying input without changes.")
            shutil.copyfile(input_path, output_path)
            emit_progress(1.0)
            shutil.rmtree(temp_directory, ignore_errors=True)
            return

        segment_plans = build_segment_mask_plans(segments, source_directory)
        if not segment_plans:
            emit_status("No stable watermark masks survived the thresholds. Copying input without changes.")
            shutil.copyfile(input_path, output_path)
            emit_progress(1.0)
            shutil.rmtree(temp_directory, ignore_errors=True)
            return

        plans_by_frame = build_plans_by_frame(frame_count, segment_plans)

        emit_status("Rendering stable mask overlay preview..." if render_mask_only else "Applying identical segment masks across each enabled range...")
        progress_stride = max(1, frame_count // 200)

        for frame_index in range(frame_count):
            frame = load_temp_array(source_directory, frame_index).astype(np.uint8, copy=False)
            frame_mask = compose_frame_mask(frame.shape, plans_by_frame[frame_index])
            if cv2.countNonZero(frame_mask) == 0:
                output_frame = frame.copy() if render_mask_only else frame
            elif render_mask_only:
                output_frame = render_mask_overlay(frame, frame_mask)
            else:
                if model_manager is None:
                    raise RuntimeError("LaMa model was not initialized for inpaint processing.")
                output_frame = process_image_with_lama(frame, frame_mask, model_manager, job)
            save_temp_array(processed_directory, frame_index, output_frame)

            if frame_index == frame_count - 1 or (frame_index + 1) % progress_stride == 0:
                emit_progress(0.55 + 0.30 * ((frame_index + 1) / frame_count))

        if not render_mask_only:
            apply_temporal_segment_blend(frame_count, source_directory, processed_directory, segment_plans)

        ffmpeg = resolve_ffmpeg()
        if ffmpeg is not None:
            emit_status("Encoding Discord-safe MP4 with high-quality H.264...")
            encode_processed_video_with_ffmpeg(
                ffmpeg,
                input_path,
                discord_safe_output_path,
                processed_directory,
                frame_count,
                fps if fps > 0 else 30.0,
                width,
                height,
                preserve_audio,
            )
            move_temp_to_output(discord_safe_output_path, output_path)
        else:
            emit_status("ffmpeg was not found. Falling back to OpenCV mp4v export; Discord compatibility may be limited.")
            writer = cv2.VideoWriter(
                temp_video_path,
                cv2.VideoWriter_fourcc(*"mp4v"),
                fps if fps > 0 else 30.0,
                (width, height),
            )
            if not writer.isOpened():
                raise RuntimeError(f"Failed to create output video: {temp_video_path}")

            emit_status("Encoding fallback MP4 output...")
            for frame_index in range(frame_count):
                output_frame = load_temp_array(processed_directory, frame_index).astype(np.uint8, copy=False)
                writer.write(output_frame)
                if frame_index == frame_count - 1 or (frame_index + 1) % progress_stride == 0:
                    emit_progress(0.95 + 0.05 * ((frame_index + 1) / frame_count))

            writer.release()
            writer = None
            if preserve_audio:
                copy_audio_if_possible(input_path, output_path, temp_video_path)
            else:
                emit_status("Saving processed video without audio copy.")
                move_temp_to_output(temp_video_path, output_path)
        shutil.rmtree(temp_directory, ignore_errors=True)
        emit_progress(1.0)
    except Exception:
        if writer is not None:
            writer.release()
        if capture is not None:
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
    render_mask_only = bool(job.get("renderMaskOnly", False))
    model_manager: ModelManager | None = None
    if render_mask_only:
        emit_status("Stable mask preview mode enabled.")
    else:
        device = resolve_device(job.get("devicePreference", "cuda-preferred"))
        quality_preset = str(job.get("qualityPreset", "max")).strip() or "max"

        emit_status(f"Loading LaMa model on {device} ({quality_preset} quality)...")
        model_manager = load_lama_model(device)
        emit_status("LaMa model loaded.")

    suffix = Path(input_path).suffix.lower()
    if suffix in VIDEO_EXTENSIONS:
        process_video(input_path, output_path, tracks, model_manager, job, mask_padding)
    else:
        process_image(input_path, output_path, tracks, model_manager, job, mask_padding)

    emit_status("LaMa processing finished.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"ERROR {exc}", file=sys.stderr, flush=True)
        raise
