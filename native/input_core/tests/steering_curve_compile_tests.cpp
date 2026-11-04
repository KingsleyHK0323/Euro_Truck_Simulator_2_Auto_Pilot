// Edited by: OpenAI-Codex_Source_Code (OpenAI-Codex_Responsible: Source_Code)
#include "ets2ap/input/steering_curve.h"

#include <array>
#include <limits>
#include <optional>
#include <span>

using ets2ap::input::ClampSteeringForSpeed;
using ets2ap::input::IsValidSteeringCurve;
using ets2ap::input::SteeringCurvePoint;
using ets2ap::input::SteeringLimitForSpeed;

namespace {

// Synthetic values exercise the contract; they are not vehicle calibration.
constexpr std::array kCurve{
    SteeringCurvePoint{0.0, 0.75},
    SteeringCurvePoint{10.0, 0.50},
    SteeringCurvePoint{20.0, 0.25},
};
constexpr std::array kFlatCurve{
    SteeringCurvePoint{0.0, 0.50},
    SteeringCurvePoint{10.0, 0.50},
};
constexpr std::array kOffsetCurve{
    SteeringCurvePoint{5.0, 0.50},
    SteeringCurvePoint{15.0, 0.25},
};
constexpr std::array kZeroCurve{
    SteeringCurvePoint{0.0, 0.0},
    SteeringCurvePoint{10.0, 0.0},
};
constexpr std::array kLargeSpeedCurve{
    SteeringCurvePoint{0.0, 1.0},
    SteeringCurvePoint{std::numeric_limits<double>::max(), 0.50},
};
constexpr std::array kTinySpeedCurve{
    SteeringCurvePoint{0.0, 1.0},
    SteeringCurvePoint{std::numeric_limits<double>::denorm_min() * 2.0, 0.50},
};
constexpr std::array kSinglePoint{SteeringCurvePoint{0.0, 0.50}};
constexpr std::array kDuplicateSpeed{
    SteeringCurvePoint{0.0, 0.75},
    SteeringCurvePoint{0.0, 0.50},
};
constexpr std::array kDescendingSpeed{
    SteeringCurvePoint{10.0, 0.75},
    SteeringCurvePoint{5.0, 0.50},
};
constexpr std::array kNegativeSpeed{
    SteeringCurvePoint{-1.0, 0.75},
    SteeringCurvePoint{10.0, 0.50},
};
constexpr std::array kNegativeLimit{
    SteeringCurvePoint{0.0, 0.75},
    SteeringCurvePoint{10.0, -0.01},
};
constexpr std::array kExcessLimit{
    SteeringCurvePoint{0.0, 1.01},
    SteeringCurvePoint{10.0, 0.50},
};
constexpr std::array kIncreasingLimit{
    SteeringCurvePoint{0.0, 0.50},
    SteeringCurvePoint{10.0, 0.75},
};
constexpr std::array kInvalidLatePoint{
    SteeringCurvePoint{0.0, 0.75},
    SteeringCurvePoint{10.0, 0.50},
    SteeringCurvePoint{20.0, 0.75},
};
constexpr double kNan = std::numeric_limits<double>::quiet_NaN();
constexpr double kInfinity = std::numeric_limits<double>::infinity();
constexpr std::array kNanSpeed{
    SteeringCurvePoint{kNan, 0.75},
    SteeringCurvePoint{10.0, 0.50},
};
constexpr std::array kInfiniteSpeed{
    SteeringCurvePoint{0.0, 0.75},
    SteeringCurvePoint{kInfinity, 0.50},
};
constexpr std::array kNanLimit{
    SteeringCurvePoint{0.0, kNan},
    SteeringCurvePoint{10.0, 0.50},
};
constexpr std::array kInfiniteLimit{
    SteeringCurvePoint{0.0, kInfinity},
    SteeringCurvePoint{10.0, 0.50},
};

constexpr bool IsValue(std::optional<double> actual, double expected) {
    return actual.has_value() && *actual == expected;
}

static_assert(IsValidSteeringCurve(kCurve));
static_assert(IsValidSteeringCurve(kFlatCurve));
static_assert(IsValidSteeringCurve(kZeroCurve));
static_assert(IsValidSteeringCurve(kLargeSpeedCurve));
static_assert(IsValidSteeringCurve(kTinySpeedCurve));
static_assert(!IsValidSteeringCurve(std::span<const SteeringCurvePoint>{}));
static_assert(!SteeringLimitForSpeed(std::span<const SteeringCurvePoint>{}, 0.0).has_value());
static_assert(!ClampSteeringForSpeed(std::span<const SteeringCurvePoint>{}, 0.0, 0.0).has_value());
static_assert(!IsValidSteeringCurve(kSinglePoint));
static_assert(!IsValidSteeringCurve(kDuplicateSpeed));
static_assert(!IsValidSteeringCurve(kDescendingSpeed));
static_assert(!IsValidSteeringCurve(kNegativeSpeed));
static_assert(!IsValidSteeringCurve(kNegativeLimit));
static_assert(!IsValidSteeringCurve(kExcessLimit));
static_assert(!IsValidSteeringCurve(kIncreasingLimit));
static_assert(!IsValidSteeringCurve(kNanSpeed));
static_assert(!IsValidSteeringCurve(kInfiniteSpeed));
static_assert(!IsValidSteeringCurve(kNanLimit));
static_assert(!IsValidSteeringCurve(kInfiniteLimit));

static_assert(IsValue(SteeringLimitForSpeed(kCurve, 0.0), 0.75));
static_assert(IsValue(SteeringLimitForSpeed(kCurve, 5.0), 0.625));
static_assert(IsValue(SteeringLimitForSpeed(kCurve, 10.0), 0.50));
static_assert(IsValue(SteeringLimitForSpeed(kCurve, 15.0), 0.375));
static_assert(IsValue(SteeringLimitForSpeed(kCurve, 20.0), 0.25));
static_assert(IsValue(SteeringLimitForSpeed(kCurve, -0.0), 0.75));
static_assert(IsValue(SteeringLimitForSpeed(kLargeSpeedCurve,
    std::numeric_limits<double>::max() * 0.5), 0.75));
static_assert(IsValue(SteeringLimitForSpeed(kTinySpeedCurve,
    std::numeric_limits<double>::denorm_min()), 0.75));
static_assert(!SteeringLimitForSpeed(kOffsetCurve, 4.0).has_value());
static_assert(!SteeringLimitForSpeed(kOffsetCurve, 16.0).has_value());
static_assert(!SteeringLimitForSpeed(kCurve, -1.0).has_value());
static_assert(!SteeringLimitForSpeed(kCurve, kNan).has_value());
static_assert(!SteeringLimitForSpeed(kCurve, kInfinity).has_value());
static_assert(!SteeringLimitForSpeed(kCurve, -kInfinity).has_value());
static_assert(!SteeringLimitForSpeed(kInvalidLatePoint, 5.0).has_value());

static_assert(IsValue(ClampSteeringForSpeed(kCurve, 5.0, 0.90), 0.625));
static_assert(IsValue(ClampSteeringForSpeed(kCurve, 5.0, -0.90), -0.625));
static_assert(IsValue(ClampSteeringForSpeed(kCurve, 5.0, 0.25), 0.25));
static_assert(IsValue(ClampSteeringForSpeed(kCurve, 5.0, 0.0), 0.0));
static_assert(IsValue(ClampSteeringForSpeed(kZeroCurve, 5.0, -0.5), 0.0));
static_assert(!ClampSteeringForSpeed(kCurve, 5.0, 1.01).has_value());
static_assert(!ClampSteeringForSpeed(kCurve, 5.0, -1.01).has_value());
static_assert(!ClampSteeringForSpeed(kCurve, 5.0, kNan).has_value());
static_assert(!ClampSteeringForSpeed(kCurve, 5.0, kInfinity).has_value());
static_assert(!ClampSteeringForSpeed(kCurve, 5.0, -kInfinity).has_value());
static_assert(!ClampSteeringForSpeed(kCurve, kNan, 0.0).has_value());
static_assert(!ClampSteeringForSpeed(kInvalidLatePoint, 5.0, 0.0).has_value());

} // namespace
