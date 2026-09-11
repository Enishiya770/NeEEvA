"""Structured necessary observations for the expanded development corpus.

Accepts generator configs (``specification``) or exported rows
(``configuration``). Description text is deliberately never parsed. All final
semantic, palm, repetition and naturalness judgments remain manual.
"""
from __future__ import annotations

from collections.abc import Mapping


_ARM_FAMILIES={"raise","point","wave","circle","elbow_bend","forearm_rotation",
    "beckon","dismiss","present","offer","stop","low-sweep","figure-eight",
    "emphasize","self-indicate","relax","bilateral_spread","alternating_arms"}
_HEAD_FAMILIES={"nod","shake","head_tilt","head_look"}
_TORSO_FAMILIES={"torso_lean","torso_twist","bow"}


def _rotation(metric,reference,primary=None):
    axes=[primary]+[axis for axis in ("pitch","yaw","roll") if axis!=primary] if primary else ["pitch","yaw","roll"]
    return [{"metric":metric,"axis":axis,"reference":reference,"expectation":"report_only"} for axis in axes]


def _height(side,target):
    """Only explicit target enums yield a gate; approximate references are named."""
    base={"metric":"wrist_relative_height_m","side":side}
    if target in ("chest","shoulder"):
        return {**base,"reference":target,"expectation":"reach_band","band_m":[-.12,.12]},None
    if target=="face":
        return {**base,"reference":"head","expectation":"reach_band","band_m":[-.12,.12]},"Head-joint height is only a proxy for face level; verify the actual face-relative target visually."
    if target in ("overhead","high"):
        return {**base,"reference":"head","expectation":"above","threshold_m":.05},"Verify the hand remains above the head as requested; one threshold frame does not establish a hold or complete action."
    if target in ("waist","below_waist","low"):
        return {**base,"reference":"chest","expectation":"below","threshold_m":0.0},"Below-chest height is a weaker necessary observation for the waist/below-waist target; the evaluator has no waist landmark and does not certify the exact level."
    return {**base,"reference":"chest","expectation":"report_only"},"No explicit supported height target is encoded; evaluate the intended target manually."


def assessment_for(config):
    if not isinstance(config,Mapping):
        raise ValueError("assessment_for expects a structured configuration mapping")
    specification=config.get("specification",config.get("configuration",{}))
    if not isinstance(specification,Mapping):
        raise ValueError("The configuration/specification field must be a mapping, not prompt text")
    family=config.get("family")
    side=config.get("side")
    if not isinstance(family,str) or not family:
        raise ValueError("The structured family is required")
    observations=[]
    manual=[
        "Verify the requested side, direction, target and intended complete gesture in real avatar playback.",
        "Check every stated palm orientation, finger/forearm shape and precise contact visually; these are not automatically evaluated.",
        "Check repetition count, ordering, simultaneity, pauses, endpoint return and duration against the description.",
        "Review naturalness, smoothness, body stability, unintended limb motion and target-avatar clipping."]
    def arm(side_value,target=None,report_only=False):
        if side_value not in ("left","right","both"):
            raise ValueError("Arm configurations require an explicit left/right/both side")
        height,note=_height(side_value,target)
        if report_only:
            height={"metric":"wrist_relative_height_m","side":side_value,"reference":"chest","expectation":"report_only"}
        observations.extend([height,{"metric":"wrist_excursion_m","side":side_value,"expectation":"report_only"}])
        if note:
            manual.append(note)
    if family in _ARM_FAMILIES:
        arm(side,specification.get("target"))
        if side in ("left","right"):
            observations.append({"metric":"wrist_excursion_m","side":"right" if side=="left" else "left","expectation":"report_only"})
        if family in ("raise","point"):
            manual.append("The explicit target-height gate does not verify forward/diagonal/outward/across direction, arm extension or the palm orientation; verify those separately.")
        if family=="alternating_arms":
            # Bilateral simultaneity would be incorrect for an alternating task.
            observations=[item for item in observations if item["metric"]!="wrist_relative_height_m"]
            for active_side in ("left","right"):
                height,note=_height(active_side,specification.get("target"))
                observations.append(height)
                if note and note not in manual:
                    manual.append(note)
            observations.append({"metric":"event_order","expectation":"report_only",
                "events":[],"requested_order":specification.get("order"),"note":"Alternating order requires manual review; side-height reach is evaluated independently, not simultaneously."})
    elif family in _HEAD_FAMILIES:
        primary={"nod":"pitch","shake":"yaw","head_tilt":"roll"}.get(family,"pitch" if side in ("up","down") else "yaw")
        observations.extend(_rotation("head_rotation_excursion_deg","upper_chest",primary))
        observations.extend(_rotation("upper_torso_rotation_excursion_deg","hips"))
    elif family in _TORSO_FAMILIES:
        primary="yaw" if family=="torso_twist" else ("roll" if family=="torso_lean" and side in ("left","right") else "pitch")
        observations.extend(_rotation("upper_torso_rotation_excursion_deg","hips",primary))
        observations.extend(_rotation("head_rotation_excursion_deg","upper_chest"))
        observations.append({"metric":"wrist_excursion_m","side":"both","expectation":"report_only"})
    elif family in ("shrug","shoulder_roll"):
        observations.append({"metric":"shoulder_height_change_m","side":"both","reference":"hips","expectation":"report_only"})
        observations.extend(_rotation("head_rotation_excursion_deg","upper_chest"))
        observations.extend(_rotation("upper_torso_rotation_excursion_deg","hips"))
    elif family in ("combination","sequence"):
        # Compound config IDs are not a parser for their natural-language meaning.
        arm("both",report_only=True)
        observations.extend(_rotation("head_rotation_excursion_deg","upper_chest"))
        observations.extend(_rotation("upper_torso_rotation_excursion_deg","hips"))
        observations.append({"metric":"event_order","expectation":"report_only",
            "events":list(specification.get("events",[])),"configuration_id":specification.get("configuration"),
            "temporal":specification.get("temporal"),"note":"Constituent actions and temporal relationship remain manual unless separately structured and implemented."})
    else:
        manual.append("Unrecognized family: no automatic geometric criterion was invented.")
    count=specification.get("count")
    if count is not None:
        if isinstance(count,bool) or not isinstance(count,int) or count<1:
            raise ValueError("Explicit repetition count must be a positive integer")
        observations.append({"metric":"repetition_count","event":specification.get("action",family),
            "expected":count,"expectation":"report_only"})
    elif specification.get("ending")=="repeat":
        manual.append("The repeat ending has no numeric count in the structured configuration; read the requested count during manual review rather than inventing one.")
    return {"automatic_semantic_pass":False,"machine_observations":observations,"manual_checks":manual,
        "metric_policy":"Explicit geometric observations are necessary evidence only. Any-frame reach is not a hold, direction, palm, repetition, temporal or naturalness pass. Overall semanticPass remains unknown.",
        "label_source":"generalization_labels.assessment_for structured config only",
        "height_coordinates":"World +Y wrist minus Core27 reference; shoulder is Arm humeral pivot; head proxy and waist limitations are stated in manual checks."}
