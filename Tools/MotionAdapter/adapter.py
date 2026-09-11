"""Small mapping into the original, unnormalized ARDY conditioning space."""
import torch
from torch import nn


class MotionAdapter(nn.Module):
    def __init__(self, architecture="mlp"):
        super().__init__()
        if architecture == "linear":
            self.network = nn.Linear(2048, 4096)
        elif architecture == "mlp":
            self.network = nn.Sequential(nn.LayerNorm(2048), nn.Linear(2048, 2048), nn.GELU(), nn.Linear(2048, 4096))
        else:
            raise ValueError("Unknown architecture")
        self.architecture = architecture
        self.register_buffer("target_mean", torch.zeros(4096))
        self.register_buffer("target_std", torch.ones(4096))

    def standardized(self, features):
        return self.network(features)

    def forward(self, features):
        return self.standardized(features) * self.target_std + self.target_mean


def load_adapter(path, device="cpu", expected_contract=None):
    payload = torch.load(path, map_location="cpu", weights_only=True)
    if payload.get("schema") != 1:
        raise ValueError("Unsupported checkpoint schema")
    if expected_contract is not None and payload["qwen_contract"] != expected_contract:
        raise ValueError("Qwen feature contract changed; retraining or calibration is required")
    model = MotionAdapter(payload["architecture"])
    model.load_state_dict(payload["state_dict"], strict=True)
    return model.to(device).eval(), payload
