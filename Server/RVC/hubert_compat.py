"""Lossless HuBERT weight-normalization key compatibility across Torch releases."""

_PREFIX = "encoder.pos_conv_embed.conv."
_LEGACY = (_PREFIX + "weight_g", _PREFIX + "weight_v")
_PARAMETRIZED = (_PREFIX + "parametrizations.weight.original0",
                _PREFIX + "parametrizations.weight.original1")


def remap_weight_norm_state(state: dict, expected_keys) -> tuple[dict, bool]:
    """Rename the two equivalent tensors, never initialize or approximate them."""
    expected = set(expected_keys)
    for source, target in ((_PARAMETRIZED, _LEGACY), (_LEGACY, _PARAMETRIZED)):
        if not all(key in expected for key in target) or not any(key in state for key in source):
            continue
        if not all(key in state for key in source):
            raise ValueError("HuBERT checkpoint contains an incomplete weight-normalization pair")
        if any(key in state for key in target):
            raise ValueError("HuBERT checkpoint contains conflicting weight-normalization layouts")
        result = state.copy()
        for old, new in zip(source, target):
            result[new] = result.pop(old)
        return result, True
    return state, False


def install_hubert_checkpoint_compatibility() -> None:
    """Adapt this vendor's Transformers loader without changing its checkpoint."""
    import sys
    from pathlib import Path
    import torch
    import infer.hubert as hubert
    from transformers.models.hubert.modeling_hubert import HubertPositionalConvEmbedding

    if getattr(hubert, "_neeeva_checkpoint_compatibility", False):
        return
    original_load = hubert.HubertModelWithFinalProj.from_pretrained

    def compatible_load(cls, path, *args, **kwargs):
        config = cls.config_class.from_pretrained(path, local_files_only=True)
        # Use the installed implementation's actual state layout rather than a
        # version-string guess. This is one convolution, not a second HuBERT.
        with torch.no_grad():
            probe = HubertPositionalConvEmbedding(config)
        expected = {"encoder.pos_conv_embed." + key for key in probe.state_dict()}
        del probe
        checkpoint = Path(path) / "pytorch_model.bin"
        state = kwargs.pop("state_dict", None)
        if state is None:
            state = torch.load(checkpoint, map_location="cpu", weights_only=True)
        state, changed = remap_weight_norm_state(state, expected)
        requested_info = kwargs.pop("output_loading_info", False)
        model, info = original_load(path, *args, state_dict=state,
            output_loading_info=True, **kwargs)
        if any(info.get(key) for key in ("missing_keys", "unexpected_keys", "mismatched_keys", "error_msgs")):
            raise RuntimeError("HuBERT checkpoint did not load completely: " + str(info))
        if any(not torch.isfinite(parameter).all().item() for parameter in model.parameters()):
            raise RuntimeError("HuBERT checkpoint contains non-finite parameters")
        if changed:
            print("[RVC/HuBERT] mapped equivalent positional weight-normalization keys; all checkpoint tensors loaded", file=sys.stderr, flush=True)
        return (model, info) if requested_info else model

    original_features = hubert.extract_hubert_features

    def checked_features(*args, **kwargs):
        features = original_features(*args, **kwargs)
        if not torch.isfinite(features).all().item():
            raise RuntimeError("HuBERT produced non-finite features; refusing silent/invalid conversion")
        return features

    hubert.HubertModelWithFinalProj.from_pretrained = classmethod(compatible_load)
    hubert.extract_hubert_features = checked_features
    hubert._neeeva_checkpoint_compatibility = True
