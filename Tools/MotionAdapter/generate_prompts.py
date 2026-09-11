"""Generate a reproducible pilot corpus; related paraphrases never cross splits."""
import argparse
import itertools
import json
import random
from pathlib import Path

from .data import read_records


ACTIONS = {
    "wave": "waves the {side} hand with {size} movements",
    "raise_arm": "raises and lowers the {side} arm with {size} movements",
    "point": "makes {size} pointing gestures to the {side} with the {side} hand",
    "extend_arm": "extends the {side} arm outward with a {size} movement and brings it back",
    "shift_weight": "shifts weight toward the {side} with a {size} movement",
    "turn": "turns the body to the {side} with a {size} movement",
    "nod": "nods the head with {size} movements",
    "shake_head": "shakes the head from side to side with {size} movements",
    "shrug": "shrugs both shoulders with {size} movements",
    "bow": "bows forward with a {size} movement and straightens up",
}
PREFIXES = ["A person", "Someone", "A standing person", "An individual", "A person who is standing",
            "A person standing upright", "A person standing on the floor"]
SENTENCES = ["{subject} {action} {pace}.", "While standing in place, {subject_lower} {action} {pace}."]


def generate(count=2000, seed=42):
    rng = random.Random(seed)
    groups = []
    for family, template in ACTIONS.items():
        sides = ("left", "right") if "{side}" in template else ("both",)
        local = list(itertools.product(sides, ("small", "moderate", "large"),
                                       ("slowly", "at a moderate pace", "quickly")))
        rng.shuffle(local)
        # Each family has all three splits; all paraphrases of each tuple stay together.
        n_val = max(1, round(len(local) * .1))
        for i, (side, size, pace) in enumerate(local):
            split = "val" if i < n_val else "test" if i < 2 * n_val else "train"
            key = f"{family}/{side}/{size}/{pace}"
            variants = []
            for prefix, sentence in itertools.product(PREFIXES, SENTENCES):
                action = template.format(side=side, size=size)
                variants.append(sentence.format(subject=prefix, subject_lower=prefix[0].lower()+prefix[1:],
                                                action=action, pace=pace))
            rng.shuffle(variants)
            groups.append((key, family, split, variants))
    rng.shuffle(groups)
    rows = []
    for variant in range(14):
        for key, family, split, variants in groups:
            rows.append({"id": f"motion-{len(rows):05d}", "semantic_group": key, "family": family,
                         "text": variants[variant], "split": split, "source": "synthetic_template_v1"})
    if not 1 <= count <= len(rows):
        raise ValueError(f"Count must be between 1 and {len(rows)}")
    return rows[:count]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--count", type=int, default=2000)
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()
    rows = generate(args.count, args.seed)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("".join(json.dumps(r, ensure_ascii=False)+"\n" for r in rows), encoding="utf-8")
    read_records(args.output)
    print(json.dumps({"rows": len(rows), "groups": len({r['semantic_group'] for r in rows}),
                      "splits": {s: sum(r['split'] == s for r in rows) for s in ['train', 'val', 'test']}}))


if __name__ == "__main__":
    main()
