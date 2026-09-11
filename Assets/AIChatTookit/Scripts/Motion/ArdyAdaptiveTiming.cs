using System;

namespace NeEEvA.Motion
{
    /// <summary>A resolved command schedule, not a measurement of the rendered avatar's speed.</summary>
    [Serializable]
    public sealed class ArdyResolvedTiming
    {
        public string policy, tempo, curve, reason;
        public string limitScope = "commanded-local-angle; external Animator/root motion excluded";
        public bool durationFixed;
        public int cycles;
        public float amplitude, actualSeconds, targetFrequencyHz, resolvedFrequencyHz;
        public float velocityLimit, accelerationLimit, velocityBound, accelerationBound;
        public float normalizedVelocityBound, normalizedAccelerationBound, envelopeFraction;
        public float preparationSeconds, preparationAngularDistance;
        public float preparationVelocityRatio, preparationAccelerationRatio;
        public string preparationLimitingJoint;
        public ArdyResolvedTiming Copy() => (ArdyResolvedTiming)MemberwiseClone();
    }

    public struct ArdyTimingSample
    {
        public double value, velocity, acceleration;
    }

    /// <summary>
    /// Opt-in C2 command curves. Bounds enclose the complete curve, including both envelopes.
    /// Pure System.Math code so the runtime resolver and its proof checks can run without Unity.
    /// </summary>
    public static class ArdyAdaptiveTiming
    {
        public const double EnvelopeFraction = 0.15;
        public const double WristVelocityLimit = 160, WristAccelerationLimit = 1500;
        public const double HeadVelocityLimit = 60, HeadAccelerationLimit = 400;
        public const double MinimumPreparationSeconds = 0.2, MaximumPreparationSeconds = 2.7;
        public const double QuinticVelocityMaximum = 1.875;
        public const double QuinticAccelerationMaximum = 5.773502691896258;
        private static readonly Bounds[] CurveBounds = { default(Bounds), FindBounds(1), FindBounds(2), FindBounds(3) };

        public static ArdyResolvedTiming Resolve(ArdyControlPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            plan.Validate();
            var result = new ArdyResolvedTiming {
                policy = plan.timingPolicy, tempo = plan.tempo, durationFixed = plan.timingDurationFixed,
                cycles = plan.cycles, amplitude = plan.joint == "none" ? 0 : plan.amplitude
            };
            if (plan.timingPolicy == "legacy")
            {
                result.curve = "legacy-cubic-envelope-sine";
                result.reason = "legacy-numeric-duration-unchanged";
                result.actualSeconds = plan.seconds;
                result.targetFrequencyHz = result.resolvedFrequencyHz = plan.joint == "none" ? 0 : plan.cycles / plan.seconds;
                result.preparationSeconds = 1.2f;
                return result;
            }
            result.curve = "quintic-envelope-sine-v1";
            result.envelopeFraction = (float)EnvelopeFraction;
            bool head = plan.joint == "head";
            result.velocityLimit = (float)(head ? HeadVelocityLimit : WristVelocityLimit);
            result.accelerationLimit = (float)(head ? HeadAccelerationLimit : WristAccelerationLimit);
            if (plan.joint == "none")
            {
                result.actualSeconds = plan.end == "hold" ? 0 : plan.seconds;
                result.reason = plan.end == "hold" ? "pure-hold-no-local-curve" : "static-dwell-numeric-duration";
                return result;
            }
            double target = head ? (plan.tempo == "gentle" ? 0.45 : plan.tempo == "brisk" ? 0.85 : 0.65)
                : (plan.tempo == "gentle" ? 0.65 : plan.tempo == "brisk" ? 1.4 : 1.05);
            Bounds bounds = CurveBounds[plan.cycles];
            result.targetFrequencyHz = (float)target;
            result.normalizedVelocityBound = UpperFloat(bounds.velocity);
            result.normalizedAccelerationBound = UpperFloat(bounds.acceleration);
            double velocitySeconds = plan.amplitude * bounds.velocity / result.velocityLimit;
            double accelerationSeconds = Math.Sqrt(plan.amplitude * bounds.acceleration / result.accelerationLimit);
            double required = Math.Max(1, Math.Max(plan.cycles / 1.5, Math.Max(velocitySeconds, accelerationSeconds)));
            double duration;
            if (plan.timingDurationFixed)
            {
                duration = plan.seconds;
                if (duration < required)
                    throw new ArgumentException("adaptive-timing-unreachable: explicit seconds exceed a full-curve velocity, acceleration or frequency limit; amplitude was not reduced.");
                result.reason = "explicit-duration-verified";
            }
            else
            {
                double preferred = plan.cycles / target;
                duration = Math.Max(preferred, required);
                result.reason = required > preferred
                    ? (accelerationSeconds >= velocitySeconds && accelerationSeconds >= 1 && accelerationSeconds >= plan.cycles / 1.5
                        ? "extended-for-acceleration-limit" : velocitySeconds >= 1 && velocitySeconds >= plan.cycles / 1.5
                            ? "extended-for-velocity-limit" : "extended-for-duration-or-frequency-limit")
                    : "joint-tempo-target";
                // Round upwards so storing the schedule as a Unity float never shortens the proven duration.
                duration = Math.Ceiling(duration * 100000) / 100000;
            }
            if (duration > 6)
                throw new ArgumentException("adaptive-timing-unreachable: the requested cycles and amplitude require more than 6 seconds; amplitude and cycles were not reduced.");
            result.actualSeconds = plan.timingDurationFixed ? plan.seconds : UpperFloat(duration);
            // An exact six-second request is permitted; avoid an upward audit float passing that boundary.
            if (result.actualSeconds > 6) result.actualSeconds = 6;
            result.resolvedFrequencyHz = plan.cycles / result.actualSeconds;
            result.velocityBound = UpperFloat(plan.amplitude * bounds.velocity / result.actualSeconds);
            result.accelerationBound = UpperFloat(plan.amplitude * bounds.acceleration / (result.actualSeconds * (double)result.actualSeconds));
            return result;
        }

        /// <summary>Angle in degrees and its derivatives in degrees/s and degrees/s².</summary>
        public static ArdyTimingSample Evaluate(ArdyResolvedTiming timing, double seconds)
        {
            if (timing == null) throw new ArgumentNullException(nameof(timing));
            if (timing.policy != "adaptive-v1") throw new ArgumentException("The legacy curve stays in its original float execution path.");
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (timing.amplitude == 0 || timing.actualSeconds <= 0 || seconds <= 0 || seconds >= timing.actualSeconds)
                return default(ArdyTimingSample);
            double u = seconds / timing.actualSeconds, w = 2 * Math.PI * timing.cycles;
            Envelope(u, out double e, out double d, out double dd);
            double sin = Math.Sin(w * u), cos = Math.Cos(w * u), scale = timing.amplitude;
            return new ArdyTimingSample {
                value = scale * e * sin,
                velocity = scale * (d * sin + e * w * cos) / timing.actualSeconds,
                acceleration = scale * (dd * sin + 2 * d * w * cos - e * w * w * sin)
                    / (timing.actualSeconds * (double)timing.actualSeconds)
            };
        }

        public static double ResolvePreparation(double angularDistance, double velocityLimit, double accelerationLimit)
        {
            if (!Finite(angularDistance) || angularDistance < 0 || !Finite(velocityLimit) || velocityLimit <= 0
                || !Finite(accelerationLimit) || accelerationLimit <= 0) throw new ArgumentOutOfRangeException(nameof(angularDistance));
            double seconds = Math.Max(MinimumPreparationSeconds, Math.Max(QuinticVelocityMaximum * angularDistance / velocityLimit,
                Math.Sqrt(QuinticAccelerationMaximum * angularDistance / accelerationLimit)));
            if (seconds > MaximumPreparationSeconds)
                throw new InvalidOperationException("adaptive-prepare-unreachable: the observed pose distance needs more than the 2.7-second preparation budget; no pose jump was applied.");
            return seconds;
        }

        public static double Quintic(double value)
        {
            double x = Math.Max(0, Math.Min(1, value));
            return x * x * x * (10 + x * (-15 + 6 * x));
        }

        private static double QuinticDerivative(double x) => 30 * x * x * (1 - x) * (1 - x);
        private static double QuinticSecondDerivative(double x) => 60 * x * (1 - x) * (1 - 2 * x);
        private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        private static float UpperFloat(double x)
        {
            float result = (float)x;
            return result < x ? result + Math.Max(1e-7f, Math.Abs(result) * 1.2e-7f) : result;
        }

        private static void Envelope(double u, out double e, out double d, out double dd)
        {
            if (u >= EnvelopeFraction && u <= 1 - EnvelopeFraction) { e = 1; d = dd = 0; return; }
            bool rising = u < EnvelopeFraction;
            double x = (rising ? u : 1 - u) / EnvelopeFraction;
            e = Quintic(x);
            d = QuinticDerivative(x) / EnvelopeFraction * (rising ? 1 : -1);
            dd = QuinticSecondDerivative(x) / (EnvelopeFraction * EnvelopeFraction);
        }

        private struct Bounds { public double velocity, acceleration; }
        private struct Interval
        {
            public double lo, hi;
            public Interval(double low, double high) { lo = low; hi = high; }
            public double AbsMaximum => Math.Max(Math.Abs(lo), Math.Abs(hi));
            public static Interval operator +(Interval a, Interval b) => new Interval(a.lo + b.lo, a.hi + b.hi);
            public static Interval operator *(Interval a, double b) => b >= 0
                ? new Interval(a.lo * b, a.hi * b) : new Interval(a.hi * b, a.lo * b);
            public static Interval operator *(Interval a, Interval b)
            {
                double a0 = a.lo * b.lo, a1 = a.lo * b.hi, a2 = a.hi * b.lo, a3 = a.hi * b.hi;
                return new Interval(Math.Min(Math.Min(a0, a1), Math.Min(a2, a3)), Math.Max(Math.Max(a0, a1), Math.Max(a2, a3)));
            }
        }

        private static Bounds FindBounds(int cycles)
        {
            double w = 2 * Math.PI * cycles;
            // In the constant-envelope middle the exact bounds are w and w².
            var result = new Bounds { velocity = w, acceleration = w * w };
            const int partitions = 512;
            for (int side = 0; side < 2; side++)
            for (int i = 0; i < partitions; i++)
            {
                double x0 = i / (double)partitions, x1 = (i + 1) / (double)partitions;
                double u0 = side == 0 ? x0 * EnvelopeFraction : 1 - x1 * EnvelopeFraction;
                double u1 = side == 0 ? x1 * EnvelopeFraction : 1 - x0 * EnvelopeFraction;
                // Each interval encloses a continuous domain, not sampled extrema. Q is
                // monotonic; Q' extrema are 0,.5,1; Q'' extrema are (3±sqrt(3))/6.
                var e = new Interval(Quintic(x0), Quintic(x1));
                var d = PolynomialRange(x0, x1, false) * ((side == 0 ? 1 : -1) / EnvelopeFraction);
                var dd = PolynomialRange(x0, x1, true) * (1 / (EnvelopeFraction * EnvelopeFraction));
                Interval sin = TrigRange(w * u0, w * u1, false), cos = TrigRange(w * u0, w * u1, true);
                Interval velocity = d * sin + e * cos * w;
                Interval acceleration = dd * sin + d * cos * (2 * w) + e * sin * (-w * w);
                result.velocity = Math.Max(result.velocity, velocity.AbsMaximum);
                result.acceleration = Math.Max(result.acceleration, acceleration.AbsMaximum);
            }
            // Margin covers floating-point rounding in interval arithmetic and trig evaluation.
            result.velocity = result.velocity * (1 + 1e-10) + 1e-9;
            result.acceleration = result.acceleration * (1 + 1e-10) + 1e-9;
            return result;
        }

        private static Interval PolynomialRange(double x0, double x1, bool second)
        {
            double a = second ? QuinticSecondDerivative(x0) : QuinticDerivative(x0);
            double b = second ? QuinticSecondDerivative(x1) : QuinticDerivative(x1);
            var result = new Interval(Math.Min(a, b), Math.Max(a, b));
            if (second)
            {
                IncludePolynomialExtremum(ref result, x0, x1, (3 - Math.Sqrt(3)) / 6, true);
                IncludePolynomialExtremum(ref result, x0, x1, (3 + Math.Sqrt(3)) / 6, true);
            }
            else IncludePolynomialExtremum(ref result, x0, x1, 0.5, false);
            return result;
        }

        private static void IncludePolynomialExtremum(ref Interval range, double x0, double x1, double x, bool second)
        {
            if (x < x0 || x > x1) return;
            double value = second ? QuinticSecondDerivative(x) : QuinticDerivative(x);
            range.lo = Math.Min(range.lo, value); range.hi = Math.Max(range.hi, value);
        }

        private static Interval TrigRange(double x0, double x1, bool cosine)
        {
            double a = cosine ? Math.Cos(x0) : Math.Sin(x0), b = cosine ? Math.Cos(x1) : Math.Sin(x1);
            var result = new Interval(Math.Min(a, b), Math.Max(a, b));
            double offset = cosine ? 0 : Math.PI / 2;
            int first = (int)Math.Ceiling((x0 - offset) / Math.PI), last = (int)Math.Floor((x1 - offset) / Math.PI);
            for (int k = first; k <= last; k++)
            {
                double value = (k & 1) == 0 ? 1 : -1;
                result.lo = Math.Min(result.lo, value); result.hi = Math.Max(result.hi, value);
            }
            return result;
        }
    }
}
