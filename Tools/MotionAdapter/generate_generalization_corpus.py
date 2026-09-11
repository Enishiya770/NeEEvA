"""Broader, explicitly synthetic motion descriptions with configuration-level splits.

This is a distillation corpus, not motion capture or labels of observed success.
The original pilot and the independently frozen final evaluation are immutable.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path

from .data import read_records, records_hash
from .generalization_labels import assessment_for

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
STYLES = [
    '{action}.',
    'While standing in place, {action}.',
    'A person will {action}.',
    'During a conversation, {action}.',
    'Without taking a step, {action}.',
    'The standing person should {action}.',
    'Keeping the feet planted, {action}.',
    'Perform this upper-body movement: {action}.',
]


def configurations():
    result = []

    def add(key, family, side, description, **details):
        # Explicit group keys include all controlled attributes; paraphrases are
        # generated only after the group has received its split.
        result.append({'key': key, 'family': family, 'side': side,
                       'description': description, 'specification': details})

    for side in ('left', 'right', 'both'):
        arms = 'both arms' if side == 'both' else f'the {side} arm'
        hands = 'both hands' if side == 'both' else f'the {side} hand'
        elbows = 'the elbows' if side == 'both' else 'the elbow'
        for direction, path in [('forward', 'forward'), ('diagonal', 'diagonally outward')]:
            for level, target in [('chest', 'chest level'), ('face', 'face level'), ('overhead', 'above the head')]:
                # Reserve pure one-arm-overhead raises; high waving may share
                # that atomic pose without sharing the complete test action.
                if level == 'overhead' and side != 'both':
                    continue
                for ending, tail in [('hold', 'hold briefly, and return to rest'),
                                     ('repeat', 'return to rest; perform three full lifts in total')]:
                    if side == 'both' and direction == 'forward' and level == 'chest' and ending == 'hold':
                        continue  # Reserved test: both-chest-approach-no-contact.
                    add(f'raise/{side}/{direction}/{level}/{ending}', 'raise', side,
                        f'lift {arms} {path} until {hands} reach {target}, with palms turned inward; {tail}',
                        action='raise', target=level, direction=direction, ending=ending)
        for target, words in [('waist', 'near the waist'), ('chest', 'in front of the chest'),
                              ('high', 'high above the head')]:
            for count in (1, 3, 4):
                add(f'wave/{side}/{target}/{count}', 'wave', side,
                    f'wave {hands} {words} {count} times in a friendly greeting, then relax {arms}',
                    action='wave', target=target, count=count)
        for direction, words in [('up', 'diagonally upward'), ('down', 'toward the ground ahead'),
                                 ('forward', 'straight ahead'), ('outward', 'out to the sides'),
                                 ('across', 'across the front of the body')]:
            for tempo in ('slowly', 'briskly'):
                if direction == 'outward' and side != 'both':
                    words = f'out to the {side} side'
                target, pose = ('face', 'at face height with palms turned inward') if direction == 'up' else (
                    ('below_waist', 'below waist height with palms facing down') if direction == 'down' else
                    ('chest', 'at chest height with palms facing up'))
                add(f'point/{side}/{direction}/{tempo}', 'point', side,
                    f'{tempo} extend {arms} {words} {pose}, pause, and bring {hands} back',
                    action='point', direction=direction, tempo=tempo, target=target)
        for direction, words in [('forward', 'forward circles'), ('backward', 'backward circles'),
                                 ('inward', 'small inward circles'), ('outward', 'wide outward circles')]:
            for count in (2, 3):
                add(f'circle/{side}/{direction}/{count}', 'circle', side,
                    f'make {count} {words} with {arms}, keeping the movement controlled and then returning to rest',
                    action='circle', direction=direction, count=count)
        for level, words in [('waist', 'at waist level'), ('chest', 'at chest level')]:
            for count in (2, 3):
                add(f'elbow/{side}/{level}/{count}', 'elbow_bend', side,
                    f'bend and straighten {elbows} {count} times with {hands} {words} and the upper arms relaxed',
                    action='elbow_bend', target=level, count=count)
        for orientation, words in [('up', 'turn the palms upward'), ('down', 'turn the palms downward'),
                                   ('alternating', 'alternate between palms up and palms down')]:
            add(f'wrist/{side}/{orientation}', 'forearm_rotation', side,
                f'hold {hands} in front of the chest and {words} by rotating the forearms, then lower {arms}',
                action='forearm_rotation', orientation=orientation)
        for gesture, words in [
            ('beckon', f'beckon toward the body with {hands}, using three gentle inward sweeps'),
            ('dismiss', f'make two outward dismissive gestures with {hands} at chest height'),
            ('present', f'present an idea by sweeping {hands} outward at chest height with palms up'),
            ('offer', f'offer {hands} forward with palms facing up and elbows bent, then draw back'),
            ('stop', f'push {hands} forward twice at face height with palms facing away'),
            ('low-sweep', f'sweep {hands} from one side to the other twice in front of the abdomen'),
            ('figure-eight', f'trace a small figure-eight shape in the air with {hands} in front of the chest'),
            ('emphasize', f'make three short downward emphasis gestures with {hands} while explaining'),
            ('self-indicate', f'bring {hands} toward the upper chest to indicate oneself, without touching'),
            ('relax', f'lower {arms} from a raised position to rest alongside the body in a smooth motion'),
        ]:
            add(f'gesture/{side}/{gesture}', gesture, side, words,
                action=gesture, target='gesture_specific')

    for spread, words in [('diagonal', 'diagonally forward at face height'),
                           ('low', 'out to the sides below the waist'),
                           ('high-v', 'upward into a wide V shape')]:
        for count in (1, 3):
            add(f'bilateral/spread/{spread}/{count}', 'bilateral_spread', 'both',
                f'open both arms {words} with palms facing up, then bring them back; do this {count} times',
                action='spread', target=spread, count=count)
    for order in ('left-first', 'right-first'):
        first, second = ('left', 'right') if order == 'left-first' else ('right', 'left')
        for target in ('waist', 'face'):
            add(f'alternate/{order}/{target}', 'alternating_arms', 'both',
                f'alternate lifting the {first} hand and the {second} hand to {target} height for three rounds',
                action='alternating_raise', target=target, count=3, order=order)

    for count in (1, 3, 4):
        for pace in ('slow', 'gentle', 'quick'):
            nods = f'one {pace} nod' if count == 1 else f'{count} {pace} nods'
            add(f'head/nod/{count}/{pace}', 'nod', 'none',
                f'make {nods} of the head and then look forward again', action='nod', count=count, pace=pace)
    for count in (2, 3, 4):
        for size in ('small', 'moderate'):
            add(f'head/shake/{count}/{size}', 'shake', 'both',
                f'shake the head from side to side for {count} {size} cycles, then face forward',
                action='shake', count=count, amplitude=size)
    for side, size, count in [('left', 'small', 1), ('left', 'moderate', 1),
                              ('left', 'small', 3), ('right', 'moderate', 1), ('right', 'small', 3)]:
        add(f'head/tilt/{side}/{size}/{count}', 'head_tilt', side,
            f'tilt the head toward the {side} shoulder and return upright {count} times with {size} movements',
            action='tilt', count=count, amplitude=size)
    for direction in ('right', 'up', 'down'):
        for ending, words in [('hold', 'pause briefly and look ahead again'),
                              ('repeat', 'return forward; perform three full turns in total')]:
            add(f'head/look/{direction}/{ending}', 'head_look', direction,
                f'look {direction} by turning the head, {words}', action='look', ending=ending)

    for side, size in [('left', 'moderate'), ('right', 'small'), ('right', 'moderate'),
                       ('forward', 'moderate'), ('backward', 'small')]:
        for count in (2, 3):
            add(f'torso/lean/{side}/{size}/{count}', 'torso_lean', side,
                f'lean the upper body {side} and return upright {count} times with {size} movements',
                action='lean', count=count, amplitude=size)
    for side in ('left', 'right'):
        for count in (2, 3):
            add(f'torso/twist/{side}/{count}', 'torso_twist', side,
                f'rotate the upper torso toward the {side} and back {count} times while the pelvis stays forward',
                action='twist', count=count)
    for depth in ('moderate', 'deep'):
        for arms in ('hands near the abdomen', 'arms slightly open to the sides'):
            add(f'bow/{depth}/{arms.replace(" ","-")}', 'bow', 'none',
                f'make a {depth} bow with {arms}, and slowly straighten the back', action='bow', amplitude=depth)
    for count in (2, 3, 4):
        add(f'shoulders/shrug/{count}', 'shrug', 'both',
            f'shrug both shoulders {count} times to express uncertainty and then relax them', action='shrug', count=count)
    for direction in ('forward', 'backward'):
        for count in (2, 3):
            add(f'shoulders/roll/{direction}/{count}', 'shoulder_roll', 'both',
                f'roll both shoulders {direction} for {count} slow circles', action='roll', count=count, direction=direction)

    combinations = [
        ('nod-right-face', 'nod once while lifting the right hand to face level'),
        ('nod-both-open', 'nod three times while opening both hands outward with palms facing up'),
        ('shake-left-stop', 'shake the head twice while showing the left palm forward at face level'),
        ('shrug-right-tilt', 'shrug twice while tilting the head slightly to the right'),
        ('left-look-right-waist', 'look left while moving the right hand outward at waist height'),
        ('up-look-both-v', 'look upward while stretching both arms into a high V'),
        ('right-lean-left-reach', 'lean slightly right while reaching the left hand forward at face level'),
        ('left-lean-both-low', 'lean moderately left while extending both hands downward with relaxed elbows'),
        ('left-wave-right-point', 'wave the left hand above the head while the right arm points downward'),
        ('right-wave-left-offer', 'wave the right hand at chest height while holding the left palm upward near the waist'),
        ('left-high-right-low', 'lift the left hand to face level while extending the right hand diagonally downward'),
        ('right-high-left-low', 'lift the right hand to face level while extending the left hand diagonally downward'),
        ('both-elbows-head-up', 'bend both elbows twice while looking slightly upward'),
        ('torso-left-right-wave', 'turn the torso moderately left while waving the right hand at face height'),
    ]
    for key, description in combinations:
        add('combine/'+key, 'combination', 'mixed', description,
            action='combination', temporal='simultaneous', configuration=key)
    sequences = [
        ('nod-then-left-wave', 'nod once, then lift the left hand and wave three times at chest height'),
        ('both-wave-then-bow', 'wave both hands near the waist once, lower the arms, and make a moderate bow'),
        ('shrug-then-right-offer', 'shrug twice, relax the shoulders, then offer the right palm forward'),
        ('look-right-then-left', 'turn the head right, then left, and finally look forward'),
        ('right-then-left-face', 'raise the right hand to face height and lower it, then do the same with the left hand'),
        ('left-circle-then-nod', 'make two circles with the left arm, relax it, then nod three times'),
        ('both-raise-then-wave', 'lift both hands high above the head, then wave them three times before lowering them'),
        ('left-offer-then-right', 'offer the left palm forward, draw it back, then offer the right palm forward'),
        ('bow-then-open', 'make a deep bow, straighten up, then open both arms diagonally forward'),
        ('point-up-then-down', 'point both hands diagonally upward, then sweep them down toward the waist'),
        ('head-up-then-nod', 'look upward, bring the head level, then make one deliberate nod'),
        ('low-open-close-three', 'open both arms sideways below the waist and close them again for three gentle cycles'),
        ('right-shrug-then-wave', 'roll the right shoulder twice, then make two high waves with the right hand'),
        ('head-tilt-then-offer', 'tilt the head moderately right, return upright, then offer both palms forward'),
    ]
    for key, description in sequences:
        add('sequence/'+key, 'sequence', 'mixed', description,
            action='sequence', temporal='ordered_sequence', configuration=key)
    return result


def build():
    configs = configurations()
    if len({c['key'] for c in configs}) != len(configs):
        raise ValueError('Duplicate semantic configurations')
    # Stratify whole configuration groups within each family. Related syntax
    # variants never cross the train/val/test boundary.
    assignment = {}
    for family in sorted({c['family'] for c in configs}):
        group = sorted([c for c in configs if c['family'] == family],
                       key=lambda c: hashlib.sha256(('generalization-v1:'+c['key']).encode()).hexdigest())
        n = max(1, round(len(group)*.12))
        for i, config in enumerate(group):
            assignment[config['key']] = 'val' if i < n else 'test' if i < 2*n else 'train'
    rows = []
    for config in configs:
        group = 'expanded-v1/'+config['key']
        action = config['description'].replace('1 times', 'once')
        if config['side'] in ('left', 'right'):
            action = action.replace('with palms', 'with the palm').replace('the palms', 'the palm')
            action = action.replace('the forearms', 'the forearm')
            action = action.replace(f"the {config['side']} hand reach ", f"the {config['side']} hand reaches ")
        for variant, template in enumerate(STYLES):
            text = template.format(action=action)
            text = text[0].upper()+text[1:]
            if len(text) > 240:
                raise ValueError(f'Description exceeds production contract: {text}')
            rows.append({'id': f'expanded-v1-{len(rows):05d}', 'semantic_group': group,
                'family': config['family'], 'text': text, 'split': assignment[config['key']],
                'source': 'assistant_authored_compositional_templates_v1', 'language': 'en',
                'configuration': config['specification'], 'side': config['side'],
                'template_id': variant, 'evaluation_track': 'stationary_upper_body_distillation',
                'assessment': assessment_for(config), 'include_in_success_summary': True,
                'labels_status': 'intended_description_not_observed_motion_success'})
    return rows, configs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=HERE/'runtime/generalization-v1/extension.jsonl')
    args = parser.parse_args()
    if args.output.exists():
        raise FileExistsError('Version the corpus instead of replacing a frozen export manifest')
    rows, configs = build()
    final_eval = read_records(HERE/'data/generalization-eval-v1.jsonl')
    diagnostics = read_records(HERE/'runtime/teacher-motion-diagnostic/prompts.jsonl')[:4]
    forbidden = {r['text'].lower().rstrip('.') for r in final_eval+diagnostics}
    if any(r['text'].lower().rstrip('.') in forbidden for r in rows):
        raise ValueError('Exact evaluation/diagnostic text leaked into the extension')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(''.join(json.dumps(r, ensure_ascii=False)+'\n' for r in rows), encoding='utf-8')
    read_records(args.output)
    report = {'rows': len(rows), 'groups': len(configs), 'families': dict(Counter(c['family'] for c in configs)),
        'splits': dict(Counter(r['split'] for r in rows)), 'records_sha256': records_hash(rows),
        'final_eval_sha256': records_hash(final_eval), 'semantic_review_required_before_export': True,
        'known_diagnostics': 'Four original failure strings excluded; their action families are seen development targets, never unseen generalization claims.',
        'limitation': 'Explicit synthetic text distillation corpus; 8 shared surface templates are not 8 independent motion concepts or human-captured examples.',
        'configurations': configs}
    args.output.with_suffix('.manifest.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({k:report[k] for k in ('rows','groups','families','splits','records_sha256')}, indent=2))


if __name__ == '__main__':
    main()
