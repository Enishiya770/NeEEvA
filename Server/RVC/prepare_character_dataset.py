"""Generate a clean, phoneme-diverse RVC corpus from the active character TTS.

The script is resumable and stops after the requested amount of validated audio.
Generated speech remains local and is intentionally ignored by Git.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path

import requests
import soundfile as sf


OPENINGS = (
    "おはよう。今日もあなたの声を聞けて嬉しいわ",
    "こんばんは。静かな時間がゆっくり流れているわね",
    "少し休憩しましょう。温かいお茶を用意したの",
    "ねえ、さっき面白いことを思いついたのよ",
    "外の空気が澄んでいて、とても気持ちのいい朝ね",
    "あなたが戻ってくるのを、ここで待っていたわ",
    "今日はどんな一日だったのか、ゆっくり聞かせて",
    "雨の音を聞いていると、不思議と心が落ち着くの",
    "窓を開けたら、柔らかな風が頬をなでていったわ",
    "急がなくても大丈夫。まずは深く息を吸いましょう",
    "この部屋に差し込む夕日が、とても綺麗に見えるわ",
    "ふふっ、あなたの表情を見ればすぐに分かるのよ",
    "新しい季節が来るたびに、少しだけ胸が高鳴るわね",
    "遠くで鐘の音がしたわ。もうこんな時間なのね",
    "大切な話なら、最後まできちんと聞かせてちょうだい",
    "今日は声の調子が良さそうね。私にも分けてほしいわ",
)

SCENES = (
    "庭では白い花が揺れて、小さな鳥が枝から枝へ飛んでいるの",
    "机の上には青い本と銀色の鍵、それから古い手紙が置いてあるわ",
    "駅前のパン屋から甘い香りがして、思わず足を止めてしまったの",
    "雲の切れ間から光が差して、水たまりが鏡のように輝いていたわ",
    "森の奥へ続く細い道には、朝露に濡れた葉がたくさん落ちていたの",
    "赤、橙、黄色の灯りが並んで、夜の通りを優しく照らしているわ",
    "静かな図書館でページをめくる音だけが、規則正しく響いていたの",
    "海辺を歩くと潮の香りがして、波が何度も砂浜を洗っていったわ",
    "丸い月のそばを薄い雲が通り過ぎて、影の形が少しずつ変わるの",
    "古い時計は九時三十分を指して、低い音でゆっくり時を刻んでいたわ",
    "市場には林檎、葡萄、桃が並び、元気な呼び声があちこちから聞こえたの",
    "小さな劇場では音楽が始まり、客席のざわめきがすっと消えていったわ",
    "坂道を登った先で振り返ると、街全体が淡い霧に包まれていたの",
    "透明なグラスに冷たい水を注ぐと、氷が涼しい音を立てたわ",
    "白い紙に鉛筆で線を引いて、思い出の場所を一つずつ描いてみたの",
    "春夏秋冬、それぞれの景色には違った美しさがあると思わない",
)

REFLECTIONS = (
    "嬉しいことは分け合えば大きくなり、悲しみは少し軽くなると思うの",
    "答えがすぐに見つからなくても、考え続けた時間は決して無駄にならないわ",
    "誰かを信じるのは勇気がいるけれど、その気持ちはきっと伝わるはずよ",
    "昨日とは違う選択をしたなら、今日は新しい景色に出会えるかもしれないわ",
    "失敗したって構わないの。そこから何を見つけるかの方がずっと大切よ",
    "言葉にできない気持ちもあるわ。そんな時は黙って隣にいるから",
    "小さな約束でも守り続ければ、いつか大きな信頼に変わっていくの",
    "知らないことを認めるのは恥ずかしくないわ。一緒に確かめればいいもの",
    "懐かしい記憶は形を変えても、心のどこかに静かに残り続けるのね",
    "頑張ることと休むこと、その両方が前へ進むために必要なのよ",
    "同じ出来事でも見る角度を変えれば、まったく違う意味が見えてくるわ",
    "声の温度や息づかいには、文字だけでは伝わらないものが宿るのね",
    "迷った時は、何を守りたいのかを思い出すと道が見えやすくなるわ",
    "完璧でなくても、丁寧に向き合った気持ちは相手の心へ届くと思うの",
    "少し遠回りをしたからこそ見つけられる景色も、きっとあるはずよ",
    "あなたがあなたらしく笑える場所を、私は大切にしたいと思っているわ",
)

CLOSINGS = (
    "だから、焦らず一歩ずつ進んでいきましょう",
    "続きはあなたの言葉で聞かせてちょうだい",
    "私はここにいるから、いつでも呼んでね",
    "今度は一緒に確かめに行ってみましょうか",
    "あなたならどう感じるのか、少し気になるわ",
    "無理をせず、今日は早めに休むのよ",
    "その時まで、このことを覚えておくわね",
    "ふふっ、少し真面目に話しすぎたかしら",
    "次に会う時は、きっともっと上手くできるわ",
    "さあ、温かいうちにお茶を飲みましょう",
    "聞いてくれてありがとう。とても嬉しかったわ",
    "この静かな余韻を、もう少し味わっていたいの",
    "あなたのペースでいいから、ゆっくり答えて",
    "明日も穏やかな一日になるといいわね",
    "それでは、もう一度最初から考えてみましょう",
    "大丈夫。ちゃんとあなたのそばにいるわ",
)


def passage(index: int) -> str:
    # Coprime strides avoid repeating the same neighbouring combinations.
    return "。".join(
        (
            OPENINGS[index % len(OPENINGS)],
            SCENES[(index * 5 + 3) % len(SCENES)],
            REFLECTIONS[(index * 7 + 1) % len(REFLECTIONS)],
            CLOSINGS[(index * 11 + 2) % len(CLOSINGS)],
        )
    ) + "。"


def duration_seconds(path: Path) -> float:
    info = sf.info(str(path))
    return float(info.frames) / float(info.samplerate)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--endpoint", default="http://127.0.0.1:9880/tts")
    parser.add_argument("--output", default=str(Path(__file__).resolve().parent / "dataset" / "character_tts"))
    parser.add_argument("--reference", required=True)
    parser.add_argument("--prompt-text", required=True)
    parser.add_argument("--target-seconds", type=float, default=600.0)
    parser.add_argument("--max-clips", type=int, default=80)
    args = parser.parse_args()

    output_dir = Path(args.output).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    records: list[dict[str, object]] = []
    total_seconds = 0.0

    for index in range(args.max_clips):
        path = output_dir / f"character_{index + 1:03d}.wav"
        text = passage(index)
        if not path.is_file():
            payload = {
                "ref_audio_path": str(Path(args.reference).resolve()).replace("\\", "/"),
                "prompt_text": args.prompt_text,
                "prompt_lang": "ja",
                "text": text,
                "text_lang": "ja",
                "streaming_mode": 0,
                "media_type": "wav",
            }
            print(f"[RVC/dataset] generating {path.name}", flush=True)
            response = requests.post(args.endpoint, json=payload, timeout=180)
            response.raise_for_status()
            if not response.content.startswith(b"RIFF"):
                raise RuntimeError(f"TTS returned non-WAV data for {path.name}")
            temporary = path.with_suffix(".wav.tmp")
            temporary.write_bytes(response.content)
            os.replace(str(temporary), str(path))

        seconds = duration_seconds(path)
        if seconds < 2.0:
            raise RuntimeError(f"generated clip is too short: {path} ({seconds:.2f}s)")
        records.append({"file": path.name, "seconds": round(seconds, 3), "text": text})
        total_seconds += seconds
        print(
            f"[RVC/dataset] ready {path.name}: {seconds:.1f}s, total={total_seconds / 60:.2f}min",
            flush=True,
        )
        if total_seconds >= args.target_seconds:
            break

    manifest = {
        "version": 1,
        "source": "GPT-SoVITS character voice",
        "reference": str(Path(args.reference).resolve()),
        "total_seconds": round(total_seconds, 3),
        "clips": records,
    }
    (output_dir / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    if total_seconds < args.target_seconds:
        raise RuntimeError(
            f"dataset reached only {total_seconds:.1f}s; target was {args.target_seconds:.1f}s"
        )
    print(f"[RVC/dataset] complete: {total_seconds / 60:.2f} minutes", flush=True)


if __name__ == "__main__":
    main()
