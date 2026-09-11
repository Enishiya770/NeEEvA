using System;
using System.Collections.Generic;
using System.Globalization;

namespace NeEEvA.Motion
{
    /// <summary>A role-selected semantic plan. Empty strings preserve omitted fields for ledger merging.
    /// Source quotes are claims here; only the chat ledger can verify them against accepted user input.</summary>
    [Serializable]
    public sealed class ArdyActionPlan
    {
        public string purpose = "", mode = "", scope = "";
        public string left = "", right = "", leftPalm = "", rightPalm = "";
        public string joint = "", axis = "", tempo = "", size = "", repeat = "", end = "", text = "";
        // Numbers remain strings so omitted, explicit zero and inherited values are distinct.
        public string leftBend = "", rightBend = "", amplitude = "", cycles = "", seconds = "";
        public int userTurn;
        public string evidence = "", lockFields = "", releaseFields = "";

        private static readonly string[] valueFields = {
            "purpose", "mode", "scope", "left", "right", "leftPalm", "rightPalm", "joint", "axis",
            "tempo", "size", "repeat", "end", "text", "leftBend", "rightBend", "amplitude", "cycles", "seconds"
        };
        private static readonly string[] numericFields = { "leftBend", "rightBend", "amplitude", "cycles", "seconds" };

        public string[] LockFields => ParseFields(lockFields);
        public string[] ReleaseFields => ParseFields(releaseFields);
        public static string[] ValueFields => (string[])valueFields.Clone();

        public ArdyActionPlan Copy()
        {
            var copy = new ArdyActionPlan();
            foreach (string field in valueFields) copy.SetValue(field, GetValue(field));
            copy.userTurn = userTurn;
            copy.evidence = evidence;
            copy.lockFields = lockFields;
            copy.releaseFields = releaseFields;
            return copy;
        }

        public bool HasField(string field) => !string.IsNullOrEmpty(GetValue(field));

        public string GetValue(string field)
        {
            switch (CanonicalFieldName(field))
            {
                case "purpose": return purpose; case "mode": return mode; case "scope": return scope;
                case "left": return left; case "right": return right;
                case "leftPalm": return leftPalm; case "rightPalm": return rightPalm;
                case "joint": return joint; case "axis": return axis; case "tempo": return tempo;
                case "size": return size; case "repeat": return repeat; case "end": return end; case "text": return text;
                case "leftBend": return leftBend; case "rightBend": return rightBend;
                case "amplitude": return amplitude; case "cycles": return cycles; case "seconds": return seconds;
                case "userTurn": return userTurn > 0 ? userTurn.ToString(CultureInfo.InvariantCulture) : "";
                case "evidence": return evidence; case "lock": return lockFields; case "release": return releaseFields;
                default: throw new ArgumentException("Unknown semantic plan field: " + field);
            }
        }

        public void SetValue(string field, string value)
        {
            value = value ?? "";
            switch (CanonicalFieldName(field))
            {
                case "purpose": purpose = value; break; case "mode": mode = value; break; case "scope": scope = value; break;
                case "left": left = value; break; case "right": right = value; break;
                case "leftPalm": leftPalm = value; break; case "rightPalm": rightPalm = value; break;
                case "joint": joint = value; break; case "axis": axis = value; break; case "tempo": tempo = value; break;
                case "size": size = value; break; case "repeat": repeat = value; break; case "end": end = value; break; case "text": text = value; break;
                case "leftBend": leftBend = value; break; case "rightBend": rightBend = value; break;
                case "amplitude": amplitude = value; break; case "cycles": cycles = value; break; case "seconds": seconds = value; break;
                case "userTurn":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
                        throw new ArgumentException("userTurn must be a positive accepted user-turn ID.");
                    userTurn = number; break;
                case "evidence": evidence = value; break; case "lock": lockFields = value; break; case "release": releaseFields = value; break;
                default: throw new ArgumentException("Unknown semantic plan field: " + field);
            }
        }

        public static string CanonicalFieldName(string field)
        {
            foreach (string candidate in valueFields)
                if (string.Equals(field, candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
            foreach (string candidate in new[] { "userTurn", "evidence", "lock", "release" })
                if (string.Equals(field, candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
            throw new ArgumentException("Unknown semantic plan field: " + field);
        }

        public static bool IsConstraintField(string field)
        {
            string canonical;
            try { canonical = CanonicalFieldName(field); }
            catch (ArgumentException) { return false; }
            return canonical != "purpose" && canonical != "scope" && canonical != "text" && Array.IndexOf(valueFields, canonical) >= 0;
        }

        public string Effective(string field, string fallback) => HasField(field) ? GetValue(field) : fallback;

        /// <summary>Validates effective values after ledger merging. Inherited locks may have older source records.</summary>
        public void Validate() => ValidateCore(false);

        /// <summary>Validates an unmerged role command, including the presence of source claims for new locks/releases.</summary>
        public void ValidateRaw() => ValidateCore(true);

        private void ValidateCore(bool requireSourceMetadata)
        {
            foreach (string field in valueFields)
            {
                string value = GetValue(field);
                if (value == null || value != value.Trim() || value.IndexOfAny(new[] { '<', '>', '&', '\r', '\n' }) >= 0)
                    throw new ArgumentException("Plan fields must be trimmed single-line values without markup: " + field);
            }
            Choice(purpose, "purpose", "greet", "farewell", "display", "explain", "agree", "disagree", "other");
            Choice(mode, "mode", "oscillate", "hold", "free");
            Choice(scope, "scope", "new", "continue");
            foreach (string arm in new[] { left, right })
                Choice(arm, "arm", "none", "current", "forward", "outward", "up", "down", "forward-up", "outward-up");
            foreach (string palm in new[] { leftPalm, rightPalm })
                Choice(palm, "palm", "keep", "partner", "up", "down", "inward", "outward");
            Choice(joint, "joint", "none", "left-wrist", "right-wrist", "wrists", "head");
            Choice(axis, "axis", "auto", "up", "right", "forward", "palm-normal");
            Choice(tempo, "tempo", "gentle", "natural", "brisk");
            Choice(size, "size", "small", "medium", "large");
            Choice(repeat, "repeat", "once", "twice", "thrice");
            Choice(end, "end", "idle", "hold");

            if (userTurn < 0 || evidence == null || evidence.Length > 256 || evidence != evidence.Trim() ||
                evidence.IndexOfAny(new[] { '<', '>', '&', '\r', '\n' }) >= 0)
                throw new ArgumentException("Source evidence must be a bounded, unescaped, single-line user quote.");
            if (requireSourceMetadata && (userTurn > 0) != !string.IsNullOrEmpty(evidence))
                throw new ArgumentException("userTurn and evidence must be supplied together.");
            var locked = new HashSet<string>(LockFields, StringComparer.Ordinal);
            var released = new HashSet<string>(ReleaseFields, StringComparer.Ordinal);
            if (requireSourceMetadata && (locked.Count > 0 || released.Count > 0) && userTurn <= 0)
                throw new ArgumentException("lock/release need an accepted userTurn and evidence quote.");
            foreach (string field in locked)
            {
                if (!HasField(field)) throw new ArgumentException("A newly locked constraint must supply its value: " + field);
            }
            foreach (string field in numericFields)
                if (HasField(field) && !locked.Contains(field))
                    throw new ArgumentException("Explicit numeric fields require a sourced lock: " + field);
            if (HasField("leftBend")) Number(leftBend, 0, 110, "leftBend");
            if (HasField("rightBend")) Number(rightBend, 0, 110, "rightBend");
            if ((left == "current" && HasField("leftBend")) || (right == "current" && HasField("rightBend")))
                throw new ArgumentException("current preserves the measured arm pose; use a direction to specify a new elbow angle.");
            if (HasField("amplitude")) Number(amplitude, 1, joint == "head" ? 12 : 20, "amplitude");
            if (HasField("cycles") && (!int.TryParse(cycles, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1 || count > 3))
                throw new ArgumentException("cycles must be an integer from 1 to 3.");
            if (HasField("seconds")) Number(seconds, 1, 6, "seconds");
            if ((HasField("amplitude") && HasField("size")) || (HasField("cycles") && HasField("repeat")) ||
                (HasField("seconds") && HasField("tempo")))
                throw new ArgumentException("Numeric amplitude/cycles/seconds cannot coexist with size/repeat/tempo; release an old constraint before changing representation.");
            if (Effective("mode", "oscillate") == "free")
            {
                if (text.Length == 0 || text.Length > 240 || text.IndexOf('"') >= 0 || !HasAsciiLetter(text))
                    throw new ArgumentException("free needs one English motion description of 1 to 240 characters.");
                foreach (char c in text) if (c > 127) throw new ArgumentException("free text must be a short English description.");
                foreach (string field in valueFields)
                    if (field != "purpose" && field != "mode" && field != "scope" && field != "text" && HasField(field))
                        throw new ArgumentException("free accepts only purpose, mode, scope, text and source metadata.");
            }
            else if (HasField("text")) throw new ArgumentException("Only free plans accept text.");
            if (Effective("mode", "oscillate") == "hold" &&
                ((HasField("joint") && joint != "none") || HasField("axis") || HasField("amplitude") || HasField("cycles") ||
                 HasField("seconds") || HasField("tempo") || HasField("size") || HasField("repeat")))
                throw new ArgumentException("hold is static: omit local-motion and timing fields rather than setting them to zero.");
        }

        public static float Number(string value, float min, float max, string field)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) ||
                float.IsNaN(number) || float.IsInfinity(number) || number < min || number > max)
                throw new ArgumentException(field + " is not a finite number in the supported range.");
            return number;
        }

        public static int RepeatCount(string value) => value == "once" ? 1 : value == "thrice" ? 3 : 2;
        private static bool HasAsciiLetter(string value)
        {
            foreach (char c in value) if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) return true;
            return false;
        }
        private static void Choice(string value, string field, params string[] choices)
        {
            if (value.Length > 0 && Array.IndexOf(choices, value) < 0) throw new ArgumentException("Unsupported " + field + ": " + value);
        }
        private static string[] ParseFields(string csv)
        {
            if (csv == null) throw new ArgumentException("Constraint lists cannot be null.");
            if (csv.Length == 0) return new string[0];
            var values = new List<string>();
            foreach (string token in csv.Split(','))
            {
                string field = CanonicalFieldName(token.Trim());
                if (!IsConstraintField(field) || values.Contains(field))
                    throw new ArgumentException("Constraint fields must be known, unique values; metadata cannot be locked.");
                values.Add(field);
            }
            return values.ToArray();
        }
    }
}
