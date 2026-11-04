// Edited by: OpenAI-Codex_Source_Code (OpenAI-Codex_Responsible: Source_Code)
#pragma once

#include <cstddef>
#include <limits>
#include <optional>
#include <span>

namespace ets2ap::input {

struct SteeringCurvePoint {
    double speed_mps;
    double maximum_steering_ratio;
};

// This generic utility does not enforce the independent native hard cap.
// Any future integration must apply the approved cap separately and fail closed.

namespace detail {

[[nodiscard]] constexpr bool IsFiniteCurveValue(double value) noexcept {
    constexpr double maximum = std::numeric_limits<double>::max();
    return value >= -maximum && value <= maximum;
}

} // namespace detail

[[nodiscard]] constexpr bool IsValidSteeringCurve(
    std::span<const SteeringCurvePoint> points) noexcept {
    if (points.size() < 2) return false;
    for (std::size_t i = 0; i < points.size(); ++i) {
        const auto& point = points[i];
        if (!detail::IsFiniteCurveValue(point.speed_mps) || point.speed_mps < 0.0 ||
            !detail::IsFiniteCurveValue(point.maximum_steering_ratio) ||
            point.maximum_steering_ratio < 0.0 || point.maximum_steering_ratio > 1.0) {
            return false;
        }
        if (i > 0 &&
            (point.speed_mps <= points[i - 1].speed_mps ||
             point.maximum_steering_ratio > points[i - 1].maximum_steering_ratio)) {
            return false;
        }
    }
    return true;
}

[[nodiscard]] constexpr std::optional<double> SteeringLimitForSpeed(
    std::span<const SteeringCurvePoint> points, double speed_mps) noexcept {
    if (!IsValidSteeringCurve(points) || !detail::IsFiniteCurveValue(speed_mps) ||
        speed_mps < points.front().speed_mps || speed_mps > points.back().speed_mps) {
        return std::nullopt;
    }
    if (speed_mps == points.front().speed_mps) {
        return points.front().maximum_steering_ratio;
    }
    for (std::size_t i = 1; i < points.size(); ++i) {
        const auto& upper = points[i];
        if (speed_mps == upper.speed_mps) return upper.maximum_steering_ratio;
        if (speed_mps < upper.speed_mps) {
            const auto& lower = points[i - 1];
            const double speed_interval = upper.speed_mps - lower.speed_mps;
            if (!detail::IsFiniteCurveValue(speed_interval) || speed_interval <= 0.0) {
                return std::nullopt;
            }
            const double fraction = (speed_mps - lower.speed_mps) /
                                    (upper.speed_mps - lower.speed_mps);
            if (!detail::IsFiniteCurveValue(fraction) || fraction < 0.0 || fraction > 1.0) {
                return std::nullopt;
            }
            const double interpolated = lower.maximum_steering_ratio +
                (upper.maximum_steering_ratio - lower.maximum_steering_ratio) * fraction;
            if (!detail::IsFiniteCurveValue(interpolated)) return std::nullopt;
            if (interpolated < upper.maximum_steering_ratio) return upper.maximum_steering_ratio;
            if (interpolated > lower.maximum_steering_ratio) return lower.maximum_steering_ratio;
            return interpolated;
        }
    }
    return std::nullopt;
}

[[nodiscard]] constexpr std::optional<double> ClampSteeringForSpeed(
    std::span<const SteeringCurvePoint> points, double speed_mps,
    double steering_ratio) noexcept {
    if (!detail::IsFiniteCurveValue(steering_ratio) ||
        steering_ratio < -1.0 || steering_ratio > 1.0) {
        return std::nullopt;
    }
    const auto limit = SteeringLimitForSpeed(points, speed_mps);
    if (!limit) return std::nullopt;
    if (steering_ratio < -*limit) return -*limit;
    if (steering_ratio > *limit) return *limit;
    return steering_ratio;
}

} // namespace ets2ap::input
