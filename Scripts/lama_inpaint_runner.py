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


# Coverage / mask tuning. Change values here without touching the pipeline below.
COVERAGE_CONFIG = {
    # Lower = white-ish watermark pixels start expanding earlier inside the box.
    "white_expansion_threshold": 0.64,
    # Larger = white expansion grows farther in pixels.
    "white_expansion_radius": 10,
    # Lower start / end = inpaint replaces darker fade pixels more aggressively.
    "light_replace_start": 0.32,
    "light_replace_end": 0.78,
    # Minimum alpha once a pixel is treated as supported watermark coverage.
    "light_replace_support_floor": 0.16,
    # Extra alpha contributed by confidence-driven support.
    "light_replace_confidence_support": 0.58,
    # Feather on the final replacement mask edge.
    "mask_edge_feather_sigma": 3.0,
    # Watermark confidence inputs. More aggressive = easier fade capture.
    "watermark_lift_sigma": 5.0,
    "watermark_lift_scale": 0.10,
    "watermark_edge_scale": 0.16,
    "watermark_confidence_low": 0.16,
    "watermark_confidence_high": 0.56,
    "watermark_confidence_halo_support": 0.72,
    # Hysteresis keeps weak pixels alive around stronger detections.
    "watermark_hysteresis_radius": 2,
    "watermark_hysteresis_iterations": 5,
    # Temporal propagation. Higher decay / gains = longer fade-in / fade-out capture.
    "watermark_temporal_support_decay": 0.985,
    "watermark_temporal_support_floor": 0.15,
    "watermark_previous_support_gain": 1.24,
    "watermark_next_support_gain": 1.12,
    # Segment backfill. Force a few frames before/after the first and last confident frame.
    "segment_detect_trigger": 0.16,
    "segment_backfill_before_frames": 3,
    "segment_backfill_after_frames": 3,
    "segment_backfill_floor": 0.22,
    # Segment reference frame. Helps weak fade frames borrow a stronger mask.
    "segment_reference_trigger": 0.22,
    "segment_reference_gain": 1.28,
    "segment_reference_floor": 0.20,
    # Spatial growth around the confident core and halo.
    "watermark_core_grow_radius": 2,
    "watermark_halo_radius": 8,
}

WHITE_EXPANSION_THRESHOLD = COVERAGE_CONFIG["white_expansion_threshold"]
WHITE_EXPANSION_RADIUS = COVERAGE_CONFIG["white_expansion_radius"]
LIGHT_REPLACE_START = COVERAGE_CONFIG["light_replace_start"]
LIGHT_REPLACE_END = COVERAGE_CONFIG["light_replace_end"]
LIGHT_REPLACE_SUPPORT_FLOOR = COVERAGE_CONFIG["light_replace_support_floor"]
LIGHT_REPLACE_CONFIDENCE_SUPPORT = COVERAGE_CONFIG["light_replace_confidence_support"]
MASK_EDGE_FEATHER_SIGMA = COVERAGE_CONFIG["mask_edge_feather_sigma"]
WATERMARK_LIFT_SIGMA = COVERAGE_CONFIG["watermark_lift_sigma"]
WATERMARK_LIFT_SCALE = COVERAGE_CONFIG["watermark_lift_scale"]
WATERMARK_EDGE_SCALE = COVERAGE_CONFIG["watermark_edge_scale"]
WATERMARK_CONFIDENCE_LOW = COVERAGE_CONFIG["watermark_confidence_low"]
WATERMARK_CONFIDENCE_HIGH = COVERAGE_CONFIG["watermark_confidence_high"]
WATERMARK_CONFIDENCE_HALO_SUPPORT = COVERAGE_CONFIG["watermark_confidence_halo_support"]
WATERMARK_HYSTERESIS_RADIUS = COVERAGE_CONFIG["watermark_hysteresis_radius"]
WATERMARK_HYSTERESIS_ITERATIONS = COVERAGE_CONFIG["watermark_hysteresis_iterations"]
WATERMARK_TEMPORAL_SUPPORT_DECAY = COVERAGE_CONFIG["watermark_temporal_support_decay"]
WATERMARK_TEMPORAL_SUPPORT_FLOOR = COVERAGE_CONFIG["watermark_temporal_support_floor"]
WATERMARK_PREVIOUS_SUPPORT_GAIN = COVERAGE_CONFIG["watermark_previous_support_gain"]
WATERMARK_NEXT_SUPPORT_GAIN = COVERAGE_CONFIG["watermark_next_support_gain"]
SEGMENT_DETECT_TRIGGER = COVERAGE_CONFIG["segment_detect_trigger"]
SEGMENT_BACKFILL_BEFORE_FRAMES = COVERAGE_CONFIG["segment_backfill_before_frames"]
SEGMENT_BACKFILL_AFTER_FRAMES = COVERAGE_CONFIG["segment_backfill_after_frames"]
SEGMENT_BACKFILL_FLOOR = COVERAGE_CONFIG["segment_backfill_floor"]
SEGMENT_REFERENCE_TRIGGER = COVERAGE_CONFIG["segment_reference_trigger"]
SEGMENT_REFERENCE_GAIN = COVERAGE_CONFIG["segment_reference_gain"]
SEGMENT_REFERENCE_FLOOR = COVERAGE_CONFIG["segment_reference_floor"]
WATERMARK_CORE_GROW_RADIUS = COVERAGE_CONFIG["watermark_core_grow_radius"]
WATERMARK_HALO_RADIUS = COVERAGE_CONFIG["watermark_halo_radius"]
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
class SourceFrameBundle:
    source_frame: np.ndarray
    base_mask: np.ndarray
    watermark_confidence: np.ndarray


@dataclass
class ProcessedFrameBundle:
    source_frame: np.ndarray
    processed_frame: np.ndarray
    mask: np.ndarray
    watermark_confidence: np.ndarray


@dataclass
class SegmentCoveragePlan:
    span_start: int
    span_end: int
    detected_start: int
    detected_end: int
    coverage_start: int
    coverage_end: int
    reference_index: int


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


def compute_watermark_lift_map(frame_bgr: np.ndarray) -> np.ndarray:
    hsv = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2HSV).astype(np.float32)
    value = hsv[..., 2] / 255.0
    local_value = cv2.GaussianBlur(value, (0, 0), sigmaX=WATERMARK_LIFT_SIGMA, sigmaY=WATERMARK_LIFT_SIGMA)
    lift = np.clip((value - local_value + 0.02) / WATERMARK_LIFT_SCALE, 0.0, 1.0)
    return lift


def compute_local_flatness_map(frame_bgr: np.ndarray) -> np.ndarray:
    gray = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2GRAY).astype(np.float32) / 255.0
    grad_x = cv2.Sobel(gray, cv2.CV_32F, 1, 0, ksize=3)
    grad_y = cv2.Sobel(gray, cv2.CV_32F, 0, 1, ksize=3)
    gradient = cv2.magnitude(grad_x, grad_y)
    flatness = 1.0 - np.clip(gradient / WATERMARK_EDGE_SCALE, 0.0, 1.0)
    return cv2.GaussianBlur(flatness, (0, 0), sigmaX=1.2, sigmaY=1.2)


def build_watermark_confidence_map(frame_bgr: np.ndarray, base_mask: np.ndarray) -> np.ndarray:
    if cv2.countNonZero(base_mask) == 0:
        return np.zeros(base_mask.shape, dtype=np.float32)

    whiteness = compute_whiteness_map(frame_bgr)
    lift = compute_watermark_lift_map(frame_bgr)
    flatness = compute_local_flatness_map(frame_bgr)
    confidence = 0.58 * whiteness + 0.27 * lift + 0.15 * flatness
    confidence = cv2.GaussianBlur(confidence.astype(np.float32), (0, 0), sigmaX=1.0, sigmaY=1.0)
    halo_support = cv2.GaussianBlur(confidence, (0, 0), sigmaX=2.2, sigmaY=2.2) * WATERMARK_CONFIDENCE_HALO_SUPPORT
    confidence = np.maximum(confidence, halo_support)
    confidence = np.clip(confidence, 0.0, 1.0)
    confidence[base_mask == 0] = 0.0
    return confidence


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
    alpha_confidence_map: np.ndarray | None = None,
) -> np.ndarray:
    alpha_source = base_frame if alpha_reference_frame is None else alpha_reference_frame
    alpha_map = build_light_replacement_alpha(alpha_source, mask, alpha_confidence_map)
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


def build_light_replacement_alpha(
    source_frame: np.ndarray,
    mask: np.ndarray,
    watermark_confidence: np.ndarray | None = None,
) -> np.ndarray:
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
    if watermark_confidence is not None:
        confidence_weight = np.clip(watermark_confidence.astype(np.float32), 0.0, 1.0)
        support_floor = np.where(confidence_weight >= WATERMARK_CONFIDENCE_LOW, LIGHT_REPLACE_SUPPORT_FLOOR, 0.0)
        confidence_weight = confidence_weight * confidence_weight * (3.0 - 2.0 * confidence_weight)
        alpha = np.maximum(alpha, feather * support_floor)
        alpha = np.maximum(alpha, feather * confidence_weight * LIGHT_REPLACE_CONFIDENCE_SUPPORT)
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


def build_neighbor_confidence_support(
    current_bundle: SourceFrameBundle,
    neighbor_bundle: SourceFrameBundle | None,
) -> np.ndarray | None:
    if neighbor_bundle is None:
        return None

    if cv2.countNonZero(current_bundle.base_mask) == 0 or cv2.countNonZero(neighbor_bundle.base_mask) == 0:
        return None

    backward_flow, forward_flow = calculate_bidirectional_flow(
        neighbor_bundle.source_frame,
        current_bundle.source_frame,
    )
    warped_neighbor_mask = remap_with_flow(
        neighbor_bundle.base_mask,
        backward_flow,
        interpolation=cv2.INTER_NEAREST,
        border_mode=cv2.BORDER_CONSTANT,
        border_value=0,
    )
    overlap_mask = cv2.bitwise_and(current_bundle.base_mask, warped_neighbor_mask)
    if cv2.countNonZero(overlap_mask) == 0:
        return None

    flow_confidence = build_flow_confidence_mask(backward_flow, forward_flow)
    reliable_mask = cv2.bitwise_and(overlap_mask, flow_confidence)
    if TEMPORAL_RELIABLE_ERODE_RADIUS > 0 and cv2.countNonZero(reliable_mask) > 0:
        kernel_size = TEMPORAL_RELIABLE_ERODE_RADIUS * 2 + 1
        kernel = np.ones((kernel_size, kernel_size), dtype=np.uint8)
        reliable_mask = cv2.erode(reliable_mask, kernel, iterations=1)

    if cv2.countNonZero(reliable_mask) == 0:
        return None

    warped_confidence = remap_with_flow(
        neighbor_bundle.watermark_confidence.astype(np.float32),
        backward_flow,
        interpolation=cv2.INTER_LINEAR,
        border_mode=cv2.BORDER_CONSTANT,
        border_value=0,
    )
    warped_confidence[reliable_mask == 0] = 0.0
    return np.clip(warped_confidence * WATERMARK_TEMPORAL_SUPPORT_DECAY, 0.0, 1.0)


def build_hysteresis_mask(confidence_map: np.ndarray) -> np.ndarray:
    core_mask = np.where(confidence_map >= WATERMARK_CONFIDENCE_HIGH, 255, 0).astype(np.uint8)
    support_mask = np.where(confidence_map >= WATERMARK_CONFIDENCE_LOW, 255, 0).astype(np.uint8)
    if cv2.countNonZero(core_mask) == 0:
        return support_mask
    if cv2.countNonZero(support_mask) == 0:
        return core_mask

    kernel_size = WATERMARK_HYSTERESIS_RADIUS * 2 + 1
    kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (kernel_size, kernel_size))
    grown_mask = core_mask.copy()
    for _ in range(max(1, WATERMARK_HYSTERESIS_ITERATIONS)):
        expanded_mask = cv2.dilate(grown_mask, kernel, iterations=1)
        expanded_mask = cv2.bitwise_and(expanded_mask, support_mask)
        if np.array_equal(expanded_mask, grown_mask):
            break
        grown_mask = expanded_mask

    return cv2.bitwise_or(core_mask, grown_mask)


def build_two_pass_inpaint_masks(
    frame_bgr: np.ndarray,
    base_mask: np.ndarray,
    watermark_confidence: np.ndarray,
    hysteresis_mask: np.ndarray,
) -> tuple[np.ndarray, np.ndarray]:
    if cv2.countNonZero(base_mask) == 0:
        empty_mask = np.zeros(base_mask.shape, dtype=np.uint8)
        return empty_mask, empty_mask

    core_mask = np.where(watermark_confidence >= WATERMARK_CONFIDENCE_HIGH, 255, 0).astype(np.uint8)
    if cv2.countNonZero(core_mask) == 0:
        core_mask = np.where(watermark_confidence >= 0.5 * (WATERMARK_CONFIDENCE_LOW + WATERMARK_CONFIDENCE_HIGH), 255, 0).astype(np.uint8)

    support_mask = hysteresis_mask.copy()
    if cv2.countNonZero(support_mask) == 0:
        support_mask = np.where(watermark_confidence >= WATERMARK_CONFIDENCE_LOW, 255, 0).astype(np.uint8)

    if cv2.countNonZero(support_mask) == 0:
        fallback_mask = build_adaptive_inpaint_mask(frame_bgr, base_mask)
        return fallback_mask, fallback_mask

    if WATERMARK_CORE_GROW_RADIUS > 0 and cv2.countNonZero(core_mask) > 0:
        kernel_size = WATERMARK_CORE_GROW_RADIUS * 2 + 1
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (kernel_size, kernel_size))
        core_mask = cv2.dilate(core_mask, kernel, iterations=1)

    core_mask = build_adaptive_inpaint_mask(frame_bgr, core_mask) if cv2.countNonZero(core_mask) > 0 else core_mask
    if cv2.countNonZero(core_mask) == 0:
        core_mask = support_mask.copy()

    full_mask = cv2.bitwise_or(support_mask, core_mask)
    if WATERMARK_HALO_RADIUS > 0 and cv2.countNonZero(full_mask) > 0:
        kernel_size = WATERMARK_HALO_RADIUS * 2 + 1
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (kernel_size, kernel_size))
        full_mask = cv2.dilate(full_mask, kernel, iterations=1)

    full_mask = build_adaptive_inpaint_mask(frame_bgr, full_mask)
    return core_mask, full_mask


def run_two_pass_inpaint(
    frame_bgr: np.ndarray,
    core_mask: np.ndarray,
    full_mask: np.ndarray,
    model_manager: ModelManager,
    job: dict,
) -> np.ndarray:
    working_frame = frame_bgr.copy()
    core_area = cv2.countNonZero(core_mask)
    full_area = cv2.countNonZero(full_mask)

    if core_area > 0:
        working_frame = process_image_with_lama(working_frame, core_mask, model_manager, job)

    if full_area > 0:
        masks_are_identical = core_area == full_area and cv2.countNonZero(cv2.absdiff(core_mask, full_mask)) == 0
        if core_area == 0 or not masks_are_identical:
            working_frame = process_image_with_lama(working_frame, full_mask, model_manager, job)

    return working_frame


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
        alpha_confidence_map=current_bundle.watermark_confidence,
    )
    return ProcessedFrameBundle(
        source_frame=current_bundle.source_frame,
        processed_frame=blended_frame,
        mask=current_bundle.mask,
        watermark_confidence=current_bundle.watermark_confidence,
    )


def build_source_frame_bundle(
    frame: np.ndarray,
    frame_index: int,
    runtimes: list[TrackRuntime],
    mask_padding: int,
) -> SourceFrameBundle:
    base_mask = build_mask(frame.shape, frame_index, runtimes, mask_padding)
    watermark_confidence = build_watermark_confidence_map(frame, base_mask)
    return SourceFrameBundle(
        source_frame=frame.copy(),
        base_mask=base_mask,
        watermark_confidence=watermark_confidence,
    )


def get_temp_array_path(directory: str, frame_index: int) -> str:
    return os.path.join(directory, f"{frame_index:06d}.npy")


def save_temp_array(directory: str, frame_index: int, array: np.ndarray) -> None:
    np.save(get_temp_array_path(directory, frame_index), array)


def load_temp_array(directory: str, frame_index: int) -> np.ndarray:
    return np.load(get_temp_array_path(directory, frame_index), allow_pickle=False)


def save_source_frame_bundle(
    bundle: SourceFrameBundle,
    frame_index: int,
    source_directory: str,
    mask_directory: str,
    confidence_directory: str,
) -> None:
    save_temp_array(source_directory, frame_index, bundle.source_frame)
    save_temp_array(mask_directory, frame_index, bundle.base_mask)
    save_temp_array(confidence_directory, frame_index, bundle.watermark_confidence.astype(np.float32))


def load_source_frame_bundle(
    frame_index: int,
    source_directory: str,
    mask_directory: str,
    confidence_directory: str,
) -> SourceFrameBundle:
    return SourceFrameBundle(
        source_frame=load_temp_array(source_directory, frame_index),
        base_mask=load_temp_array(mask_directory, frame_index).astype(np.uint8, copy=False),
        watermark_confidence=load_temp_array(confidence_directory, frame_index).astype(np.float32, copy=False),
    )


def merge_confidence_with_neighbor(
    current_bundle: SourceFrameBundle,
    effective_confidence: np.ndarray,
    neighbor_bundle: SourceFrameBundle | None,
    gain: float,
) -> np.ndarray:
    if neighbor_bundle is None:
        return effective_confidence

    neighbor_support = build_neighbor_confidence_support(current_bundle, neighbor_bundle)
    if neighbor_support is None:
        return effective_confidence

    propagated_support = np.clip(neighbor_support * gain, 0.0, 1.0)
    propagated_floor = np.where(
        propagated_support >= WATERMARK_CONFIDENCE_LOW * 0.75,
        WATERMARK_TEMPORAL_SUPPORT_FLOOR,
        0.0,
    )
    merged_confidence = np.maximum(
        effective_confidence,
        propagated_support,
    )
    merged_confidence = np.maximum(merged_confidence, propagated_floor)
    merged_confidence[current_bundle.base_mask == 0] = 0.0
    return merged_confidence


def compute_confidence_activation_score(confidence_map: np.ndarray, base_mask: np.ndarray) -> float:
    active_values = confidence_map[base_mask > 0]
    if active_values.size == 0:
        return 0.0
    return float(np.percentile(active_values, 95))


def build_active_spans(frame_count: int, mask_directory: str) -> list[tuple[int, int]]:
    spans: list[tuple[int, int]] = []
    span_start: int | None = None

    for frame_index in range(frame_count):
        base_mask = load_temp_array(mask_directory, frame_index).astype(np.uint8, copy=False)
        is_active = cv2.countNonZero(base_mask) > 0
        if is_active and span_start is None:
            span_start = frame_index
        elif not is_active and span_start is not None:
            spans.append((span_start, frame_index - 1))
            span_start = None

    if span_start is not None:
        spans.append((span_start, frame_count - 1))

    return spans


def build_segment_coverage_plans(
    frame_count: int,
    mask_directory: str,
    confidence_directory: str,
) -> list[SegmentCoveragePlan | None]:
    coverage_plans: list[SegmentCoveragePlan | None] = [None] * frame_count

    for span_start, span_end in build_active_spans(frame_count, mask_directory):
        best_frame_index = span_start
        best_score = -1.0
        span_center = 0.5 * (span_start + span_end)
        detected_indices: list[int] = []

        for frame_index in range(span_start, span_end + 1):
            base_mask = load_temp_array(mask_directory, frame_index).astype(np.uint8, copy=False)
            confidence_map = load_temp_array(confidence_directory, frame_index).astype(np.float32, copy=False)
            score = compute_confidence_activation_score(confidence_map, base_mask)
            is_better_score = score > best_score + 1e-6
            is_tie_but_more_central = abs(frame_index - span_center) < abs(best_frame_index - span_center)
            if is_better_score or (abs(score - best_score) <= 1e-6 and is_tie_but_more_central):
                best_score = score
                best_frame_index = frame_index
            if score >= SEGMENT_DETECT_TRIGGER:
                detected_indices.append(frame_index)

        if detected_indices:
            detected_start = detected_indices[0]
            detected_end = detected_indices[-1]
        elif best_score >= SEGMENT_REFERENCE_TRIGGER:
            detected_start = best_frame_index
            detected_end = best_frame_index
        else:
            continue

        coverage_start = max(span_start, detected_start - SEGMENT_BACKFILL_BEFORE_FRAMES)
        coverage_end = min(span_end, detected_end + SEGMENT_BACKFILL_AFTER_FRAMES)
        plan = SegmentCoveragePlan(
            span_start=span_start,
            span_end=span_end,
            detected_start=detected_start,
            detected_end=detected_end,
            coverage_start=coverage_start,
            coverage_end=coverage_end,
            reference_index=best_frame_index,
        )

        for frame_index in range(coverage_start, coverage_end + 1):
            coverage_plans[frame_index] = plan

    return coverage_plans


def propagate_temporal_confidence_maps(
    frame_count: int,
    source_directory: str,
    mask_directory: str,
    initial_confidence_directory: str,
    forward_confidence_directory: str,
    final_confidence_directory: str,
) -> None:
    emit_status("Propagating watermark confidence forward...")
    previous_effective_bundle: SourceFrameBundle | None = None

    for frame_index in range(frame_count):
        current_bundle = load_source_frame_bundle(
            frame_index,
            source_directory,
            mask_directory,
            initial_confidence_directory,
        )
        effective_confidence = merge_confidence_with_neighbor(
            current_bundle,
            current_bundle.watermark_confidence.copy(),
            previous_effective_bundle,
            WATERMARK_PREVIOUS_SUPPORT_GAIN,
        )
        save_temp_array(forward_confidence_directory, frame_index, effective_confidence.astype(np.float32))
        previous_effective_bundle = SourceFrameBundle(
            source_frame=current_bundle.source_frame,
            base_mask=current_bundle.base_mask,
            watermark_confidence=effective_confidence,
        )
        emit_progress(0.15 + 0.15 * ((frame_index + 1) / max(1, frame_count)))

    emit_status("Propagating watermark confidence backward...")
    next_effective_bundle: SourceFrameBundle | None = None

    for reverse_index, frame_index in enumerate(range(frame_count - 1, -1, -1), start=1):
        current_bundle = load_source_frame_bundle(
            frame_index,
            source_directory,
            mask_directory,
            initial_confidence_directory,
        )
        effective_confidence = load_temp_array(forward_confidence_directory, frame_index).astype(np.float32, copy=False)
        effective_confidence = merge_confidence_with_neighbor(
            current_bundle,
            effective_confidence,
            next_effective_bundle,
            WATERMARK_NEXT_SUPPORT_GAIN,
        )
        hysteresis_mask = build_hysteresis_mask(effective_confidence)
        effective_confidence[hysteresis_mask == 0] = 0.0
        save_temp_array(final_confidence_directory, frame_index, effective_confidence.astype(np.float32))
        next_effective_bundle = SourceFrameBundle(
            source_frame=current_bundle.source_frame,
            base_mask=current_bundle.base_mask,
            watermark_confidence=effective_confidence,
        )
        emit_progress(0.30 + 0.15 * (reverse_index / max(1, frame_count)))


def apply_segment_backfill_coverage(
    frame_count: int,
    source_directory: str,
    mask_directory: str,
    input_confidence_directory: str,
    output_confidence_directory: str,
    coverage_plans: list[SegmentCoveragePlan | None],
) -> None:
    emit_status("Applying segment backfill coverage...")
    reference_bundle_cache: dict[int, SourceFrameBundle] = {}

    for frame_index in range(frame_count):
        current_bundle = load_source_frame_bundle(
            frame_index,
            source_directory,
            mask_directory,
            input_confidence_directory,
        )
        extended_confidence = current_bundle.watermark_confidence.copy()
        coverage_plan = coverage_plans[frame_index]
        reference_support = None

        if coverage_plan is not None:
            reference_bundle = reference_bundle_cache.get(coverage_plan.reference_index)
            if reference_bundle is None:
                reference_bundle = load_source_frame_bundle(
                    coverage_plan.reference_index,
                    source_directory,
                    mask_directory,
                    input_confidence_directory,
                )
                reference_bundle_cache[coverage_plan.reference_index] = reference_bundle

            reference_score = compute_confidence_activation_score(
                reference_bundle.watermark_confidence,
                reference_bundle.base_mask,
            )
            if reference_score >= SEGMENT_REFERENCE_TRIGGER:
                if coverage_plan.reference_index == frame_index:
                    reference_support = reference_bundle.watermark_confidence.copy()
                else:
                    reference_support = build_neighbor_confidence_support(current_bundle, reference_bundle)
                if reference_support is not None:
                    extended_confidence = np.maximum(
                        extended_confidence,
                        np.clip(reference_support * SEGMENT_REFERENCE_GAIN, 0.0, 1.0),
                    )
                    reference_floor = np.where(
                        reference_support >= SEGMENT_REFERENCE_TRIGGER * 0.5,
                        SEGMENT_REFERENCE_FLOOR,
                        0.0,
                    )
                    extended_confidence = np.maximum(extended_confidence, reference_floor)

            is_forced_edge_frame = (
                frame_index < coverage_plan.detected_start or frame_index > coverage_plan.detected_end
            )
            if is_forced_edge_frame:
                if reference_support is not None and np.any(reference_support >= SEGMENT_DETECT_TRIGGER * 0.5):
                    forced_floor = np.where(
                        reference_support >= SEGMENT_DETECT_TRIGGER * 0.5,
                        SEGMENT_BACKFILL_FLOOR,
                        0.0,
                    )
                else:
                    forced_floor = np.where(current_bundle.base_mask > 0, SEGMENT_BACKFILL_FLOOR, 0.0)
                extended_confidence = np.maximum(extended_confidence, forced_floor)

        hysteresis_mask = build_hysteresis_mask(extended_confidence)
        extended_confidence[hysteresis_mask == 0] = 0.0
        save_temp_array(output_confidence_directory, frame_index, extended_confidence.astype(np.float32))
        emit_progress(0.45 + 0.15 * ((frame_index + 1) / max(1, frame_count)))


def process_source_frame_bundle(
    current_bundle: SourceFrameBundle,
    model_manager: ModelManager,
    job: dict,
) -> ProcessedFrameBundle:
    if cv2.countNonZero(current_bundle.base_mask) == 0:
        return ProcessedFrameBundle(
            source_frame=current_bundle.source_frame.copy(),
            processed_frame=current_bundle.source_frame.copy(),
            mask=current_bundle.base_mask.copy(),
            watermark_confidence=current_bundle.watermark_confidence.copy(),
        )

    watermark_confidence = current_bundle.watermark_confidence.copy()
    hysteresis_mask = build_hysteresis_mask(watermark_confidence)
    watermark_confidence[hysteresis_mask == 0] = 0.0
    core_mask, full_mask = build_two_pass_inpaint_masks(
        current_bundle.source_frame,
        current_bundle.base_mask,
        watermark_confidence,
        hysteresis_mask,
    )
    inpainted_frame = run_two_pass_inpaint(
        current_bundle.source_frame,
        core_mask,
        full_mask,
        model_manager,
        job,
    )
    processed_frame = composite_masked_result(
        current_bundle.source_frame,
        inpainted_frame,
        full_mask,
        alpha_confidence_map=watermark_confidence,
    )
    return ProcessedFrameBundle(
        source_frame=current_bundle.source_frame.copy(),
        processed_frame=processed_frame,
        mask=full_mask,
        watermark_confidence=watermark_confidence,
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
    source_bundle = build_source_frame_bundle(image_bgr, 0, runtimes, mask_padding)
    result_bundle = process_source_frame_bundle(source_bundle, model_manager, job)
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
    source_directory = os.path.join(temp_directory, "source-frames")
    mask_directory = os.path.join(temp_directory, "base-masks")
    initial_confidence_directory = os.path.join(temp_directory, "confidence-initial")
    forward_confidence_directory = os.path.join(temp_directory, "confidence-forward")
    final_confidence_directory = os.path.join(temp_directory, "confidence-final")
    extended_confidence_directory = os.path.join(temp_directory, "confidence-extended")
    temp_video_path = os.path.join(temp_directory, "video-temp.mp4")
    os.makedirs(source_directory, exist_ok=True)
    os.makedirs(mask_directory, exist_ok=True)
    os.makedirs(initial_confidence_directory, exist_ok=True)
    os.makedirs(forward_confidence_directory, exist_ok=True)
    os.makedirs(final_confidence_directory, exist_ok=True)
    os.makedirs(extended_confidence_directory, exist_ok=True)

    writer: cv2.VideoWriter | None = None
    processed_queue: deque[ProcessedFrameBundle] = deque()
    previous_output_bundle: ProcessedFrameBundle | None = None

    try:
        emit_status("Collecting source frames and watermark confidence...")
        frame_index = 0

        while True:
            ok, frame = capture.read()
            if not ok or frame is None or frame.size == 0:
                break

            source_bundle = build_source_frame_bundle(frame, frame_index, runtimes, mask_padding)
            save_source_frame_bundle(
                source_bundle,
                frame_index,
                source_directory,
                mask_directory,
                initial_confidence_directory,
            )
            frame_index += 1
            emit_progress(0.15 * (frame_index / max(1, total_frames)))

        capture.release()
        capture = None

        frame_count = frame_index
        if frame_count == 0:
            raise RuntimeError(f"Failed to read video frames: {input_path}")

        propagate_temporal_confidence_maps(
            frame_count,
            source_directory,
            mask_directory,
            initial_confidence_directory,
            forward_confidence_directory,
            final_confidence_directory,
        )
        segment_coverage_plans = build_segment_coverage_plans(
            frame_count,
            mask_directory,
            final_confidence_directory,
        )
        apply_segment_backfill_coverage(
            frame_count,
            source_directory,
            mask_directory,
            final_confidence_directory,
            extended_confidence_directory,
            segment_coverage_plans,
        )

        writer = cv2.VideoWriter(
            temp_video_path,
            cv2.VideoWriter_fourcc(*"mp4v"),
            fps if fps > 0 else 30.0,
            (width, height),
        )
        if not writer.isOpened():
            raise RuntimeError(f"Failed to create output video: {temp_video_path}")

        progress_stride = max(1, frame_count // 200)
        emit_status("Applying LaMa inpainting with two-pass temporal masks...")
        written_frames = 0

        for frame_index in range(frame_count):
            source_bundle = load_source_frame_bundle(
                frame_index,
                source_directory,
                mask_directory,
                extended_confidence_directory,
            )
            processed_queue.append(process_source_frame_bundle(source_bundle, model_manager, job))

            while len(processed_queue) >= 2:
                current_bundle = processed_queue[0]
                next_bundle = processed_queue[1]
                output_bundle = temporally_stabilize_bundle(current_bundle, previous_output_bundle, next_bundle)
                writer.write(output_bundle.processed_frame)
                previous_output_bundle = output_bundle
                processed_queue.popleft()
                written_frames += 1
                if written_frames == frame_count or written_frames % progress_stride == 0:
                    emit_progress(0.60 + 0.40 * (written_frames / frame_count))

        while processed_queue:
            current_bundle = processed_queue[0]
            next_bundle = processed_queue[1] if len(processed_queue) > 1 else None
            output_bundle = temporally_stabilize_bundle(current_bundle, previous_output_bundle, next_bundle)
            writer.write(output_bundle.processed_frame)
            previous_output_bundle = output_bundle
            processed_queue.popleft()
            written_frames += 1
            if written_frames == frame_count or written_frames % progress_stride == 0:
                emit_progress(0.60 + 0.40 * (written_frames / frame_count))

        writer.release()
        writer = None
        copy_audio_if_possible(input_path, output_path, temp_video_path)
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
