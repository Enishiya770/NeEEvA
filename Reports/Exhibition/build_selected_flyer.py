from pathlib import Path
from html import escape
import json
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib.colors import HexColor
from reportlab.lib.units import mm

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'output' / 'pdf'
OUT.mkdir(parents=True, exist_ok=True)
W, H = 148, 210
PDF = OUT / 'neeeva-flyer-selected-layout.pdf'
pdfmetrics.registerFont(TTFont('YaHei', 'C:/Windows/Fonts/msyh.ttc', subfontIndex=0))
pdfmetrics.registerFont(TTFont('YaHeiBold', 'C:/Windows/Fonts/msyhbd.ttc', subfontIndex=0))
PAPER = '#FCFAF7'
INK = '#292237'
PURPLE = '#705486'
MUTED = '#716A78'
LINE = '#D8CFDF'
LILAC = '#EBE3F0'
c = canvas.Canvas(str(PDF), pagesize=(W*mm,H*mm), pageCompression=1)
c.setTitle('NeEEvA | A5 双面宣传单排版稿')
c.setAuthor('杭州昕宁科技有限公司')
all_text = []

class Page:
    def __init__(self, name):
        self.name = name
        self.svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="148mm" height="210mm" viewBox="0 0 148 210"><title>NeEEvA {name}</title>']
        self.rect(0,0,W,H,PAPER)
    def rect(self,x,y,w,h,fill,stroke=None,sw=.25,r=0):
        c.setFillColor(HexColor(fill))
        c.setStrokeColor(HexColor(stroke or fill))
        c.setLineWidth(sw*mm)
        if r:
            c.roundRect(x*mm,(H-y-h)*mm,w*mm,h*mm,r*mm,stroke=bool(stroke),fill=1)
        else:
            c.rect(x*mm,(H-y-h)*mm,w*mm,h*mm,stroke=bool(stroke),fill=1)
        self.svg.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}" fill="{fill}" stroke="{stroke or "none"}" stroke-width="{sw}"/>')
    def line(self,x1,y1,x2,y2,color=LINE,sw=.25):
        c.setStrokeColor(HexColor(color)); c.setLineWidth(sw*mm)
        c.line(x1*mm,(H-y1)*mm,x2*mm,(H-y2)*mm)
        self.svg.append(f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{color}" stroke-width="{sw}"/>')
    def circle(self,x,y,r,fill,stroke=None,sw=.25):
        c.setFillColor(HexColor(fill)); c.setStrokeColor(HexColor(stroke or fill)); c.setLineWidth(sw*mm)
        c.circle(x*mm,(H-y)*mm,r*mm,fill=1,stroke=bool(stroke))
        self.svg.append(f'<circle cx="{x}" cy="{y}" r="{r}" fill="{fill}" stroke="{stroke or "none"}" stroke-width="{sw}"/>')
    def text(self,x,y,value,size=10,color=INK,bold=False,anchor='start'):
        font='YaHeiBold' if bold else 'YaHei'
        width=pdfmetrics.stringWidth(value,font,size)/mm
        left=x if anchor=='start' else x-width/2 if anchor=='middle' else x-width
        assert left>=6 and left+width<=W-6, (value,left,width)
        c.setFillColor(HexColor(color)); c.setFont(font,size)
        fn=c.drawString if anchor=='start' else c.drawCentredString if anchor=='middle' else c.drawRightString
        fn(x*mm,(H-y)*mm,value)
        self.svg.append(f'<text x="{x}" y="{y}" fill="{color}" font-family="Microsoft YaHei, Noto Sans CJK SC, sans-serif" font-size="{size/mm:.5f}" font-weight="{700 if bold else 400}" text-anchor="{anchor}">{escape(value)}</text>')
        all_text.append({'page':self.name,'text':value,'x':x,'baseline':y,'size_pt':size})
    def portal(self,x,y,w,h,color,sw=.3):
        # Architectural doorway outline; supports the two-world visual theme.
        c.setStrokeColor(HexColor(color));c.setLineWidth(sw*mm)
        p=c.beginPath();p.moveTo(x*mm,(H-y-h)*mm);p.lineTo(x*mm,(H-y-w/2)*mm)
        p.curveTo(x*mm,(H-y-w/2*.448)*mm,(x+w/2*.448)*mm,(H-y)*mm,(x+w/2)*mm,(H-y)*mm)
        p.curveTo((x+w-w/2*.448)*mm,(H-y)*mm,(x+w)*mm,(H-y-w/2*.448)*mm,(x+w)*mm,(H-y-w/2)*mm)
        p.lineTo((x+w)*mm,(H-y-h)*mm);c.drawPath(p)
        self.svg.append(f'<path d="M{x},{y+h} V{y+w/2} C{x},{y+w/2*.448} {x+w/2*.448},{y} {x+w/2},{y} C{x+w-w/2*.448},{y} {x+w},{y+w/2*.448} {x+w},{y+w/2} V{y+h}" fill="none" stroke="{color}" stroke-width="{sw}"/>')
    def finish(self):
        self.svg.append('</svg>')
        (OUT/f'neeeva-flyer-{self.name}.svg').write_text('\n'.join(self.svg),encoding='utf-8')
        c.showPage()

front=Page('front')
front.rect(12,12,1.2,6,PURPLE)
front.text(17,18,'NeEEvA',23,bold=True)
front.text(12,29,'虚拟具身智能解决方案',10.3,color=MUTED)
front.text(12,56,'走进彼此的世界',29,bold=True)
# The provided copy says "图片". Preserve that as an explicit replaceable image area.
front.rect(12,70,124,113,LILAC)
front.rect(17,75,114,103,'#F1EBF4')
front.portal(28,82,62,94,'#C7B5D4',.45)
front.portal(58,88,62,88,'#D5C7DE',.45)
front.line(22,176,126,176,'#D5C7DE',.25)
front.rect(50,122,48,13,'#F1EBF4')
front.text(74,130,'图片',11,color=PURPLE,anchor='middle')
front.line(12,194,32,194,LINE,.3)
front.line(116,194,136,194,LINE,.3)
front.text(74,195.5,'感知 · 理解 · 决策 · 行动',10.5,color=PURPLE,anchor='middle')
front.finish()

back=Page('back')
back.text(12,22,'彼此的世界，',23,bold=True)
back.text(12,34,'在这里相遇',23,bold=True)
back.rect(12,44,124,26,'#EEE8F3')
back.rect(12,44,1.2,26,PURPLE)
back.text(18,54,'你所在的世界，',13.5,bold=True)
back.text(18,64,'分享游戏、学习、工作……',10.4,color=MUTED)
# A small double-ring motif connects the two equally weighted worlds.
back.line(74,71.5,74,76,LINE,.35)
back.circle(72.8,74.1,1.4,PAPER,PURPLE,.3)
back.circle(75.2,74.1,1.4,PAPER,PURPLE,.3)
back.rect(12,79,124,26,'#F1EFEA')
back.rect(134.8,79,1.2,26,'#AA9691')
back.text(18,89,'角色所在的世界，',13.5,bold=True)
back.text(18,99,'携手探索、互动、创造……',10.4,color=MUTED)
back.text(12,123,'从单纯交流，到真正参与',15.1,bold=True)
back.text(12,134,'探索人与智能体共生的未来——',10.5,color=MUTED)
back.line(12,143,136,143,LINE,.3)
back.text(12,154,'项目原型已开源 · 支持本地部署',10,color=PURPLE,bold=True)
back.text(12,164,'欢迎算力与硬件支持，以及场景试点、',8.8,color=MUTED)
back.text(12,170,'IP 合作、联合开发与投资交流。',8.8,color=MUTED)
# Honest empty placeholder: it must never be mistaken for a working QR code.
back.rect(12,180,20,20,PAPER,LINE,.3)
for x,y,dx,dy in [(12,180,1,1),(32,180,-1,1),(12,200,1,-1),(32,200,-1,-1)]:
    back.line(x,y,x+dx*3,y,PURPLE,.55)
    back.line(x,y,x,y+dy*3,PURPLE,.55)
back.text(22,191.2,'二维码',7,color=MUTED,anchor='middle')
back.text(39,186,'了解项目',12.5,bold=True)
back.text(39,197,'杭州昕宁科技有限公司 · NeEEvA',8,color=MUTED)
back.finish()
c.save()
(OUT/'neeeva-flyer-layout-text.json').write_text(json.dumps(all_text,ensure_ascii=False,indent=2),encoding='utf-8')
(OUT/'neeeva-flyer-layout-notes.txt').write_text('A5 双面排版稿：148 × 210 mm。\n文案依据用户确认的两张内容截图；仅调整换行、标点间距与信息层级。\nfront.svg / back.svg 保留可编辑文字，字体 Microsoft YaHei；PDF 内嵌字体。\n正面“图片”为主视觉占位；背面二维码为不可扫描占位。\n本版无出血，供审阅与设计编辑；替换主图及真实二维码后再按印厂要求输出印刷文件。\n',encoding='utf-8')
print(str(PDF))
