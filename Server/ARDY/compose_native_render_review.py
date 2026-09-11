"""Compose unmodified real-VRM render frames into matched review sheets/GIFs."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image,ImageDraw,ImageFont,ImageSequence


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def font(size):
    for path in (Path("C:/Windows/Fonts/segoeui.ttf"),Path("C:/Windows/Fonts/arial.ttf")):
        if path.is_file():
            return ImageFont.truetype(str(path),size)
    return ImageFont.load_default()


def main(args):
    render=json.loads(args.render_manifest.read_text(encoding="utf-8"))
    spec=json.loads(args.review_spec.read_text(encoding="utf-8"))
    lookup={entry["id"]:entry for entry in render["clips"] if entry["mapping"]=="current-root-global"}
    args.output.mkdir(parents=True,exist_ok=True)
    width=480
    height=450
    header=114
    row_label=30
    times=spec.get("sampleTimesSeconds",[.5,1.5,3,5])
    report={"schema":1,"renderManifest":str(args.render_manifest.resolve()),"renderManifestSha256":sha(args.render_manifest),
        "reviewSpecSha256":sha(args.review_spec),"compositionScriptSha256":sha(__file__),
        "method":"CPU image composition only: same-frame panels, aspect-preserving resize, text annotations, GIF palette quantization. No generated or edited avatar poses, time warping or motion correction.",
        "fps":20,"baseline":"Same captured real Animator pose for all panels","mapping":"original production mapping (current-root-global)",
        "playerSourceSha256":render["playerSourceSha256"],"capturedPoseSha256":render["capturedPoseSha256"],"reviews":[]}
    title_font=font(25)
    label_font=font(20)
    note_font=font(17)
    for review in spec["reviews"]:
        items=review["items"]
        sources=[lookup[item["renderId"]] for item in items]
        counts=[len(source["samples"]) for source in sources]
        if len(set(counts))!=1:
            raise ValueError("Paired panels must have equal frame counts")
        count=counts[0]
        for source in sources:
            if source["mapping"]!="current-root-global":
                raise ValueError("This review preserves the original production mapping")
        sheet_path=args.output/(review["id"]+".png")
        gif_path=args.output/(review["id"]+".gif")
        if sheet_path.exists() or gif_path.exists():
            raise FileExistsError("Use a new review ID/output; existing visual evidence is not overwritten")
        def canvas(rows):
            im=Image.new("RGB",(width*len(items),header+(height+row_label)*rows),"white")
            draw=ImageDraw.Draw(im)
            draw.text((16,9),review["title"],fill="#17232a",font=title_font)
            draw.text((16,43),"Real NEVA VRM | Original mapping | Same grounded history",fill="#415361",font=note_font)
            for i,item in enumerate(items):
                draw.text((i*width+16,78),item["label"],fill="#17232a",font=label_font)
            return im
        def paste_frame(im,frame,row):
            draw=ImageDraw.Draw(im)
            top=header+row*(height+row_label)
            draw.text((16,top+3),f"Playback {frame/20:.2f} s",fill="#263b47",font=note_font)
            for col,source in enumerate(sources):
                path=Path(source["framesDirectory"])/f"frame-{frame:04d}.png"
                with Image.open(path) as original:
                    if original.size!=(960,900):
                        raise ValueError("Unexpected renderer aspect ratio; do not distort input frames")
                    tile=original.convert("RGB").resize((width,height),Image.Resampling.LANCZOS)
                    im.paste(tile,(col*width,top+row_label))
        sample_frames=[round(t*20) for t in times]
        if any(frame>=count for frame in sample_frames):
            raise ValueError("Review sample time is outside the rendered timeline")
        sheet=canvas(len(times))
        for row,frame in enumerate(sample_frames):
            paste_frame(sheet,frame,row)
        sheet.save(sheet_path)
        palette=sheet.quantize(colors=256,method=Image.Quantize.MEDIANCUT)
        frames=[]
        for frame in range(count):
            im=canvas(1)
            paste_frame(im,frame,0)
            frames.append(im.quantize(palette=palette,dither=Image.Dither.NONE))
        frames[0].save(gif_path,save_all=True,append_images=frames[1:],duration=50,loop=0,optimize=False,disposal=2)
        for frame in frames:
            frame.close()
        with Image.open(gif_path) as check:
            duration=sum(frame.info.get("duration",0) for frame in ImageSequence.Iterator(check))
            encoded_count=check.n_frames
        if duration!=count*50:
            raise ValueError("GIF duration no longer matches the real 20 FPS render")
        entry={"id":review["id"],"contactSheet":str(sheet_path.resolve()),"gif":str(gif_path.resolve()),
            "contactSheetSha256":sha(sheet_path),"gifSha256":sha(gif_path),"samplePlaybackSeconds":times,
            "gifFrames":encoded_count,"durationMilliseconds":duration,"panels":[]}
        for item,source in zip(items,sources):
            raw_hashes={str(frame):sha(Path(source["framesDirectory"])/f"frame-{frame:04d}.png") for frame in sample_frames}
            entry["panels"].append({"label":item["label"],"renderId":item["renderId"],"inputClipSha256":source["inputSha256"],
                "framesDirectory":source["framesDirectory"],"sampleImageSha256":raw_hashes,"distinctSampleImages":len(set(raw_hashes.values()))})
        report["reviews"].append(entry)
        print(json.dumps({"id":review["id"],"gifFrames":encoded_count,"durationMilliseconds":duration,"contactSheet":str(sheet_path)},ensure_ascii=False),flush=True)
    (args.output/(args.review_spec.stem+"-manifest.json")).write_text(json.dumps(report,indent=2),encoding="utf-8")


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--render-manifest",type=Path,required=True)
    parser.add_argument("--review-spec",type=Path,required=True,help="JSON reviews:[{id,title,items:[{label,renderId}]}]")
    parser.add_argument("--output",type=Path,required=True)
    main(parser.parse_args())
