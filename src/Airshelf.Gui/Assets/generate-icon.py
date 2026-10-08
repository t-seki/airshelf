#!/usr/bin/env python3
"""airshelf.ico の生成スクリプト(#52 / 軸A A1)。

デザイン: チャコール円バッジ + 赤い録音ドット(左下)から右上へ白い電波弧。
16px のトレイ実寸で判読できる太いシルエットを優先している。
1024px マスターで描き、16/20/24/32/48/256 へ LANCZOS 縮小してマルチ解像度 ICO に格納する。

再生成: python3 generate-icon.py  (要 Pillow。airshelf.ico を上書き)
"""
from PIL import Image, ImageDraw
import os

S = 1024  # マスターキャンバス
SIZES = [16, 20, 24, 32, 48, 256]

CHARCOAL = (38, 42, 51, 255)
RED = (229, 57, 53, 255)
WHITE = (250, 250, 250, 255)


def draw_master() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = 20
    d.ellipse([pad, pad, S - pad, S - pad], fill=CHARCOAL)
    # 電波の発信源(赤い録音ドット)を左下寄りに置き、右上へ弧を放つ
    ox, oy = int(S * 0.34), int(S * 0.66)
    d.ellipse([ox - 90, oy - 90, ox + 90, oy + 90], fill=RED)
    for r in (240, 420):
        bbox = [ox - r, oy - r, ox + r, oy + r]
        d.arc(bbox, start=-80, end=10, fill=WHITE, width=78)
    return img


def main() -> None:
    master = draw_master()
    frames = [master.resize((s, s), Image.LANCZOS) for s in SIZES]
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "airshelf.ico")
    frames[-1].save(
        out,
        format="ICO",
        append_images=frames[:-1],
        sizes=[(s, s) for s in SIZES],
    )
    print("wrote", out)


if __name__ == "__main__":
    main()
