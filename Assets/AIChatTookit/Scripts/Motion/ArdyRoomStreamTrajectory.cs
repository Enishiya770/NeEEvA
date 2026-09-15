using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>Time-parameterized, checked character-root path, encoded as ARDY pelvis controls.</summary>
    public static class ArdyRoomStreamTrajectory
    {
        /// <summary>One native session's geometric continuation; discard on cancellation or rejected response.</summary>
        public sealed class PathState
        {
            internal List<Knot> path;
            internal float resumeDistance;
            internal Quaternion anchorRotation;
            public float CommittedSpeed { get; internal set; }
            public string LastPlanningMode { get; internal set; }
            public float LastHardInitialSpeed { get; internal set; }
            public float LastNominalInitialSpeed { get; internal set; }
            public bool HasPlan => path != null && path.Count > 0;
            public void Clear() { path = null; resumeDistance = 0; CommittedSpeed = 0; LastPlanningMode = null;
                LastHardInitialSpeed = LastNominalInitialSpeed = 0; }
        }

        public static ArdyLocomotionTarget[] Build(ArdyRoomRoute route, Vector3 sourcePosition,
            float sourceHeading, float worldSpeed, float desiredSpeed, Quaternion anchorRotation,
            Vector3 anchorGround, float scale, int frames = 40, float terminalSpeed = 0,
            Vector3 rootOffset = default, Func<Vector3, Vector3, bool> validateRootSegment = null,
            PathState continuity = null)
        {
            if (route?.Corners == null || route.Corners.Length == 0 || !ArdyLocomotionClip.Finite(sourcePosition) ||
                !ArdyLocomotionClip.Finite(desiredSpeed) || !ArdyLocomotionClip.Finite(worldSpeed) ||
                !ArdyLocomotionClip.Finite(terminalSpeed) || !ArdyLocomotionClip.Finite(sourceHeading) ||
                !ArdyLocomotionClip.Finite(rootOffset) || !ArdyLocomotionClip.Finite(anchorGround) ||
                !ArdyLocomotionClip.Finite(scale) || scale <= 0 || frames != 40 || route.Corners.Length > 256)
                throw new ArgumentException("Invalid continuous route or measured velocity.");
            // Offset is the imported pelvis's horizontal position in avatar metres (already
            // multiplied by avatar world scale), not in ARDY source metres.
            rootOffset.y = 0;
            Vector3 pelvis = anchorGround + anchorRotation * new Vector3(sourcePosition.x * scale, 0, sourcePosition.z * scale);
            Vector3 root = pelvis - anchorRotation * Quaternion.Euler(0, sourceHeading, 0) * rootOffset;
            root.y = anchorGround.y;
            var corners = new List<Vector3> { root };
            for (int i = 0; i < route.Corners.Length; i++)
            {
                if (!ArdyLocomotionClip.Finite(route.Corners[i])) throw new ArgumentException("Invalid route corner.");
                Vector3 point = route.Corners[i]; point.y = root.y;
                if (i == 0 && Vector3.Distance(point, root) > .001f)
                    throw new InvalidOperationException("route-control-start-mismatch: plan must start at the actual character root, not its pelvis or a projected substitute.");
                if (Vector3.Distance(corners[corners.Count - 1], point) > .00001f) corners.Add(point);
            }
            float speed = Mathf.Clamp(worldSpeed, 0, 1.5f * scale);
            if (speed <= .000001f) speed = 0; // Numerical zero, not the millimetre/s idle-sway range.
            float limit = Mathf.Min(1.2f, 1.45f * scale);
            desiredSpeed = Mathf.Clamp(desiredSpeed, 0, limit);
            terminalSpeed = Mathf.Clamp(terminalSpeed, 0, desiredSpeed);
            int geometryBudget = 96;
            // A NavMesh projection can introduce a very short first waypoint. Remove it only
            // when the same root guard certifies the replacement segment; never silently snap.
            if (corners.Count > 2 && Vector3.Distance(corners[0], corners[1]) < .08f &&
                Checked(corners[0], corners[2], validateRootSegment, ref geometryBudget)) corners.RemoveAt(1);
            var path = WarmPath(continuity, corners, sourceHeading, speed, anchorRotation, validateRootSegment, ref geometryBudget);
            if (continuity != null) continuity.LastPlanningMode = path != null ? "warm-continuation" : "new-path";
            path = path ?? BuildPath(corners, sourceHeading, speed, anchorRotation, validateRootSegment, ref geometryBudget);
            Parameterize(path, speed, desiredSpeed, terminalSpeed, limit, continuity);
            // A nonzero terminal speed is useful for a moving user's lookahead only when
            // this window does not run beyond that checked endpoint. Otherwise plan a real
            // deceleration; holding the endpoint at speed would introduce an abrupt stop.
            if (terminalSpeed > 0 && path[path.Count - 1].time < frames / 20f - .0001f)
                Parameterize(path, speed, desiredSpeed, 0, limit, continuity);
            var targets = new ArdyLocomotionTarget[frames];
            for (int i = 0; i < frames; i++)
            {
                Sample(path, (i + 1) / 20f, out Vector3 point, out float heading);
                Vector3 targetPelvis = point + anchorRotation * Quaternion.Euler(0, heading, 0) * rootOffset;
                Vector3 source = Quaternion.Inverse(anchorRotation) * (targetPelvis - anchorGround) / scale; source.y = 0;
                targets[i] = new ArdyLocomotionTarget { rootPosition = source, headingDegrees = heading };
            }
            if (continuity != null)
            {
                SampleState(path, .4f, out float committedDistance, out float committedSpeed);
                continuity.path = path;
                continuity.resumeDistance = committedDistance;
                continuity.CommittedSpeed = committedSpeed;
                continuity.anchorRotation = anchorRotation;
            }
            return targets;
        }

        private const float Spacing = .025f, AngularSpeed = 80f, NominalAngularSpeed = 60f,
            Acceleration = 1.25f, Braking = 1.2f, NominalBraking = .8f;
        internal sealed class Knot
        {
            internal Vector3 point;
            internal float heading, speed, distance, time;
            internal bool stop;
        }

        private static List<Knot> WarmPath(PathState state, List<Vector3> corners, float initialHeading, float initialSpeed,
            Quaternion anchorRotation, Func<Vector3, Vector3, bool> validator, ref int budget)
        {
            if (state == null || !state.HasPlan || state.path.Count < 2 || initialSpeed <= .000001f || validator == null ||
                Quaternion.Angle(state.anchorRotation, anchorRotation) > .001f) return null;
            var old = state.path;
            // Continue from the committed control time, not the nearest point to the
            // model's root. Projecting a centimetre of forward tracking error onto the
            // old path consumes braking distance while retaining the old velocity.
            Vector3 root = corners[0];
            float near = Mathf.Clamp(state.resumeDistance, 0, old[old.Count - 1].distance);
            AtDistance(old, near, out Vector3 originalRoot, out float originalHeading);
            if (Vector3.Distance(root, originalRoot) > .08f) return null;
            float remaining = old[old.Count - 1].distance - near;
            if (remaining < .04f) return null;
            float keepLength = Mathf.Min(remaining, Mathf.Max(.4f, initialSpeed * .7f));
            float keepDistance = near + keepLength;
            int endIndex = old.Count - 1;
            for (int i = 1; i < old.Count; i++)
            {
                if (old[i].distance < keepDistance) continue;
                endIndex = i;
                // Join a straight part just after the current bend when it is close. This
                // keeps that bend's radius fixed as the acknowledged seam approaches it.
                for (int j = i; j < old.Count - 1 && old[j].distance <= keepDistance + .25f; j++)
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(old[j].heading, old[j + 1].heading)) < .05f && !old[j].stop)
                    { endIndex = j; break; }
                }
                break;
            }
            keepDistance = old[endIndex].distance;
            keepLength = keepDistance - near;
            // A rigid local correction preserves the already feasible arc lengths,
            // curvature and distance to a planned stop. It starts at the actual native
            // pose; every transformed segment must still pass the current room guard.
            float headingCorrection = Mathf.DeltaAngle(originalHeading, initialHeading);
            Quaternion correction = Quaternion.Euler(0, headingCorrection, 0);
            var path = new List<Knot> { new Knot { point = root, heading = initialHeading } };
            for (int i = 1; i <= endIndex; i++)
            {
                if (old[i].distance <= near + .00001f) continue;
                Vector3 point = root + correction * (old[i].point - originalRoot);
                if (!Checked(path[path.Count - 1].point, point, validator, ref budget)) return null;
                path.Add(new Knot { point = point, heading = old[i].heading + headingCorrection, stop = old[i].stop });
            }
            if (path.Count < 2) return null;
            Vector3 join = path[path.Count - 1].point;
            Vector3 goal = corners[corners.Count - 1];
            if (Vector3.Distance(join, goal) < .001f) return path;

            // Goals may move every update. Keep the current safe bend and attach the newly
            // planned route beyond it. The connector itself must be accepted by the same guard.
            int closestSegment = 1; float closest = float.PositiveInfinity;
            for (int i = 1; i < corners.Count; i++)
            {
                Vector3 delta = corners[i] - corners[i - 1];
                float t = delta.sqrMagnitude < .000000001f ? 0 : Mathf.Clamp01(Vector3.Dot(join - corners[i - 1], delta) / delta.sqrMagnitude);
                float error = Vector3.SqrMagnitude(join - corners[i - 1] - delta * t);
                if (error < closest) { closest = error; closestSegment = i; }
            }
            int selected = -1;
            for (int i = closestSegment; i < corners.Count && i < closestSegment + 8; i++)
            {
                if (Vector3.Distance(join, corners[i]) < .005f) continue;
                if (Checked(join, corners[i], validator, ref budget)) { selected = i; break; }
            }
            if (selected < 0) return null;
            var tailCorners = new List<Vector3> { join };
            for (int i = selected; i < corners.Count; i++) tailCorners.Add(corners[i]);
            List<Knot> tail;
            try
            {
                tail = BuildPath(tailCorners, path[path.Count - 1].heading, Mathf.Max(.05f, old[endIndex].speed),
                    anchorRotation, validator, ref budget);
            }
            catch (InvalidOperationException)
            {
                // No feasible optional connector curve: put a genuine planned stop at the
                // end of the preserved prefix, then turn along the already-checked connector.
                tail = BuildPath(tailCorners, path[path.Count - 1].heading, 0, anchorRotation, validator, ref budget);
            }
            path[path.Count - 1].stop |= tail[0].stop;
            for (int i = 1; i < tail.Count; i++) path.Add(tail[i]);
            return path;
        }

        private static void AtDistance(List<Knot> path, float distance, out Vector3 point, out float heading)
        {
            for (int i = 1; i < path.Count; i++)
            {
                if (path[i].distance < distance) continue;
                float length = path[i].distance - path[i - 1].distance;
                float t = length > .000001f ? Mathf.Clamp01((distance - path[i - 1].distance) / length) : 1;
                point = Vector3.Lerp(path[i - 1].point, path[i].point, t);
                heading = Mathf.LerpAngle(path[i - 1].heading, path[i].heading, t); return;
            }
            point = path[path.Count - 1].point; heading = path[path.Count - 1].heading;
        }

        private static List<Knot> BuildPath(List<Vector3> corners, float initialHeading, float initialSpeed,
            Quaternion anchorRotation, Func<Vector3, Vector3, bool> validator, ref int budget)
        {
            var path = new List<Knot> { new Knot { point = corners[0], heading = initialHeading } };
            if (corners.Count == 1) return path;
            Quaternion inverse = Quaternion.Inverse(anchorRotation);
            Vector3 firstDirection = (corners[1] - corners[0]).normalized;
            float firstHeading = Heading(inverse * firstDirection);
            float initialTurn = Mathf.Abs(Mathf.DeltaAngle(initialHeading, firstHeading));
            if (initialSpeed > .000001f && initialSpeed <= .005f && initialTurn > .5f)
            {
                // A tiny real root velocity is still momentum. Brake over its actual short
                // distance before marking a zero-speed turn, instead of both classifying it
                // as stationary and later rejecting its nonzero initial speed.
                float stoppingDistance = initialSpeed * initialSpeed / (2 * Braking) + .0000001f;
                Vector3 stop = corners[0] + anchorRotation * Quaternion.Euler(0, initialHeading, 0) * Vector3.forward * stoppingDistance;
                if (!Checked(corners[0], stop, validator, ref budget) || !Checked(stop, corners[1], validator, ref budget))
                    throw new InvalidOperationException("route-control-micro-braking: the short physical stop and turn connector were not certified.");
                path.Add(new Knot { point = stop, heading = initialHeading, stop = true });
                Turn(path, Heading(inverse * (corners[1] - stop)));
            }
            else if (initialSpeed > .000001f && initialTurn > .5f)
            {
                // Replanning changes the desired tangent, but cannot erase current momentum.
                // An initial tangent-preserving cubic is admitted only by the actual root guard.
                float blend = Mathf.Min(Vector3.Distance(corners[0], corners[1]) * .60f,
                    Mathf.Max(.20f, initialSpeed * .65f));
                Vector3 a = corners[0], d = a + firstDirection * blend;
                Vector3 b = a + anchorRotation * Quaternion.Euler(0, initialHeading, 0) * Vector3.forward * (blend / 3f);
                Vector3 c = d - firstDirection * (blend / 3f);
                var curve = new List<Knot>();
                int count = Mathf.Clamp(Mathf.CeilToInt(blend / Spacing), 8, 24);
                Vector3 prior = a;
                bool safe = blend >= .04f && initialTurn < 100f;
                for (int j = 1; j <= count && safe; j++)
                {
                    float t = j / (float)count, u = 1 - t;
                    Vector3 p = u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
                    Vector3 tangent = 3 * u * u * (b - a) + 6 * u * t * (c - b) + 3 * t * t * (d - c);
                    safe = tangent.sqrMagnitude > .00000001f && Checked(prior, p, validator, ref budget);
                    curve.Add(new Knot { point = p, heading = Heading(inverse * tangent) }); prior = p;
                }
                if (!safe) throw new InvalidOperationException("route-control-initial-turn: no checked continuous turning corridor for the current velocity.");
                path.AddRange(curve);
            }
            else if (initialSpeed <= .000001f && initialTurn > .001f)
                Turn(path, firstHeading);

            for (int i = 1; i < corners.Count - 1; i++)
            {
                Vector3 corner = corners[i];
                Vector3 incoming = (corner - path[path.Count - 1].point).normalized;
                Vector3 outgoing = (corners[i + 1] - corner).normalized;
                float angle = Vector3.SignedAngle(incoming, outgoing, Vector3.up);
                if (Mathf.Abs(angle) < .1f) { Line(path, corner, Heading(inverse * incoming)); continue; }
                float trim = Mathf.Min(.32f, Vector3.Distance(path[path.Count - 1].point, corner) * .40f,
                    Vector3.Distance(corner, corners[i + 1]) * .40f);
                List<Knot> accepted = null;
                if (Mathf.Abs(angle) < 175f)
                {
                    // At most three radii and a shared fixed query budget. Narrow corners use
                    // the original certified polyline with a stop/turn, never an unchecked arc.
                    for (int attempt = 0; attempt < 3 && accepted == null; attempt++, trim *= .5f)
                    {
                        if (trim < .01f || validator == null || budget <= 0) break;
                        float radius = trim / Mathf.Tan(Mathf.Abs(angle) * Mathf.Deg2Rad / 2f);
                        Vector3 begin = corner - incoming * trim;
                        Vector3 center = begin + Quaternion.Euler(0, Mathf.Sign(angle) * 90, 0) * incoming * radius;
                        Vector3 radial = begin - center;
                        int count = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(radius * Mathf.Abs(angle) * Mathf.Deg2Rad / .04f,
                            Mathf.Abs(angle) / 8f)), 2, 28);
                        var arc = new List<Knot> { new Knot { point = begin, heading = Heading(inverse * incoming) } };
                        bool safe = true; Vector3 previous = begin;
                        for (int j = 1; j <= count && safe; j++)
                        {
                            float turn = angle * j / count;
                            Vector3 point = center + Quaternion.Euler(0, turn, 0) * radial;
                            safe = Checked(previous, point, validator, ref budget);
                            arc.Add(new Knot { point = point, heading = Heading(inverse * (Quaternion.Euler(0, turn, 0) * incoming)) });
                            previous = point;
                        }
                        if (safe) accepted = arc;
                    }
                }
                if (accepted != null)
                {
                    Line(path, accepted[0].point, accepted[0].heading);
                    for (int j = 1; j < accepted.Count; j++) path.Add(accepted[j]);
                }
                else
                {
                    Line(path, corner, Heading(inverse * incoming));
                    Turn(path, Heading(inverse * outgoing));
                }
            }
            Vector3 end = corners[corners.Count - 1];
            Vector3 lastDirection = end - path[path.Count - 1].point;
            if (lastDirection.sqrMagnitude > .000000001f) Line(path, end, Heading(inverse * lastDirection));
            return path;
        }

        private static bool Checked(Vector3 from, Vector3 to, Func<Vector3, Vector3, bool> validator, ref int budget)
        {
            if (validator == null || budget <= 0) return false;
            // Geometry candidates can span metres, whereas the live guard deliberately
            // accepts only bounded body steps. Preserve that contract instead of treating
            // a long clear connector as an obstacle (or weakening the live step limit).
            int count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to) / .25f));
            if (count > budget) return false;
            Vector3 previous = from;
            for (int i = 1; i <= count; i++)
            {
                Vector3 next = Vector3.Lerp(from, to, i / (float)count);
                budget--;
                if (!validator(previous, next)) return false;
                previous = next;
            }
            return true;
        }

        private static float Heading(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        private static void Line(List<Knot> path, Vector3 end, float heading)
        {
            Vector3 begin = path[path.Count - 1].point;
            int count = Mathf.CeilToInt(Vector3.Distance(begin, end) / Spacing);
            for (int i = 1; i <= count; i++)
                path.Add(new Knot { point = Vector3.Lerp(begin, end, i / (float)count), heading = heading });
        }

        private static void Turn(List<Knot> path, float heading)
        {
            path[path.Count - 1].stop = true;
            path.Add(new Knot { point = path[path.Count - 1].point, heading = heading, stop = true });
        }

        private static void Parameterize(List<Knot> path, float initialSpeed, float desiredSpeed, float terminalSpeed, float maximumSpeed,
            PathState diagnostics)
        {
            var hard = new float[path.Count];
            var nominal = new float[path.Count];
            float distance = 0;
            for (int i = 0; i < path.Count; i++)
            {
                if (i > 0) distance += Vector3.Distance(path[i - 1].point, path[i].point);
                path[i].distance = distance;
                hard[i] = path[i].stop ? 0 : Mathf.Max(maximumSpeed, initialSpeed);
                nominal[i] = path[i].stop ? 0 : desiredSpeed;
            }
            for (int i = 1; i < path.Count; i++)
            {
                float ds = path[i].distance - path[i - 1].distance;
                float angle = Mathf.Abs(Mathf.DeltaAngle(path[i - 1].heading, path[i].heading));
                if (ds > .000001f && angle > .00001f)
                {
                    float hardCurvature = ds * AngularSpeed / angle;
                    float nominalCurvature = ds * NominalAngularSpeed / angle;
                    hard[i - 1] = Mathf.Min(hard[i - 1], hardCurvature);
                    hard[i] = Mathf.Min(hard[i], hardCurvature);
                    nominal[i - 1] = Mathf.Min(nominal[i - 1], nominalCurvature);
                    nominal[i] = Mathf.Min(nominal[i], nominalCurvature);
                }
            }
            hard[hard.Length - 1] = Mathf.Min(hard[hard.Length - 1], terminalSpeed);
            nominal[nominal.Length - 1] = Mathf.Min(nominal[nominal.Length - 1], terminalSpeed);
            for (int i = path.Count - 2; i >= 0; i--)
            {
                float ds = path[i + 1].distance - path[i].distance;
                hard[i] = Mathf.Min(hard[i], Mathf.Sqrt(hard[i + 1] * hard[i + 1] + 2 * Braking * ds));
                nominal[i] = Mathf.Min(nominal[i], Mathf.Sqrt(nominal[i + 1] * nominal[i + 1] + 2 * NominalBraking * ds));
            }
            // Nominal planning leaves tracking/error room. It is not a hard admission
            // test: a measured seam can be faster than nominal while remaining physically
            // feasible. Only the independently computed hard envelope may reject it.
            if (diagnostics != null) { diagnostics.LastHardInitialSpeed = hard[0]; diagnostics.LastNominalInitialSpeed = nominal[0]; }
            if (initialSpeed > hard[0] + .0001f)
                throw new InvalidOperationException($"route-control-braking-distance: current speed {initialSpeed:F3} m/s exceeds safe initial speed {hard[0]:F3} m/s for this turning/stopping corridor ({diagnostics?.LastPlanningMode ?? "new-path"}, nominal {nominal[0]:F3}).");
            path[0].speed = initialSpeed;
            for (int i = 1; i < path.Count; i++)
            {
                Knot previous = path[i - 1], next = path[i];
                float ds = next.distance - previous.distance;
                float minimum = Mathf.Sqrt(Mathf.Max(0, previous.speed * previous.speed - 2 * Braking * ds));
                float maximum = Mathf.Sqrt(previous.speed * previous.speed + 2 * Acceleration * ds);
                // Reach nominal gradually from the actual committed speed; never reset
                // velocity to its lower nominal value or spend more than the hard budget.
                next.speed = Mathf.Min(hard[i], Mathf.Clamp(nominal[i], minimum, maximum));
                float dt = ds > .000001f ? (previous.speed + next.speed > .000001f ? 2 * ds / (previous.speed + next.speed) : float.PositiveInfinity) :
                    Mathf.Abs(Mathf.DeltaAngle(previous.heading, next.heading)) / AngularSpeed;
                next.time = previous.time + dt;
            }
        }

        private static void Sample(List<Knot> path, float time, out Vector3 point, out float heading)
        {
            for (int i = 1; i < path.Count; i++)
            {
                Knot a = path[i - 1], b = path[i];
                if (time > b.time) continue;
                float duration = b.time - a.time;
                float fraction = 0;
                if (!float.IsInfinity(duration) && duration > .000001f)
                {
                    float elapsed = Mathf.Clamp(time - a.time, 0, duration);
                    float ds = b.distance - a.distance;
                    fraction = ds > .000001f ? (a.speed * elapsed + .5f * (b.speed - a.speed) / duration * elapsed * elapsed) / ds : elapsed / duration;
                }
                point = Vector3.Lerp(a.point, b.point, fraction);
                heading = Mathf.LerpAngle(a.heading, b.heading, fraction); return;
            }
            point = path[path.Count - 1].point; heading = path[path.Count - 1].heading;
        }

        private static void SampleState(List<Knot> path, float time, out float distance, out float speed)
        {
            for (int i = 1; i < path.Count; i++)
            {
                Knot a = path[i - 1], b = path[i];
                if (time > b.time) continue;
                float duration = b.time - a.time;
                if (float.IsInfinity(duration) || duration <= .000001f)
                { distance = a.distance; speed = a.speed; return; }
                float elapsed = Mathf.Clamp(time - a.time, 0, duration);
                float acceleration = (b.speed - a.speed) / duration;
                distance = a.distance + a.speed * elapsed + .5f * acceleration * elapsed * elapsed;
                speed = Mathf.Max(0, a.speed + acceleration * elapsed); return;
            }
            distance = path[path.Count - 1].distance; speed = path[path.Count - 1].speed;
        }

        public static ArdyLocomotionClip Splice(ArdyLocomotionClip previous, int prefix, ArdyLocomotionClip next,
            int drop, out float rootStep, out float jointStep, Func<int, bool> appliedJoint = null)
        {
            previous.Validate(); next.Validate();
            if (drop < 0 || prefix <= drop || prefix > previous.frames.Length)
                throw new ArgumentException("Invalid committed streaming prefix.");
            rootStep = Vector3.Distance(previous.frames[prefix - 1].rootPosition, next.frames[0].rootPosition);
            jointStep = 0;
            for (int j = 0; j < 27; j++)
                if (appliedJoint == null || appliedJoint(j)) jointStep = Mathf.Max(jointStep, Quaternion.Angle(
                    previous.rotationClip.SampleDelta(j, (prefix - 1) / 20f), next.rotationClip.SampleDelta(j, 0)));
            if (rootStep > .1f || jointStep > 35f)
                throw new InvalidOperationException($"Streaming seam is discontinuous: source root {rootStep:F4} m, joint {jointStep:F2} degrees.");
            int count = prefix - drop + next.frames.Length;
            var result = new ArdyLocomotionClip { schema = previous.schema, id = previous.id,
                sourceRootHeight = previous.sourceRootHeight, sourceOrigin = previous.sourceOrigin,
                sourceHeadingDegrees = previous.sourceHeadingDegrees, source = previous.source,
                frames = new ArdyLocomotionFrame[count], targets = new ArdyLocomotionTarget[count],
                rotationClip = new ArdyMotionClip { schema = previous.rotationClip.schema, id = previous.rotationClip.id,
                    text = previous.rotationClip.text, fps = 20, source = previous.rotationClip.source,
                    jointNames = previous.rotationClip.jointNames, jointParents = previous.rotationClip.jointParents,
                    restGlobalRotations = previous.rotationClip.restGlobalRotations, frames = new ArdyMotionFrame[count] } };
            for (int i = 0; i < count; i++)
            {
                bool old = i < prefix - drop; var from = old ? previous : next;
                int index = old ? i + drop : i - prefix + drop;
                result.frames[i] = from.frames[index]; result.targets[i] = from.targets[index];
                result.rotationClip.frames[i] = from.rotationClip.frames[index];
            }
            result.Validate(); return result;
        }
    }
}
